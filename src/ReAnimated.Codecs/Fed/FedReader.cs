using System.Buffers.Binary;
using System.Text;

namespace ReAnimated.Codecs.Fed;

public static class FedReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static FedDocument Read(
        string path,
        FedLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return Read(
            stream,
            Path.GetFileNameWithoutExtension(fullPath),
            limits);
    }

    public static FedDocument Read(
        Stream stream,
        string name,
        FedLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!stream.CanRead)
        {
            throw new ArgumentException(
                "The FED stream must be readable.",
                nameof(stream));
        }

        limits ??= FedLimits.Default;
        limits.Validate();
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining < 0 || remaining > limits.MaximumFileBytes)
            {
                throw new InvalidDataException(
                    $"FED payload size {remaining:N0} is outside the configured limit.");
            }
        }

        CountingReader reader = new(stream, limits);
        int expressionCount = reader.ReadBoundedInt32(
            "expression count",
            limits.MaximumExpressions);
        List<FedExpression> expressions = new(expressionCount);
        List<FedDiagnostic> diagnostics = [];
        HashSet<string> expressionNames =
            new(StringComparer.OrdinalIgnoreCase);
        int totalWeights = 0;

        for (int expressionIndex = 0;
             expressionIndex < expressionCount;
             expressionIndex++)
        {
            string expressionName = reader.ReadString(
                $"expression {expressionIndex} name");
            if (string.IsNullOrWhiteSpace(expressionName))
            {
                throw new InvalidDataException(
                    $"FED expression {expressionIndex} has an empty name.");
            }

            if (!expressionNames.Add(expressionName))
            {
                if (limits.RejectDuplicateNames)
                {
                    throw new InvalidDataException(
                        $"FED contains duplicate expression '{expressionName}'.");
                }

                diagnostics.Add(new FedDiagnostic(
                    "FED001",
                    FedDiagnosticSeverity.Warning,
                    $"Expression '{expressionName}' duplicates an earlier expression; source order is preserved and name lookup returns the first occurrence.",
                    expressionIndex));
            }

            int weightCount = reader.ReadBoundedInt32(
                $"expression {expressionIndex} weight count",
                limits.MaximumWeightsPerExpression);
            totalWeights = checked(totalWeights + weightCount);
            if (totalWeights > limits.MaximumTotalWeights)
            {
                throw new InvalidDataException(
                    "FED total morph weight count exceeds the configured limit.");
            }

            List<FedMorphWeight> weights = new(weightCount);
            HashSet<string> morphNames =
                new(StringComparer.OrdinalIgnoreCase);
            for (int weightIndex = 0;
                 weightIndex < weightCount;
                 weightIndex++)
            {
                string morphName = reader.ReadString(
                    $"expression {expressionIndex} morph {weightIndex} name");
                if (string.IsNullOrWhiteSpace(morphName))
                {
                    throw new InvalidDataException(
                        $"FED expression '{expressionName}' has an empty morph name.");
                }

                if (!morphNames.Add(morphName))
                {
                    if (limits.RejectDuplicateNames)
                    {
                        throw new InvalidDataException(
                            $"FED expression '{expressionName}' contains duplicate morph '{morphName}'.");
                    }

                    diagnostics.Add(new FedDiagnostic(
                        "FED002",
                        FedDiagnosticSeverity.Warning,
                        $"Expression '{expressionName}' contains duplicate morph '{morphName}'; both ordered weights are preserved.",
                        expressionIndex,
                        weightIndex));
                }

                float weight = reader.ReadSingle(
                    $"expression {expressionIndex} morph {weightIndex} weight");
                if (!float.IsFinite(weight))
                {
                    throw new InvalidDataException(
                        $"FED morph '{morphName}' has a non-finite weight.");
                }

                weights.Add(new FedMorphWeight(morphName, weight));
            }

            expressions.Add(new FedExpression(expressionName, weights));
        }

        if (limits.RejectTrailingBytes)
        {
            int trailing = stream.ReadByte();
            if (trailing != -1)
            {
                throw new InvalidDataException(
                    "FED payload contains trailing bytes.");
            }
        }

        return new FedDocument(name, expressions, diagnostics);
    }

    private sealed class CountingReader
    {
        private readonly Stream _stream;
        private readonly FedLimits _limits;
        private readonly byte[] _scalar = new byte[sizeof(int)];
        private int _bytesRead;
        private int _stringBytes;

        public CountingReader(Stream stream, FedLimits limits)
        {
            _stream = stream;
            _limits = limits;
        }

        public int ReadBoundedInt32(string label, int maximum)
        {
            ReadExactly(_scalar);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_scalar);
            if (value < 0 || value > maximum)
            {
                throw new InvalidDataException(
                    $"FED {label} {value} is unsafe.");
            }

            return value;
        }

        public float ReadSingle(string label)
        {
            ReadExactly(_scalar);
            int bits = BinaryPrimitives.ReadInt32LittleEndian(_scalar);
            float value = BitConverter.Int32BitsToSingle(bits);
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException(
                    $"FED {label} is not finite.");
            }

            return value;
        }

        public string ReadString(string label)
        {
            Span<byte> lengthBytes = stackalloc byte[sizeof(ushort)];
            ReadExactly(lengthBytes);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            if (length <= 0 || length > _limits.MaximumStringBytes)
            {
                throw new InvalidDataException(
                    $"FED {label} length {length} is unsafe.");
            }

            _stringBytes = checked(_stringBytes + length);
            if (_stringBytes > _limits.MaximumTotalStringBytes)
            {
                throw new InvalidDataException(
                    "FED total string bytes exceed the configured limit.");
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
            ReadExactly(bytes);
            if (bytes.AsSpan().Contains((byte)0))
            {
                throw new InvalidDataException(
                    $"FED {label} contains an embedded NUL.");
            }

            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"FED {label} is not valid UTF-8.",
                    exception);
            }
        }

        private void ReadExactly(Span<byte> buffer)
        {
            _bytesRead = checked(_bytesRead + buffer.Length);
            if (_bytesRead > _limits.MaximumFileBytes)
            {
                throw new InvalidDataException(
                    "FED payload exceeds the configured file limit.");
            }

            int total = 0;
            while (total < buffer.Length)
            {
                int read = _stream.Read(buffer[total..]);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "FED payload ended unexpectedly.");
                }

                total += read;
            }
        }
    }
}
