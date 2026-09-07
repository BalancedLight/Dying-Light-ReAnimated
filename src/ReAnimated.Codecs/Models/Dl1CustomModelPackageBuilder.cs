using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1CustomModelPackageRequest
{
    public required FbxModelAuthoringImportResult Model { get; init; }

    /// <summary>
    /// Parent selected by the author. The builder owns only a sanitized
    /// resource-specific child directory beneath this location.
    /// </summary>
    public required string ParentOutputDirectory { get; init; }

    public required string CompilerExecutablePath { get; init; }

    public string? RetailData0PakPath { get; init; }

    public required string ResourceName { get; init; }

    public string SurfaceName { get; init; } = "default";

    public string? AnimationScriptAlias { get; init; }

    public ImmutableArray<CustomModelAnimationClip> AnimationSelections { get; init; } = [];

    internal Func<Dl1SourceModelBuildRequest, CancellationToken, Task<Dl1SourceModelBuildResult>>?
        SourceWriterOverride { get; init; }

    internal Func<Dl1OfficialModelCompilerRequest, CancellationToken, Task<Dl1OfficialModelCompilerResult>>?
        ModelCompilerOverride { get; init; }

    internal Func<CustomModelAnimationLibraryRequest, CancellationToken, Task<CustomModelAnimationLibraryResult>>?
        AnimationExporterOverride { get; init; }

    public TimeSpan CompilerTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record Dl1CustomModelPackageResult(
    string PackageDirectory,
    string ManifestPath,
    Dl1SourceModelBuildResult SourceModel,
    Dl1OfficialModelCompilerResult CompiledModel,
    CustomModelAnimationLibraryResult? AnimationLibrary,
    ImmutableDictionary<string, string> OutputSha256)
{
    public Dl1StockAnimationReference? StockAnimationReference { get; init; }
}

/// <summary>
/// Builds the complete DL1 custom-model handoff as a single transaction. A
/// failed loose writer, official compiler, animation export, or final
/// validation can never replace the previous valid package.
/// </summary>
public static class Dl1CustomModelPackageBuilder
{
    private const string OwnershipMarker = ".dl-reanimated-package-owned";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<Dl1CustomModelPackageResult> BuildAsync(
        Dl1CustomModelPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string resourceName = Dl1SourceModelWriter.SanitizeName(request.ResourceName, 55);
        string surfaceName = Dl1SourceModelWriter.SanitizeName(request.SurfaceName, 63);
        string? alias = string.IsNullOrWhiteSpace(request.AnimationScriptAlias)
            ? null
            : Dl1SourceModelWriter.RequireExactResourceName(
                request.AnimationScriptAlias,
                63,
                "animation script alias");
        ImmutableArray<CustomModelAnimationClip> selections = request.AnimationSelections.IsDefaultOrEmpty
            ? request.Model.Package.Document.AnimationClips
            : request.AnimationSelections;
        bool stockReference = request.Model.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary;
        bool hasAnimations = !stockReference && selections.Any(static selection => selection.Included);
        Dl1StockAnimationReference? stockBank = null;
        if (stockReference)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(request.RetailData0PakPath))
                throw new InvalidOperationException("Existing-bank package export requires an animation-script alias and the selected retail Data0.pak.");
            stockBank = await Task.Run(() => Dl1StockAnimationReferenceValidator.Validate(
                request.RetailData0PakPath, alias, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        if (hasAnimations && string.IsNullOrWhiteSpace(alias))
        {
            throw new InvalidOperationException(
                "Selected animation stacks require an animation script alias. " +
                "Set one Models > Animation script alias before building the DL1 package.");
        }
        if (!stockReference && !hasAnimations && !string.IsNullOrWhiteSpace(alias))
        {
            throw new InvalidOperationException(
                "An animation script alias cannot be packaged without at least one included animation stack. " +
                "Include an animation or clear the alias so the model package cannot reference a missing animation library.");
        }

        string parent = Path.GetFullPath(request.ParentOutputDirectory);
        Directory.CreateDirectory(parent);
        string packageName = $"{resourceName}_dl1_package";
        string target = Path.Combine(parent, packageName);
        string staging = Path.Combine(parent, $".{packageName}.{Guid.NewGuid():N}.staging");
        string backup = Path.Combine(parent, $".{packageName}.{Guid.NewGuid():N}.backup");
        EnsureOwnedChild(parent, target);
        EnsureOwnedChild(parent, staging);
        EnsureOwnedChild(parent, backup);

        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(
            Path.Combine(staging, OwnershipMarker),
            "Owned by DL ReAnimated. Safe to replace only as one validated package.\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        bool published = false;
        try
        {
            string looseDirectory = Path.Combine(staging, "loose");
            string compiledDirectory = Path.Combine(staging, "compiled");
            string animationDirectory = Path.Combine(staging, "animations");
            Directory.CreateDirectory(looseDirectory);
            Directory.CreateDirectory(compiledDirectory);

            Dl1SourceModelBuildRequest sourceRequest = new()
            {
                Model = request.Model,
                OutputDirectory = looseDirectory,
                ResourceName = resourceName,
                SurfaceName = surfaceName,
                AnimationScriptAlias = alias,
            };
            Dl1SourceModelBuildResult source = await (
                request.SourceWriterOverride?.Invoke(sourceRequest, cancellationToken) ??
                Dl1SourceModelWriter.WriteAsync(sourceRequest, cancellationToken)).ConfigureAwait(false);

            string modelRpackPath = Path.Combine(compiledDirectory, $"{resourceName}_pc.rpack");
            Dl1OfficialModelCompilerRequest compilerRequest = new()
            {
                Model = request.Model,
                CompilerExecutablePath = request.CompilerExecutablePath,
                RetailData0PakPath = request.RetailData0PakPath,
                OutputRpackPath = modelRpackPath,
                ResourceName = resourceName,
                SurfaceName = surfaceName,
                AnimationScriptAlias = alias,
                Timeout = request.CompilerTimeout,
                CharacterId = request.Model.Package.Document.BuildSettings.CharacterId,
            };
            Dl1OfficialModelCompilerResult compiled = await (
                request.ModelCompilerOverride?.Invoke(compilerRequest, cancellationToken) ??
                Dl1OfficialModelCompiler.CompileAsync(compilerRequest, cancellationToken)).ConfigureAwait(false);

            CustomModelAnimationLibraryResult? animations = null;
            if (hasAnimations)
            {
                Directory.CreateDirectory(animationDirectory);
                CustomModelAnimationLibraryRequest animationRequest = new()
                {
                    Model = request.Model,
                    OutputPath = Path.Combine(animationDirectory, $"{alias}_pc.rpack"),
                    Selections = selections,
                    AnimationScriptAlias = alias,
                };
                animations = await (
                    request.AnimationExporterOverride?.Invoke(animationRequest, cancellationToken) ??
                    CustomModelAnimationLibraryExporter.ExportAsync(animationRequest, cancellationToken)).ConfigureAwait(false);
            }

            ValidateStagedOutputs(request.Model, source, compiled, animations, alias, hasAnimations);
            ImmutableDictionary<string, string> hashes = await HashOutputsAsync(
                staging,
                cancellationToken).ConfigureAwait(false);
            string manifestPath = Path.Combine(staging, "dl1-model-package.json");
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    format = "dl-reanimated-csharp-dl1-model-package",
                    schemaVersion = 1,
                    resourceName,
                    surfaceName,
                    animationScriptAlias = alias,
                    referenceExistingAnimationLibrary = stockReference,
                    stockAnimationReference = stockBank,
                    runtimeAnimationBindingVerified = false,
                    nativeCompanionNotes = source.NativeCompanionNotes,
                    input = new
                    {
                        request.Model.Package.Document.ModelId,
                        request.Model.Package.Document.Source.ContentSha256,
                        request.Model.Package.Document.RigSignature,
                    },
                    outputs = hashes.OrderBy(static pair => pair.Key).Select(static pair => new
                    {
                        path = pair.Key,
                        sha256 = pair.Value,
                    }),
                    animations = animations?.AnimationNames ?? [],
                    completedUtc = DateTimeOffset.UtcNow,
                },
                ManifestJsonOptions);
            await File.WriteAllBytesAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            hashes = hashes.SetItem(
                Path.GetRelativePath(staging, manifestPath).Replace('\\', '/'),
                Sha256(manifest));

            cancellationToken.ThrowIfCancellationRequested();
            PublishDirectoryAtomically(staging, target, backup);
            published = true;
            return new Dl1CustomModelPackageResult(
                target,
                Path.Combine(target, Path.GetFileName(manifestPath)),
                Remap(source, staging, target),
                Remap(compiled, staging, target),
                animations is null ? null : Remap(animations, staging, target),
                hashes)
            {
                StockAnimationReference = stockBank,
            };
        }
        finally
        {
            if (!published && Directory.Exists(staging))
            {
                TryDeleteOwnedDirectory(staging);
            }

            if (published && Directory.Exists(backup))
            {
                TryDeleteOwnedDirectory(backup);
            }
        }
    }

    private static void ValidateRequest(Dl1CustomModelPackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        request.Model.Package.Document.Validate();
        if (!request.Model.Package.Document.Bones.IsEmpty)
        {
            _ = request.Model.Package.Document
                .CreateDl1AnimationRigDefinition();
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentOutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CompilerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurfaceName);
        if (request.ModelCompilerOverride is null && !File.Exists(request.CompilerExecutablePath))
        {
            throw new FileNotFoundException(
                "The Dying Light Developer Tools compiler was not found.",
                request.CompilerExecutablePath);
        }
    }

    private static void ValidateStagedOutputs(
        FbxModelAuthoringImportResult model,
        Dl1SourceModelBuildResult source,
        Dl1OfficialModelCompilerResult compiled,
        CustomModelAnimationLibraryResult? animations,
        string? alias,
        bool hasAnimations)
    {
        RequireNonEmpty(source.SourceMshPath, "Chrome source .msh");
        RequireNonEmpty(source.BoneScriptPath, "bone script .bscr");
        if (!string.IsNullOrWhiteSpace(alias))
        {
            string animationScriptPath = source.AnimationScriptPath
                ?? throw new InvalidDataException("The staged animation alias .ascr is missing.");
            RequireNonEmpty(animationScriptPath, "animation alias .ascr");
            string expectedDeclaration =
                $"AnimScriptAlias(\"{Dl1SourceModelWriter.AnimationScriptFileName(alias)}\")";
            string declaration = File.ReadAllText(animationScriptPath).Trim();
            if (!string.Equals(declaration, expectedDeclaration, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The staged animation alias .ascr must declare exactly '{expectedDeclaration}'.");
            }
        }

        RequireNonEmpty(source.CharacterDefinitionPath, "DL1 character definition .chr");
        foreach (string name in source.NativeCompanionFiles)
            RequireNonEmpty(Path.Combine(Path.GetDirectoryName(source.SourceMshPath)!, name), "native companion " + name);
        foreach (string path in compiled.NativeCompanionPaths) RequireNonEmpty(path, "compiled-model native companion");

        RequireNonEmpty(compiled.OutputRpackPath, "compiled model RPack");
        RequireNonEmpty(compiled.CompiledMeshObjectPath, "compiled mesh object");
        bool hasLocallyAuthoredMaterials = model.Package.Document.Materials.IsEmpty ||
            model.Package.Document.Materials.Any(static material =>
                string.IsNullOrWhiteSpace(material.ExistingDl1MaterialReference));
        if (hasLocallyAuthoredMaterials)
        {
            RequireNonEmpty(compiled.MaterialDatabasePath, "compiled local_dx11.mp material database");
        }

        if (hasAnimations)
        {
            if (animations is null || animations.AnimationNames.IsEmpty)
            {
                throw new InvalidDataException(
                    "Selected animation stacks produced no animation-library resources.");
            }

            RequireNonEmpty(animations.OutputPath, "animation RPack");
            RequireNonEmpty(animations.ManifestPath, "animation manifest");
            if (!string.Equals(animations.AnimationScriptName, alias, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The staged animation RPack does not use the requested extensionless type-322 resource identity.");
            }
        }
    }

    private static void RequireNonEmpty(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidDataException($"The staged {description} is missing or empty.");
        }
    }

    private static async Task<ImmutableDictionary<string, string>> HashOutputsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(static path => !string.Equals(Path.GetFileName(path), OwnershipMarker, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            builder.Add(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                Convert.ToHexString(hash).ToLowerInvariant());
        }

        return builder.ToImmutable();
    }

    private static void PublishDirectoryAtomically(string staging, string target, string backup)
    {
        bool movedPrevious = false;
        if (Directory.Exists(target))
        {
            EnsureOwnedPackage(target);
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
                try
                {
                    Directory.Move(backup, target);
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Publishing the new DL1 package failed, and restoring the previous package also failed. " +
                        $"The previous validated package remains preserved at '{backup}'.",
                        rollbackException);
                }
            }

            throw;
        }

    }

    private static void EnsureOwnedPackage(string directory)
    {
        if (!File.Exists(Path.Combine(directory, OwnershipMarker)))
        {
            throw new IOException(
                $"Refusing to replace '{directory}' because it is not an owned DL ReAnimated package directory.");
        }
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        EnsureOwnedPackage(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static void TryDeleteOwnedDirectory(string directory)
    {
        try
        {
            DeleteOwnedDirectory(directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void EnsureOwnedChild(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new IOException("The package path escaped its selected output parent.");
        }
    }

    private static Dl1SourceModelBuildResult Remap(
        Dl1SourceModelBuildResult value,
        string oldRoot,
        string newRoot) => value with
        {
            SourceMshPath = Remap(value.SourceMshPath, oldRoot, newRoot),
            CharacterDefinitionPath = Remap(value.CharacterDefinitionPath, oldRoot, newRoot),
            BoneScriptPath = Remap(value.BoneScriptPath, oldRoot, newRoot),
            AnimationScriptPath = RemapOptional(value.AnimationScriptPath, oldRoot, newRoot),
            ManifestPath = Remap(value.ManifestPath, oldRoot, newRoot),
            BlockedOutputsPath = Remap(value.BlockedOutputsPath, oldRoot, newRoot),
        };

    private static Dl1OfficialModelCompilerResult Remap(
        Dl1OfficialModelCompilerResult value,
        string oldRoot,
        string newRoot) => value with
        {
            OutputRpackPath = Remap(value.OutputRpackPath, oldRoot, newRoot),
            CompiledMeshObjectPath = Remap(value.CompiledMeshObjectPath, oldRoot, newRoot),
            MaterialDatabasePath = RemapOptional(value.MaterialDatabasePath, oldRoot, newRoot),
            ReceiptPath = Remap(value.ReceiptPath, oldRoot, newRoot),
            RawCompiledMeshObjectPath = RemapOptional(value.RawCompiledMeshObjectPath, oldRoot, newRoot),
            CompiledTextureObjectPaths = value.CompiledTextureObjectPaths.Select(path => Remap(path, oldRoot, newRoot)).ToImmutableArray(),
            NativeCompanionPaths = value.NativeCompanionPaths.Select(path => Remap(path, oldRoot, newRoot)).ToImmutableArray(),
            DependencySidecars = value.DependencySidecars.Select(sidecar => sidecar with
            {
                Path = Remap(sidecar.Path, oldRoot, newRoot),
            }).ToImmutableArray(),
        };

    private static CustomModelAnimationLibraryResult Remap(
        CustomModelAnimationLibraryResult value,
        string oldRoot,
        string newRoot) => value with
        {
            OutputPath = Remap(value.OutputPath, oldRoot, newRoot),
            ManifestPath = Remap(value.ManifestPath, oldRoot, newRoot),
        };

    private static string Remap(string value, string oldRoot, string newRoot) =>
        Path.Combine(newRoot, Path.GetRelativePath(oldRoot, value));

    private static string? RemapOptional(string? value, string oldRoot, string newRoot) =>
        value is null ? null : Remap(value, oldRoot, newRoot);

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
