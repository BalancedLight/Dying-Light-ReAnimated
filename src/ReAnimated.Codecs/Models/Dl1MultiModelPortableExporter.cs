using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public enum Dl1PortableTargetKind
{
    CustomModel,
    RetailReference,
}

public enum Dl1PortableAnimationRole
{
    Body,
    Facial,
}

public sealed record Dl1PortableAnimationResource
{
    public required Guid VariantId { get; init; }

    public required string Name { get; init; }

    public required Dl1PortableAnimationRole Role { get; init; }

    public required byte[] Payload { get; init; }

    public required int FrameCount { get; init; }

    public required float FramesPerSecond { get; init; }

    public required string SourceFingerprint { get; init; }
}

public sealed record Dl1PortableMorphResource
{
    public required string RelativePath { get; init; }

    public required byte[] Payload { get; init; }
}

public sealed record Dl1PortableModelOutputRequest
{
    public required Guid ModelId { get; init; }

    public required string ModelName { get; init; }

    public required Dl1PortableTargetKind TargetKind { get; init; }

    public required string TargetFingerprint { get; init; }

    public required string AnimationLibraryName { get; init; }

    public ImmutableArray<Dl1PortableAnimationResource> Animations { get; init; } = [];

    /// <summary>
    /// User-owned output of the official compiler. Retail targets must leave
    /// this null so no retail model bytes can enter a portable package.
    /// </summary>
    public byte[]? CompiledCustomModelRpack { get; init; }

    /// <summary>
    /// Receipt-bound evidence from the same official-compiler invocation that
    /// produced <see cref="CompiledCustomModelRpack"/>. Custom targets require
    /// it; retail references must leave it null.
    /// </summary>
    public Dl1OfficialModelCompilerEvidence? OfficialCompilerEvidence
    { get; init; }

    /// <summary>
    /// Portable metadata references for the channels embedded in the compiled
    /// model RPack. These are not fabricated standalone morph payloads.
    /// </summary>
    public ImmutableArray<Dl1PortableMorphResource> CompiledMorphResources { get; init; } = [];

    /// <summary>
    /// Stable retail resource identity only. It is metadata, not payload.
    /// </summary>
    public string? RetailResourceReference { get; init; }
}

public sealed record Dl1MultiModelPortableExportRequest
{
    /// <summary>
    /// Parent selected by the user. The exporter owns only one sanitized
    /// child directory beneath it.
    /// </summary>
    public required string ParentOutputDirectory { get; init; }

    public string PackageName { get; init; } = "dl-reanimated-portable-output";

    public required ImmutableArray<Dl1PortableModelOutputRequest> Models { get; init; }
}

public sealed record Dl1PortableModelOutputResult(
    Guid ModelId,
    string ModelName,
    Dl1PortableTargetKind TargetKind,
    string DirectoryPath,
    string AnimationRpackPath,
    string AnimationLibraryName,
    ImmutableArray<string> BodyAnimations,
    ImmutableArray<string> FacialAnimations,
    string? CustomModelRpackPath,
    ImmutableArray<string> MorphResourcePaths,
    ImmutableDictionary<string, string> OutputSha256);

public sealed record Dl1MultiModelPortableExportResult(
    string PackageDirectory,
    string ManifestPath,
    ImmutableArray<Dl1PortableModelOutputResult> Models);

