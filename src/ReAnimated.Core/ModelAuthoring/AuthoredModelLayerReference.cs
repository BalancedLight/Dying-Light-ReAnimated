using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Portable reference to source-linked authored edits. The source FBX remains the sole base mesh.</summary>
public sealed record AuthoredModelLayerReference
{
    public const string PayloadEntryPath = "authoring/model-layer.bin";
    public int Version { get; init; } = 1;
    public string EntryPath { get; init; } = PayloadEntryPath;
    public string ContentSha256 { get; init; } = string.Empty;
    public int PayloadLength { get; init; }
    public CustomModelRigMode SourceRigMode { get; init; } = CustomModelRigMode.Auto;
    public bool SourceIgnoreMorphChannels { get; init; }

    public void Validate()
    {
        if (Version != 1 || !string.Equals(EntryPath, PayloadEntryPath, StringComparison.Ordinal) ||
            PayloadLength <= 0 || PayloadLength > AuthoredModelLayerCodec.MaximumPayloadBytes || !Enum.IsDefined(SourceRigMode))
            throw new ArgumentException("The authored model layer reference is invalid or unsupported.");
        ProjectAssetReference.ValidateSha256(ContentSha256, nameof(ContentSha256));
    }
}
