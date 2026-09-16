using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Deterministic bounded binary codec for source-linked authored edits.</summary>
public static class AuthoredModelLayerCodec
{
    public const int MaximumPayloadBytes = 256 * 1024 * 1024;
    private const int HeaderBytes = 8;
    private static ReadOnlySpan<byte> Signature => "AMLY"u8;

    public static ImmutableArray<byte> Serialize(AuthoredModelLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        layer.Validate();
        using var stream = new MemoryStream();
        stream.Write(Signature);
        WriteInt32(stream, AuthoredModelLayer.CurrentVersion);
        WriteString(stream, layer.SourceSha256);
        WriteString(stream, layer.SourceGeometryFingerprint);
        WriteString(stream, layer.TargetRigSignature);

        AuthoredBoneIdentity[] bones = layer.Bones.OrderBy(static bone => bone.Id).ToArray();
        WriteCount(stream, bones.Length);
        foreach (AuthoredBoneIdentity bone in bones)
        {
            WriteGuid(stream, bone.Id);
            WriteString(stream, bone.Name);
        }

        AuthoredComponentEdits[] components = layer.Components
            .OrderBy(static component => component.ComponentId, StringComparer.Ordinal)
            .ToArray();
        WriteCount(stream, components.Length);
        foreach (AuthoredComponentEdits component in components)
        {
            WriteString(stream, component.ComponentId);
            WriteInt32(stream, component.ControlPointCount);
            WriteInt32(stream, component.PolygonVertexCount);
            AuthoredInverseBind[] inverseBinds = component.InverseBinds
                .OrderBy(static inverseBind => inverseBind.BoneId)
                .ToArray();
            WriteCount(stream, inverseBinds.Length);
            foreach (AuthoredInverseBind inverseBind in inverseBinds)
            {
                WriteGuid(stream, inverseBind.BoneId);
                WriteMatrix(stream, inverseBind.Matrix);
            }

            AuthoredPointEdit[] points = component.Points.OrderBy(static point => point.ControlPointIndex).ToArray();
            WriteCount(stream, points.Length);
            foreach (AuthoredPointEdit point in points)
            {
                WriteInt32(stream, point.ControlPointIndex);
                WriteVector(stream, point.PositionDelta);
                stream.WriteByte(point.ReplaceWeights ? (byte)1 : (byte)0);
                AuthoredSkinInfluence[] weights = point.Weights.OrderBy(static weight => weight.BoneId).ToArray();
                WriteCount(stream, weights.Length);
                foreach (AuthoredSkinInfluence weight in weights)
                {
                    WriteGuid(stream, weight.BoneId);
                    WriteDouble(stream, weight.Weight);
                }
            }

            AuthoredVectorDelta[] normals = component.Normals.OrderBy(static delta => delta.Index).ToArray();
            WriteCount(stream, normals.Length);
            foreach (AuthoredVectorDelta normal in normals)
            {
                WriteInt32(stream, normal.Index);
                WriteVector(stream, normal.Delta);
            }

            AuthoredMorphEdits[] morphs = component.Morphs.OrderBy(static morph => morph.DescriptorHash).ToArray();
            WriteCount(stream, morphs.Length);
            foreach (AuthoredMorphEdits morph in morphs)
            {
                WriteUInt32(stream, morph.DescriptorHash);
                stream.WriteByte(morph.HasNormalDeltas switch
                {
                    null => (byte)0,
                    false => (byte)1,
                    true => (byte)2,
                });
                WriteDeltas(stream, morph.PositionDeltas);
                WriteDeltas(stream, morph.NormalDeltas);
            }
        }

        if (stream.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Authored model layer exceeds the bounded payload size.");
        }

        return ImmutableArray.Create(stream.ToArray());
    }

