using System.Text;

namespace ReAnimated.Codecs.Fed;

/// <summary>Bounded little-endian FED authoring, validated by the ordinary reader before publication.</summary>
public static class FedWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Write(FedDocument document, FedLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        limits ??= FedLimits.Default;
        limits.Validate();
        if (document.Expressions is null || document.Expressions.Count > limits.MaximumExpressions)
            throw new InvalidDataException("FED expression count exceeds the configured limit.");
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, StrictUtf8, leaveOpen: true);
        int totalWeights = 0, stringBytes = 0;
        writer.Write(document.Expressions.Count);
        foreach (FedExpression expression in document.Expressions)
        {
            WriteName(expression.Name);
            if (expression.Weights is null || expression.Weights.Count > limits.MaximumWeightsPerExpression ||
                (totalWeights += expression.Weights.Count) > limits.MaximumTotalWeights)
                throw new InvalidDataException("FED morph count exceeds the configured limit.");
            writer.Write(expression.Weights.Count);
            foreach (FedMorphWeight row in expression.Weights)
            {
                WriteName(row.MorphName);
                if (!float.IsFinite(row.Weight)) throw new InvalidDataException("FED morph weight is not finite.");
                writer.Write(row.Weight);
                CheckSize();
            }
        }
        CheckSize();
        stream.Position = 0;
        _ = FedReader.Read(stream, document.Name, limits);
        return stream.ToArray();

        void CheckSize()
        {
            if (stream.Length > limits.MaximumFileBytes)
                throw new InvalidDataException("FED payload exceeds the configured file limit.");
        }
        void WriteName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
                throw new InvalidDataException("FED names cannot be empty or contain NUL.");
            int count;
            try { count = StrictUtf8.GetByteCount(name); }
            catch (EncoderFallbackException exception) { throw new InvalidDataException("FED name is not valid Unicode.", exception); }
            if (count > ushort.MaxValue || count > limits.MaximumStringBytes ||
                (stringBytes += count) > limits.MaximumTotalStringBytes)
                throw new InvalidDataException("FED string exceeds the configured limit.");
            writer.Write((ushort)count);
            writer.Write(StrictUtf8.GetBytes(name));
            CheckSize();
        }
    }

    public static void Write(Stream destination, FedDocument document, FedLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("FED destination must be writable.", nameof(destination));
        destination.Write(Write(document, limits));
    }

    public static void Write(string path, FedDocument document, FedLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = Write(document, limits);
        File.WriteAllBytes(path, bytes);
    }
}
