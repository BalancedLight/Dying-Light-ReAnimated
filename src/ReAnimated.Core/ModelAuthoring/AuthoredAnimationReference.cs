namespace ReAnimated.Core.ModelAuthoring;

public sealed record AuthoredAnimationReference
{
    public string RigSignature { get; init; } = string.Empty;
    public string PayloadSha256 { get; init; } = string.Empty;
    public long PayloadLength { get; init; }

    public void Validate()
    {
        RigContractRules.Hash(RigSignature, nameof(RigSignature));
        RigContractRules.Hash(PayloadSha256, nameof(PayloadSha256));
        if (PayloadLength <= 0 || PayloadLength > DerivedAnimationDataCodec.MaximumPayloadBytes)
            throw new ArgumentException("The authored animation payload length is invalid.");
    }

    public static string EntryPath(Guid clipId)
    {
        RigContractRules.Identifier(clipId, nameof(clipId));
        return $"animation/authored/{clipId:N}.json";
    }
}