    public static AuthoredModelLayer Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes || payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Authored model layer payload size is outside the supported bounds.");
        }

        var reader = new Reader(payload);
        if (!reader.ReadBytes(Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Authored model layer signature is invalid.");
        }

        int version = reader.ReadInt32();
        if (version != AuthoredModelLayer.CurrentVersion)
        {
            throw new InvalidDataException($"Authored model layer version {version} is unsupported.");
        }

        string sourceSha256 = reader.ReadString();
        string sourceGeometryFingerprint = reader.ReadString();
        string targetRigSignature = reader.ReadString();
        int boneCount = reader.ReadCount(AuthoredModelLayer.MaximumBones, 20, "bone");
        var bones = ImmutableArray.CreateBuilder<AuthoredBoneIdentity>(boneCount);
        for (int index = 0; index < boneCount; index++)
        {
            bones.Add(new AuthoredBoneIdentity(reader.ReadGuid(), reader.ReadString()));
        }

        int componentCount = reader.ReadCount(AuthoredModelLayer.MaximumComponents, 28, "component");
        var components = ImmutableArray.CreateBuilder<AuthoredComponentEdits>(componentCount);
        long totalEdits = 0;
        for (int index = 0; index < componentCount; index++)
        {
            components.Add(ReadComponent(ref reader, ref totalEdits));
        }

        if (!reader.IsAtEnd)
        {
            throw new InvalidDataException("Authored model layer contains trailing bytes.");
        }

        var layer = new AuthoredModelLayer
        {
            Version = version,
            SourceSha256 = sourceSha256,
            SourceGeometryFingerprint = sourceGeometryFingerprint,
            TargetRigSignature = targetRigSignature,
            Bones = bones.MoveToImmutable(),
            Components = components.MoveToImmutable(),
        };
        try
        {
            layer.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Authored model layer payload contains invalid contract data.", exception);
        }

        return layer;
    }

    private static AuthoredComponentEdits ReadComponent(ref Reader reader, ref long totalEdits)
    {
        string componentId = reader.ReadString();
        int controlPointCount = reader.ReadInt32();
        int polygonVertexCount = reader.ReadInt32();
        if (controlPointCount < 0 || controlPointCount > AuthoredModelLayer.MaximumSourceControlPoints ||
            polygonVertexCount < 0 || polygonVertexCount > AuthoredModelLayer.MaximumPolygonVertices)
        {
            throw new InvalidDataException("Authored component source counts are outside the supported bounds.");
        }

        int inverseBindCount = reader.ReadCount(AuthoredModelLayer.MaximumBones, 144, "inverse bind");
        var inverseBinds = ImmutableArray.CreateBuilder<AuthoredInverseBind>(inverseBindCount);
        for (int index = 0; index < inverseBindCount; index++)
        {
            inverseBinds.Add(new AuthoredInverseBind(reader.ReadGuid(), reader.ReadMatrix()));
        }

        int pointCount = reader.ReadCount(controlPointCount, 33, "point edit");
        EnsureEdits(ref totalEdits, pointCount);
        var points = ImmutableArray.CreateBuilder<AuthoredPointEdit>(pointCount);
        for (int index = 0; index < pointCount; index++)
        {
            int controlPointIndex = reader.ReadInt32();
            Vector3D delta = reader.ReadVector();
            bool replace = reader.ReadBoolean();
            int weightCount = reader.ReadCount(256, 24, "weight");
            EnsureEdits(ref totalEdits, weightCount);
            var weights = ImmutableArray.CreateBuilder<AuthoredSkinInfluence>(weightCount);
            for (int weightIndex = 0; weightIndex < weightCount; weightIndex++)
            {
                weights.Add(new AuthoredSkinInfluence(reader.ReadGuid(), reader.ReadDouble()));
            }

            points.Add(new AuthoredPointEdit
            {
                ControlPointIndex = controlPointIndex,
                PositionDelta = delta,
                ReplaceWeights = replace,
                Weights = weights.MoveToImmutable(),
            });
        }

        int normalCount = reader.ReadCount(polygonVertexCount, 28, "normal edit");
        EnsureEdits(ref totalEdits, normalCount);
        var normals = ImmutableArray.CreateBuilder<AuthoredVectorDelta>(normalCount);
        for (int index = 0; index < normalCount; index++)
        {
            normals.Add(new AuthoredVectorDelta(reader.ReadInt32(), reader.ReadVector()));
        }

        int morphCount = reader.ReadCount(AuthoredModelLayer.MaximumMorphsPerComponent, 13, "morph edit");
        var morphs = ImmutableArray.CreateBuilder<AuthoredMorphEdits>(morphCount);
        for (int index = 0; index < morphCount; index++)
        {
            uint descriptor = reader.ReadUInt32();
            bool? hasNormalDeltas = reader.ReadTriStateBoolean();
            int positionCount = reader.ReadCount(controlPointCount, 28, "morph position delta");
            EnsureEdits(ref totalEdits, positionCount);
            var positions = ReadDeltas(ref reader, positionCount);
            int normalDeltaCount = reader.ReadCount(polygonVertexCount, 28, "morph normal delta");
            EnsureEdits(ref totalEdits, normalDeltaCount);
            var normalDeltas = ReadDeltas(ref reader, normalDeltaCount);
            morphs.Add(new AuthoredMorphEdits
            {
                DescriptorHash = descriptor,
                PositionDeltas = positions,
                NormalDeltas = normalDeltas,
                HasNormalDeltas = hasNormalDeltas,
            });
        }

        return new AuthoredComponentEdits
        {
            ComponentId = componentId,
            ControlPointCount = controlPointCount,
            PolygonVertexCount = polygonVertexCount,
            Points = points.MoveToImmutable(),
            Normals = normals.MoveToImmutable(),
            Morphs = morphs.MoveToImmutable(),
            InverseBinds = inverseBinds.MoveToImmutable(),
        };
    }

    private static ImmutableArray<AuthoredVectorDelta> ReadDeltas(ref Reader reader, int count)
    {
        var result = ImmutableArray.CreateBuilder<AuthoredVectorDelta>(count);
        for (int index = 0; index < count; index++)
        {
            result.Add(new AuthoredVectorDelta(reader.ReadInt32(), reader.ReadVector()));
        }

        return result.MoveToImmutable();
    }

    private static void EnsureEdits(ref long totalEdits, int count)
    {
        totalEdits = checked(totalEdits + count);
        if (totalEdits > AuthoredModelLayer.MaximumLayerEdits)
        {
            throw new InvalidDataException("Authored model layer edit count exceeds the bounded limit.");
        }
    }

    private static void WriteDeltas(Stream stream, ImmutableArray<AuthoredVectorDelta> deltas)
    {
        AuthoredVectorDelta[] ordered = deltas.OrderBy(static delta => delta.Index).ToArray();
        WriteCount(stream, ordered.Length);
        foreach (AuthoredVectorDelta delta in ordered)
        {
            WriteInt32(stream, delta.Index);
            WriteVector(stream, delta.Delta);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteGuid(Stream stream, Guid value) => stream.Write(value.ToByteArray());

    private static void WriteVector(Stream stream, Vector3D value)
    {
        WriteDouble(stream, value.X);
        WriteDouble(stream, value.Y);
        WriteDouble(stream, value.Z);
    }

    private static void WriteMatrix(Stream stream, TransformMatrix value)
    {
        WriteDouble(stream, value.M11);
        WriteDouble(stream, value.M12);
        WriteDouble(stream, value.M13);
        WriteDouble(stream, value.M14);
        WriteDouble(stream, value.M21);
        WriteDouble(stream, value.M22);
        WriteDouble(stream, value.M23);
        WriteDouble(stream, value.M24);
        WriteDouble(stream, value.M31);
        WriteDouble(stream, value.M32);
        WriteDouble(stream, value.M33);
        WriteDouble(stream, value.M34);
        WriteDouble(stream, value.M41);
        WriteDouble(stream, value.M42);
        WriteDouble(stream, value.M43);
        WriteDouble(stream, value.M44);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteCount(Stream stream, int value) => WriteInt32(stream, value);

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public bool IsAtEnd => _offset == _data.Length;

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            Ensure(count);
            ReadOnlySpan<byte> result = _data.Slice(_offset, count);
            _offset += count;
            return result;
        }

        public int ReadInt32()
        {
            ReadOnlySpan<byte> bytes = ReadBytes(4);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        public uint ReadUInt32()
        {
            ReadOnlySpan<byte> bytes = ReadBytes(4);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public double ReadDouble()
        {
            ReadOnlySpan<byte> bytes = ReadBytes(8);
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
        }

        public Guid ReadGuid() => new(ReadBytes(16));

    public Vector3D ReadVector() => new(ReadDouble(), ReadDouble(), ReadDouble());

        public TransformMatrix ReadMatrix() => new(
            ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble(),
            ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble(),
            ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble(),
            ReadDouble(), ReadDouble(), ReadDouble(), ReadDouble());

        public bool ReadBoolean()
        {
            byte value = ReadBytes(1)[0];
            return value switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException("Authored model layer boolean value is invalid."),
            };
        }

        public bool? ReadTriStateBoolean()
        {
            byte value = ReadBytes(1)[0];
            return value switch
            {
                0 => null,
                1 => false,
                2 => true,
                _ => throw new InvalidDataException("Authored model layer tri-state value is invalid."),
            };
        }

        public string ReadString()
        {
            int length = ReadInt32();
            if (length < 0 || length > AuthoredModelLayer.MaximumStringBytes)
            {
                throw new InvalidDataException("Authored model layer string length is outside the supported bound.");
            }

            ReadOnlySpan<byte> bytes = ReadBytes(length);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Authored model layer contains invalid UTF-8 text.", exception);
            }
        }

        public int ReadCount(int maximum, int minimumBytesPerItem, string description)
        {
            int count = ReadInt32();
            if (count < 0 || count > maximum ||
                (long)count * minimumBytesPerItem > _data.Length - _offset)
            {
                throw new InvalidDataException($"Authored model layer {description} count is invalid or truncated.");
            }

            return count;
        }

        private void Ensure(int count)
        {
            if (count < 0 || count > _data.Length - _offset)
            {
                throw new InvalidDataException("Authored model layer payload is truncated.");
            }
        }
    }
}
