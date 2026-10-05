using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Bounded, single-surface OBJ interchange for a current neutral face. OBJ
/// positions are written in authoring metres, one vertex per original control
/// point in source order. Face rows retain the exact ordered control-point
/// triangles. No nearest-point or reordered-vertex matching is performed.
/// </summary>
public static class ManualMorphObjCodec
{
    public const int MaximumObjBytes = 64 * 1024 * 1024;
    public const int MaximumFileBytes = MaximumObjBytes;
    public const int MaximumControlPoints = 1_000_000;
    public const int MaximumTriangles = 2_000_000;
    private const int MaximumLineCharacters = 4096;
    private const double CoincidentCornerTolerance = 1e-9;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeNeutral(FbxModelSurface target)
    {
        ExpectedSurface expected = ReadExpected(target);
        var text = new StringBuilder(Math.Min(MaximumFileBytes, 256 + expected.Positions.Length * 80 + expected.Triangles.Length * 16));
        text.Append("# DL ReAnimated neutral face sculpt; positions in metres; preserve vertex and triangle order.\n");
        foreach (Vector3D position in expected.Positions)
            text.Append("v ").Append(RoundTrip(position.X)).Append(' ')
                .Append(RoundTrip(position.Y)).Append(' ').Append(RoundTrip(position.Z)).Append('\n');
        for (int i = 0; i < expected.Triangles.Length; i += 3)
            text.Append("f ").Append(expected.Triangles[i] + 1).Append(' ')
                .Append(expected.Triangles[i + 1] + 1).Append(' ')
                .Append(expected.Triangles[i + 2] + 1).Append('\n');
        byte[] bytes = StrictUtf8.GetBytes(text.ToString());
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("The neutral face OBJ exceeds its byte bound.");
        return bytes;
    }

