using System.Buffers.Binary;
using System.Text;

namespace ReAnimated.Codecs.CompactMesh;

public static class CompactMeshDecoder
{
    private const int MinimumHeaderSize = 0xB0;
    private const int EntityTablePointerOffset = 0x08;
    private const int EntityCountOffset = 0x64;
    private const int RootCountOffset = 0x68;
    private const int EntityStride = 0xD0;
    private const int LocalMatrixOffset = 0x00;
    private const int ReferenceMatrixOffset = 0x30;
    private const int BoundsOffset = 0x60;
    private const int NamePointerOffset = 0x78;
    private const int LodTablePointerOffset = 0x80;
    private const int MeshLinkPointerOffset = 0x88;
    private const int BoneIndexPointer0Offset = 0x90;
    private const int BoneIndexPointer1Offset = 0x98;
    private const int FlagsOffset = 0xC0;
    private const int ParentIndexOffset = 0xC6;
    private const int EntityTypeOffset = 0xC8;
    private const int ChildCountOffset = 0xC9;
    private const int LodCountOffset = 0xCA;
    private const int MaximumEntities = 1_000_000;
    private const int MaximumNameBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CompactMeshDocument Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < MinimumHeaderSize)
        {
            throw new InvalidDataException(
                $"Compact mesh payload is only 0x{payload.Length:X} bytes.");
        }

        ReadOnlySpan<byte> data = payload.Span;
        int entityCount = checked(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(
                data[EntityCountOffset..]));
        int rootCount = checked(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(
                data[RootCountOffset..]));
        if (entityCount > MaximumEntities)
        {
            throw new InvalidDataException(
                $"Compact mesh entity count {entityCount:N0} is unsafe.");
        }

        int tableOffset = DecodeFixupPointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                data[EntityTablePointerOffset..]),
            data.Length,
            "entity table");
        long tableEnd = checked(
            (long)tableOffset + (long)entityCount * EntityStride);
        if (tableEnd > data.Length)
        {
            throw new InvalidDataException(
                $"Compact entity table ends at 0x{tableEnd:X}, beyond payload 0x{data.Length:X}.");
        }

        List<CompactMeshEntity> entities = new(entityCount);
        List<CompactMeshDiagnostic> diagnostics = [];
        for (int index = 0; index < entityCount; index++)
        {
            int rowOffset = checked(tableOffset + index * EntityStride);
            ReadOnlySpan<byte> row = data.Slice(rowOffset, EntityStride);
            int nameOffset = DecodeFixupPointer(
                BinaryPrimitives.ReadUInt64LittleEndian(
                    row[NamePointerOffset..]),
                data.Length,
                $"entity {index} name");
            string name = ReadNullTerminatedUtf8(
                data,
                nameOffset,
                $"entity {index} name");
            CompactMatrix3x4 local = ReadMatrix(row[LocalMatrixOffset..]);
            CompactMatrix3x4 reference =
                ReadMatrix(row[ReferenceMatrixOffset..]);
            CompactBounds bounds = new(
                ReadSingle(row, BoundsOffset),
                ReadSingle(row, BoundsOffset + 4),
                ReadSingle(row, BoundsOffset + 8),
                ReadSingle(row, BoundsOffset + 12),
                ReadSingle(row, BoundsOffset + 16),
                ReadSingle(row, BoundsOffset + 20));
            short parent =
                BinaryPrimitives.ReadInt16LittleEndian(row[ParentIndexOffset..]);
            CompactMeshEntityType type =
                (CompactMeshEntityType)row[EntityTypeOffset];

            if (!local.IsFinite)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH003",
                    CompactMeshDiagnosticSeverity.Error,
                    $"Entity '{name}' contains a non-finite local matrix.",
                    index));
            }

            if (!reference.IsFinite)
            {
                if (parent < 0 &&
                    type == CompactMeshEntityType.Mesh)
                {
                    diagnostics.Add(new CompactMeshDiagnostic(
                        "CMESH011",
                        CompactMeshDiagnosticSeverity.Warning,
                        $"Plain static root entity '{name}' contains a non-finite secondary reference matrix. Retail static world transforms use the finite local matrix; the opaque serialized reference values are retained.",
                        index));
                }
                else
                {
                    diagnostics.Add(new CompactMeshDiagnostic(
                        "CMESH003",
                        CompactMeshDiagnosticSeverity.Error,
                        $"Entity '{name}' contains a non-finite reference matrix required by its animation-bearing or non-root layout.",
                        index));
                }
            }

            if (!bounds.IsFinite)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH004",
                    CompactMeshDiagnosticSeverity.Warning,
                    $"Entity '{name}' contains non-finite bounds.",
                    index));
            }

            entities.Add(new CompactMeshEntity(
                index,
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(row[FlagsOffset..]),
                bounds,
                parent,
                type,
                row[ChildCountOffset],
                row[LodCountOffset],
                local,
                reference,
                BinaryPrimitives.ReadUInt64LittleEndian(
                    row[LodTablePointerOffset..]),
                BinaryPrimitives.ReadInt32LittleEndian(
                    row[MeshLinkPointerOffset..]))
            {
                RawBoneIndexPointer0 =
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        row[BoneIndexPointer0Offset..]),
                RawBoneIndexPointer1 =
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        row[BoneIndexPointer1Offset..]),
            });
        }

        AddHierarchyDiagnostics(entities, rootCount, diagnostics);
        return new CompactMeshDocument(
            entityCount,
            rootCount,
            tableOffset,
            entities,
            diagnostics);
    }

    private static void AddHierarchyDiagnostics(
        List<CompactMeshEntity> entities,
        int declaredRootCount,
        List<CompactMeshDiagnostic> diagnostics)
    {
        int observedRoots = entities.Count(static entity =>
            entity.ParentIndex < 0);
        if (declaredRootCount > entities.Count)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESH001",
                CompactMeshDiagnosticSeverity.Error,
                $"Declared root count {declaredRootCount} exceeds entity count {entities.Count}."));
        }
        else if (declaredRootCount != observedRoots)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESH002",
                CompactMeshDiagnosticSeverity.Information,
                $"Header root count is {declaredRootCount}; {observedRoots} entities have no parent."));
        }

        int[] actualChildCounts = new int[entities.Count];
        bool[] invalidParent = new bool[entities.Count];
        foreach (CompactMeshEntity entity in entities)
        {
            if (entity.ParentIndex == entity.Index)
            {
                invalidParent[entity.Index] = true;
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH005",
                    CompactMeshDiagnosticSeverity.Error,
                    $"Entity '{entity.Name}' is its own parent.",
                    entity.Index));
            }
            else if (entity.ParentIndex >= entities.Count)
            {
                invalidParent[entity.Index] = true;
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH006",
                    CompactMeshDiagnosticSeverity.Error,
                    $"Entity '{entity.Name}' references parent {entity.ParentIndex} outside the table.",
                    entity.Index));
            }
            else if (entity.ParentIndex >= 0)
            {
                actualChildCounts[entity.ParentIndex]++;
            }

            byte unknownBits = (byte)(
                (byte)entity.EntityType &
                ~(byte)(
                    CompactMeshEntityType.Mesh |
                    CompactMeshEntityType.SkinnedMesh |
                    CompactMeshEntityType.Helper |
                    CompactMeshEntityType.Bone |
                    CompactMeshEntityType.Hull));
            if (unknownBits != 0)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH007",
                    CompactMeshDiagnosticSeverity.Warning,
                    $"Entity '{entity.Name}' has unknown type bits 0x{unknownBits:X2}.",
                    entity.Index));
            }
        }

        foreach (CompactMeshEntity entity in entities)
        {
            if (entity.ChildCount != actualChildCounts[entity.Index])
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH008",
                    CompactMeshDiagnosticSeverity.Warning,
                    $"Entity '{entity.Name}' declares {entity.ChildCount} children but {actualChildCounts[entity.Index]} reference it.",
                    entity.Index));
            }
        }

        Dictionary<string, int> firstByName =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (CompactMeshEntity entity in entities)
        {
            if (!firstByName.TryAdd(entity.Name, entity.Index))
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESH009",
                    CompactMeshDiagnosticSeverity.Warning,
                    $"Entity name '{entity.Name}' is duplicated.",
                    entity.Index));
            }
        }

        byte[] visit = new byte[entities.Count];
        for (int index = 0; index < entities.Count; index++)
        {
            if (!invalidParent[index])
            {
                Visit(index, entities, invalidParent, visit, diagnostics);
            }
        }
    }

    private static void Visit(
        int index,
        IReadOnlyList<CompactMeshEntity> entities,
        IReadOnlyList<bool> invalidParent,
        IList<byte> visit,
        List<CompactMeshDiagnostic> diagnostics)
    {
        if (visit[index] == 2)
        {
            return;
        }

        if (visit[index] == 1)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESH010",
                CompactMeshDiagnosticSeverity.Error,
                $"Compact hierarchy contains a cycle at entity '{entities[index].Name}'.",
                index));
            return;
        }

        visit[index] = 1;
        short parent = entities[index].ParentIndex;
        if (parent >= 0 && !invalidParent[index])
        {
            Visit(parent, entities, invalidParent, visit, diagnostics);
        }

        visit[index] = 2;
    }

    private static int DecodeFixupPointer(
        ulong value,
        int payloadSize,
        string label)
    {
        if (value == 0 || value - 1 >= (ulong)payloadSize)
        {
            throw new InvalidDataException(
                $"Compact mesh {label} pointer 0x{value:X} is invalid.");
        }

        return checked((int)(value - 1));
    }

    private static string ReadNullTerminatedUtf8(
        ReadOnlySpan<byte> data,
        int offset,
        string label)
    {
        int available = Math.Min(MaximumNameBytes, data.Length - offset);
        int terminator = data.Slice(offset, available).IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException(
                $"Compact mesh {label} is not NUL terminated.");
        }

        try
        {
            return StrictUtf8.GetString(data.Slice(offset, terminator));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Compact mesh {label} is not valid UTF-8.",
                exception);
        }
    }

    // Chrome's compact mtx34 is serialized as three affine rows and the
    // runtime composes it as global = parent * local. Preserve that order in
    // CompactMatrix3x4; transposition for System.Numerics' row-vector
    // convention belongs only at the renderer adapter boundary.
    private static CompactMatrix3x4 ReadMatrix(ReadOnlySpan<byte> data) =>
        new(
            ReadSingle(data, 0),
            ReadSingle(data, 4),
            ReadSingle(data, 8),
            ReadSingle(data, 12),
            ReadSingle(data, 16),
            ReadSingle(data, 20),
            ReadSingle(data, 24),
            ReadSingle(data, 28),
            ReadSingle(data, 32),
            ReadSingle(data, 36),
            ReadSingle(data, 40),
            ReadSingle(data, 44));

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
}