/// <summary>
/// Publishes checked project models as a single portable directory
/// transaction. Each target receives an independent animation-library RPack;
/// custom targets may additionally include user-owned compiler products,
/// while retail targets are structurally unable to carry retail model bytes.
/// </summary>
public static class Dl1MultiModelPortableExporter
{
    private const string OwnershipMarker = ".dl-reanimated-portable-owned";
    private const int MaximumModelCount = 128;
    private const int MaximumAnimationCountPerModel = 4_096;
    private const int MaximumMorphResourceCountPerModel = 4_096;
    private const int MaximumMorphInventoryReferenceBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<Dl1MultiModelPortableExportResult> ExportAsync(
        Dl1MultiModelPortableExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedPortableRequest validated = Validate(request);
        string staging = Path.Combine(
            validated.ParentDirectory,
            $".{validated.PackageDirectoryName}.{Guid.NewGuid():N}.staging");
        string target = Path.Combine(
            validated.ParentDirectory,
            validated.PackageDirectoryName);
        if (Directory.Exists(target))
        {
            RequireOwnedDirectory(target);
        }

        string backup = Path.Combine(
            validated.ParentDirectory,
            $".{validated.PackageDirectoryName}.{Guid.NewGuid():N}.backup");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(
            Path.Combine(staging, OwnershipMarker),
            "Owned by DL ReAnimated. Replace only as one validated portable package.\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        bool published = false;
        try
        {
            var results = ImmutableArray.CreateBuilder<Dl1PortableModelOutputResult>(
                validated.Models.Length);
            for (int index = 0; index < validated.Models.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatedPortableModel model = validated.Models[index];
                results.Add(await WriteModelAsync(
                    staging,
                    index,
                    model,
                    cancellationToken).ConfigureAwait(false));
            }

            ImmutableArray<Dl1PortableModelOutputResult> stagedResults =
                results.MoveToImmutable();
            string stagedManifest = Path.Combine(staging, "portable-manifest.json");
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "dl-reanimated-multi-model-portable-output",
                schemaVersion = 1,
                modelCount = stagedResults.Length,
                models = stagedResults.Select((result, index) => new
                {
                    result.ModelId,
                    result.ModelName,
                    result.TargetKind,
                    targetFingerprint = validated.Models[index].Source.TargetFingerprint,
                    retailResourceReference =
                        validated.Models[index].Source.RetailResourceReference,
                    directory = Relative(staging, result.DirectoryPath),
                    animationRpack = Relative(staging, result.AnimationRpackPath),
                    result.AnimationLibraryName,
                    animations = validated.Models[index].Source.Animations
                        .OrderBy(static animation => animation.Name, StringComparer.Ordinal)
                        .Select(animation => new
                        {
                            animation.VariantId,
                            animation.Name,
                            animation.Role,
                            animation.SourceFingerprint,
                            animation.FrameCount,
                            animation.FramesPerSecond,
                        }),
                    customModelRpack = result.CustomModelRpackPath is null
                        ? null
                        : Relative(staging, result.CustomModelRpackPath),
                    officialCompilerEvidence = validated.Models[index]
                        .Source.OfficialCompilerEvidence,
                    morphResources = result.MorphResourcePaths.Select(path => Relative(staging, path)),
                    outputs = result.OutputSha256.OrderBy(static pair => pair.Key).Select(static pair => new
                    {
                        path = pair.Key,
                        sha256 = pair.Value,
                    }),
                }),
                safety = new
                {
                    retailTargetsContainReferencesOnly = true,
                    validationMode = "offline-package-round-trip",
                    liveGameProof = false,
                },
                completedUtc = DateTimeOffset.UtcNow,
            }, JsonOptions);
            await WriteFileDurablyAsync(
                stagedManifest,
                manifest,
                cancellationToken).ConfigureAwait(false);
            ValidateStagedManifest(stagedManifest, stagedResults.Length);
            PublishOwnedDirectory(staging, target, backup);
            published = true;
            return new Dl1MultiModelPortableExportResult(
                target,
                Path.Combine(target, Path.GetFileName(stagedManifest)),
                stagedResults.Select(result => Remap(result, staging, target)).ToImmutableArray());
        }
        finally
        {
            if (!published && Directory.Exists(staging))
            {
                DeleteOwnedDirectory(staging);
            }

            if (published && Directory.Exists(backup))
            {
                DeleteOwnedDirectory(backup);
            }
        }
    }

    private static async Task<Dl1PortableModelOutputResult> WriteModelAsync(
        string stagingRoot,
        int index,
        ValidatedPortableModel model,
        CancellationToken cancellationToken)
    {
        string directoryName = $"{index + 1:D3}-{model.SafeModelName}";
        string directory = Path.Combine(stagingRoot, directoryName);
        Directory.CreateDirectory(directory);

        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var bodySequences = ImmutableArray.CreateBuilder<AnimationScrSequence>();
        foreach (Dl1PortableAnimationResource animation in model.Source.Animations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Anm2Reader.Read(animation.Payload, animation.Name, cancellationToken: cancellationToken);
            string resourceName = Dl1SourceModelWriter.RequireExactResourceName(
                animation.Name,
                63,
                "portable animation resource name");
            payloads.Add(resourceName, animation.Payload);
            if (animation.Role == Dl1PortableAnimationRole.Body)
            {
                bodySequences.Add(new AnimationScrSequence(
                    resourceName,
                    resourceName + ".anm2",
                    0,
                    animation.FrameCount - 1,
                    animation.FramesPerSecond));
            }
        }

        AnimationScrSections script = AnimationScrCodec.Build(
            bodySequences.ToImmutable());
        byte[] animationRpack = Rp6lAnimationLibraryCodec.Build(
            payloads,
            new Dictionary<string, Rp6lAnimationScript>(StringComparer.OrdinalIgnoreCase)
            {
                [model.AnimationLibraryName] = new Rp6lAnimationScript(
                    script.RecordsAndNames,
                    script.IndexAndNames),
            });
        string animationRpackPath = Path.Combine(
            directory,
            model.AnimationLibraryName + "_pc.rpack");
        await WriteFileDurablyAsync(
            animationRpackPath,
            animationRpack,
            cancellationToken).ConfigureAwait(false);
        Rp6lAnimationLibrary reopened = await Rp6lAnimationLibraryCodec.ExtractAsync(
            animationRpackPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reopened.Animations.Count != payloads.Count ||
            !reopened.AnimationScripts.ContainsKey(model.AnimationLibraryName))
        {
            throw new InvalidDataException(
                $"Portable animation RPack for '{model.Source.ModelName}' failed its offline round trip.");
        }

        string? customModelPath = null;
        var morphPaths = ImmutableArray.CreateBuilder<string>();
        if (model.Source.TargetKind == Dl1PortableTargetKind.CustomModel)
        {
            customModelPath = Path.Combine(directory, model.SafeModelName + "_pc.rpack");
            await WriteFileDurablyAsync(
                customModelPath,
                model.Source.CompiledCustomModelRpack!,
                cancellationToken).ConfigureAwait(false);
            string packagedModelSha256 = await Sha256FileAsync(
                customModelPath,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    packagedModelSha256,
                    model.Source.OfficialCompilerEvidence!
                        .OutputRpackSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Custom target '{model.Source.ModelName}' packaged RPack bytes do not match the official-compiler evidence.");
            }

            Rp6lArchive compiledModel = await OpenCompiledModelRpackAsync(
                customModelPath,
                model.Source.ModelName,
                cancellationToken).ConfigureAwait(false);
            if (!compiledModel.Resources.Any(static resource =>
                    resource.ResourceType == Rp6lResourceTypes.Mesh))
            {
                throw new InvalidDataException(
                    $"Custom target '{model.Source.ModelName}' compiler output contains no DL1 mesh resource.");
            }

            string morphDirectory = Path.Combine(directory, "morphs");
            foreach (Dl1PortableMorphResource resource in model.Source.CompiledMorphResources)
            {
                string destination = ResolveChildPath(morphDirectory, resource.RelativePath);
                await WriteFileDurablyAsync(
                    destination,
                    resource.Payload,
                    cancellationToken).ConfigureAwait(false);
                morphPaths.Add(destination);
            }
        }

        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes.Add(Relative(directory, path), await Sha256FileAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return new Dl1PortableModelOutputResult(
            model.Source.ModelId,
            model.Source.ModelName,
            model.Source.TargetKind,
            directory,
            animationRpackPath,
            model.AnimationLibraryName,
            model.Source.Animations
                .Where(static animation => animation.Role == Dl1PortableAnimationRole.Body)
                .Select(static animation => animation.Name)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray(),
            model.Source.Animations
                .Where(static animation => animation.Role == Dl1PortableAnimationRole.Facial)
                .Select(static animation => animation.Name)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray(),
            customModelPath,
            morphPaths.ToImmutable(),
            hashes.ToImmutable());
    }

    private static async Task<Rp6lArchive> OpenCompiledModelRpackAsync(
        string path,
        string modelName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Rp6lArchive.OpenAsync(
                path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is EndOfStreamException or
            InvalidDataException or
            OverflowException)
        {
            throw new InvalidDataException(
                $"Custom target '{modelName}' compiler output is not a valid bounded RP6L archive.",
                exception);
        }
    }

    private static ValidatedPortableRequest Validate(Dl1MultiModelPortableExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentOutputDirectory);
        if (request.Models.IsDefaultOrEmpty || request.Models.Length > MaximumModelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Select between 1 and {MaximumModelCount:N0} models for portable output.");
        }

        if (request.Models.Select(static model => model.ModelId).Distinct().Count() != request.Models.Length)
        {
            throw new InvalidOperationException("Portable output contains duplicate project model identities.");
        }

        string parent = Path.GetFullPath(request.ParentOutputDirectory);
        Directory.CreateDirectory(parent);
        string packageName = Dl1SourceModelWriter.SanitizeName(request.PackageName, 80);
        var models = ImmutableArray.CreateBuilder<ValidatedPortableModel>(request.Models.Length);
        foreach (Dl1PortableModelOutputRequest model in request.Models)
        {
            ArgumentNullException.ThrowIfNull(model);
            if (model.ModelId == Guid.Empty)
            {
                throw new InvalidDataException("Portable model identities cannot be empty.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelName);
            RequireSha256(model.TargetFingerprint, "target model fingerprint");
            string safeModelName = Dl1SourceModelWriter.SanitizeName(model.ModelName, 55);
            string libraryName = Dl1SourceModelWriter.RequireExactResourceName(
                model.AnimationLibraryName,
                63,
                "portable animation library name");
            if (model.Animations.IsDefaultOrEmpty ||
                model.Animations.Length > MaximumAnimationCountPerModel)
            {
                throw new InvalidDataException(
                    $"Portable model '{model.ModelName}' must contain between 1 and {MaximumAnimationCountPerModel:N0} selected animation resources.");
            }

            if (model.Animations.Any(static animation => animation is null) ||
                model.Animations
                    .Select(static animation => (animation.VariantId, animation.Role))
                    .Distinct()
                    .Count() != model.Animations.Length ||
                model.Animations.Select(static animation => animation.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != model.Animations.Length)
            {
                throw new InvalidDataException(
                    $"Portable model '{model.ModelName}' repeats an animation variant role or resource name.");
            }

            foreach (Dl1PortableAnimationResource animation in model.Animations)
            {
                if (animation.VariantId == Guid.Empty ||
                    animation.Payload is null ||
                    animation.Payload.Length == 0 ||
                    animation.FrameCount <= 0 ||
                    !float.IsFinite(animation.FramesPerSecond) ||
                    animation.FramesPerSecond is < 1 or > 240)
                {
                    throw new InvalidDataException(
                        $"Portable animation '{animation.Name}' contains invalid identity, timing, or payload data.");
                }

                RequireSha256(animation.SourceFingerprint, "animation source fingerprint");
            }

            if (model.TargetKind == Dl1PortableTargetKind.RetailReference)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(model.RetailResourceReference);
                if (model.CompiledCustomModelRpack is not null ||
                    model.OfficialCompilerEvidence is not null ||
                    !model.CompiledMorphResources.IsDefaultOrEmpty)
                {
                    throw new InvalidOperationException(
                        $"Retail target '{model.ModelName}' may contain authored animations and references only; retail model or morph bytes are forbidden.");
                }
            }
            else
            {
                if (model.CompiledCustomModelRpack is not { Length: > 0 })
                {
                    throw new InvalidDataException(
                        $"Custom target '{model.ModelName}' requires its user-owned compiled model RPack.");
                }

                if (model.RetailResourceReference is not null)
                {
                    throw new InvalidDataException(
                        $"Custom target '{model.ModelName}' cannot claim a retail resource identity.");
                }

                Dl1OfficialModelCompilerEvidence evidence =
                    model.OfficialCompilerEvidence ??
                    throw new InvalidDataException(
                        $"Custom target '{model.ModelName}' requires receipt-bound official-compiler evidence.");
                ValidateOfficialCompilerEvidence(model, evidence);

                if (model.CompiledMorphResources.Length > MaximumMorphResourceCountPerModel)
                {
                    throw new InvalidDataException(
                        $"Custom target '{model.ModelName}' exceeds the portable morph-resource limit.");
                }

                if (model.CompiledMorphResources.Length !=
                    evidence.VerifiedMorphChannelCount)
                {
                    throw new InvalidDataException(
                        $"Custom target '{model.ModelName}' portable morph inventory has {model.CompiledMorphResources.Length:N0} channel reference(s), but its official-compiler evidence verified {evidence.VerifiedMorphChannelCount:N0} channel(s).");
                }

                var inventoryIndexes = new HashSet<int>();
                var inventoryNames = new HashSet<string>(
                    StringComparer.Ordinal);
                foreach (Dl1PortableMorphResource resource in model.CompiledMorphResources)
                {
                    ValidateRelativePath(resource.RelativePath, "portable morph resource path");
                    if (!resource.RelativePath.EndsWith(
                            ".morph.json",
                            StringComparison.OrdinalIgnoreCase) ||
                        resource.Payload is not
                            { Length: > 0 and <=
                                MaximumMorphInventoryReferenceBytes })
                    {
                        throw new InvalidDataException(
                            $"Custom target '{model.ModelName}' contains an invalid morph inventory reference.");
                    }

                    PortableMorphInventoryIdentity identity =
                        ValidateMorphInventoryReference(
                            model.ModelName,
                            resource.Payload);
                    if (!inventoryIndexes.Add(identity.Index) ||
                        !inventoryNames.Add(identity.Name))
                    {
                        throw new InvalidDataException(
                            $"Custom target '{model.ModelName}' repeats a morph inventory index or exact channel name.");
                    }
                }
            }

            models.Add(new ValidatedPortableModel(
                model,
                safeModelName,
                libraryName));
        }

        if (models.Select(static model => model.SafeModelName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != models.Count)
        {
            throw new InvalidOperationException(
                "Checked model names collide after DL1-safe name normalization.");
        }

        return new ValidatedPortableRequest(parent, packageName, models.MoveToImmutable());
    }

    private static void ValidateOfficialCompilerEvidence(
        Dl1PortableModelOutputRequest model,
        Dl1OfficialModelCompilerEvidence evidence)
    {
        RequireSha256(
            evidence.OutputRpackSha256,
            "compiled model RPack fingerprint");
        RequireSha256(
            evidence.CompilerFingerprint,
            "official compiler fingerprint");
        RequireSha256(
            evidence.ToolFingerprint,
            "compiler bridge tool fingerprint");
        RequireSha256(
            evidence.BuildReceiptInputFingerprint,
            "model build receipt input fingerprint");
        RequireSha256(
            evidence.BuildReceiptOutputManifestFingerprint,
            "model build receipt output manifest fingerprint");
        if (evidence.BuildState !=
                CustomModelBuildState.CompilerValidated ||
            !string.Equals(
                evidence.ToolFingerprint,
                Dl1OfficialModelCompiler.CurrentToolFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Custom target '{model.ModelName}' official-compiler evidence is stale or was not compiler-validated by the current bridge contract.");
        }

        string actualRpackSha256 = Convert.ToHexStringLower(
            SHA256.HashData(model.CompiledCustomModelRpack!));
        if (!string.Equals(
                actualRpackSha256,
                evidence.OutputRpackSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Custom target '{model.ModelName}' compiled RPack does not match its official-compiler output fingerprint.");
        }

        if (evidence.VerifiedMorphChannelCount < 0 ||
            evidence.VerifiedMorphBindingCount < 0)
        {
            throw new InvalidDataException(
                $"Custom target '{model.ModelName}' official-compiler morph counts cannot be negative.");
        }

        if (evidence.VerifiedMorphChannelCount == 0)
        {
            if (evidence.VerifiedMorphBindingCount != 0 ||
                evidence.MorphDeltaFormat is not null)
            {
                throw new InvalidDataException(
                    $"Custom target '{model.ModelName}' reports morph bindings or encoding without a verified morph-channel inventory.");
            }

            return;
        }

        if (evidence.VerifiedMorphBindingCount == 0 ||
            evidence.MorphDeltaFormat !=
                CompiledMorphDeltaFormat.PcHalf4)
        {
            throw new InvalidDataException(
                $"Custom target '{model.ModelName}' morph-bearing PC compiler evidence must contain at least one compact-mesh binding encoded as {CompiledMorphDeltaFormat.PcHalf4}.");
        }
    }

    private static PortableMorphInventoryIdentity
        ValidateMorphInventoryReference(
            string modelName,
            byte[] payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out JsonElement format) ||
                format.GetString() !=
                    "dl-reanimated-compiled-morph-inventory-reference" ||
                !root.TryGetProperty(
                    "schemaVersion",
                    out JsonElement schemaVersion) ||
                schemaVersion.GetInt32() != 1 ||
                !root.TryGetProperty("index", out JsonElement index) ||
                index.GetInt32() < 0 ||
                !root.TryGetProperty("name", out JsonElement name) ||
                string.IsNullOrWhiteSpace(name.GetString()) ||
                !root.TryGetProperty(
                    "compiledPayload",
                    out JsonElement compiledPayload) ||
                compiledPayload.GetString() !=
                    "embedded-in-official-compiler-model-rpack")
            {
                throw new InvalidDataException(
                    $"Custom target '{modelName}' contains a morph sidecar that is not an official-compiler payload inventory reference.");
            }

            return new PortableMorphInventoryIdentity(
                index.GetInt32(),
                name.GetString()!);
        }
        catch (Exception exception) when (
            exception is JsonException or
            InvalidOperationException or
            FormatException or
            OverflowException)
        {
            throw new InvalidDataException(
                $"Custom target '{modelName}' contains a malformed morph inventory reference.",
                exception);
        }
    }

    private static void ValidateStagedManifest(string path, int expectedModelCount)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        if (root.GetProperty("format").GetString() !=
                "dl-reanimated-multi-model-portable-output" ||
            root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("modelCount").GetInt32() != expectedModelCount ||
            root.GetProperty("models").GetArrayLength() != expectedModelCount)
        {
            throw new InvalidDataException("Portable output manifest failed its offline validation round trip.");
        }
    }

    private static void PublishOwnedDirectory(string staging, string target, string backup)
    {
        bool movedPrevious = false;
        if (Directory.Exists(target))
        {
            RequireOwnedDirectory(target);
            Directory.Move(target, backup);
            movedPrevious = true;
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (movedPrevious && !Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }
    }

    private static void RequireOwnedDirectory(string path)
    {
        if (!File.Exists(Path.Combine(path, OwnershipMarker)))
        {
            throw new InvalidOperationException(
                $"Portable output directory '{path}' is not owned by DL ReAnimated and will not be replaced.");
        }
    }

    private static void DeleteOwnedDirectory(string path)
    {
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, OwnershipMarker)))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task WriteFileDurablyAsync(
        string path,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(
                    payload,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(
                    cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ResolveChildPath(string root, string relativePath)
    {
        ValidateRelativePath(relativePath, nameof(relativePath));
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Portable resource path escapes its model directory.");
        }

        return fullPath;
    }

    private static void ValidateRelativePath(string value, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathRooted(value) ||
            value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"{description} must be a safe relative path.");
        }
    }

    private static void RequireSha256(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{description} must be a 64-character SHA-256 value.");
        }
    }

    private static async Task<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static Dl1PortableModelOutputResult Remap(
        Dl1PortableModelOutputResult value,
        string oldRoot,
        string newRoot) => value with
        {
            DirectoryPath = ReplaceRoot(value.DirectoryPath, oldRoot, newRoot),
            AnimationRpackPath = ReplaceRoot(value.AnimationRpackPath, oldRoot, newRoot),
            CustomModelRpackPath = value.CustomModelRpackPath is null
                ? null
                : ReplaceRoot(value.CustomModelRpackPath, oldRoot, newRoot),
            MorphResourcePaths = value.MorphResourcePaths
                .Select(path => ReplaceRoot(path, oldRoot, newRoot))
                .ToImmutableArray(),
        };

    private static string ReplaceRoot(string path, string oldRoot, string newRoot) =>
        Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path));

    private sealed record ValidatedPortableRequest(
        string ParentDirectory,
        string PackageDirectoryName,
        ImmutableArray<ValidatedPortableModel> Models);

    private sealed record ValidatedPortableModel(
        Dl1PortableModelOutputRequest Source,
        string SafeModelName,
        string AnimationLibraryName);

    private sealed record PortableMorphInventoryIdentity(
        int Index,
        string Name);
}
