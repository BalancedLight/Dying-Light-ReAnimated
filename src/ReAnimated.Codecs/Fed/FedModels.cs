namespace ReAnimated.Codecs.Fed;

public sealed record FedLimits
{
    public static FedLimits Default { get; } = new();

    public int MaximumFileBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumExpressions { get; init; } = 100_000;

    public int MaximumWeightsPerExpression { get; init; } = 100_000;

    public int MaximumTotalWeights { get; init; } = 2_000_000;

    public int MaximumStringBytes { get; init; } = 4096;

    public int MaximumTotalStringBytes { get; init; } = 8 * 1024 * 1024;

    public bool RejectTrailingBytes { get; init; } = true;

    public bool RejectDuplicateNames { get; init; }

    internal void Validate()
    {
        if (MaximumFileBytes <= 0 ||
            MaximumExpressions <= 0 ||
            MaximumWeightsPerExpression <= 0 ||
            MaximumTotalWeights <= 0 ||
            MaximumStringBytes <= 0 ||
            MaximumTotalStringBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FedLimits),
                "All FED limits must be positive.");
        }
    }
}

public sealed record FedMorphWeight(string MorphName, float Weight);

public enum FedDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record FedDiagnostic(
    string Code,
    FedDiagnosticSeverity Severity,
    string Message,
    int? ExpressionIndex = null,
    int? WeightIndex = null);

public sealed record FedExpression(
    string Name,
    IReadOnlyList<FedMorphWeight> Weights);

public sealed record FedDocument(
    string Name,
    IReadOnlyList<FedExpression> Expressions,
    IReadOnlyList<FedDiagnostic> Diagnostics)
{
    public FedExpression? FindExpression(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Expressions.FirstOrDefault(expression =>
            string.Equals(
                expression.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
    }
}
