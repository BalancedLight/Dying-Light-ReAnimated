using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Core.Geometry;

/// <summary>Identity of the actual selected geometry, not a validation result or only the original file hash.</summary>
internal static class SourceGeometryFingerprint
{
    public static string Compute(SourceGeometryAnalysis analysis, string purpose, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] scratch = new byte[24];
        WriteText(purpose); WriteText(analysis.SourceSha256);
        foreach (var component in analysis.Components.OrderBy(static c => c.Geometry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteText(component.Geometry.Id);
            WriteInt(component.Geometry.ControlPoints.Length);
            foreach (var point in component.Geometry.ControlPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteInt64LittleEndian(scratch, BitConverter.DoubleToInt64Bits(point.X));
                BinaryPrimitives.WriteInt64LittleEndian(scratch.AsSpan(8), BitConverter.DoubleToInt64Bits(point.Y));
                BinaryPrimitives.WriteInt64LittleEndian(scratch.AsSpan(16), BitConverter.DoubleToInt64Bits(point.Z));
                hash.AppendData(scratch);
            }
            WriteInt(component.Triangles.Length);
            foreach (var triangle in component.Triangles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteInt32LittleEndian(scratch, triangle.A);
                BinaryPrimitives.WriteInt32LittleEndian(scratch.AsSpan(4), triangle.B);
                BinaryPrimitives.WriteInt32LittleEndian(scratch.AsSpan(8), triangle.C);
                BinaryPrimitives.WriteInt32LittleEndian(scratch.AsSpan(12), triangle.Source.PolygonIndex);
                BinaryPrimitives.WriteInt32LittleEndian(scratch.AsSpan(16), triangle.Source.TriangleInPolygon);
                hash.AppendData(scratch.AsSpan(0, 20));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void WriteInt(int value) { BinaryPrimitives.WriteInt32LittleEndian(scratch, value); hash.AppendData(scratch.AsSpan(0, 4)); }
        void WriteText(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); WriteInt(bytes.Length); hash.AppendData(bytes); }
    }
}
