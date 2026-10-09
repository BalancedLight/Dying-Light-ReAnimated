using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public sealed class CustomModelFormatException : FormatException
{
    public CustomModelFormatException(string message)
        : base(message)
    {
    }

    public CustomModelFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads and atomically writes deterministic .dlrmodel containers. Entry
/// timestamps and ordering are fixed so the same document and payloads produce
/// the same package bytes.
/// </summary>
public static class CustomModelPackageSerializer
{
    public const long MaximumPackageBytes = 1024L * 1024L * 1024L;

    public const long MaximumSourceFbxBytes = 768L * 1024L * 1024L;

    public const long MaximumTextureBytes = 256L * 1024L * 1024L;

    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static CustomModelPackage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumPackageBytes)
            {
                throw new CustomModelFormatException(
                    $"Custom-model packages cannot exceed {MaximumPackageBytes:N0} bytes.");
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            Dictionary<string, ZipArchiveEntry> entries = BuildEntryMap(archive);
            ZipArchiveEntry manifestEntry = GetRequiredEntry(entries, CustomModelPackage.ManifestEntryPath);
            if (manifestEntry.Length > 8L * 1024L * 1024L)
            {
                throw new CustomModelFormatException("The custom-model manifest is unreasonably large.");
            }

            CustomModelDocument document;
            using (Stream manifestStream = manifestEntry.Open())
            {
                document = JsonSerializer.Deserialize<CustomModelDocument>(
                    manifestStream,
                    SerializerOptions) ??
                    throw new CustomModelFormatException("The custom-model manifest was empty.");
            }

            document = MigrateToCurrent(document);
            document.Validate();
            ZipArchiveEntry sourceEntry = GetRequiredEntry(entries, document.Source.EmbeddedEntryPath);
            ImmutableArray<byte> sourceFbx = ReadBoundedEntry(sourceEntry, MaximumSourceFbxBytes);
            VerifySha256(sourceFbx.AsSpan(), document.Source.ContentSha256, document.Source.EmbeddedEntryPath);

            var texturePayloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(
                StringComparer.Ordinal);
            foreach (CustomModelTextureBinding texture in document.Materials
                         .SelectMany(static material => material.Textures)
                         .Where(static texture => texture.PackageEntryPath is not null)
                         .OrderBy(static texture => texture.PackageEntryPath, StringComparer.Ordinal))
            {
                string entryPath = texture.PackageEntryPath!;
                if (texturePayloads.ContainsKey(entryPath))
                {
                    continue;
                }

                ZipArchiveEntry textureEntry = GetRequiredEntry(entries, entryPath);
                ImmutableArray<byte> payload = ReadBoundedEntry(textureEntry, MaximumTextureBytes);
                VerifySha256(payload.AsSpan(), texture.ContentSha256, entryPath);
                texturePayloads.Add(entryPath, payload);
            }

            ImmutableArray<byte> revisionPayload = [];
            if (document.GeometryRevision is { } revision)
                revisionPayload = ReadBoundedEntry(GetRequiredEntry(entries, ModelGeometryRevisionReference.PayloadEntryPath), MaximumSourceFbxBytes);
            ImmutableArray<byte> authoredPayload = [];
            if (document.AuthoredLayer is { } authored)
            {
                ZipArchiveEntry entry = GetRequiredEntry(entries, authored.EntryPath);
                if (entry.Length != authored.PayloadLength) throw new CustomModelFormatException("The authored layer length differs from its manifest reference.");
                authoredPayload = ReadBoundedEntry(entry, AuthoredModelLayerCodec.MaximumPayloadBytes);
            }
            var derivedPayloads = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<byte>>();
            var derivedClipIds = document.AnimationClips.Where(static clip => clip.DerivedMotion is not null)
                .Select(static clip => clip.Id).ToHashSet();
            foreach (CustomModelAnimationClip clip in document.AnimationClips.Where(static clip => clip.DerivedMotion is not null).OrderBy(static clip => clip.Id))
            {
                DerivedMotionReference reference = clip.DerivedMotion!;
                string entryPath = DerivedAnimationDataCodec.EntryPath(clip.Id);
                ZipArchiveEntry entry = GetRequiredEntry(entries, entryPath);
                if (entry.Length != reference.PayloadLength)
                    throw new CustomModelFormatException($"The derived animation length differs from its manifest reference for clip '{clip.Id}'.");
                ImmutableArray<byte> payload = ReadBoundedEntry(entry, DerivedAnimationDataCodec.MaximumPayloadBytes);
                VerifySha256(payload.AsSpan(), reference.PayloadSha256, entryPath);
                DerivedAnimationData data = DerivedAnimationDataCodec.Deserialize(payload.AsSpan());
                if (data.ClipId != clip.Id)
                    throw new CustomModelFormatException($"The derived animation payload '{entryPath}' identifies another clip.");
                derivedPayloads.Add(clip.Id, payload);
            }
            foreach (string entryPath in entries.Keys.Where(static path => path.StartsWith("animation/derived/", StringComparison.Ordinal)))
            {
                string fileName = entryPath["animation/derived/".Length..];
                if (!fileName.EndsWith(".json", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(fileName[..^5], "N", out Guid clipId) || !derivedClipIds.Contains(clipId))
                    throw new CustomModelFormatException($"The package contains an orphan or malformed derived animation entry '{entryPath}'.");
            }
            var authoredAnimations = ImmutableDictionary.CreateBuilder<Guid, ImmutableArray<byte>>();
            foreach (CustomModelAnimationClip clip in document.AnimationClips.Where(static clip => clip.AuthoredAnimation is not null))
            {
                AuthoredAnimationReference reference = clip.AuthoredAnimation!;
                string authoredEntryPath = AuthoredAnimationReference.EntryPath(clip.Id);
                ZipArchiveEntry entry = GetRequiredEntry(entries, authoredEntryPath);
                if (entry.Length != reference.PayloadLength)
                    throw new CustomModelFormatException("An authored animation length differs from its manifest.");
                ImmutableArray<byte> payload = ReadBoundedEntry(entry, DerivedAnimationDataCodec.MaximumPayloadBytes);
                VerifySha256(payload.AsSpan(), reference.PayloadSha256, authoredEntryPath);
                authoredAnimations.Add(clip.Id, payload);
            }
            foreach (string authoredEntryPath in entries.Keys.Where(static entryPath => entryPath.StartsWith("animation/authored/", StringComparison.Ordinal)))
            {
                string fileName = authoredEntryPath["animation/authored/".Length..];
                if (!fileName.EndsWith(".json", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(fileName[..^5], "N", out Guid id) || !authoredAnimations.ContainsKey(id))
                    throw new CustomModelFormatException("The package contains an orphan authored animation.");
            }
            var companionPayloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
            ImmutableArray<byte> decodedCharacter = [];
            if (document.CharacterResources is { } character)
            {
                decodedCharacter = ReadBoundedEntry(GetRequiredEntry(entries, CharacterResourceInventory.DecodedEntryPath), MaximumSourceFbxBytes);
                VerifySha256(decodedCharacter.AsSpan(), character.DecodedSha256, CharacterResourceInventory.DecodedEntryPath);
                foreach (var resource in character.Resources.Where(r => r.EntryPath is not null))
                    companionPayloads.Add(resource.EntryPath!, ReadBoundedEntry(GetRequiredEntry(entries, resource.EntryPath!), MaximumSourceFbxBytes));
            }
            var package = new CustomModelPackage(document, sourceFbx, texturePayloads.ToImmutable())
            {
                AuthoredLayerPayload = authoredPayload,
                DecodedCharacterPayload = decodedCharacter,
                GeometryRevisionPayload = revisionPayload,
                CompanionPayloads = companionPayloads.ToImmutable(),
                DerivedAnimationPayloads = derivedPayloads.ToImmutable(),
                AuthoredAnimationPayloads = authoredAnimations.ToImmutable(),
            };
            ValidatePayloads(package);
            return package;
        }
        catch (CustomModelFormatException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new CustomModelFormatException("The .dlrmodel ZIP container is invalid.", exception);
        }
        catch (JsonException exception)
        {
            throw new CustomModelFormatException("The custom-model manifest contains invalid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new CustomModelFormatException("The custom-model manifest contains invalid domain state.", exception);
        }
    }

    public static string SaveAtomic(CustomModelPackage package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ImmutableArray<byte> serialized = Serialize(package);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The model-package path must have a parent directory.", nameof(path));
        }

        EnsureExistingTargetCanBeReplaced(fullPath);
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(serialized.AsSpan());
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Materializes the deterministic package bytes used both by SaveAtomic
    /// and by project-source handoff. Keeping this path shared guarantees that
    /// embedded and user-selected textures, stack selections, cadence, and
    /// root policies survive a Models-to-Animate transition byte-for-byte.
    /// </summary>
    public static ImmutableArray<byte> Serialize(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Document.Validate();
        ValidatePayloads(package);
        byte[] manifestBytes=JsonSerializer.SerializeToUtf8Bytes(package.Document,SerializerOptions);
        if(manifestBytes.Length>8L*1024*1024) throw new CustomModelFormatException("The custom-model manifest is unreasonably large.");
        var paths=new HashSet<string>(StringComparer.Ordinal);
        long expandedBytes=0;
        void Reserve(string path,long length)
        {
            CustomModelSourceIdentity.ValidatePackageEntryPath(path,nameof(package));
            if(!paths.Add(path) || paths.Count>65536) throw new CustomModelFormatException("Package payload identities overlap or exceed the entry bound.");
            if(length<0 || length>MaximumPackageBytes-expandedBytes) throw new CustomModelFormatException("The expanded package exceeds its bounded payload limit.");
            expandedBytes+=length;
        }
        Reserve(CustomModelPackage.ManifestEntryPath,manifestBytes.Length);
        Reserve(package.Document.Source.EmbeddedEntryPath,package.SourceFbx.Length);
        if(package.Document.GeometryRevision is not null)Reserve(ModelGeometryRevisionReference.PayloadEntryPath,package.GeometryRevisionPayload.Length);
        if(package.Document.CharacterResources is not null)
        {
            Reserve(CharacterResourceInventory.DecodedEntryPath,package.DecodedCharacterPayload.Length);
            foreach(var (path,payload) in package.CompanionPayloads)Reserve(path,payload.Length);
        }
        if(package.Document.AuthoredLayer is { } authoredLayer)Reserve(authoredLayer.EntryPath,package.AuthoredLayerPayload.Length);
        foreach(var (id,payload) in package.DerivedAnimationPayloads)Reserve(DerivedAnimationDataCodec.EntryPath(id),payload.Length);
        foreach(var (id,payload) in package.AuthoredAnimationPayloads)Reserve(AuthoredAnimationReference.EntryPath(id),payload.Length);
        foreach(var (path,payload) in package.TexturePayloads)Reserve(path,payload.Length);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, CustomModelPackage.ManifestEntryPath, manifestBytes, CompressionLevel.Optimal);
            if (package.Document.GeometryRevision is not null)
                WriteEntry(archive, ModelGeometryRevisionReference.PayloadEntryPath, package.GeometryRevisionPayload.AsSpan(), CompressionLevel.Optimal);
            WriteEntry(
                archive,
                package.Document.Source.EmbeddedEntryPath,
                package.SourceFbx.AsSpan(),
                CompressionLevel.Optimal);
            if (package.Document.CharacterResources is not null)
            {
                WriteEntry(archive, CharacterResourceInventory.DecodedEntryPath, package.DecodedCharacterPayload.AsSpan(), CompressionLevel.Optimal);
                foreach (var (path, bytes) in package.CompanionPayloads.OrderBy(p => p.Key, StringComparer.Ordinal))
                    WriteEntry(archive, path, bytes.AsSpan(), CompressionLevel.Optimal);
            }
            if (package.Document.AuthoredLayer is { } authored)
                WriteEntry(archive, authored.EntryPath, package.AuthoredLayerPayload.AsSpan(), CompressionLevel.Optimal);
            foreach ((Guid clipId, ImmutableArray<byte> payload) in package.DerivedAnimationPayloads.OrderBy(static item => item.Key))
                WriteEntry(archive, DerivedAnimationDataCodec.EntryPath(clipId), payload.AsSpan(), CompressionLevel.Optimal);
            foreach ((Guid clipId, ImmutableArray<byte> payload) in package.AuthoredAnimationPayloads.OrderBy(static item => item.Key))
                WriteEntry(archive, AuthoredAnimationReference.EntryPath(clipId), payload.AsSpan(), CompressionLevel.Optimal);
            foreach ((string entryPath, ImmutableArray<byte> payload) in package.TexturePayloads
                         .OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                WriteEntry(archive, entryPath, payload.AsSpan(), CompressionLevel.Optimal);
            }
        }

        if (stream.Length > MaximumPackageBytes)
        {
            throw new CustomModelFormatException(
                $"The serialized custom-model package exceeds {MaximumPackageBytes:N0} bytes.");
        }

        return ImmutableArray.Create(stream.ToArray());
    }

    /// <summary>
    /// Migrates package metadata in memory while leaving the embedded FBX and
    /// texture bytes untouched. Schema 1 had no authored-helper, camera, or
    /// morph inventory fields, so their schema-2 defaults are authoritative.
    /// </summary>
    public static CustomModelDocument MigrateToCurrent(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.Format,
                CustomModelDocument.CurrentFormat,
                StringComparison.Ordinal))
        {
            throw new CustomModelFormatException(
                $"Unsupported custom-model format '{document.Format}'.");
        }

        if (document.SchemaVersion < 7)
            document = document with { AuthoredLayer = null };

        if (document.SchemaVersion < 9)
            document = document with
            {
                WorkflowMode = document.RigConformance?.AppliedOutputRigSignature is not null ||
                    document.RigMode == CustomModelRigMode.Dl1HumanoidFit
                    ? CustomModelWorkflowMode.Character
                    : CustomModelWorkflowMode.OriginalRig,
            };

        return document.SchemaVersion switch
        {
            CustomModelDocument.CurrentSchemaVersion => document,
            1 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                AuthoredHelpers = [],
                Camera = new CustomModelCameraMetadata(),
                MorphChannels = [],
                MorphSignature = CustomModelDocument.EmptyMorphSignature,
                RigConformance = null,
                RiggingSession = null,
                SecondaryMotion = new(),
                FacialPresets = new(),
            },
            // Schema 2 predates rig conformance. An absent layer simply means
            // the model keeps its imported rig, so the migration is additive.
            2 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                RigConformance = null,
                RiggingSession = null,
                SecondaryMotion = new(),
                FacialPresets = new(),
            },
            // Schema 3 has no model-owned secondary motion or facial preset library.
            3 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                RiggingSession = null,
                SecondaryMotion = new(),
                FacialPresets = new(),
            },
            // Schema 4 already owns physics/presets; retain them. The new bank
            // reference switch defaults false so earlier authored behavior stays intact.
            4 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                RiggingSession = null,
                BuildSettings = document.BuildSettings with { ReferenceExistingAnimationLibrary = false },
            },
            // Existing conformance, stock-reference, facial and physics decisions
            // retain their previous meanings. Studio participation is opt-in.
            5 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                RiggingSession = null,
            },
            6 => document with { SchemaVersion = CustomModelDocument.CurrentSchemaVersion, AuthoredLayer = null },
            7 => document with { SchemaVersion = CustomModelDocument.CurrentSchemaVersion },
            8 => document with { SchemaVersion = CustomModelDocument.CurrentSchemaVersion },
            _ => throw new CustomModelFormatException(
                $"Unsupported custom-model schema {document.SchemaVersion}; expected schema 1 through {CustomModelDocument.CurrentSchemaVersion}."),
        };
    }

    private static Dictionary<string, ZipArchiveEntry> BuildEntryMap(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expandedBytes = 0;
        if (archive.Entries.Count > 65536) throw new CustomModelFormatException("The package contains too many entries.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.Length > MaximumPackageBytes - expandedBytes)
                throw new CustomModelFormatException("The expanded package exceeds its bounded payload limit.");
            expandedBytes += entry.Length;
            string path = entry.FullName.Replace('\\', '/');
            CustomModelSourceIdentity.ValidatePackageEntryPath(path, nameof(archive));
            if (!entries.TryAdd(path, entry))
            {
                throw new CustomModelFormatException($"The .dlrmodel contains duplicate entry '{path}'.");
            }
        }

        return entries;
    }

    private static ZipArchiveEntry GetRequiredEntry(
        Dictionary<string, ZipArchiveEntry> entries,
        string path)
    {
        if (!entries.TryGetValue(path, out ZipArchiveEntry? entry))
        {
            throw new CustomModelFormatException($"The .dlrmodel is missing required entry '{path}'.");
        }

        return entry;
    }

    private static ImmutableArray<byte> ReadBoundedEntry(ZipArchiveEntry entry, long maximumBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes || entry.CompressedLength > MaximumPackageBytes)
        {
            throw new CustomModelFormatException(
                $"Entry '{entry.FullName}' exceeds its {maximumBytes:N0}-byte limit.");
        }

        if (entry.Length > int.MaxValue)
        {
            throw new CustomModelFormatException($"Entry '{entry.FullName}' cannot be materialized safely.");
        }

        var bytes = new byte[(int)entry.Length];
        using Stream input = entry.Open();
        int totalRead = 0;
        while (totalRead < bytes.Length)
        {
            int read = input.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0)
            {
                throw new CustomModelFormatException($"Entry '{entry.FullName}' ended unexpectedly.");
            }

            totalRead += read;
        }

        if (input.ReadByte() != -1)
        {
            throw new CustomModelFormatException($"Entry '{entry.FullName}' expanded beyond its declared size.");
        }

        return ImmutableArray.Create(bytes);
    }

    private static void ValidatePayloads(CustomModelPackage package)
    {
        if (package.Document.GeometryRevision is { } revision)
        {
            revision.Validate();
            if (revision.SourceSha256 != package.Document.Source.ContentSha256 || package.GeometryRevisionPayload.Length != revision.PayloadLength)
                throw new CustomModelFormatException("Geometry revision source or payload length differs from its reference.");
            VerifySha256(package.GeometryRevisionPayload.AsSpan(), revision.ContentSha256, ModelGeometryRevisionReference.PayloadEntryPath);
        }
        else if (!package.GeometryRevisionPayload.IsEmpty) throw new CustomModelFormatException("Geometry revision bytes have no reference.");
        if (package.Document.CharacterResources is { } character)
        {
            if (package.DecodedCharacterPayload.Length != character.DecodedByteLength) throw new CustomModelFormatException("Decoded character payload length differs from the inventory.");
            VerifySha256(package.DecodedCharacterPayload.AsSpan(), character.DecodedSha256, CharacterResourceInventory.DecodedEntryPath);
            var declared = character.Resources.Where(r => r.EntryPath is not null).ToDictionary(r => r.EntryPath!, StringComparer.Ordinal);
            if (package.CompanionPayloads.Count != declared.Count) throw new CustomModelFormatException("Companion payloads differ from the inventory.");
            foreach (var (path, bytes) in package.CompanionPayloads)
            {
                if (!declared.TryGetValue(path, out var record) || bytes.Length != record.ByteLength || bytes.Length > MaximumSourceFbxBytes)
                    throw new CustomModelFormatException($"Companion payload '{path}' has no matching bounded inventory record.");
                VerifySha256(bytes.AsSpan(), record.ContentSha256!, path);
            }
            foreach(var record in character.Resources.Where(resource=>resource.NativeResource is not null))
            {
                var bytes=package.CompanionPayloads[record.EntryPath!].AsSpan();
                foreach(var item in record.NativeResource!.Items)
                    VerifySha256(bytes.Slice(checked((int)item.PayloadOffset),item.ByteLength),item.ContentSha256,record.EntryPath!);
            }
            foreach(var record in character.Resources.Where(resource=>resource.Material is not null))
            {
                var receipt=record.Material!;
                var providerRecord=character.Resources.Single(resource=>resource.Id==receipt.ProviderResourceId);
                var provider=package.CompanionPayloads[providerRecord.EntryPath!].AsSpan();
                var material=package.CompanionPayloads[record.EntryPath!].AsSpan();
                if(receipt.PayloadOffset>provider.Length-material.Length ||
                    !provider.Slice(checked((int)receipt.PayloadOffset),material.Length).SequenceEqual(material) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(material)!=receipt.NameHash ||
                    BinaryPrimitives.ReadUInt16LittleEndian(material[16..])!=receipt.TechniqueCount ||
                    BinaryPrimitives.ReadUInt16LittleEndian(material[18..])!=receipt.Textures.Length)
                    throw new CustomModelFormatException("Material payload differs from its original provider.");
                int textureOffset=checked(22+BinaryPrimitives.ReadUInt16LittleEndian(material[22..]));
                if(receipt.Textures.Length>0 && (textureOffset<24 || textureOffset>material.Length-receipt.Textures.Length*12))
                    throw new CustomModelFormatException("Material texture extent is invalid.");
                foreach(var texture in receipt.Textures)
                {
                    var row=material.Slice(textureOffset+texture.Index*12,12);
                    if(BinaryPrimitives.ReadUInt32LittleEndian(row)!=texture.SamplerState ||
                        BinaryPrimitives.ReadUInt32LittleEndian(row[4..])!=texture.TextureNameHash ||
                        BinaryPrimitives.ReadUInt32LittleEndian(row[8..])!=texture.LoadFlags)
                        throw new CustomModelFormatException("Material texture rows differ from their original provider.");
                    if (texture.NameSource is { } name)
                    {
                        var original = provider.Slice(checked((int)name.PayloadOffset), name.ByteLength);
                        byte[] expected = new byte[name.ByteLength];
                        Encoding.ASCII.GetBytes(name.Name).CopyTo(expected, 0);
                        if (!original.SequenceEqual(expected))
                            throw new CustomModelFormatException("The texture name differs from its original provider.");
                        VerifySha256(original, name.PayloadSha256, providerRecord.EntryPath!);
                    }
                }
            }
            foreach(var record in character.Resources.Where(resource=>resource.PackedEffect is not null))
            {
                var receipt=record.PackedEffect!;
                var bundleRecord=character.Resources.Single(resource=>resource.Id==receipt.BundleResourceId);
                var bundle=package.CompanionPayloads[bundleRecord.EntryPath!].AsSpan();
                var name=Encoding.UTF8.GetBytes(receipt.StoredName);
                int kindOffset=checked(receipt.EntryOffset+name.Length+1);
                if(receipt.EntryOffset>bundle.Length-name.Length || kindOffset>=bundle.Length || receipt.TextOffset!=kindOffset+1 ||
                    !bundle.Slice(receipt.EntryOffset,name.Length).SequenceEqual(name) || bundle[kindOffset-1]!=0 ||
                    unchecked((sbyte)bundle[kindOffset])!=receipt.Kind || bundle[receipt.TextOffset+receipt.TextByteLength]!=0 ||
                    !bundle.Slice(receipt.TextOffset,receipt.TextByteLength).SequenceEqual(package.CompanionPayloads[record.EntryPath!].AsSpan()))
                    throw new CustomModelFormatException("Packed effect bytes differ from their original bundle.");
            }
        }
        else if (!package.DecodedCharacterPayload.IsEmpty || package.CompanionPayloads.Count != 0)
            throw new CustomModelFormatException("Character payloads require an inventory.");
        ValidateAuthoredLayer(package);
        ValidateDerivedAnimationPayloads(package);
        ValidateAuthoredAnimationPayloads(package);
        if (package.SourceFbx.IsDefaultOrEmpty || package.SourceFbx.Length > MaximumSourceFbxBytes)
        {
            throw new ArgumentException("A model package must contain a bounded immutable source snapshot.", nameof(package));
        }

        VerifySha256(package.SourceFbx.AsSpan(), package.Document.Source.ContentSha256, "source FBX");
        HashSet<string> declaredPaths = package.Document.Materials
            .SelectMany(static material => material.Textures)
            .Where(static texture => texture.PackageEntryPath is not null)
            .Select(static texture => texture.PackageEntryPath!)
            .ToHashSet(StringComparer.Ordinal);
        foreach ((string entryPath, ImmutableArray<byte> payload) in package.TexturePayloads)
        {
            if (entryPath == AuthoredModelLayerReference.PayloadEntryPath)
                throw new ArgumentException("A texture cannot use the reserved authored-layer path.", nameof(package));
            CustomModelSourceIdentity.ValidatePackageEntryPath(entryPath, nameof(package));
            if (!declaredPaths.Contains(entryPath))
            {
                throw new ArgumentException(
                    $"Texture payload '{entryPath}' is not declared by the model manifest.",
                    nameof(package));
            }

            if (payload.IsDefaultOrEmpty || payload.Length > MaximumTextureBytes)
            {
                throw new ArgumentException($"Texture payload '{entryPath}' is empty or too large.", nameof(package));
            }

            string expectedHash = package.Document.Materials
                .SelectMany(static material => material.Textures)
                .First(texture => string.Equals(texture.PackageEntryPath, entryPath, StringComparison.Ordinal))
                .ContentSha256;
            VerifySha256(payload.AsSpan(), expectedHash, entryPath);
        }

        foreach (string declaredPath in declaredPaths)
        {
            if (!package.TexturePayloads.ContainsKey(declaredPath))
            {
                throw new ArgumentException(
                    $"The manifest declares texture payload '{declaredPath}', but no bytes were supplied.",
                    nameof(package));
            }
        }
    }

    private static void ValidateDerivedAnimationPayloads(CustomModelPackage package)
    {
        if (package.DerivedAnimationPayloads is null)
            throw new CustomModelFormatException("Derived animation payload storage must be initialized.");
        var clips = package.Document.AnimationClips.ToDictionary(static clip => clip.Id);
        long totalBytes = 0;
        foreach ((Guid clipId, ImmutableArray<byte> payload) in package.DerivedAnimationPayloads)
        {
            if (!clips.TryGetValue(clipId, out CustomModelAnimationClip? clip) || clip.DerivedMotion is not { } reference)
                throw new CustomModelFormatException($"Derived animation payload '{clipId}' has no manifest reference.");
            if (reference.SourceClipId == clipId)
                throw new CustomModelFormatException($"Derived animation metadata for clip '{clipId}' must identify its original source clip separately from the derived clip.");
            if (payload.IsDefaultOrEmpty || payload.Length > DerivedAnimationDataCodec.MaximumPayloadBytes ||
                payload.Length != reference.PayloadLength)
                throw new CustomModelFormatException($"Derived animation payload '{clipId}' is missing, oversized or has the wrong length.");
            totalBytes = checked(totalBytes + payload.Length);
            if (totalBytes > 512L * 1024L * 1024L)
                throw new CustomModelFormatException("The aggregate derived animation payloads exceed the package bound.");
            VerifySha256(payload.AsSpan(), reference.PayloadSha256, DerivedAnimationDataCodec.EntryPath(clipId));
            DerivedAnimationData data = DerivedAnimationDataCodec.Deserialize(payload.AsSpan());
            if (data.ClipId != clipId || data.FrameCount != clip.FrameCount)
                throw new CustomModelFormatException($"Derived animation payload '{clipId}' identifies another clip.");
        }
        foreach (CustomModelAnimationClip clip in package.Document.AnimationClips)
        {
            if (clip.DerivedMotion is { } reference)
            {
                reference.Validate();
                if (!package.DerivedAnimationPayloads.ContainsKey(clip.Id))
                    throw new CustomModelFormatException($"Clip '{clip.Id}' declares derived motion without a payload.");
            }
        }
    }

    public static void ValidateAuthoredAnimationPayloads(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.AuthoredAnimationPayloads is null)
            throw new CustomModelFormatException("Authored animation payloads must be initialized.");
        Dictionary<Guid, CustomModelAnimationClip> clips = package.Document.AnimationClips
            .Where(static clip => clip.AuthoredAnimation is not null).ToDictionary(static clip => clip.Id);
        long total = 0;
        foreach ((Guid id, ImmutableArray<byte> payload) in package.AuthoredAnimationPayloads)
        {
            if (!clips.TryGetValue(id, out CustomModelAnimationClip? clip))
                throw new CustomModelFormatException("An authored animation payload has no manifest reference.");
            AuthoredAnimationReference reference = clip.AuthoredAnimation!;
            reference.Validate();
            if (payload.IsDefaultOrEmpty || payload.Length != reference.PayloadLength)
                throw new CustomModelFormatException("An authored animation payload is missing or has the wrong length.");
            total = checked(total + payload.Length);
            if (total > 512L * 1024L * 1024L)
                throw new CustomModelFormatException("Authored animation payloads exceed the package bound.");
            VerifySha256(payload.AsSpan(), reference.PayloadSha256, AuthoredAnimationReference.EntryPath(id));
            DerivedAnimationData data = DerivedAnimationDataCodec.Deserialize(payload.AsSpan());
            if (data.ClipId != id || data.FrameCount != clip.FrameCount || data.FrameRate != clip.FrameRate)
                throw new CustomModelFormatException("Authored animation timing or identity differs from its manifest.");
        }
        if (clips.Keys.Any(id => !package.AuthoredAnimationPayloads.ContainsKey(id)))
            throw new CustomModelFormatException("An authored animation has no payload.");
    }

    public static AuthoredModelLayer? ValidateAuthoredLayer(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Document.AuthoredLayer is not { } reference)
        {
            if (!package.AuthoredLayerPayload.IsDefaultOrEmpty) throw new CustomModelFormatException("Authored bytes have no manifest reference.");
            return null;
        }
        reference.Validate();
        if (package.AuthoredLayerPayload.IsDefaultOrEmpty || package.AuthoredLayerPayload.Length != reference.PayloadLength)
            throw new CustomModelFormatException("The authored-layer payload is missing or has the wrong length.");
        VerifySha256(package.AuthoredLayerPayload.AsSpan(), reference.ContentSha256, reference.EntryPath);
        var layer = AuthoredModelLayerCodec.Deserialize(package.AuthoredLayerPayload.AsSpan());
        // The base-rig signature remains independent of unweighted helper edits.
        // Version 2 additionally binds weighted helper names to their persistent IDs.
        var baseNames = package.Document.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var layerNames = layer.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var helpers = package.Document.AuthoredHelpers.ToDictionary(h => h.Name, StringComparer.Ordinal);
        bool identitiesMatch = layer.Version == 1
            ? layerNames.SetEquals(baseNames)
            : baseNames.IsSubsetOf(layerNames) && layer.Bones.Where(b => !baseNames.Contains(b.Name))
                .All(b => helpers.TryGetValue(b.Name, out var helper) && helper.Id == b.Id);
        if (!string.Equals(layer.SourceSha256, package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(layer.TargetRigSignature, CustomModelContractSignatures.ComputeRig(package.Document.Bones), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.Document.RigSignature, CustomModelContractSignatures.ComputeRig(package.Document.CreateEffectiveBones()), StringComparison.OrdinalIgnoreCase) ||
            !identitiesMatch)
            throw new CustomModelFormatException("The authored layer belongs to a different source, target rig or bound helper identity.");
        return layer;
    }

    private static void VerifySha256(ReadOnlySpan<byte> payload, string expectedHash, string subject)
    {
        string actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomModelFormatException(
                $"The SHA-256 for '{subject}' is {actualHash}, expected {expectedHash}.");
        }
    }

    private static void EnsureExistingTargetCanBeReplaced(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            _ = Load(fullPath);
        }
        catch (CustomModelFormatException exception)
        {
            throw new CustomModelFormatException(
                "Refusing to overwrite an existing .dlrmodel that is not a supported DL ReAnimated model package.",
                exception);
        }
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        ReadOnlySpan<byte> payload,
        CompressionLevel compressionLevel)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, compressionLevel);
        entry.LastWriteTime = DeterministicTimestamp;
        using Stream output = entry.Open();
        output.Write(payload);
    }

    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = CreateTypeInfoResolver(),
        };
        options.Converters.Add(new FrameRateJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static DefaultJsonTypeInfoResolver CreateTypeInfoResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            // These math types intentionally expose calculated diagnostic
            // getters to evaluators. Suppress only those getters: record-
            // constructor coordinates and exact matrix cells remain authored
            // package state and must round-trip.
            string[] calculatedProperties = typeInfo.Type switch
            {
                Type type when type == typeof(Vector3D) =>
                    ["LengthSquared", "Length", "IsFinite"],
                Type type when type == typeof(QuaternionD) =>
                    ["LengthSquared", "IsFinite"],
                Type type when type == typeof(TransformTRS) =>
                    ["IsFinite"],
                Type type when type == typeof(TransformMatrix) =>
                    ["Translation", "LinearDeterminant", "IsFinite"],
                _ => [],
            };
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (calculatedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    property.ShouldSerialize = static (_, _) => false;
                }
            }
        });
        return resolver;
    }

    private sealed class FrameRateJsonConverter : JsonConverter<FrameRate>
    {
        public override FrameRate Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("A custom-model frame rate must be an object.");
            }

            int? numerator = null;
            int? denominator = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("A custom-model frame rate contains malformed JSON.");
                }

                string propertyName = reader.GetString() ?? string.Empty;
                if (!reader.Read())
                {
                    throw new JsonException("A custom-model frame rate ended unexpectedly.");
                }

                switch (propertyName)
                {
                    case "numerator":
                        numerator = reader.GetInt32();
                        break;
                    case "denominator":
                        denominator = reader.GetInt32();
                        break;
                    // Early development packages included this calculated
                    // getter. Accept it on read so those packages are not
                    // stranded, but never write it again.
                    case "framesPerSecond":
                        _ = reader.GetDouble();
                        break;
                    default:
                        throw new JsonException($"Unsupported frame-rate property '{propertyName}'.");
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || numerator is null || denominator is null)
            {
                throw new JsonException("A custom-model frame rate requires numerator and denominator.");
            }

            return new FrameRate(numerator.Value, denominator.Value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            FrameRate value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("numerator", value.Numerator);
            writer.WriteNumber("denominator", value.Denominator);
            writer.WriteEndObject();
        }
    }
}


