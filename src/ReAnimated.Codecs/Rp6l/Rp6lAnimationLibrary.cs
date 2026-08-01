using System.Buffers.Binary;
using System.Text;

namespace ReAnimated.Codecs.Rp6l;

public sealed record Rp6lAnimationScript(byte[] HeaderSection, byte[] BodySection);

public sealed record Rp6lAnimationLibrary(
    IReadOnlyDictionary<string, byte[]> Animations,
    IReadOnlyDictionary<string, Rp6lAnimationScript> AnimationScripts);

public enum Rp6lAppendConflictPolicy
{
    Fail,
    Replace,
}

/// <summary>
/// Deterministic writer for the DL1 animation-library RP6L shape used by
/// common_anims_sp_pc.rpack. Retail content is accepted only as caller-provided
/// bytes and is never bundled by this assembly.
/// </summary>
public static class Rp6lAnimationLibraryCodec
{
    private const short BuilderInformationType = Rp6lResourceTypes.BuilderInformation;
    private const string AnimationBuilderName = "_ANIMATION_";
    private const string ScriptBuilderName = "_ANIMATION_SCR_";

    public static byte[] Build(
        IReadOnlyDictionary<string, byte[]> animations,
        IReadOnlyDictionary<string, Rp6lAnimationScript> animationScripts)
    {
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(animationScripts);
        KeyValuePair<string, byte[]>[] animationRows = animations
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        KeyValuePair<string, Rp6lAnimationScript>[] scriptRows = animationScripts
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        ValidateNames(animationRows.Select(static pair => pair.Key), "animation");
        ValidateNames(scriptRows.Select(static pair => pair.Key), "animation script");
        if (animationRows.Select(static pair => pair.Key)
            .Intersect(
                scriptRows.Select(static pair => pair.Key),
                StringComparer.OrdinalIgnoreCase)
            .Any())
        {
            throw new ArgumentException(
                "Animation and animation-script resource names must not overlap.");
        }

        foreach ((string name, byte[] payload) in animationRows)
        {
            ArgumentNullException.ThrowIfNull(payload, name);
        }

        foreach ((string name, Rp6lAnimationScript script) in scriptRows)
        {
            ArgumentNullException.ThrowIfNull(script, name);
            ArgumentNullException.ThrowIfNull(script.HeaderSection, name);
            ArgumentNullException.ThrowIfNull(script.BodySection, name);
        }

        var names = new List<string>(
            2 + animationRows.Length + scriptRows.Length)
        {
            AnimationBuilderName,
            ScriptBuilderName,
        };
        names.AddRange(animationRows.Select(static pair => pair.Key));
        names.AddRange(scriptRows.Select(static pair => pair.Key));
        Dictionary<string, int> nameIndices = names
            .Select(static (name, index) => new KeyValuePair<string, int>(name, index))
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);

