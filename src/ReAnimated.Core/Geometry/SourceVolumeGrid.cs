using ReAnimated.Core.Mathematics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Core.Geometry;

public sealed record SourceVolumeGridOptions
{
    public int LongestAxisCells { get; init; } = 64;
    public int PaddingCells { get; init; } = 1;
    public int MaximumCells { get; init; } = 1_000_000;
    /// <summary>Diagnostic mode only: retains unknown cells rather than presenting open geometry as a solid.</summary>
    public bool AllowUnreliableTopology { get; init; }
}

public readonly record struct SourceVolumeGridCell(SourceVolumeLocation Location, double? SignedField);
public readonly record struct SourceVolumeGridProgress(int CompletedSlices, int TotalSlices);

/// <summary>
/// Bounded cell-centred volume samples tied to one immutable source snapshot. Unknown values stay
/// unavailable. The lattice is disposable analysis data, not replacement render topology or a rig.
/// A union field is not an exact CSG boundary distance, and a sampled lattice can miss thin features.
/// </summary>
public sealed class SourceVolumeGrid
{
    private readonly SourceVolumeLocation[] _locations;
    private readonly double[] _fields;
    public string SourceSha256 { get; }
    public string InputFingerprint { get; }
    public Vector3D Min { get; }
    public double CellSize { get; }
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }
    public int CellCount => _locations.Length;
    public int InteriorCellCount { get; }
    public int UnknownCellCount { get; }
    public bool HasUsableInterior => InteriorCellCount > 0;
    public bool HasCompleteField => HasUsableInterior && UnknownCellCount == 0;
    public SourceVolumeFieldKind FieldKind { get; }

    private SourceVolumeGrid(string hash, string inputFingerprint, Vector3D min, double cellSize, int x, int y, int z,
        SourceVolumeLocation[] locations, double[] fields, int inside, int unknown, SourceVolumeFieldKind fieldKind)
    {
        SourceSha256 = hash; InputFingerprint = inputFingerprint; Min = min; CellSize = cellSize; SizeX = x; SizeY = y; SizeZ = z;
        _locations = locations; _fields = fields; InteriorCellCount = inside; UnknownCellCount = unknown; FieldKind = fieldKind;
    }

    public static SourceVolumeGrid Build(SourceGeometryVolume volume, SourceVolumeGridOptions? options = null,
        IProgress<SourceVolumeGridProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ValidateOptions(options);
        if (!volume.HasGeometry) throw new InvalidDataException("There is no source surface to sample.");
        if (volume.HasUnreliableTopology && !options.AllowUnreliableTopology)
            throw new InvalidDataException("Volume sampling requires closed source shells; inspect or exclude unreliable components first.");
        Vector3D extent = volume.Max - volume.Min;
        double longest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        double spacing = longest / options.LongestAxisCells;
        if (!double.IsFinite(spacing) || spacing <= 0) throw new InvalidDataException("Source geometry has no usable volume extent.");
        Vector3D min = volume.Min - Vector3D.One * (spacing * options.PaddingCells);
        Vector3D max = volume.Max + Vector3D.One * (spacing * options.PaddingCells);
        return BuildCore(volume, options, min, max, extent, spacing, progress,
            countEmptyAsUnknown: true, includePaddingInCount: true, requestedRegion: null,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds a disposable cell grid over an explicit global authoring-space
    /// box. The box is never clipped to the source and its cell centres remain
    /// inside the requested bounds. Padding is intentionally not added outside
    /// an explicit region; <see cref="SourceVolumeGridOptions.PaddingCells"/>
    /// remains a default-build compatibility option.
    /// </summary>
    public static SourceVolumeGrid BuildRegion(
        SourceGeometryVolume volume,
        Vector3D minimum,
        Vector3D maximum,
        SourceVolumeGridOptions? options = null,
        IProgress<SourceVolumeGridProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ValidateOptions(options);
        ValidateRegionBounds(minimum, maximum);
        if (!volume.HasGeometry) throw new InvalidDataException("There is no source surface to sample.");
        if (volume.HasUnreliableTopology && !options.AllowUnreliableTopology)
            throw new InvalidDataException("Volume sampling requires closed source shells; inspect or exclude unreliable components first.");

        Vector3D extent = maximum - minimum;
        double longest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        double spacing = longest / options.LongestAxisCells;
        if (!double.IsFinite(spacing) || spacing <= 0)
            throw new InvalidDataException("The requested region has no usable extent.");
        return BuildCore(volume, options, minimum, maximum, extent, spacing, progress,
            countEmptyAsUnknown: false, includePaddingInCount: false, requestedRegion: (minimum, maximum),
            cancellationToken: cancellationToken);
    }

    private static SourceVolumeGrid BuildCore(
        SourceGeometryVolume volume,
        SourceVolumeGridOptions options,
        Vector3D min,
        Vector3D max,
        Vector3D extent,
        double spacing,
        IProgress<SourceVolumeGridProgress>? progress,
        bool countEmptyAsUnknown,
        bool includePaddingInCount,
        (Vector3D Minimum, Vector3D Maximum)? requestedRegion,
        CancellationToken cancellationToken)
    {
        int x = Count(extent.X), y = Count(extent.Y), z = Count(extent.Z);
        long count = (long)x * y * z;
        if (count > options.MaximumCells) throw new ArgumentException("Volume lattice exceeds its cell budget; choose a lower resolution or a smaller region.", nameof(options));
        Vector3D firstCellOffset = includePaddingInCount
            ? Vector3D.Zero
            : new Vector3D(AxisOffset(extent.X, x), AxisOffset(extent.Y, y), AxisOffset(extent.Z, z));
        Vector3D latticeMin = min + firstCellOffset;
        if (!latticeMin.IsFinite || !max.IsFinite) throw new InvalidDataException("Volume lattice bounds exceed the finite coordinate range.");
        var locations = new SourceVolumeLocation[(int)count];
        var fields = new double[(int)count];
        int inside = 0, unknown = 0;
        bool sampledAny = false;
        for (int iz = 0; iz < z; iz++)
        {
            for (int iy = 0; iy < y; iy++)
            for (int ix = 0; ix < x; ix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = (iz * y + iy) * x + ix;
                var sample = volume.Sample(latticeMin + new Vector3D(ix + .5, iy + .5, iz + .5) * spacing,
                    surfaceTolerance: spacing * 1e-5, cancellationToken: cancellationToken);
                locations[index] = sample.Location;
                fields[index] = sample.SignedField ?? double.NaN;
                if (sample.Location == SourceVolumeLocation.Interior) inside++;
                if (sample.Location != SourceVolumeLocation.Empty) sampledAny = true;
                if (sample.Location == SourceVolumeLocation.Unknown ||
                    sample.SignedField is null &&
                    (countEmptyAsUnknown || sample.Location != SourceVolumeLocation.Empty)) unknown++;
            }
            progress?.Report(new(iz + 1, z));
            cancellationToken.ThrowIfCancellationRequested();
        }
        var kind = !sampledAny || volume.HasUnreliableTopology ? SourceVolumeFieldKind.Unavailable : volume.Shells.Length == 1
            ? SourceVolumeFieldKind.SignedDistanceToInputShell : SourceVolumeFieldKind.UnionOfSignedShellFields;
        string identity;
        if (requestedRegion is { } region)
        {
            identity = string.Format(CultureInfo.InvariantCulture,
                "source-volume-grid-region-v1|{0}|{1:R}|{2:R}|{3:R}|{4:R}|{5:R}|{6:R}|{7:R}|{8:R}|{9:R}|{10:R}|{11}|{12}|{13}|{14}",
                volume.GeometryFingerprint, region.Minimum.X, region.Minimum.Y, region.Minimum.Z,
                region.Maximum.X, region.Maximum.Y, region.Maximum.Z, latticeMin.X, latticeMin.Y, latticeMin.Z, spacing, x, y, z,
                options.AllowUnreliableTopology);
        }
        else
        {
            identity = string.Create(CultureInfo.InvariantCulture,
                $"source-volume-grid-v1|{volume.GeometryFingerprint}|{latticeMin.X:R}|{latticeMin.Y:R}|{latticeMin.Z:R}|{spacing:R}|{x}|{y}|{z}|{options.AllowUnreliableTopology}");
        }
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(volume.SourceSha256, fingerprint, latticeMin, spacing, x, y, z, locations, fields, inside, unknown, kind);

        int Count(double length) => checked(Math.Max(1, (int)Math.Ceiling(length / spacing)) +
            (includePaddingInCount ? 2 * options.PaddingCells : 0));

        double AxisOffset(double length, int cells) => (length - cells * spacing) * .5;
    }

    private static void ValidateOptions(SourceVolumeGridOptions options)
    {
        if (options.LongestAxisCells is < 4 or > 512 ||
            options.PaddingCells is < 0 or > 8 ||
            options.MaximumCells is <= 0 or > 8_000_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Volume lattice resolution, padding or cell budget is outside supported limits.");
    }

    private static void ValidateRegionBounds(Vector3D minimum, Vector3D maximum)
    {
        if (!minimum.IsFinite || !maximum.IsFinite ||
            maximum.X <= minimum.X || maximum.Y <= minimum.Y || maximum.Z <= minimum.Z)
            throw new ArgumentException("A source-volume region requires finite bounds with strictly positive extent.");
    }

    public SourceVolumeGridCell GetCell(int x, int y, int z)
    {
        int index = Index(x, y, z);
        return new(_locations[index], double.IsNaN(_fields[index]) ? null : _fields[index]);
    }

    public Vector3D GetPosition(int x, int y, int z)
    {
        _ = Index(x, y, z);
        return Min + new Vector3D(x + .5, y + .5, z + .5) * CellSize;
    }

    private int Index(int x, int y, int z)
    {
        if ((uint)x >= (uint)SizeX || (uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ)
            throw new ArgumentOutOfRangeException(nameof(x), "Volume cell coordinate is outside the lattice.");
        return (z * SizeY + y) * SizeX + x;
    }
}