    public static ImmutableArray<Vector3D> ComputePositionDeltas(FbxModelSurface target, ReadOnlySpan<byte> objBytes)
    {
        ExpectedSurface expected = ReadExpected(target);
        if (objBytes.IsEmpty || objBytes.Length > MaximumFileBytes)
            throw new InvalidDataException("The sculpt OBJ is empty or exceeds its byte bound.");
        string source;
        try { source = StrictUtf8.GetString(objBytes); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("The sculpt OBJ is not valid UTF-8.", exception); }
        if (source.Contains('\0')) throw new InvalidDataException("The sculpt OBJ contains NUL.");
        var positions = new List<Vector3D>(expected.Positions.Length);
        var triangles = new List<int>(expected.Triangles.Length);
        int textureCount = 0, normalCount = 0;
        bool sawFace = false, sawObject = false, sawGroup = false;
        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > MaximumLineCharacters)
                throw new InvalidDataException("A sculpt OBJ line exceeds its bound.");
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            switch (fields[0])
            {
                case "v":
                    if (sawFace || fields.Length != 4 || positions.Count >= expected.Positions.Length)
                        throw new InvalidDataException("Sculpt vertices must precede faces and retain the exact control-point count.");
                    positions.Add(new(ParseFinite(fields[1]), ParseFinite(fields[2]), ParseFinite(fields[3])));
                    break;
                case "vt":
                    if (sawFace || fields.Length is < 2 or > 4 || textureCount >= MaximumControlPoints)
                        throw new InvalidDataException("Invalid sculpt texture-coordinate row.");
                    for (int i = 1; i < fields.Length; i++) _ = ParseFinite(fields[i]);
                    textureCount++;
                    break;
                case "vn":
                    if (sawFace || fields.Length != 4 || normalCount >= MaximumControlPoints)
                        throw new InvalidDataException("Invalid sculpt normal row.");
                    for (int i = 1; i < fields.Length; i++) _ = ParseFinite(fields[i]);
                    normalCount++;
                    break;
                case "f":
                    if (positions.Count != expected.Positions.Length || fields.Length != 4 ||
                        triangles.Count / 3 >= MaximumTriangles)
                        throw new InvalidDataException("Sculpt faces must be triangles after the exact vertex inventory.");
                    sawFace = true;
                    for (int i = 1; i <= 3; i++)
                        triangles.Add(ParseFacePoint(fields[i], positions.Count, textureCount, normalCount));
                    int last = triangles.Count - 3;
                    if (triangles[last] == triangles[last + 1] || triangles[last] == triangles[last + 2] ||
                        triangles[last + 1] == triangles[last + 2])
                        throw new InvalidDataException("A sculpt triangle has repeated control points.");
                    break;
                case "o":
                    if (sawFace || sawObject || fields.Length != 2)
                        throw new InvalidDataException("Multiple or late OBJ objects can change sculpt topology.");
                    sawObject = true;
                    break;
                case "g":
                    if (sawFace || sawGroup || fields.Length != 2)
                        throw new InvalidDataException("Multiple or late OBJ groups can change sculpt topology.");
                    sawGroup = true;
                    break;
                case "s":
                    if (fields.Length != 2 || !(fields[1] is "off" or "0") &&
                        (!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int group) ||
                         group is < 1 or > 65535))
                        throw new InvalidDataException("Invalid sculpt smoothing-group metadata.");
                    break;
                case "usemtl":
                    if (fields.Length != 2 || !SafeStyleName(fields[1]))
                        throw new InvalidDataException("Invalid sculpt material metadata.");
                    break;
                case "mtllib":
                    if (fields.Length is < 2 or > 16 || fields.Skip(1).Any(name => !SafeStyleName(name)))
                        throw new InvalidDataException("Invalid sculpt material-library metadata.");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported sculpt OBJ directive '{fields[0]}'.");
            }
        }
        if (positions.Count != expected.Positions.Length || !triangles.SequenceEqual(expected.Triangles))
            throw new InvalidDataException("Sculpt control-point count or ordered triangle topology changed.");
        return expected.CornerPointIndexes.Select((point, vertex) =>
            positions[point] - target.Vertices[vertex].Position).ToImmutableArray();
    }

    private static ExpectedSurface ReadExpected(FbxModelSurface target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.SourceGeometry is null || target.SourceGeometry.ControlPoints.IsDefaultOrEmpty ||
            target.SourceCorners.IsDefault || target.SourceCorners.Length != target.Vertices.Length ||
            target.Vertices.IsDefaultOrEmpty || target.Indices.IsDefaultOrEmpty || target.Indices.Length % 3 != 0 ||
            target.SourceGeometry.ControlPoints.Length > MaximumControlPoints ||
            target.Indices.Length / 3 > MaximumTriangles)
            throw new InvalidDataException("Manual OBJ requires bounded, original control-point and triangle provenance.");
        int count = target.SourceGeometry.ControlPoints.Length;
        Vector3D[] points = target.SourceGeometry.ControlPoints.ToArray();
        bool[] used = new bool[count];
        var corners = ImmutableArray.CreateBuilder<int>(target.Vertices.Length);
        for (int i = 0; i < target.Vertices.Length; i++)
        {
            int point = target.SourceCorners[i].ControlPointIndex;
            Vector3D position = target.Vertices[i].Position;
            if ((uint)point >= (uint)count || !position.IsFinite ||
                used[point] && (points[point] - position).Length > CoincidentCornerTolerance)
                throw new InvalidDataException("Target corners have invalid or conflicting neutral control-point positions.");
            points[point] = position;
            used[point] = true;
            corners.Add(point);
        }
        for (int i = 0; i < count; i++)
            if (!points[i].IsFinite)
                throw new InvalidDataException("An unused original control point has nonfinite authoring-space coordinates.");
        var triangles = ImmutableArray.CreateBuilder<int>(target.Indices.Length);
        for (int i = 0; i < target.Indices.Length; i++)
        {
            uint vertex = target.Indices[i];
            if (vertex >= (uint)corners.Count)
                throw new InvalidDataException("A target triangle references a missing render corner.");
            triangles.Add(corners[(int)vertex]);
        }
        for (int i = 0; i < triangles.Count; i += 3)
            if (triangles[i] == triangles[i + 1] || triangles[i] == triangles[i + 2] ||
                triangles[i + 1] == triangles[i + 2])
                throw new InvalidDataException("A target triangle collapses in original control-point topology.");
        return new(points.ToImmutableArray(), corners.ToImmutable(), triangles.ToImmutable());
    }

    private static int ParseFacePoint(string token, int vertices, int textures, int normals)
    {
        string[] parts = token.Split('/');
        if (parts.Length is < 1 or > 3 || parts[0].Length == 0)
            throw new InvalidDataException("A sculpt face has a malformed vertex reference.");
        int point = PositiveIndex(parts[0], vertices, "vertex");
        if (parts.Length >= 2)
        {
            if (parts[1].Length == 0 && parts.Length == 2)
                throw new InvalidDataException("A sculpt face has an empty texture reference.");
            if (parts[1].Length > 0) _ = PositiveIndex(parts[1], textures, "texture");
        }
        if (parts.Length == 3)
        {
            if (parts[2].Length == 0)
                throw new InvalidDataException("A sculpt face has an incomplete normal or texture reference.");
            _ = PositiveIndex(parts[2], normals, "normal");
        }
        return point - 1;
    }

    private static int PositiveIndex(string text, int maximum, string label)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
            index <= 0 || index > maximum)
            throw new InvalidDataException($"A sculpt face has an invalid {label} index.");
        return index;
    }

    private static double ParseFinite(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
            throw new InvalidDataException("A sculpt OBJ coordinate is not finite invariant-culture text.");
        return value;
    }

    private static string RoundTrip(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // Styling is ignored. Only local, bounded names are accepted; no library
    // is opened and no path is resolved during sculpt import.
    private static bool SafeStyleName(string value) => value.Length is > 0 and <= 256 &&
        !value.Contains(':') && !value.Contains('/') && !value.Contains('\\') &&
        value.All(c => !char.IsControl(c));

    private sealed record ExpectedSurface(ImmutableArray<Vector3D> Positions,
        ImmutableArray<int> CornerPointIndexes, ImmutableArray<int> Triangles);
}