        byte[] animationBuilder = Pad16(
            Encoding.UTF8.GetBytes(
                string.Concat(animationRows.Select(static pair => $"+{pair.Key}\n"))));
        byte[] scriptBuilder = Pad16(
            Encoding.UTF8.GetBytes(
                string.Concat(scriptRows.Select(static pair => $"+{pair.Key}\n"))));
        var chunks = new List<WritableChunk>();
        var animationChunkIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string name, byte[] payload) in animationRows)
        {
            animationChunkIndices.Add(name, chunks.Count);
            chunks.Add(new WritableChunk(64, 2, payload, 1, 2));
        }

        var scriptChunkIndices =
            new Dictionary<string, (int Header, int Body)>(StringComparer.Ordinal);
        foreach ((string name, Rp6lAnimationScript script) in scriptRows)
        {
            int headerIndex = chunks.Count;
            chunks.Add(new WritableChunk(66, 2, script.HeaderSection, 1, 2));
            int bodyIndex = chunks.Count;
            chunks.Add(new WritableChunk(67, 2, script.BodySection, 1, 2));
            scriptChunkIndices.Add(name, (headerIndex, bodyIndex));
        }

        int builderChunkIndex = chunks.Count;
        byte[] builderPayload = new byte[animationBuilder.Length + scriptBuilder.Length];
        animationBuilder.CopyTo(builderPayload, 0);
        scriptBuilder.CopyTo(builderPayload, animationBuilder.Length);
        chunks.Add(new WritableChunk(255, 4, builderPayload, 1, 1));

        var items = new List<WritableItem>();
        items.Add(new WritableItem(
            builderChunkIndex,
            0,
            checked((short)nameIndices[AnimationBuilderName]),
            0,
            TrimmedLength(animationBuilder),
            0));
        items.Add(new WritableItem(
            builderChunkIndex,
            0,
            checked((short)nameIndices[ScriptBuilderName]),
            animationBuilder.Length,
            TrimmedLength(scriptBuilder),
            0));

        var resources = new List<WritableResource>
        {
            new(1, BuilderInformationType, nameIndices[AnimationBuilderName], 0),
            new(1, BuilderInformationType, nameIndices[ScriptBuilderName], 1),
        };
        foreach ((string name, byte[] payload) in animationRows)
        {
            int firstItem = items.Count;
            items.Add(new WritableItem(
                animationChunkIndices[name],
                0,
                checked((short)nameIndices[name]),
                0,
                payload.Length,
                0));
            resources.Add(new WritableResource(
                1,
                Rp6lResourceTypes.Animation,
                nameIndices[name],
                firstItem));
        }

        foreach ((string name, Rp6lAnimationScript script) in scriptRows)
        {
            int firstItem = items.Count;
            (int headerChunk, int bodyChunk) = scriptChunkIndices[name];
            items.Add(new WritableItem(
                headerChunk,
                0,
                checked((short)nameIndices[name]),
                0,
                script.HeaderSection.Length,
                0));
            items.Add(new WritableItem(
                bodyChunk,
                0,
                checked((short)nameIndices[name]),
                0,
                script.BodySection.Length,
                0));
            resources.Add(new WritableResource(
                2,
                Rp6lResourceTypes.AnimationScript,
                nameIndices[name],
                firstItem));
        }

        return BuildContainer(chunks, items, resources, names);
    }

    public static async Task<Rp6lAnimationLibrary> ExtractAsync(
        string path,
        Rp6lChunkCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(
            path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        bool ownsCache = cache is null;
        cache ??= new Rp6lChunkCache();
        try
        {
            var animations = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var scripts =
                new Dictionary<string, Rp6lAnimationScript>(StringComparer.OrdinalIgnoreCase);
            foreach (Rp6lResourceDescriptor resource in archive.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resource.ResourceType == Rp6lResourceTypes.Animation)
                {
                    if (resource.Items.Count != 1)
                    {
                        throw new InvalidDataException(
                            $"Animation '{resource.Name}' must contain exactly one item.");
                    }

                    animations.Add(
                        resource.Name,
                        await archive.ReadItemBytesAsync(
                            resource.Items[0],
                            cache,
                            cancellationToken: cancellationToken).ConfigureAwait(false));
                }
                else if (resource.ResourceType == Rp6lResourceTypes.AnimationScript)
                {
                    if (resource.Items.Count != 2)
                    {
                        throw new InvalidDataException(
                            $"Animation script '{resource.Name}' must contain exactly two items.");
                    }

                    byte[] header = await archive.ReadItemBytesAsync(
                        resource.Items[0],
                        cache,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    byte[] body = await archive.ReadItemBytesAsync(
                        resource.Items[1],
                        cache,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    scripts.Add(resource.Name, new Rp6lAnimationScript(header, body));
                }
            }

            return new Rp6lAnimationLibrary(animations, scripts);
        }
        finally
        {
            if (ownsCache)
            {
                await cache.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static async Task AppendAtomicAsync(
        string existingPath,
        string outputPath,
        IReadOnlyDictionary<string, byte[]> animations,
        IReadOnlyDictionary<string, Rp6lAnimationScript> animationScripts,
        Rp6lAppendConflictPolicy conflictPolicy = Rp6lAppendConflictPolicy.Fail,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Rp6lAnimationLibrary existing = await ExtractAsync(
            existingPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var mergedAnimations =
            new Dictionary<string, byte[]>(existing.Animations, StringComparer.OrdinalIgnoreCase);
        var mergedScripts = new Dictionary<string, Rp6lAnimationScript>(
            existing.AnimationScripts,
            StringComparer.OrdinalIgnoreCase);
        Merge(mergedAnimations, animations, conflictPolicy, "animation");
        Merge(mergedScripts, animationScripts, conflictPolicy, "animation script");
        byte[] output = Build(mergedAnimations, mergedScripts);
        await WriteAtomicAsync(outputPath, output, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Output path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] BuildContainer(
        IReadOnlyList<WritableChunk> chunks,
        IReadOnlyList<WritableItem> items,
        IReadOnlyList<WritableResource> resources,
        List<string> names)
    {
        var nameOffsets = new int[names.Count];
        using var nameBlob = new MemoryStream();
        for (var index = 0; index < names.Count; index++)
        {
            nameOffsets[index] = checked((int)nameBlob.Length);
            byte[] bytes = Encoding.UTF8.GetBytes(names[index]);
            nameBlob.Write(bytes);
            nameBlob.WriteByte(0);
        }

        byte[] namesBytes = nameBlob.ToArray();
        long tableLength = checked(
            36L +
            (20L * chunks.Count) +
            (16L * items.Count) +
            (12L * resources.Count) +
            (4L * names.Count) +
            namesBytes.Length);
        if (tableLength > uint.MaxValue)
        {
            throw new InvalidOperationException("RP6L tables exceed the 32-bit container limit.");
        }

        var chunkOffsets = new uint[chunks.Count];
        long cursor = tableLength;
        for (var index = 0; index < chunks.Count; index++)
        {
            chunkOffsets[index] = checked((uint)cursor);
            cursor = checked(cursor + chunks[index].Data.Length);
            if (cursor > uint.MaxValue)
            {
                throw new InvalidOperationException("RP6L output exceeds the 32-bit container limit.");
            }
        }

        using var output = new MemoryStream(checked((int)cursor));
        output.Write("RP6L"u8);
        WriteInt32(output, 1);
        WriteInt32(output, 0);
        WriteInt32(output, items.Count);
        WriteInt32(output, chunks.Count);
        WriteInt32(output, resources.Count);
        WriteInt32(output, namesBytes.Length);
        WriteInt32(output, names.Count);
        WriteInt32(output, 1);
        for (var index = 0; index < chunks.Count; index++)
        {
            WritableChunk chunk = chunks[index];
            WriteUInt16(output, chunk.Flags);
            WriteUInt16(output, chunk.Category);
            WriteUInt32(output, chunkOffsets[index]);
            WriteUInt32(output, checked((uint)chunk.Data.Length));
            WriteInt32(output, 0);
            WriteUInt16(output, chunk.Unknown1);
            WriteUInt16(output, chunk.Unknown2);
        }

        foreach (WritableItem item in items)
        {
            output.WriteByte(checked((byte)item.ChunkIndex));
            output.WriteByte(item.Flags);
            WriteInt16(output, item.LogicalType);
            WriteUInt32(output, checked((uint)item.Offset));
            WriteInt32(output, item.Size);
            WriteInt32(output, item.Unknown);
        }

        foreach (WritableResource resource in resources)
        {
            WriteInt16(output, resource.ItemCount);
            WriteInt16(output, resource.ResourceType);
            WriteInt32(output, resource.NameIndex);
            WriteInt32(output, resource.FirstItemIndex);
        }

        foreach (int offset in nameOffsets)
        {
            WriteInt32(output, offset);
        }

        output.Write(namesBytes);
        foreach (WritableChunk chunk in chunks)
        {
            output.Write(chunk.Data);
        }

        return output.ToArray();
    }

    private static void Merge<T>(
        Dictionary<string, T> destination,
        IReadOnlyDictionary<string, T> additions,
        Rp6lAppendConflictPolicy policy,
        string label)
    {
        ArgumentNullException.ThrowIfNull(additions);
        foreach ((string name, T value) in additions)
        {
            if (!destination.TryAdd(name, value))
            {
                if (policy == Rp6lAppendConflictPolicy.Fail)
                {
                    throw new InvalidOperationException(
                        $"The existing RP6L already contains {label} '{name}'.");
                }

                destination[name] = value;
            }
        }
    }

    private static void ValidateNames(IEnumerable<string> names, string label)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Contains('\0') ||
                name.Contains('\r') ||
                name.Contains('\n') ||
                name is AnimationBuilderName or ScriptBuilderName ||
                !unique.Add(name))
            {
                throw new ArgumentException($"Invalid or duplicate {label} name '{name}'.");
            }
        }
    }

    private static byte[] Pad16(byte[] source)
    {
        int length = checked((source.Length + 15) & ~15);
        if (length == source.Length)
        {
            return source;
        }

        var output = new byte[length];
        source.CopyTo(output, 0);
        return output;
    }

    private static int TrimmedLength(byte[] source)
    {
        int length = source.Length;
        while (length > 0 && source[length - 1] == 0)
        {
            length--;
        }

        return length;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record WritableChunk(
        ushort Flags,
        ushort Category,
        byte[] Data,
        ushort Unknown1,
        ushort Unknown2);

    private readonly record struct WritableItem(
        int ChunkIndex,
        byte Flags,
        short LogicalType,
        int Offset,
        int Size,
        int Unknown);

    private readonly record struct WritableResource(
        short ItemCount,
        short ResourceType,
        int NameIndex,
        int FirstItemIndex);
}
