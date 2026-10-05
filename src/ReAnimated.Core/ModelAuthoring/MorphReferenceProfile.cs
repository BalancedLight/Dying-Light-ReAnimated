using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Source identity and geometry inventory for a reusable morph source.</summary>
public sealed record MorphReferenceProfile
{
    public string SourceSha256 { get; init; } = string.Empty;
    public string? RigSignature { get; init; }
    public ImmutableArray<MorphReferenceChannel> Channels { get; init; } = [];
    public ImmutableArray<MorphReferenceSurface> Surfaces { get; init; } = [];
    public string TopologyFingerprint { get; init; } = string.Empty;

    public void Validate()
    {
        if (SourceSha256.Length != 64 || SourceSha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Morph source requires a SHA-256 identity.", nameof(SourceSha256));
        if (Channels.IsDefault || Surfaces.IsDefault || string.IsNullOrWhiteSpace(TopologyFingerprint))
            throw new ArgumentException("Morph source inventory is incomplete.", nameof(Channels));
        if (Channels.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Channels.Length)
            throw new ArgumentException("Morph channel names must be unique.", nameof(Channels));
        foreach (var channel in Channels) channel.Validate();
        foreach (var surface in Surfaces) surface.Validate();
        if (Surfaces.Select(s => s.GeometryIdentity + "|" + s.LodIndex).Distinct(StringComparer.Ordinal).Count() != Surfaces.Length)
            throw new ArgumentException("Morph surface identities must be unique.", nameof(Surfaces));
        if (!string.Equals(TopologyFingerprint, MorphProfileFingerprint.Compute(Surfaces), StringComparison.Ordinal))
            throw new ArgumentException("Morph topology fingerprint does not match the inventory.", nameof(TopologyFingerprint));
    }
}

public sealed record MorphReferenceChannel(
    int Index,
    string Name,
    uint DescriptorHash,
    long SourceChannelObjectId = 0,
    long SourceShapeObjectId = 0)
{
    public void Validate()
    {
        if (Index < 0 || string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Morph channel provenance is invalid.");
    }
}

public sealed record MorphReferenceSurface(
    string Name,
    string GeometryIdentity,
    int LodIndex,
    ImmutableArray<Vector3D> NeutralPositions,
    ImmutableArray<int> Indices)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(GeometryIdentity) || LodIndex < 0 ||
            NeutralPositions.IsDefaultOrEmpty || Indices.IsDefaultOrEmpty || Indices.Length % 3 != 0 ||
            Indices.Any(i => i < 0 || i >= NeutralPositions.Length) || NeutralPositions.Any(p=>!p.IsFinite))
            throw new ArgumentException("Morph surface topology is invalid.", nameof(Name));
    }
}

public static class MorphProfileFingerprint
{
    public static string Compute(IEnumerable<MorphReferenceSurface> surfaces)
    {
        var text = new StringBuilder();
        foreach (var surface in surfaces.OrderBy(s => s.GeometryIdentity, StringComparer.Ordinal).ThenBy(s => s.LodIndex))
            text.Append(surface.Name).Append('|').Append(surface.GeometryIdentity).Append('|').Append(surface.LodIndex).Append('|')
                .Append(surface.NeutralPositions.Length).Append(':').Append(string.Join(',', surface.Indices)).Append(':')
                .Append(string.Join(';', surface.NeutralPositions.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}")))).Append(';');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
