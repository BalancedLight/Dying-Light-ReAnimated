using System.Collections.Immutable;
using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;
using CoreVector3 = ReAnimated.Core.Mathematics.Vector3D;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1OfficialModelCompilerRequest
{
    public required FbxModelAuthoringImportResult Model { get; init; }

    public required string CompilerExecutablePath { get; init; }

    /// <summary>
    /// Retail DL1 Data0.pak supplies the varlist include graph omitted by the
    /// standalone Developer Tools installation. Only the bounded compiler
    /// bootstrap entries are staged; no retail asset is published.
    /// </summary>
    public string? RetailData0PakPath { get; init; }

    public required string OutputRpackPath { get; init; }

    public required string ResourceName { get; init; }

    /// <summary>
    /// One safe Developer Tools directory component under data/characters.
    /// When omitted, the resource name is used for backward compatibility.
    /// </summary>
    public string? CharacterId { get; init; }

    public string SurfaceName { get; init; } = "default";

    public string? AnimationScriptAlias { get; init; }

    /// <summary>
    /// Validated loose animation source graph staged into the isolated model
    /// compiler project before the mesh is compiled. This allows genuine
    /// compiler dependency sidecars to observe the same alias target and ANM2
    /// files that the deployment transaction will publish.
    /// </summary>
    public PreparedCustomModelAnimationLibrary? AnimationLibrary { get; init; }

    /// <summary>
    /// Optional existing Developer Tools material database. When supplied it
    /// is staged into the isolated compiler project and updated in place by
    /// Techland's material compiler; every pre-existing material record must
    /// survive validation before the result is published.
    /// </summary>
    public string? ExistingMaterialDatabasePath { get; init; }

    /// <summary>
    /// Project root containing the source .dmt files for the existing material
    /// database. A full staged rebuild can then preserve its inventory while
    /// adding the model's materials; the project itself is never compiled in place.
    /// </summary>
    public string? ExistingMaterialSourceRoot { get; init; }

    /// <summary>Verify unchanged current materials for a geometry-only update without running the material compiler or publishing shared databases.</summary>
    public bool ReuseVerifiedExistingMaterials { get; init; }
    /// <summary>Offline diagnostic control only; incomplete dependencies remain blocked for deployment.</summary>
    public bool AllowIncompleteCharacterDiagnostics { get; init; }
    /// <summary>Current ordinary mesh whose material layout, skin and morph features must be preserved.</summary>
    public string? ExistingCompiledMeshObjectPath { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Optional private staging root for isolated compiler jobs; no machine path is embedded in the project.</summary>
    public string? WorkingDirectoryRoot { get; init; }
}

public sealed record Dl1OfficialModelCompilerResult(
    string ResourceName,
    string OutputRpackPath,
    string CompiledMeshObjectPath,
    string? MaterialDatabasePath,
    string ReceiptPath,
    string CompilerFingerprint,
    ImmutableArray<string> ResourceNames,
    CustomModelBuildReceipt BuildReceipt,
    string CompilerLog)
{
    public ImmutableArray<string> Warnings { get; init; } = [];

    public ImmutableArray<string> CompiledTextureObjectPaths { get; init; } = [];
    public ImmutableDictionary<string,string> DetachedMeshObjectPaths {get;init;}=ImmutableDictionary<string,string>.Empty;

    /// <summary>Native reference/debug database companions required for subsequent SDK updates.</summary>
    public ImmutableArray<string> MaterialDatabaseCompanionPaths { get; init; } = [];
    /// <summary>Exact current material inputs that must remain unchanged through publication.</summary>
    public ImmutableDictionary<string, string> VerifiedExistingMaterialFileHashes { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Loose FED/MPCloth and PHX sources accompanying this compiled model.</summary>
    public ImmutableArray<string> NativeCompanionPaths { get; init; } = [];

    /// <summary>Unlinked official compiler output, retained for diagnostics and never deployed as a runtime object.</summary>
    public string? RawCompiledMeshObjectPath { get; init; }

    public ImmutableArray<Dl1OfficialCompilerDependencySidecar> DependencySidecars { get; init; } = [];

    /// <summary>
    /// Typed evidence produced only after the official compiler RPack has
    /// completed its bounded compact-mesh round trip. Portable packaging uses
    /// this instead of guessing at opaque mesh bytes.
    /// </summary>
    public required Dl1OfficialModelCompilerEvidence CompilerEvidence
    { get; init; }
}

public sealed record Dl1OfficialModelCompilerEvidence
{
    public required string OutputRpackSha256 { get; init; }

    public required string CompilerFingerprint { get; init; }

    public required string ToolFingerprint { get; init; }

    public required string BuildReceiptInputFingerprint { get; init; }

    public required string BuildReceiptOutputManifestFingerprint { get; init; }

    public required CustomModelBuildState BuildState { get; init; }

    public required int VerifiedMorphChannelCount { get; init; }

    public required int VerifiedMorphBindingCount { get; init; }

    public string? SkinningReadBackContractFingerprint { get; init; }

    public int VerifiedSkinningSurfaceCount { get; init; }

    public int VerifiedSkinningSubsetCount { get; init; }

    public int VerifiedSkinningVertexCount { get; init; }

    public int VerifiedSkinningInfluenceCount { get; init; }

    public ImmutableArray<Dl1CompiledSkinSubsetMaterialReadBack> SkinningMaterialReadBack { get; init; } = [];
    public Dl1CompiledSkinValidationEvidence? SkinDefinitionReadBack { get; init; }

    public Dl1CompiledChrIdentityReadBackEvidence? ChrIdentityReadBack { get; init; }

    public Dl1PreparedPhysicalNodeReadBackEvidence? PreparedPhysicalNodeReadBack { get; init; }

    public required CompiledMorphDeltaFormat? MorphDeltaFormat { get; init; }
    public ImmutableArray<Dl1BoneScriptReadBack> BoneScriptReadBack { get; init; } = [];
    public ImmutableArray<Dl1CompiledRigNodeReadBack> RigReadBack { get; init; } = [];
    public int ShadingVerticesVerified { get; init; }
    public CharacterCompiledResourceReadback? CharacterResourceReadback { get; init; }
    public CharacterMaterialPublicationReadback? CharacterMaterialReadback { get; init; }
}

public sealed record Dl1OfficialAnimationCompilerRequest
{
    public required PreparedCustomModelAnimationLibrary Library { get; init; }

    public required string CompilerExecutablePath { get; init; }

    public required string RetailData0PakPath { get; init; }

    public required string OutputDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record Dl1OfficialAnimationCompilerResult(
    ImmutableDictionary<string, string> CompiledObjectPaths,
    string CompilerFingerprint,
    string CompilerLog)
{
    public ImmutableArray<Dl1OfficialCompilerDependencySidecar> DependencySidecars { get; init; } = [];
}

/// <summary>
/// Isolated bridge to Techland's installed Dying Light Developer Tools mesh
/// compiler. The bridge owns a unique temporary workshop, never writes to an
/// installed game or an existing Developer Tools project, and publishes output
/// only after the resulting RP6L contains the requested type-272 mesh and all
/// custom texture dependencies authored for that mesh. Techland's material
/// compiler emits the companion <c>local_dx11.mp</c> database separately; it
/// is validated and published atomically with the RPack.
/// </summary>
public static class Dl1OfficialModelCompiler
{
    public const string MaterialExportWarning =
        "Materials couldn't be exported, you may need to assign your own inside of Developer Tools!";
    private const string ToolContractIdentity =
        "dl-reanimated-csharp-model-compiler-chr-skin-material-serialization-physical-node-all-lod-readback-prepared-surface-identity-material-byte-residual-attributed-triangles-sdk-material-graph-padding-character-full-native-effects-textures-material-publication-opaque-union-runtime-companions-detached-mesh-material-instances-preset-source-native-loader-reviews-chunk-grouping-v44";
    private const int MaximumCompilerLogCharacters = 4 * 1024 * 1024;
    private const long MaximumBootstrapEntryBytes = 16L * 1024L * 1024L;
    private const long MaximumBootstrapTotalBytes = 64L * 1024L * 1024L;
    private const int WindowsAccessViolationExitCode = unchecked((int)0xC0000005);
    private const int WindowsIllegalInstructionExitCode = unchecked((int)0xC000001D);
    private const string CompilerEnvironmentVariable = "DLR_DL1_RESPACK_COMPILER";

    private static readonly string[] UnsupportedCompiledOutputs = [".skn"];
    private static readonly string[] SpeechLabels = ["open", "W", "ShCh", "PBM", "FV", "wide", "tBack", "tRoof", "tTeeth"];

    private static readonly (string Target, string[] Sources)[] BootstrapFiles =
    [
        ("data/enginedefs.mth", ["Shaders/Common/EngineDefs.mth", "EngineDefs.mth"]),
        ("data/resourcepackcfg.scr", ["ResourcePackCfg.scr"]),
        ("data/varlist_descriptions.scr", ["varlist_descriptions.scr"]),
        ("data/varlist_descriptions_game.scr", ["varlist_descriptions_game.scr"]),
        ("data/quickaccessvarsdefault.scr", ["QuickAccessVarsDefault.scr"]),
        ("data/map_creator_varlist.scr", ["map_creator_varlist.scr"]),
        ("data/defaultrespackcompiler.rules", ["DefaultResPackCompiler.rules"]),
        ("data/resourcepackfolders.scr", ["ResourcePackFolders.scr"]),
    ];

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Identifies the complete source-writer and compiler-validation contract
    /// represented by a model-build receipt. Changing CHR, MSH, material,
    /// animation-alias, or archive-validation semantics must change the
    /// contract identity so older receipts fail closed when reopened.
    /// </summary>
    public static string CurrentToolFingerprint { get; } =
        Sha256(Encoding.UTF8.GetBytes(ToolContractIdentity));

    public static bool IsCurrentBuildReceipt(
        CustomModelBuildReceipt? receipt,
        FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (receipt is not
            {
                State: CustomModelBuildState.CompilerValidated,
            } ||
            !string.Equals(
                receipt.ToolFingerprint,
                CurrentToolFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CustomModelBuildSettings settings = model.Package.Document.BuildSettings;
        string expectedInputFingerprint = CalculateInputFingerprint(
            model,
            settings.ResourceName,
            settings.SurfaceName,
            settings.AnimationScriptAlias);
        return string.Equals(
            receipt.InputFingerprint,
            expectedInputFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static ImmutableArray<Dl1CompiledRigNodeReadBack> ValidatePreparedRigReadBack(
        Dl1SourceModelBuildResult sourceBuild,
        CompactMeshDocument hierarchy)
    {
        ArgumentNullException.ThrowIfNull(sourceBuild);
        ArgumentNullException.ThrowIfNull(hierarchy);
        if (sourceBuild.AuthoredRigContract is { } contract)
            return Dl1CompiledRigValidator.Validate(contract, hierarchy);
        if (!sourceBuild.BoneScriptPolicies.IsDefault)
            throw new InvalidDataException("Studio compilation requires the source writer's prepared contract.");
        return [];
    }

    public static string CalculateInputFingerprint(
        FbxModelAuthoringImportResult model,
        string resourceName,
        string surfaceName,
        string? animationScriptAlias)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Package.Document.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceName);
        string canonicalResourceName = Dl1SourceModelWriter.SanitizeName(resourceName, 55);
        string canonicalSurfaceName = Dl1SourceModelWriter.SanitizeName(surfaceName, 63);
        // Navigation, review history and job epochs do not change emitted resources.
        // Keep all source bytes, authored layers, recipes, masks and other build decisions in the hash.
        CustomModelPackage fingerprintPackage = model.Package with {
            Document = model.Package.Document with { LastBuildReceipt = null,
                RiggingSession = model.Package.Document.RiggingSession is { } session ? session with {
                    Stage = RigStudioStage.Import, Generation = session.Id, Revision = 0,
                    Stages = Enum.GetValues<RigStudioStage>().Select(static stage => new RigStageState { Stage = stage }).ToImmutableArray(),
                    ValidationHistory = [],
                } : null,
            },
        };
        ImmutableArray<byte> packageBytes =
            CustomModelPackageSerializer.Serialize(fingerprintPackage);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("dl-reanimated-model-compiler-input-v3\0"u8);
        hash.AppendData(packageBytes.AsSpan());
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"\0resource={canonicalResourceName}\0surface={canonicalSurfaceName}\0animationAlias={animationScriptAlias?.Trim() ?? string.Empty}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string? FindDefaultCompilerExecutable()
    {
        var candidates = new List<string>();
        string? configured = Environment.GetEnvironmentVariable(CompilerEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured.Trim().Trim('"'));
        }

        AddProgramFilesCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddProgramFilesCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        for (char drive = 'C'; drive <= 'Z'; drive++)
        {
            candidates.Add($@"{drive}:\SteamLibrary\steamapps\common\Dying Light Developer Tools\ResPackCompilerConsole_x64_rwdi.exe");
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    public static async Task<Dl1OfficialModelCompilerResult> CompileAsync(
        Dl1OfficialModelCompilerRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        string compilerPath = Path.GetFullPath(request.CompilerExecutablePath);
        string materialCompilerPath = ResolveMaterialCompilerExecutable(compilerPath);
        string outputRpackPath = Path.GetFullPath(request.OutputRpackPath);
        string resourceName = Dl1SourceModelWriter.SanitizeName(request.ResourceName, 55);
        string characterId = Dl1DeveloperToolsProjectDeployer.NormalizeCharacterId(
            string.IsNullOrWhiteSpace(request.CharacterId) ? resourceName : request.CharacterId);
        string compilerFingerprint = await Sha256FileAsync(compilerPath, cancellationToken).ConfigureAwait(false);
        string materialCompilerFingerprint = await Sha256FileAsync(
            materialCompilerPath,
            cancellationToken).ConfigureAwait(false);
        // Techland's resource compiler can fault in deep per-user staging trees
        // after emitting otherwise valid mesh and texture objects. Keep the
        // default job root compact; an explicit root still wins for callers
        // that need a different private staging location.
        string jobContainer = request.WorkingDirectoryRoot is { } workingRoot ? Path.GetFullPath(workingRoot) : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLRMC");
        string jobId = Guid.NewGuid().ToString("N");
        string jobDirectory = Path.Combine(jobContainer, jobId);
        string workshopDirectory = Path.Combine(jobDirectory, "Workshop");
        // This is a disposable local bootstrap folder and the compiler's -dn
        // value. Keep it short because it prefixes every staged source/output
        // path; public resource and character names remain unchanged.
        string projectName = $"_dlrm_{jobId[..8]}";
        string projectDirectory = Path.Combine(workshopDirectory, projectName);
        string virtualDirectory = $"data/characters/{characterId}";
        string virtualMshPath = $"{virtualDirectory}/{resourceName}.msh";
        string stagedSourceDirectory = Path.Combine(
            projectDirectory,
            virtualDirectory.Replace('/', Path.DirectorySeparatorChar));
        string rsrcPath = Path.Combine(projectDirectory, "model_resources.rsrc");
        string rulesPath = Path.Combine(projectDirectory, "model_resource.rules");
        string outputName = $"{resourceName}_pc.rpack";
        string? ownedSdkMaterialDirectory = null;
        var compilerLog = new StringBuilder();
        var warnings = ImmutableArray.CreateBuilder<string>();
        bool completedSuccessfully = false;

        Directory.CreateDirectory(stagedSourceDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dl1SourceModelBuildResult sourceBuild = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = request.Model,
                    OutputDirectory = stagedSourceDirectory,
                    ResourceName = resourceName,
                    SurfaceName = request.SurfaceName,
                    AnimationScriptAlias = request.AnimationScriptAlias,
                    AllowIncompleteCharacterDiagnostics=request.AllowIncompleteCharacterDiagnostics,
                },
                cancellationToken).ConfigureAwait(false);

            ImmutableArray<CompilerResourceUnit> units = CreateCompilerResourceUnits(
                resourceName,
                virtualMshPath,
                virtualDirectory,
                sourceBuild.TextureSourceFiles,
                jobDirectory);
            ValidateNativeCompilerPaths(units.SelectMany(unit => new[]
            {
                Path.Combine(projectDirectory, unit.VirtualSourcePath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(unit.OutputDirectory, unit.ExpectedObjectFileName + "_dep"),
            }).Append(rulesPath).Append(rsrcPath));

            if(sourceBuild.SkinDefinitionSourcePath is { } skinSourcePath)
            {
                string destination=Path.Combine(projectDirectory,virtualDirectory.Replace('/',Path.DirectorySeparatorChar),resourceName+".skn");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if(!Path.GetFullPath(skinSourcePath).Equals(Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)) File.Copy(skinSourcePath,destination);
            }
            warnings.AddRange(sourceBuild.NativeCompanionNotes);
            warnings.AddRange(sourceBuild.CapabilityDiagnostics.Select(d => "Capability profile: " + d.Message + " " + d.CorrectiveOperation));
            foreach (string name in sourceBuild.NativeCompanionFiles)
            {
                string relative=name.StartsWith("character-resources/",StringComparison.Ordinal)?Dl1NativeCompanionWriter.PreservedVirtualPath(name):
                    name.EndsWith(".phx",StringComparison.OrdinalIgnoreCase)?$"data/odephysics/meshpartcloth/{name}":$"{virtualDirectory}/{name}";
                if(!relative.StartsWith("data/",StringComparison.Ordinal) || relative.Split('/').Any(p=>p is "." or "..") || relative.Contains(':'))
                    throw new InvalidDataException("Native companion virtual path is not portable.");
                string destination=Path.Combine(projectDirectory,relative.Replace('/',Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string originalPath=Path.Combine(stagedSourceDirectory,name);
                if(!Path.GetFullPath(originalPath).Equals(Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)) File.Copy(originalPath,destination);
            }

            if (request.AnimationLibrary is not null)
            {
                await StageModelAnimationSourcesAsync(
                    request,
                    projectDirectory,
                    cancellationToken).ConfigureAwait(false);
            }

            string devToolsData = ResolveDeveloperToolsDataDirectory(compilerPath);
            CopyCompilerBootstrap(devToolsData, projectDirectory);
            await CopyRetailCompilerBootstrapAsync(
                request.RetailData0PakPath!,
                projectDirectory,
                cancellationToken).ConfigureAwait(false);
            string dataDirectory = Path.Combine(projectDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "resourcepackfolders.scr"),
                CreateResourcePackFoldersScript(),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "common_ovr.scr"),
                CreateCommonOverrideScript(),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "common_ovr.scr"),
                CreateCommonOverrideScript(),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                rsrcPath,
                CreateResourceScript(
                    resourceName,
                    virtualMshPath,
                    virtualDirectory,
                    sourceBuild.CustomMaterialReferences,
                    sourceBuild.TextureSourceFiles),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                rulesPath,
                CreateResourceRules(),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            string[] locallyAuthoredMaterialReferences = sourceBuild.CustomMaterialReferences
                .Where(reference => File.Exists(Path.Combine(
                    stagedSourceDirectory,
                    $"{Path.GetFileNameWithoutExtension(reference)}.dmt")))
                .ToArray();
            ImmutableDictionary<string, string> reusedMaterialFiles = ImmutableDictionary<string, string>.Empty;
            CompiledMeshGeometryDocument? previousMaterialGeometry = null;
            if (request.ReuseVerifiedExistingMaterials)
            {
                if (request.ExistingMaterialDatabasePath is null || request.ExistingMaterialSourceRoot is null || request.ExistingCompiledMeshObjectPath is null)
                    throw new InvalidDataException("Verified material reuse requires the current database, material source root, and compiled mesh.");
                reusedMaterialFiles = ValidateMaterialReuseSources(stagedSourceDirectory,
                    Path.Combine(request.ExistingMaterialSourceRoot, "data", "characters", characterId),
                    sourceBuild.CustomMaterialReferences, sourceBuild.TextureSourceFiles, request.ExistingMaterialDatabasePath);
                string priorObject = Path.GetFullPath(request.ExistingCompiledMeshObjectPath);
                reusedMaterialFiles = reusedMaterialFiles.Add(priorObject, await Sha256FileAsync(priorObject, cancellationToken).ConfigureAwait(false));
                previousMaterialGeometry = await ReadMaterialReuseGeometryAsync(priorObject, resourceName,
                    Path.Combine(jobDirectory, "PriorMaterialCache"), cancellationToken).ConfigureAwait(false);
            }
            string? fullMaterialBuildDirectory = null;
            if (!request.ReuseVerifiedExistingMaterials && locallyAuthoredMaterialReferences.Length > 0 &&
                !string.IsNullOrWhiteSpace(request.ExistingMaterialSourceRoot))
            {
                fullMaterialBuildDirectory = "data/characters/_dlr_material_build";
                int stagedMaterialCount = await StageCompleteMaterialSourcesAsync(
                    request.ExistingMaterialSourceRoot,
                    stagedSourceDirectory,
                    Path.Combine(projectDirectory, fullMaterialBuildDirectory.Replace('/', Path.DirectorySeparatorChar)),
                    cancellationToken,
                    stockMaterialRoot: devToolsData).ConfigureAwait(false);
                AppendBounded(compilerLog, $"Full material rebuild staged {stagedMaterialCount} source .dmt files.\r\n");
            }
            string? existingMaterialDatabasePath = string.IsNullOrWhiteSpace(
                request.ExistingMaterialDatabasePath)
                ? null
                : Path.GetFullPath(request.ExistingMaterialDatabasePath);
            string? compiledMaterialDatabase = null;
            if (existingMaterialDatabasePath is not null && fullMaterialBuildDirectory is null)
            {
                if (!File.Exists(existingMaterialDatabasePath))
                {
                    throw new FileNotFoundException(
                        "The requested base local_dx11.mp material database was not found.",
                        existingMaterialDatabasePath);
                }

                compiledMaterialDatabase = Path.Combine(
                    projectDirectory,
                    "Assets_PC",
                    "local_dx11.mp");
                Directory.CreateDirectory(Path.GetDirectoryName(compiledMaterialDatabase)!);
                File.Copy(existingMaterialDatabasePath, compiledMaterialDatabase, overwrite: true);
                if (!request.ReuseVerifiedExistingMaterials && locallyAuthoredMaterialReferences.Length>0)
                {
                // The SDK update initializer routes its database to the tool installation's
                // game folder even when source -gamedir is isolated. Own a fresh output folder,
                // stage the complete native graph there, then copy validated products back.
                string sdkRoot = Path.GetDirectoryName(materialCompilerPath)!;
                string sdkDirectory = Path.GetFullPath(Path.Combine(sdkRoot, projectName));
                if (!string.Equals(Path.GetDirectoryName(sdkDirectory), Path.GetFullPath(sdkRoot), StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(sdkDirectory) || File.Exists(sdkDirectory))
                    throw new InvalidDataException("The fresh SDK material output directory is not available.");
                Directory.CreateDirectory(Path.Combine(sdkDirectory, "assets_pc"));
                ownedSdkMaterialDirectory = sdkDirectory;
                string snapshotDirectory = Path.GetDirectoryName(existingMaterialDatabasePath)!;
                foreach (string name in MaterialDatabaseFileNames)
                {
                    string input = name == "local_dx11.mp" ? existingMaterialDatabasePath : Path.Combine(snapshotDirectory, name);
                    if (!File.Exists(input))
                    {
                        if (name is "local_dx11.mp" or "local_dx11_refs.mp" or "local_dx11_debug.mp")
                            throw new InvalidDataException("Incremental material updates require the matching native main, reference, and debug databases.");
                        continue;
                    }
                    File.Copy(input, Path.Combine(sdkDirectory, "assets_pc", name));
                }
                }

            }

            if (!request.ReuseVerifiedExistingMaterials && locallyAuthoredMaterialReferences.Length > 0)
            {
                try
                {
                    AppendBounded(compilerLog, "Material compiler stage\r\n");
                    string sharedTemplateOutput = Path.Combine(
                        Path.GetDirectoryName(materialCompilerPath)
                            ?? throw new InvalidOperationException("The material compiler path has no parent directory."),
                        "MeConvTemplates_dx11.dll");
                    ProcessResult materialProcess = await Dl1MaterialCompilerSerializationGate.RunAsync(
                        sharedTemplateOutput,
                        request.Timeout,
                        (stageTimeout, stageCancellationToken) => RunCompilerStageAsync(
                            materialCompilerPath,
                            CreateMaterialCompilerCommand(
                                projectDirectory,
                                workshopDirectory,
                                fullMaterialBuildDirectory ?? virtualDirectory,
                                forceRebuild: fullMaterialBuildDirectory is not null || existingMaterialDatabasePath is null),
                            projectDirectory,
                            stageTimeout,
                            stageCancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    AppendBounded(compilerLog, materialProcess.Output);
                    if (materialProcess.ExitCode != 0)
                    {
                        throw new InvalidDataException(
                            $"Techland material compiler exited with code {materialProcess.ExitCode}.");
                    }

                    compiledMaterialDatabase = Path.Combine(
                        projectDirectory,
                        "Assets_PC",
                        "local_dx11.mp");
                    if (ownedSdkMaterialDirectory is not null)
                    {
                        foreach (string name in MaterialDatabaseFileNames)
                        {
                            string emitted = Path.Combine(ownedSdkMaterialDirectory, "assets_pc", name);
                            if (File.Exists(emitted))
                                File.Copy(emitted, Path.Combine(projectDirectory, "Assets_PC", name), overwrite: true);
                        }
                    }
                }
                catch (Exception exception) when (
                    fullMaterialBuildDirectory is null &&
                    (exception is InvalidDataException or IOException or
                    UnauthorizedAccessException or TimeoutException or
                    Win32Exception or NotSupportedException))
                {
                    warnings.Add(MaterialExportWarning);
                    AppendBounded(
                        compilerLog,
                        $"Material export was recoverable: {exception.Message}\r\n");
                    if (existingMaterialDatabasePath is null)
                    {
                        compiledMaterialDatabase = null;
                    }
                    else
                    {
                        compiledMaterialDatabase = Path.Combine(
                            projectDirectory,
                            "Assets_PC",
                            "local_dx11.mp");
                        File.Copy(
                            existingMaterialDatabasePath,
                            compiledMaterialDatabase,
                            overwrite: true);
                    }
                }
            }

            if (locallyAuthoredMaterialReferences.Length > 0)
            {
                if (compiledMaterialDatabase is null)
                {
                    throw new InvalidDataException(
                        "The model has custom materials but no compiled local_dx11.mp database was produced.");
                }

                ValidateCompiledMaterialDatabase(
                    compiledMaterialDatabase,
                    locallyAuthoredMaterialReferences,
                    sourceBuild.TextureSourceFiles);
                if (existingMaterialDatabasePath is not null)
                {
                    ValidateCompiledMaterialDatabasePreserves(
                        existingMaterialDatabasePath,
                        compiledMaterialDatabase);
                }
            }

            string? compiledMaterialCompanionDirectory=compiledMaterialDatabase is null?null:Path.GetDirectoryName(compiledMaterialDatabase);
            CharacterMaterialPublicationReadback? characterMaterialReadback=null;
            bool hasCharacterMaterials=request.Model.Package.Document.CharacterResources?.Resources.Any(resource=>
                resource.Required && !resource.IsOriginalArchive && resource.Material is not null)==true;
            if(hasCharacterMaterials)
            {
                if (compiledMaterialDatabase is null && existingMaterialDatabasePath is null && locallyAuthoredMaterialReferences.Length == 0)
                {
                    var inventory = request.Model.Package.Document.CharacterResources!;
                    string providerId = inventory.Resources.Where(resource => resource.Required && !resource.IsOriginalArchive && resource.Material is not null)
                        .Select(resource => resource.Material!.ProviderResourceId).Order(StringComparer.Ordinal).First();
                    var provider = inventory.Resources.Single(resource => resource.Id == providerId);
                    if (provider.EntryPath is null || !request.Model.Package.CompanionPayloads.TryGetValue(provider.EntryPath, out var providerBytes))
                        throw new InvalidDataException("The original character material provider payload is missing.");
                    _ = CustomModelPackageSerializer.Serialize(request.Model.Package);
                    compiledMaterialDatabase = Path.Combine(jobDirectory, "CharacterMaterialBase", "local_dx11.mp");
                    Directory.CreateDirectory(Path.GetDirectoryName(compiledMaterialDatabase)!);
                    await File.WriteAllBytesAsync(compiledMaterialDatabase, providerBytes.ToArray(), cancellationToken).ConfigureAwait(false);
                    compiledMaterialCompanionDirectory = Path.GetDirectoryName(compiledMaterialDatabase);
                    AppendBounded(compilerLog, "Original character material provider staged.\r\n");
                }
                if(compiledMaterialDatabase is null)
                    throw new InvalidDataException("The character requires a published material database containing its retained materials.");
                var selectedMaterialGraph=ImmutableArray.Create(await File.ReadAllBytesAsync(compiledMaterialDatabase,cancellationToken).ConfigureAwait(false));
                var mergedCharacterMaterials=CharacterMaterialPublication.Merge(request.Model.Package,selectedMaterialGraph,cancellationToken);
                if(!mergedCharacterMaterials.Database.AsSpan().SequenceEqual(selectedMaterialGraph.AsSpan()))
                {
                    if(request.ReuseVerifiedExistingMaterials)
                        throw new InvalidDataException("Verified material reuse cannot add character material graph records.");
                    string mergedPath=Path.Combine(jobDirectory,"CharacterMaterials","local_dx11.mp");
                    Directory.CreateDirectory(Path.GetDirectoryName(mergedPath)!);
                    string temporaryMaterialPath=mergedPath+$".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        await using(var materialOutput=new FileStream(temporaryMaterialPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,128*1024,FileOptions.Asynchronous|FileOptions.WriteThrough))
                        {await materialOutput.WriteAsync(mergedCharacterMaterials.Database.AsMemory(),cancellationToken).ConfigureAwait(false);await materialOutput.FlushAsync(cancellationToken).ConfigureAwait(false);}
                        CharacterMaterialPublication.Verify(request.Model.Package,ImmutableArray.Create(await File.ReadAllBytesAsync(temporaryMaterialPath,cancellationToken).ConfigureAwait(false)),cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();File.Move(temporaryMaterialPath,mergedPath,overwrite:false);
                    }
                    finally{if(File.Exists(temporaryMaterialPath))File.Delete(temporaryMaterialPath);}
                    ValidateCompiledMaterialDatabasePreserves(compiledMaterialDatabase,mergedPath);
                    compiledMaterialDatabase=mergedPath;
                }
                characterMaterialReadback=mergedCharacterMaterials.Readback;
            }
            var compiledObjects = ImmutableArray.CreateBuilder<string>(units.Length);
            for (int stage = 0; stage < units.Length; stage++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompilerResourceUnit unit = units[stage];
                Directory.CreateDirectory(unit.OutputDirectory);
                AppendBounded(
                    compilerLog,
                    $"Resource compiler stage {stage + 1}/{units.Length}: {unit.VirtualSourcePath}\r\n");
                ProcessResult process = await RunCompilerStageAsync(
                    compilerPath,
                    CreateCompilerCommand(
                        projectName,
                        workshopDirectory,
                        unit.OutputDirectory,
                        rulesPath,
                        rsrcPath,
                        unit.VirtualSourcePath,
                        outputName),
                    projectDirectory,
                    request.Timeout,
                    cancellationToken).ConfigureAwait(false);
                AppendBounded(compilerLog, process.Output);
                string? emittedObject = TryFindCompilerOutput(
                    unit.OutputDirectory,
                    unit.ExpectedObjectFileName);
                if (process.ExitCode != 0)
                {
                    if ((process.ExitCode == WindowsAccessViolationExitCode ||
                         process.ExitCode == WindowsIllegalInstructionExitCode) &&
                        emittedObject is not null)
                    {
                        string exceptionName = process.ExitCode == WindowsAccessViolationExitCode
                            ? "access violation 0xC0000005"
                            : "illegal instruction 0xC000001D";
                        AppendBounded(
                            compilerLog,
                            $"Techland compiler exited with {exceptionName} after emitting {Path.GetFileName(emittedObject)}. " +
                            "The isolated object will be admitted only if bounded linking and ordinary RP6L validation both pass.\r\n");
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"Techland model compiler stage {stage + 1} exited with code {process.ExitCode}. " +
                            (emittedObject is null
                                ? $"It did not emit the expected '{unit.ExpectedObjectFileName}'. "
                                : $"It emitted '{unit.ExpectedObjectFileName}', but this exit status is not accepted for publication. ") +
                            "No model bundle was published.");
                    }
                }

                compiledObjects.Add(emittedObject ?? FindCompilerOutput(
                    unit.OutputDirectory,
                    unit.ExpectedObjectFileName));
            }

            string compiledMeshObject = compiledObjects[0];
            await using var characterResourceCache=new Rp6lChunkCache(new(){CacheDirectory=Path.Combine(jobDirectory,"CharacterResourceCache"),MaximumDiskBytes=256L*1024*1024});
            var characterResources=await CharacterCompiledResourceAuthoring.WriteObjectsAsync(request.Model.Package,
                Path.Combine(jobDirectory,"CharacterResources"),characterResourceCache,cancellationToken).ConfigureAwait(false);
            compiledObjects.AddRange(characterResources.ObjectPaths);
            string compiledRpack = Path.Combine(jobDirectory, outputName);
            Rp6lCompilerObjectNormalizationResult normalization =
                await Rp6lCompilerObjectNormalizer.LinkAtomicAsync(
                    compiledObjects,
                    compiledRpack,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            AppendBounded(
                compilerLog,
                $"Linked compiler object into standalone RP6L; normalized {normalization.ConvertedResourceCount} compiler resource type(s).\r\n");
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(
                compiledRpack,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var characterResourceReadback=await CharacterCompiledResourceAuthoring.VerifyLinkedAsync(request.Model.Package,archive,
                characterResourceCache,cancellationToken).ConfigureAwait(false);
            ValidateCompiledTextureDependencies(
                archive.Resources,
                sourceBuild.TextureSourceFiles);
            Rp6lResourceDescriptor[] meshResources = archive.Resources
                .Where(static resource => resource.ResourceType == Rp6lResourceTypes.Mesh)
                .ToArray();
            if (!meshResources.Any(resource =>
                    string.Equals(resource.Name, resourceName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"The compiled RP6L does not contain the requested type-{Rp6lResourceTypes.Mesh} resource '{resourceName}'. " +
                    "No model RPack was published.");
            }

            Rp6lResourceDescriptor requestedMesh = meshResources.Single(resource =>
                string.Equals(resource.Name, resourceName, StringComparison.OrdinalIgnoreCase));
            if (requestedMesh.Items.Count < 5)
            {
                throw new InvalidDataException(
                    $"The compiled mesh '{resourceName}' has unsupported RP6L item layout {requestedMesh.Items.Count}. " +
                    "Custom FBX models must contain compact metadata, variant, resolver, vertex, and index items. " +
                    "No model RPack was published.");
            }

            string verificationCacheDirectory = Path.Combine(jobDirectory, "VerificationCache");
            int verifiedEntityCount;
            int verifiedSurfaceCount;
            int verifiedVertexCount;
            int verifiedShadingVertexCount;
            int verifiedIndexCount;
            int verifiedMorphChannelCount;
            int verifiedMorphBindingCount;
            Dl1CompiledSkinningReadBackEvidence? skinningReadBack = null;
            Dl1CompiledChrIdentityReadBackEvidence? chrIdentityReadBack = null;
            Dl1CompiledSkinValidationEvidence? skinDefinitionReadBack = null;
            Dl1PreparedPhysicalNodeReadBackEvidence? preparedPhysicalNodeReadBack = null;
            ImmutableArray<Dl1BoneScriptReadBack> boneScriptReadBack = [];
            ImmutableArray<Dl1CompiledRigNodeReadBack> rigReadBack = [];
            await using (var verificationCache = new Rp6lChunkCache(
                             new Rp6lChunkCacheOptions
                             {
                                 CacheDirectory = verificationCacheDirectory,
                                 MaximumMemoryBytes = 64L * 1024L * 1024L,
                                 MaximumMemoryEntryBytes = 64 * 1024 * 1024,
                                 MaximumDiskBytes = 256L * 1024L * 1024L,
                             }))
            {
                byte[] compactMetadata = await archive.ReadItemBytesAsync(
                    requestedMesh.Items[0],
                    verificationCache,
                    maximumBytes: 64 * 1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
                CompactMeshDocument hierarchy = CompactMeshDecoder.Decode(compactMetadata);
                if (!hierarchy.IsStructurallyValid || hierarchy.Entities.Count == 0)
                {
                    throw new InvalidDataException(
                        $"The compiled mesh '{resourceName}' has an invalid compact hierarchy. " +
                        "No model RPack was published.");
                }
                preparedPhysicalNodeReadBack = Dl1PreparedPhysicalNodeReadBackValidator.Validate(
                    sourceBuild.PreparedPhysicalNodeExpectations,
                    hierarchy);
                if (!sourceBuild.BoneScriptPolicies.IsDefault)
                {
                    boneScriptReadBack = Dl1CompiledBoneScriptValidator.Validate(hierarchy, sourceBuild.BoneScriptPolicies);
                }
                rigReadBack = ValidatePreparedRigReadBack(sourceBuild, hierarchy);

                byte[] variantDefinitions = await archive.ReadItemBytesAsync(
                    requestedMesh.Items[1],
                    verificationCache,
                    maximumBytes: 16 * 1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
                byte[] vertexData = await archive.ReadItemBytesAsync(
                    requestedMesh.Items[3],
                    verificationCache,
                    maximumBytes: 512 * 1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
                byte[] indexData = await archive.ReadItemBytesAsync(
                    requestedMesh.Items[4],
                    verificationCache,
                    maximumBytes: 512 * 1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
                CompiledMeshGeometryDocument geometry = CompiledMeshGeometryDecoder.Decode(
                    compactMetadata,
                    variantDefinitions,
                    vertexData,
                    indexData,
                    retailResourceName: resourceName,
                    cancellationToken: cancellationToken);
                CompactMeshDiagnostic[] geometryErrors = geometry.Diagnostics
                    .Where(static diagnostic =>
                        diagnostic.Severity == CompactMeshDiagnosticSeverity.Error)
                    .ToArray();
                if (geometry.Surfaces.Count == 0 ||
                    geometry.VertexCount == 0 ||
                    geometry.IndexCount == 0)
                {
                    throw new InvalidDataException(
                        $"The compiled mesh '{resourceName}' contains no renderable geometry " +
                        $"({geometry.Surfaces.Count} surfaces, {geometry.VertexCount} vertices, {geometry.IndexCount} indices). " +
                        "No model RPack was published.");
                }

                if (geometryErrors.Length > 0)
                {
                    string errorSummary = string.Join(
                        "; ",
                        geometryErrors.Take(4).Select(static diagnostic =>
                            $"{diagnostic.Code}: {diagnostic.Message}"));
                    throw new InvalidDataException(
                        $"The compiled mesh '{resourceName}' failed bounded geometry validation: {errorSummary}. " +
                        "No model RPack was published.");
                }

                if (previousMaterialGeometry is not null)
                    ValidateMaterialReuseFeatures(GetMaterialReuseFeatures(previousMaterialGeometry), GetMaterialReuseFeatures(geometry));

                skinningReadBack = Dl1CompiledSkinningReadBackValidator.Validate(
                    sourceBuild.PreparedSkinningExpectations,
                    geometry,
                    hierarchy.Entities.Count,
                    sourceBuild.PreparedMorphExpectations);
                byte[] chrBytes = await File.ReadAllBytesAsync(
                    sourceBuild.CharacterDefinitionPath,
                    cancellationToken).ConfigureAwait(false);
                Dl1ChrV4Document sourceChr = Dl1ChrV4Codec.Parse(chrBytes);
                chrIdentityReadBack = Dl1CompiledChrIdentityValidator.Validate(
                    sourceChr,
                    hierarchy,
                    geometry,
                    sourceBuild.PreparedSkinDefinitions.IsEmpty?null:sourceBuild.PreparedSkinDefinitions.Select(s=>s.Name));
                if(!sourceBuild.PreparedSkinDefinitions.IsEmpty)
                    skinDefinitionReadBack = Dl1CompiledSkinDefinitionValidator.Validate(sourceBuild.PreparedSkinDefinitions,hierarchy,geometry);

                var clothReadBackInputs = new List<(string ResourceName, NativePhxDocument Document)>();
                foreach (string name in sourceBuild.NativeCompanionFiles.Where(name =>
                             !name.StartsWith("character-resources/",StringComparison.Ordinal) && name.EndsWith(".phx", StringComparison.OrdinalIgnoreCase)))
                {
                    string source = await File.ReadAllTextAsync(Path.Combine(stagedSourceDirectory, name), cancellationToken)
                        .ConfigureAwait(false);
                    clothReadBackInputs.Add((name, Dl1ClothCodec.ReadPhx(source)));
                }
                warnings.AddRange(Dl1CompiledClothReadBackValidator.Validate(hierarchy, geometry, clothReadBackInputs));

                verifiedShadingVertexCount = Dl1CompiledShadingValidator.Validate(
                    sourceBuild.PreparedSkinningExpectations.Length, geometry.Surfaces, vertexData);
                ValidateCompiledMorphOutput(request.Model, geometry, sourceBuild.PreparedMorphExpectations, skinningReadBack.VertexCorrespondence);

                verifiedEntityCount = hierarchy.Entities.Count;
                verifiedSurfaceCount = geometry.Surfaces.Count;
                verifiedVertexCount = geometry.VertexCount;
                verifiedIndexCount = geometry.IndexCount;
                verifiedMorphChannelCount = geometry.MorphChannels.Count;
                verifiedMorphBindingCount = geometry.MorphBindings.Count;
            }

            Dl1CompiledSkinningReadBackEvidence skinningEvidence = skinningReadBack ??
                throw new InvalidOperationException("Compiled skinning read-back did not produce evidence.");
            Dl1CompiledChrIdentityReadBackEvidence chrIdentityEvidence = chrIdentityReadBack ??
                throw new InvalidOperationException("Compiled CHR identity read-back did not produce evidence.");
            Dl1PreparedPhysicalNodeReadBackEvidence preparedPhysicalNodeReadBackEvidence = preparedPhysicalNodeReadBack ??
                throw new InvalidOperationException("Prepared physical-node read-back did not produce evidence.");

            string outputDirectory = Path.GetDirectoryName(outputRpackPath)
                ?? throw new InvalidOperationException("The model RPack output path has no parent directory.");
            Directory.CreateDirectory(outputDirectory);
            string outputObjectPath = Path.Combine(outputDirectory, $"{resourceName}.msh_obj");
            string rawObjectPath = Path.Combine(outputDirectory, $"{resourceName}.msh_compiler_obj");
            VerifyMaterialReuseFilesAreCurrent(reusedMaterialFiles);
            string? outputMaterialDatabasePath = request.ReuseVerifiedExistingMaterials || compiledMaterialDatabase is null
                ? null
                : Path.Combine(outputDirectory, "local_dx11.mp");
            // The console compiler emits zero-offset, compiler-addressed units.
            // Player consumes standalone resource tables, just like objects rebuilt by Editor.
            string runtimeObjectPath = Path.Combine(Path.GetDirectoryName(compiledMeshObject)!, $"{resourceName}.runtime.msh_obj");
            await Rp6lCompilerObjectNormalizer.LinkAtomicAsync(
                new[]{compiledMeshObject}.Concat(characterResources.ObjectPaths), runtimeObjectPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var runtimeCompanionArchive=await Rp6lArchive.OpenAsync(runtimeObjectPath,cancellationToken:cancellationToken).ConfigureAwait(false);
            var runtimeCompanionReadback=await CharacterCompiledResourceAuthoring.VerifyLinkedAsync(request.Model.Package,runtimeCompanionArchive,
                characterResourceCache,cancellationToken).ConfigureAwait(false);
            if(runtimeCompanionReadback!=characterResourceReadback)
                throw new InvalidDataException("The deployed mesh object omitted retained character resources.");
            var runtimeObject = await Dl1RuntimeMeshObjectValidator.ValidateAsync(
                runtimeObjectPath, resourceName,
                request.Model.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary ? request.AnimationScriptAlias : null,
                cancellationToken).ConfigureAwait(false);
            runtimeObject = runtimeObject with { ObjectPath = outputObjectPath };
            await PublishFileAtomicallyAsync(compiledRpack, outputRpackPath, cancellationToken).ConfigureAwait(false);
            await PublishFileAtomicallyAsync(runtimeObjectPath, outputObjectPath, cancellationToken).ConfigureAwait(false);
            await PublishFileAtomicallyAsync(compiledMeshObject, rawObjectPath, cancellationToken).ConfigureAwait(false);
            var outputTextureObjectPaths = ImmutableArray.CreateBuilder<string>(Math.Max(0, compiledObjects.Count - 1));
            foreach (string compiledTextureObject in compiledObjects.Skip(1).Take(units.Length-1))
            {
                string outputTextureObjectPath = Path.Combine(
                    outputDirectory,
                    Path.GetFileName(compiledTextureObject));
                await PublishFileAtomicallyAsync(
                    compiledTextureObject,
                    outputTextureObjectPath,
                    cancellationToken).ConfigureAwait(false);
                outputTextureObjectPaths.Add(outputTextureObjectPath);
            }
            var outputDetachedObjects=ImmutableDictionary.CreateBuilder<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var (detachedName,detachedPath) in characterResources.DetachedObjectPaths)
            {
                string normalizedName=detachedName.Replace('\\','/');
                if(normalizedName.StartsWith('/') || normalizedName.Contains(':') || normalizedName.Split('/').Any(part=>part is "" or "." or ".."))
                    throw new InvalidDataException("Detached mesh output names must remain relative.");
                string output=Path.Combine(outputDirectory,"detached-objects",normalizedName.Replace('/',Path.DirectorySeparatorChar)+".msh_obj");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await PublishFileAtomicallyAsync(detachedPath,output,cancellationToken).ConfigureAwait(false);
                var reopened=await Rp6lResourceEnvelopeWriter.ReadBackAsync(output,characterResourceCache,cancellationToken).ConfigureAwait(false);
                if(reopened.Resource.ResourceType!=Rp6lResourceTypes.Mesh || reopened.Resource.Name!=detachedName)
                    throw new InvalidDataException("The detached mesh object identity changed during publication.");
                outputDetachedObjects.Add(detachedName,output);
            }
            if (compiledMaterialDatabase is not null && outputMaterialDatabasePath is not null)
            {
                await PublishFileAtomicallyAsync(
                    compiledMaterialDatabase,
                    outputMaterialDatabasePath,
                    cancellationToken).ConfigureAwait(false);
            }

            var outputMaterialCompanions = ImmutableArray.CreateBuilder<string>();
            if (!request.ReuseVerifiedExistingMaterials && compiledMaterialDatabase is not null)
                foreach (string name in MaterialDatabaseFileNames.Skip(1))
                {
                    string emitted = Path.Combine(compiledMaterialCompanionDirectory!, name);
                    if (!File.Exists(emitted)) continue;
                    string output = Path.Combine(outputDirectory, name);
                    await PublishFileAtomicallyAsync(emitted, output, cancellationToken).ConfigureAwait(false);
                    outputMaterialCompanions.Add(output);
                }

            ImmutableArray<Dl1OfficialCompilerDependencySidecar> dependencySidecars =
                await Dl1OfficialCompilerDependencySidecarCodec.PublishEmittedAsync(
                    compiledObjects,
                    outputDirectory,
                    cancellationToken).ConfigureAwait(false);
            var nativeCompanionPaths = ImmutableArray.CreateBuilder<string>();
            var nativeCompanionHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in sourceBuild.NativeCompanionFiles)
            {
                string destination = Path.Combine(outputDirectory, "native-companions",
                    name.StartsWith("character-resources/",StringComparison.Ordinal)?Dl1NativeCompanionWriter.PreservedVirtualPath(name):
                    name.EndsWith(".phx", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine("data", "odephysics", "meshpartcloth", name)
                        : Path.Combine("data", "characters", characterId, name));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await PublishFileAtomicallyAsync(Path.Combine(stagedSourceDirectory, name), destination, cancellationToken).ConfigureAwait(false);
                nativeCompanionPaths.Add(destination);
                nativeCompanionHashes.Add(destination, sourceBuild.OutputSha256[name]);
            }
            string outputRpackSha256 = await Sha256FileAsync(outputRpackPath, cancellationToken).ConfigureAwait(false);
            string outputObjectSha256 = await Sha256FileAsync(outputObjectPath, cancellationToken).ConfigureAwait(false);
            string rawObjectSha256 = await Sha256FileAsync(rawObjectPath, cancellationToken).ConfigureAwait(false);
            string? outputMaterialDatabaseSha256 = outputMaterialDatabasePath is null
                ? null
                : await Sha256FileAsync(
                    outputMaterialDatabasePath,
                    cancellationToken).ConfigureAwait(false);
            if(characterMaterialReadback is not null)
            {
                string actualMaterialPath=outputMaterialDatabasePath ?? compiledMaterialDatabase!;
                var actualMaterialReadback=CharacterMaterialPublication.Verify(request.Model.Package,
                    ImmutableArray.Create(await File.ReadAllBytesAsync(actualMaterialPath,cancellationToken).ConfigureAwait(false)),cancellationToken);
                if(actualMaterialReadback!=characterMaterialReadback)
                    throw new InvalidDataException("The published character material graph changed after verification.");
            }
            string outputManifestFingerprint = Sha256(
                Encoding.UTF8.GetBytes(
                    $"rpack={outputRpackSha256}\nmsh_obj={outputObjectSha256}\nlocal_dx11.mp={outputMaterialDatabaseSha256 ?? "none"}\n" +
                    string.Join("\n", sourceBuild.NativeCompanionFiles.Select(name => name + "=" + sourceBuild.OutputSha256[name]))));
            string inputFingerprint = CreateInputFingerprint(request, resourceName);
            CustomModelBuildReceipt buildReceipt = new()
            {
                InputFingerprint = inputFingerprint,
                ToolFingerprint = CurrentToolFingerprint,
                CompilerFingerprint = compilerFingerprint,
                OutputManifestFingerprint = outputManifestFingerprint,
                State = CustomModelBuildState.CompilerValidated,
                CompletedUtc = DateTimeOffset.UtcNow,
                BlockingReasons =
                [
                    ".skn remains unsupported; the loose package includes the evidence-backed CHR v4 definition.",
                ],
            };
            string receiptPath = Path.Combine(
                outputDirectory,
                $"{Path.GetFileNameWithoutExtension(outputRpackPath)}.model-build.json");
            byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    format = "dl-reanimated-csharp-model-compiler-receipt",
                    schemaVersion = 1,
                    resourceName,
                    state = CustomModelBuildState.CompilerValidated.ToString(),
                    input = new
                    {
                        sourceFbxSha256 = request.Model.Package.Document.Source.ContentSha256,
                        rigSignature = request.Model.Package.Document.RigSignature,
                        fingerprint = inputFingerprint,
                    },
                    compiler = new
                    {
                        fileName = Path.GetFileName(compilerPath),
                        sha256 = compilerFingerprint,
                        materialCompilerFileName = Path.GetFileName(materialCompilerPath),
                        materialCompilerSha256 = materialCompilerFingerprint,
                    },
                    linker = new
                    {
                        kind = "opaque-mesh-and-texture-object-link",
                        toolFingerprint = CurrentToolFingerprint,
                        normalization.ConvertedResourceCount,
                        runtimeObject,
                    },
                    outputs = new[]
                    {
                        new { path = Path.GetFileName(outputRpackPath), sha256 = outputRpackSha256 },
                        new { path = Path.GetFileName(outputObjectPath), sha256 = outputObjectSha256 },
                        new { path = Path.GetFileName(rawObjectPath), sha256 = rawObjectSha256 },
                        outputMaterialDatabasePath is null
                            ? null
                            : new
                            {
                                path = Path.GetFileName(outputMaterialDatabasePath),
                                sha256 = outputMaterialDatabaseSha256!,
                            },
                    }.Where(static output => output is not null),
                    detachedMeshObjects=outputDetachedObjects.Select(pair=>new{name=pair.Key,path="detached-objects/"+pair.Key+".msh_obj",sha256=Sha256(File.ReadAllBytes(pair.Value))}),
                    materialReuse = new { request.ReuseVerifiedExistingMaterials, verifiedExistingMaterialFileHashes = reusedMaterialFiles, materialFeaturesVerified = previousMaterialGeometry is not null },
                    materialDatabaseCompanions = outputMaterialCompanions.Select(path => new { path = Path.GetFileName(path), sha256 = Sha256(File.ReadAllBytes(path)) }),
                    resources = archive.Resources.Select(static resource => new
                    {
                        resource.Name,
                        resource.ResourceType,
                        resource.ItemCount,
                    }),
                    verification = new
                    {
                        boneScriptReadBack,
                        characterResourceReadback,
                        characterMaterialReadback,
                        rigReadBack,
                        entities = verifiedEntityCount,
                        surfaces = verifiedSurfaceCount,
                        vertices = verifiedVertexCount,
                        shadingVertices = verifiedShadingVertexCount,
                        indices = verifiedIndexCount,
                        morphChannels = verifiedMorphChannelCount,
                        morphBindings = verifiedMorphBindingCount,
                        morphEncoding = verifiedMorphBindingCount == 0
                            ? null
                            : CompiledMorphDeltaFormat.PcHalf4.ToString(),
                        skinningReadBackContractFingerprint = skinningEvidence.ContractFingerprint,
                        skinningSurfaces = skinningEvidence.VerifiedSurfaceCount,
                        skinningSubsets = skinningEvidence.VerifiedSubsetCount,
                        skinningVertices = skinningEvidence.VerifiedVertexCount,
                        skinningInfluences = skinningEvidence.VerifiedInfluenceCount,
                        skinningMaterials = skinningEvidence.MaterialSlots,
                        chrIdentityReadBack = chrIdentityEvidence,
                        skinDefinitionReadBack,
                        preparedPhysicalNodeReadBack = preparedPhysicalNodeReadBackEvidence,
                        preparedPhysicalNodeCount = preparedPhysicalNodeReadBackEvidence.VerifiedNodeCount,
                        customMaterials = sourceBuild.CustomMaterialReferences.Length,
                        textures = sourceBuild.TextureSourceFiles.Length,
                        nativeCompanionFiles = nativeCompanionPaths.Count,
                        nativeCompanionSourceInventoryValidated = true,
                        nativePhysicsCompiledBoundsValidated = false,
                        nativeCompanionRuntimeValidated = false,
                    },
                    nativeCompanions = nativeCompanionPaths.Select(path => new
                    {
                        path = Path.GetRelativePath(outputDirectory, path).Replace('\\', '/'),
                        sha256 = nativeCompanionHashes[path],
                    }),
                    nativeCompanionNotes = sourceBuild.NativeCompanionNotes,
                    unsupported = UnsupportedCompiledOutputs,
                    completedUtc = buildReceipt.CompletedUtc,
                },
                ReceiptJsonOptions);
            await PublishBytesAtomicallyAsync(receiptBytes, receiptPath, cancellationToken).ConfigureAwait(false);

            completedSuccessfully = true;
            return new Dl1OfficialModelCompilerResult(
                resourceName,
                outputRpackPath,
                outputObjectPath,
                outputMaterialDatabasePath,
                receiptPath,
                compilerFingerprint,
                archive.Resources.Select(static resource => resource.Name).ToImmutableArray(),
                buildReceipt,
                compilerLog.ToString())
            {
                Warnings = warnings.ToImmutable(),
                CompiledTextureObjectPaths = outputTextureObjectPaths.ToImmutable(),
                DetachedMeshObjectPaths = outputDetachedObjects.ToImmutable(),
                MaterialDatabaseCompanionPaths = outputMaterialCompanions.ToImmutable(),
                VerifiedExistingMaterialFileHashes = reusedMaterialFiles,
                NativeCompanionPaths = nativeCompanionPaths.ToImmutable(),
                RawCompiledMeshObjectPath = rawObjectPath,
                DependencySidecars = dependencySidecars,
                CompilerEvidence = new Dl1OfficialModelCompilerEvidence
                {
                    OutputRpackSha256 = outputRpackSha256,
                    CompilerFingerprint = compilerFingerprint,
                    ToolFingerprint = buildReceipt.ToolFingerprint,
                    BuildReceiptInputFingerprint =
                        buildReceipt.InputFingerprint,
                    BuildReceiptOutputManifestFingerprint =
                        buildReceipt.OutputManifestFingerprint,
                    BuildState = buildReceipt.State,
                    VerifiedMorphChannelCount =
                        verifiedMorphChannelCount,
                    VerifiedMorphBindingCount =
                        verifiedMorphBindingCount,
                    SkinningReadBackContractFingerprint = skinningEvidence.ContractFingerprint,
                    VerifiedSkinningSurfaceCount = skinningEvidence.VerifiedSurfaceCount,
                    VerifiedSkinningSubsetCount = skinningEvidence.VerifiedSubsetCount,
                    VerifiedSkinningVertexCount = skinningEvidence.VerifiedVertexCount,
                    VerifiedSkinningInfluenceCount = skinningEvidence.VerifiedInfluenceCount,
                    SkinningMaterialReadBack = skinningEvidence.MaterialSlots,
                    SkinDefinitionReadBack=skinDefinitionReadBack,
                    ChrIdentityReadBack = chrIdentityEvidence,
                    PreparedPhysicalNodeReadBack = preparedPhysicalNodeReadBackEvidence,
                    MorphDeltaFormat = verifiedMorphBindingCount == 0
                        ? null
                        : CompiledMorphDeltaFormat.PcHalf4,
                    BoneScriptReadBack = boneScriptReadBack,
                    RigReadBack = rigReadBack,
                    ShadingVerticesVerified = verifiedShadingVertexCount,
                    CharacterResourceReadback = characterResourceReadback,
                    CharacterMaterialReadback = characterMaterialReadback,
                },
            };
        }
        catch (Exception exception)
        {
            AppendBounded(
                compilerLog,
                $"Model compilation failed: {exception.GetType().Name}: {exception.Message}\r\n" +
                $"Diagnostic job retained at {jobDirectory}\r\n");
            throw;
        }
        finally
        {
            if (ownedSdkMaterialDirectory is not null)
            {
                string sdkRoot = Path.GetFullPath(Path.GetDirectoryName(materialCompilerPath)!);
                string owned = Path.GetFullPath(ownedSdkMaterialDirectory);
                if (string.Equals(Path.GetDirectoryName(owned), sdkRoot, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(owned), projectName, StringComparison.Ordinal) &&
                    Directory.Exists(owned) &&
                    (File.GetAttributes(owned) & FileAttributes.ReparsePoint) == 0 &&
                    !Directory.EnumerateFileSystemEntries(owned, "*", SearchOption.AllDirectories)
                        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    Directory.Delete(owned, recursive: true);
            }
            if (completedSuccessfully)
            {
                DeleteOwnedJobDirectory(jobContainer, jobDirectory);
            }
            else if (Directory.Exists(jobDirectory))
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(jobDirectory, "COMPILER_FAILURE.log"),
                        compilerLog.ToString(),
                        new UTF8Encoding(false));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public static async Task<Dl1OfficialAnimationCompilerResult> CompileAnimationsAsync(
        Dl1OfficialAnimationCompilerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Library);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CompilerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RetailData0PakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        if (!File.Exists(request.CompilerExecutablePath) || !File.Exists(request.RetailData0PakPath))
        {
            throw new FileNotFoundException(
                "Techland's compiler or the retail Data0.pak bootstrap was not found.");
        }

        if (request.Library.Animations.IsEmpty)
        {
            throw new InvalidOperationException("No prepared animation is available for compilation.");
        }

        string compilerPath = Path.GetFullPath(request.CompilerExecutablePath);
        string compilerFingerprint = await Sha256FileAsync(compilerPath, cancellationToken).ConfigureAwait(false);
        string jobContainer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLReAnimated",
            "AnimationCompiler",
            "Jobs");
        string jobId = Guid.NewGuid().ToString("N");
        string jobDirectory = Path.Combine(jobContainer, jobId);
        string workshopDirectory = Path.Combine(jobDirectory, "Workshop");
        // This is a disposable local bootstrap folder and the compiler's -dn
        // value. Keep it short because it prefixes every staged animation
        // path; public animation/resource names remain unchanged.
        string projectName = $"_dlra_{jobId[..8]}";
        string projectDirectory = Path.Combine(workshopDirectory, projectName);
        string animationDirectory = Path.Combine(projectDirectory, "data", "characters", "animations");
        string rulesPath = Path.Combine(projectDirectory, "animation_resource.rules");
        var compilerLog = new StringBuilder();
        bool completedSuccessfully = false;
        Directory.CreateDirectory(animationDirectory);
        try
        {
            CopyCompilerBootstrap(ResolveDeveloperToolsDataDirectory(compilerPath), projectDirectory);
            await CopyRetailCompilerBootstrapAsync(
                request.RetailData0PakPath,
                projectDirectory,
                cancellationToken).ConfigureAwait(false);
            string dataDirectory = Path.Combine(projectDirectory, "data");
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "resourcepackfolders.scr"),
                CreateAnimationResourcePackFoldersScript(),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "common_ovr.scr"),
                CreateCommonOverrideScript(),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "common_ovr.scr"),
                CreateCommonOverrideScript(),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                rulesPath,
                CreateAnimationResourceRules(),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            string outputDirectory = Path.GetFullPath(request.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);
            PreparedCustomModelAnimation[] orderedAnimations = request.Library.Animations
                .OrderBy(static animation => animation.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var compiledObjects = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            string? previousSourcePath = null;
            for (int index = 0; index < orderedAnimations.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreparedCustomModelAnimation animation = orderedAnimations[index];

                // ResPackCompiler has been observed to schedule every entry in an RSRC even when
                // -update names one source. Keep both the staged source set and RSRC to one resource
                // per process so a faulty clip cannot contaminate a sibling compilation.
                if (previousSourcePath is not null && File.Exists(previousSourcePath))
                {
                    File.Delete(previousSourcePath);
                }

                string sourcePath = Path.Combine(animationDirectory, animation.Anm2FileName);
                await File.WriteAllBytesAsync(
                    sourcePath,
                    animation.Payload,
                    cancellationToken).ConfigureAwait(false);
                previousSourcePath = sourcePath;

                string rsrcPath = Path.Combine(projectDirectory, $"animation_resource_{index:D3}.rsrc");
                await File.WriteAllTextAsync(
                    rsrcPath,
                    CreateAnimationResourceScript([animation]),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);

                string compilerOutputDirectory = Path.Combine(jobDirectory, $"CompilerAnimation_{index:D3}");
                Directory.CreateDirectory(compilerOutputDirectory);
                AppendBounded(
                    compilerLog,
                    $"Animation compiler isolated resource {index + 1}/{orderedAnimations.Length}: {animation.Name}\r\n");
                ProcessResult process = await RunCompilerStageAsync(
                    compilerPath,
                    CreateAnimationCompilerCommand(
                        projectName,
                        workshopDirectory,
                        compilerOutputDirectory,
                        rulesPath,
                        rsrcPath,
                        $"{Dl1SourceModelWriter.SanitizeName(animation.Name, 48)}_PC.rpack"),
                    projectDirectory,
                    request.Timeout,
                    cancellationToken).ConfigureAwait(false);
                AppendBounded(compilerLog, process.Output);

                string expectedName = $"{animation.Name}.anm2_obj";
                string? emittedObject = TryFindCompilerOutput(compilerOutputDirectory, expectedName) ??
                    TryFindCompilerOutput(projectDirectory, expectedName);
                ValidateAnimationCompilerExitCode(
                    process.ExitCode,
                    emittedObject is null ? 0 : 1,
                    animation.Name);
                if (process.ExitCode != 0)
                {
                    string exceptionName = process.ExitCode == WindowsAccessViolationExitCode
                        ? "access violation 0xC0000005"
                        : "illegal instruction 0xC000001D";
                    AppendBounded(
                        compilerLog,
                        $"Techland animation compiler exited with {exceptionName} after emitting {expectedName}. " +
                        "The isolated object will be admitted only if bounded type-320 normalization and archive validation both pass.\r\n");
                }

                compiledObjects.Add(
                    animation.Name,
                    emittedObject ?? FindCompilerOutput(projectDirectory, expectedName));
            }

            var published = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            var validatedObjects = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            string validationDirectory = Path.Combine(jobDirectory, "AnimationValidation");
            Directory.CreateDirectory(validationDirectory);
            foreach (PreparedCustomModelAnimation animation in orderedAnimations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string expectedName = $"{animation.Name}.anm2_obj";
                string emittedObject = compiledObjects[animation.Name];
                string validationRpack = Path.Combine(
                    validationDirectory,
                    $"{animation.Name}.validated.rpack");
                string objectContract = await ValidateCompiledAnimationObjectAsync(
                    emittedObject,
                    animation,
                    validationRpack,
                    cancellationToken).ConfigureAwait(false);
                AppendBounded(
                    compilerLog,
                    $"Validated {expectedName} as {objectContract}; its type-{Rp6lResourceTypes.Animation} payload matches the staged ANM2 byte-for-byte.\r\n");

                validatedObjects.Add(animation.Name, emittedObject);
            }

            // Do not publish any clip until every isolated compiler process and every object
            // validation has passed. Known post-emission Techland crashes are admitted only
            // after the exact emitted object has passed the same bounded type-320 checks.
            foreach (PreparedCustomModelAnimation animation in orderedAnimations)
            {
                string expectedName = $"{animation.Name}.anm2_obj";
                string destination = Path.Combine(outputDirectory, expectedName);
                await PublishFileAtomicallyAsync(
                    validatedObjects[animation.Name],
                    destination,
                    cancellationToken).ConfigureAwait(false);
                published.Add(animation.Name, destination);
            }

            ImmutableArray<Dl1OfficialCompilerDependencySidecar> dependencySidecars =
                await Dl1OfficialCompilerDependencySidecarCodec.PublishEmittedAsync(
                    validatedObjects.Values,
                    outputDirectory,
                    cancellationToken).ConfigureAwait(false);

            completedSuccessfully = true;
            return new Dl1OfficialAnimationCompilerResult(
                published.ToImmutable(),
                compilerFingerprint,
                compilerLog.ToString())
            {
                DependencySidecars = dependencySidecars,
            };
        }
        finally
        {
            if (completedSuccessfully)
            {
                DeleteOwnedJobDirectory(jobContainer, jobDirectory);
            }
            else if (Directory.Exists(jobDirectory))
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(jobDirectory, "COMPILER_FAILURE.log"),
                        compilerLog.ToString(),
                        new UTF8Encoding(false));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static string CreateResourceScript(
        string resourceName,
        string virtualMshPath,
        string virtualDirectory,
        IEnumerable<string>? customMaterialReferences = null,
        IEnumerable<string>? textureSourceFiles = null)
    {
        string normalizedDirectory = virtualDirectory.Replace('\\', '/').TrimEnd('/');
        var builder = new StringBuilder()
            .Append("import \"ResourcePackCfg.scr\"\n\n")
            .Append("sub main()\n")
            .Append("{\n")
            .Append("  configuration(cfg_common)\n")
            .Append("  {\n")
            .Append("    res( _MESH_, \"")
            .Append(EscapeScript(resourceName))
            .Append("\", \"")
            .Append(EscapeScript(virtualMshPath.Replace('\\', '/')))
            .Append("\", \"skins=Default;instances_limit=1;pos_compression=0\", true);\n");

        foreach (string textureSourceFile in (textureSourceFiles ?? [])
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(textureSourceFile.Replace('\\', '/'));
            string name = Path.GetFileNameWithoutExtension(fileName);
            builder.Append("    res( _TEXTURE_, \"")
                .Append(EscapeScript(name))
                .Append("\", \"")
                .Append(EscapeScript($"{normalizedDirectory}/{fileName}"))
                .Append("\", \"\", false);\n");
        }

        return builder
            .Append("  }\n\n")
            .Append("  configuration(cfg_PC)\n")
            .Append("  {\n")
            .Append("  }\n")
            .Append("}\n")
            .ToString();
    }

    internal static string CreateResourcePackFoldersScript() =>
        "import \"enginedefs.mth\"\n" +
        "import \"ResourcePackCfg.scr\"\n\n" +
        "sub main()\n" +
        "{\n" +
        "    default(\n" +
        "        MF_DEFAULT,\n" +
        "        MF_SKINNING,\n" +
        "        MF_SKINNING | MF_SKINNING_ONE_BONE,\n" +
        "        MF_SKINNING | MF_MORPH_TARGETS\n" +
        "    );\n" +
        "    path(\"data\\\\characters\", MF_DEFAULT, MF_SKINNING, MF_SKINNING | MF_SKINNING_ONE_BONE, MF_SKINNING | MF_MORPH_TARGETS);\n" +
        "    exclude(\"*.msh.dds\");\n" +
        "    exclude(\"*.eds.dds\");\n" +
        "    resources(_MESH_, _TEXTURE_);\n" +
        "}\n";

    internal static string CreateAnimationResourcePackFoldersScript() =>
        "import \"enginedefs.mth\"\n" +
        "import \"ResourcePackCfg.scr\"\n\n" +
        "sub main()\n" +
        "{\n" +
        "    default(MF_DEFAULT);\n" +
        "    path(\"data\\\\characters\\\\animations\", MF_DEFAULT);\n" +
        "    resources(_ANIMATION_);\n" +
        "}\n";

    internal static ImmutableArray<ImmutableArray<string>> CreateCompilerCommands(
        string compilerPath,
        string projectName,
        string workshopDirectory,
        string outputDirectory,
        string rulesPath,
        string rsrcPath,
        string virtualDirectory,
        string outputName)
    {
        return
        [
            CreateCompilerCommand(
                projectName,
                workshopDirectory,
                outputDirectory,
                rulesPath,
                rsrcPath,
                $"{virtualDirectory.TrimEnd('/')}/*.*",
                outputName),
        ];
    }

    internal static ImmutableArray<string> CreateMaterialCompilerCommand(
        string projectDirectory,
        string workshopDirectory,
        string virtualDirectory,
        bool forceRebuild = true)
    {
        string projectName = Path.GetFileName(
            projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException(
                "The material compiler project directory must end in a project folder name.",
                nameof(projectDirectory));
        }

        var arguments = ImmutableArray.CreateBuilder<string>();
        arguments.AddRange((IEnumerable<string>)
        [
            "-u",
            "local",
            "-p",
            "dx11",
            "-savedeps",
        ]);
        if (forceRebuild)
        {
            arguments.Add("-rebuild");
        }

        arguments.AddRange((IEnumerable<string>)
        [
            "-logfile",
            "Assets_PC/dl-reanimated-materials.log",
            "-game",
            projectName,
            "-gamedir",
            workshopDirectory.Replace('\\', '/').TrimEnd('/') + "/",
            $"{virtualDirectory.TrimEnd('/')}/*.dmt",
        ]);
        return arguments.ToImmutable();
    }

    private static ImmutableArray<string> CreateCompilerCommand(
        string projectName,
        string workshopDirectory,
        string outputDirectory,
        string rulesPath,
        string rsrcPath,
        string resourceFilter,
        string outputName)
    {
        string workshop = workshopDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        return
        [
            $"dn={projectName}",
            "platform=PC",
            $"output={outputName}",
            $"out={outputDirectory}",
            "/Verbose",
            "/ShowFiles",
            "/ShowMissingFiles",
            "/SaveDependencies",
            "/FC-",
            "/FS-",
            $"/WorkshopDir={workshop}",
            $"/ScriptRules={rulesPath}",
            "/LooseResources",
            "/LooseGpuResources",
            $"-updatefromrscr={rsrcPath}",
            "-update",
            resourceFilter,
        ];
    }

    internal static ImmutableArray<string> CreateAnimationCompilerCommand(
        string projectName,
        string workshopDirectory,
        string outputDirectory,
        string rulesPath,
        string rsrcPath,
        string outputName)
    {
        string workshop = workshopDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        return
        [
            $"dn={projectName}",
            "platform=PC",
            $"output={outputName}",
            $"out={outputDirectory}",
            "/Verbose",
            "/ShowFiles",
            "/ShowMissingFiles",
            "/SaveDependencies",
            "/FC-",
            "/FS-",
            $"/WorkshopDir={workshop}",
            $"/ScriptRules={rulesPath}",
            "/LooseResources",
            "/LooseGpuResources",
            $"-updatefromrscr={rsrcPath}",
            "-update",
            "*.*",
        ];
    }

    internal static void ValidateAnimationCompilerExitCode(
        int exitCode,
        int emittedObjectCount,
        string? animationName = null)
    {
        if (exitCode == 0)
        {
            return;
        }

        if ((exitCode == WindowsAccessViolationExitCode ||
             exitCode == WindowsIllegalInstructionExitCode) &&
            emittedObjectCount == 1)
        {
            return;
        }

        string resource = string.IsNullOrWhiteSpace(animationName)
            ? string.Empty
            : $" for animation '{animationName}'";
        throw new InvalidDataException(
            $"Techland animation compiler exited with code {exitCode}{resource}. " +
            $"All {emittedObjectCount} emitted animation object(s), if any, were rejected.");
    }

    /// <summary>
    /// Validates the two animation-object layouts emitted by supported
    /// Developer Tools builds. Some builds emit an ordinary, absolute-addressed
    /// RP6L <c>.anm2_obj</c>; others emit the compiler-addressed variant used by
    /// mesh objects. Neither layout is trusted until its isolated type-320 item
    /// matches the staged ANM2 byte-for-byte.
    /// </summary>
    internal static async Task<string> ValidateCompiledAnimationObjectAsync(
        string compilerObjectPath,
        PreparedCustomModelAnimation animation,
        string normalizedValidationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerObjectPath);
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValidationPath);
        if (!File.Exists(compilerObjectPath))
        {
            throw new FileNotFoundException(
                "The emitted Techland animation object was not found.",
                compilerObjectPath);
        }

        Rp6lArchive? ordinaryArchive = null;
        try
        {
            ordinaryArchive = await Rp6lArchive.OpenAsync(
                compilerObjectPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // Compiler-addressed RP6L objects may not be readable until their
            // zero-relative chunks and resource type bit are normalized.
        }

        bool hasCompilerResourceType = ordinaryArchive?.Resources.Any(
            static resource =>
                resource.ResourceType != Rp6lResourceTypes.BuilderInformation &&
                (unchecked((ushort)resource.ResourceType) & 0x8000) != 0) == true;
        if (ordinaryArchive is not null && !hasCompilerResourceType)
        {
            await ValidateOrdinaryAnimationObjectAsync(
                ordinaryArchive,
                animation,
                cancellationToken).ConfigureAwait(false);
            return "an absolute-addressed Techland animation object";
        }

        await Rp6lCompilerObjectNormalizer.NormalizeAtomicAsync(
            compilerObjectPath,
            normalizedValidationPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Rp6lArchive normalizedArchive = await Rp6lArchive.OpenAsync(
            normalizedValidationPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await ValidateOrdinaryAnimationObjectAsync(
            normalizedArchive,
            animation,
            cancellationToken).ConfigureAwait(false);
        return "a normalized compiler-addressed Techland animation object";
    }

    private static async Task ValidateOrdinaryAnimationObjectAsync(
        Rp6lArchive archive,
        PreparedCustomModelAnimation animation,
        CancellationToken cancellationToken)
    {
        Rp6lResourceDescriptor[] animationResources = archive.Resources
            .Where(static resource =>
                resource.ResourceType == Rp6lResourceTypes.Animation)
            .ToArray();
        Rp6lResourceDescriptor[] builderResources = archive.Resources
            .Where(static resource =>
                resource.ResourceType == Rp6lResourceTypes.BuilderInformation)
            .ToArray();
        if (animationResources.Length != 1 ||
            builderResources.Length != 1 ||
            archive.Resources.Count != 2)
        {
            throw new InvalidDataException(
                $"The isolated animation object must contain exactly one type-{Rp6lResourceTypes.Animation} resource and its _ANIMATION_ builder record.");
        }

        Rp6lResourceDescriptor animationResource = animationResources[0];
        Rp6lResourceDescriptor builderResource = builderResources[0];
        if (!string.Equals(
                animationResource.Name,
                animation.Name,
                StringComparison.OrdinalIgnoreCase) ||
            animationResource.Items.Count != 1)
        {
            throw new InvalidDataException(
                $"The isolated animation object does not contain exactly one type-{Rp6lResourceTypes.Animation} item named '{animation.Name}'.");
        }

        if (!string.Equals(
                builderResource.Name,
                "_ANIMATION_",
                StringComparison.OrdinalIgnoreCase) ||
            builderResource.Items.Count != 1)
        {
            throw new InvalidDataException(
                "The isolated animation object has no single _ANIMATION_ builder record.");
        }

        string validationCacheDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(archive.Path)) ??
                Path.GetTempPath(),
            $".dlr-animation-validation-{Guid.NewGuid():N}");
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = validationCacheDirectory,
                    MaximumMemoryBytes = 64L * 1024L * 1024L,
                    MaximumMemoryEntryBytes = 64 * 1024 * 1024,
                    MaximumDiskBytes = 512L * 1024L * 1024L,
                });
            byte[] emittedPayload = await archive.ReadItemBytesAsync(
                animationResource.Items[0],
                cache,
                maximumBytes: Math.Max(animation.Payload.Length, 1),
                cancellationToken).ConfigureAwait(false);
            if (!emittedPayload.AsSpan().SequenceEqual(animation.Payload))
            {
                throw new InvalidDataException(
                    $"The emitted type-{Rp6lResourceTypes.Animation} resource '{animationResource.Name}' does not match the staged ANM2 payload.");
            }

            byte[] builderPayload = await archive.ReadItemBytesAsync(
                builderResource.Items[0],
                cache,
                maximumBytes: 4096,
                cancellationToken).ConfigureAwait(false);
            string builderDirective;
            try
            {
                builderDirective = new UTF8Encoding(false, true)
                    .GetString(builderPayload)
                    .TrimEnd('\0', '\r', '\n');
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The _ANIMATION_ builder record is not valid UTF-8.",
                    exception);
            }

            string expectedDirective = animationResource.Name;
            if (builderDirective.Length != expectedDirective.Length + 1 ||
                builderDirective[0] is not ('+' or '-') ||
                !string.Equals(
                    builderDirective[1..],
                    expectedDirective,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The _ANIMATION_ builder record does not identify '{animationResource.Name}'.");
            }
        }
        finally
        {
            if (Directory.Exists(validationCacheDirectory))
            {
                Directory.Delete(validationCacheDirectory, recursive: true);
            }
        }
    }

    internal static string CreateAnimationResourceScript(
        IEnumerable<PreparedCustomModelAnimation> animations)
    {
        ArgumentNullException.ThrowIfNull(animations);
        PreparedCustomModelAnimation[] rows = animations
            .OrderBy(static animation => animation.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder()
            .Append("import \"ResourcePackCfg.scr\"\n\n")
            .Append("sub main()\n{\n  configuration(cfg_common)\n  {\n");
        foreach (PreparedCustomModelAnimation animation in rows)
        {
            builder.Append("    res( _ANIMATION_, \"")
                .Append(EscapeScript(animation.Name))
                .Append("\", \"data/characters/animations/")
                .Append(EscapeScript(animation.Anm2FileName))
                .Append("\", \"\", true, \"\");\n");
        }

        return builder.Append("  }\n  configuration(cfg_PC)\n  {\n  }\n}\n").ToString();
    }

    internal static string CreateAnimationResourceRules() => "ResourceRule(\"*.anm2\")\n";

    private static ImmutableArray<CompilerResourceUnit> CreateCompilerResourceUnits(
        string resourceName,
        string virtualMshPath,
        string virtualDirectory,
        IEnumerable<string> textureSourceFiles,
        string jobDirectory)
    {
        var units = ImmutableArray.CreateBuilder<CompilerResourceUnit>();
        units.Add(new CompilerResourceUnit(
            virtualMshPath.Replace('\\', '/'),
            $"{resourceName}.msh_obj",
            Path.Combine(jobDirectory, "CompilerResource_000_mesh")));
        int index = 1;
        foreach (string textureSourceFile in textureSourceFiles
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(textureSourceFile.Replace('\\', '/'));
            string objectFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.dds_obj";
            units.Add(new CompilerResourceUnit(
                $"{virtualDirectory.TrimEnd('/')}/{fileName}",
                objectFileName,
                Path.Combine(
                    jobDirectory,
                    $"CompilerResource_{index:000}_{Dl1SourceModelWriter.SanitizeName(Path.GetFileNameWithoutExtension(fileName), 48)}")));
            index++;
        }

        return units.ToImmutable();
    }

    // A conservative staging budget, not a claim about every native filesystem
    // call's limit. The installed compiler can fault in deep staging trees even
    // after it has emitted an object header; moving the same control to a short
    // job root succeeds. Fail before launching it rather than publishing partial data.
    internal const int MaximumNativeCompilerPathLength = 240;

    internal static void ValidateNativeCompilerPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            if (path.Length > MaximumNativeCompilerPathLength)
                throw new InvalidDataException(
                    $"The native compiler staging path is {path.Length} characters, exceeding the conservative {MaximumNativeCompilerPathLength}-character limit. " +
                    $"Choose a shorter WorkingDirectoryRoot or shorter resource/character names before compiling. Path: {path}");
        }
    }

    internal static string CreateResourceRules() =>
        "ResourceRule(\"*.msh\")\nResourceRule(\"*.dds\")\n";

    internal static void ValidateCompiledTextureDependencies(
        IReadOnlyList<Rp6lResourceDescriptor> resources,
        IEnumerable<string> textureSourceFiles)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(textureSourceFiles);

        ValidateCompiledResourceNames(
            resources,
            textureSourceFiles,
            Rp6lResourceTypes.Texture,
            "texture");
    }

    internal static void ValidateCompiledMaterialDatabase(
        string materialDatabasePath,
        IEnumerable<string> customMaterialReferences,
        IEnumerable<string> customTextureReferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialDatabasePath);
        ArgumentNullException.ThrowIfNull(customMaterialReferences);
        ArgumentNullException.ThrowIfNull(customTextureReferences);
        if (!File.Exists(materialDatabasePath))
        {
            throw new InvalidDataException(
                "Techland's material compiler did not emit Assets_PC/local_dx11.mp. No model bundle was published.");
        }

        Dictionary<uint, CompiledMaterialRecord> materials;
        using (FileStream stream = new(
                   materialDatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.RandomAccess))
        {
            try
            {
                materials = ReadCompiledMaterialRecords(stream);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    "Techland's material compiler emitted an invalid ABDM local_dx11.mp. No model bundle was published.",
                    exception);
            }
        }

        if (materials.Count == 0)
        {
            throw new InvalidDataException(
                "Techland's material compiler emitted an empty ABDM material inventory.");
        }

        string[] missingMaterials = customMaterialReferences
            .Select(ComputeCompiledResourceHash)
            .Distinct()
            .Where(hash => !materials.ContainsKey(hash))
            .Select(hash => $"0x{hash:X8}")
            .Take(16)
            .ToArray();
        if (missingMaterials.Length > 0)
        {
            throw new InvalidDataException(
                "Techland's material compiler did not add all model material references to " +
                $"Assets_PC/local_dx11.mp ({string.Join(", ", missingMaterials)} missing). " +
                "The model bundle cannot be published with unresolved materials.");
        }

        _ = customTextureReferences;
    }

    internal static ImmutableDictionary<string, string> ValidateMaterialReuseSources(
        string stagedSourceDirectory, string currentSourceDirectory,
        IEnumerable<string> materialReferences, IEnumerable<string> textureSourceFiles,
        string materialDatabasePath)
    {
        var hashes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        string database = Path.GetFullPath(materialDatabasePath);
        hashes.Add(database, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(database))));
        Dictionary<uint, CompiledMaterialRecord> records;
        using (var stream = File.OpenRead(database)) records = ReadCompiledMaterialRecords(stream);
        var textureNames = textureSourceFiles.Select(name => SafeFileName(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] references = materialReferences.ToArray();
        if (references.Length == 0) throw new InvalidDataException("Material reuse requires a named material inventory.");
        foreach (string reference in references)
        {
            string file = Path.GetFileNameWithoutExtension(SafeFileName(reference)) + ".dmt";
            string staged = Path.Combine(stagedSourceDirectory, file);
            string current = Path.GetFullPath(Path.Combine(currentSourceDirectory, file));
            CompareSource(staged, current);
            uint key = ComputeCompiledResourceHash(reference);
            if (!records.TryGetValue(key, out CompiledMaterialRecord? record))
                throw new InvalidDataException("A reused material ID is absent from the current compiled database.");
            using var xmlReader = System.Xml.XmlReader.Create(staged, new System.Xml.XmlReaderSettings {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var document = System.Xml.Linq.XDocument.Load(xmlReader);
            var expectedTextures = new HashSet<uint>();
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName.EndsWith("_tex", StringComparison.OrdinalIgnoreCase)))
            {
                string name = element.Value.Trim().Trim('"');
                if (name.Length == 0) continue;
                name = SafeFileName(name);
                if (!textureNames.Contains(name)) throw new InvalidDataException("A reused material references a new texture source.");
                CompareSource(Path.Combine(stagedSourceDirectory, name), Path.GetFullPath(Path.Combine(currentSourceDirectory, name)));
                expectedTextures.Add(ComputeCompiledResourceHash(name));
            }
            if (!expectedTextures.SetEquals(record.TextureNameHashes))
                throw new InvalidDataException("A reused material's compiled texture contract differs from its current source.");
        }
        foreach (string name in textureNames)
            CompareSource(Path.Combine(stagedSourceDirectory, name), Path.GetFullPath(Path.Combine(currentSourceDirectory, name)));
        return hashes.ToImmutable();

        static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name is "." or ".." || name.Any(c => c < 32 || c > 126))
                throw new InvalidDataException("Material reuse accepts only safe source filenames.");
            return name;
        }
        void CompareSource(string staged, string current)
        {
            if (!File.Exists(staged) || !File.Exists(current)) throw new InvalidDataException("A reused material source is missing.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A reused source must be an ordinary file.");
            string proposed = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(staged)));
            string existing = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(current)));
            if (!proposed.Equals(existing, StringComparison.Ordinal)) throw new InvalidDataException("A reused material or texture source changed.");
            hashes[current] = existing;
        }
    }

    internal static void VerifyMaterialReuseFilesAreCurrent(IReadOnlyDictionary<string, string> files)
    {
        foreach ((string path, string expected) in files)
            if (!File.Exists(path) || !Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))).Equals(expected, StringComparison.Ordinal))
                throw new InvalidDataException("A verified existing material input changed before publication.");
    }

    internal static void ValidateMaterialReuseFeatures(IReadOnlyDictionary<string, string[]> previous, IReadOnlyDictionary<string, string[]> updated)
    {
        if (previous.Count == 0 || previous.Count != updated.Count)
            throw new InvalidDataException("Material reuse cannot add or remove a material feature contract.");
        foreach ((string name, string[] features) in previous)
            if (!updated.TryGetValue(name, out string[]? proposed) || !features.ToHashSet(StringComparer.Ordinal).SetEquals(proposed))
                throw new InvalidDataException($"Reused material '{name}' changed its compiled vertex-layout, skin, or morph feature contract.");
    }

    private static Dictionary<string, string[]> GetMaterialReuseFeatures(CompiledMeshGeometryDocument geometry)
    {
        if (!geometry.MaterialDatabase.HasCompleteSlotNames) throw new InvalidDataException("Material reuse requires complete compiled material names.");
        var features = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (CompiledMeshSurface surface in geometry.Surfaces)
        {
            string layout = surface.VertexLayout.Stride + ":" + string.Join(";", surface.VertexLayout.Elements.Select(e => $"{e.RawFormat},{e.RawSemantic},{e.Channel},{e.ByteOffset},{e.ByteSize}"));
            var morphs = geometry.MorphBindings.Where(m => m.EntityIndex == surface.EntityIndex && m.LodIndex == surface.LodIndex).ToArray();
            string morph = string.Join(";", morphs.Select(m => $"{m.DeltaFormat},{m.DeltaByteStride},{m.MorphChannelIndexes.Count}").Order(StringComparer.Ordinal));
            foreach (CompiledMeshSubmesh subset in surface.Submeshes)
            {
                if (subset.DeclaredMaterialSlotIndex is not { } slot || slot >= geometry.MaterialDatabase.DeclaredSlotCount)
                    throw new InvalidDataException("Material reuse requires named subset material slots.");
                var entry = geometry.MaterialDatabase.Entries.Single(e => e.Index == slot);
                string name = NormalizeCompiledResourceName(entry.DatabaseName);
                if (!features.TryGetValue(name, out var values)) features[name] = values = new(StringComparer.Ordinal);
                values.Add($"load={entry.RawLoadValue};layout={layout};skin={subset.BonePaletteEntityIndexes.Count > 0};morph={morph}");
            }
        }
        return features.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<CompiledMeshGeometryDocument> ReadMaterialReuseGeometryAsync(string path, string resourceName, string cachePath, CancellationToken cancellationToken)
    {
        var archive = await Rp6lArchive.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        var mesh = archive.Resources.Single(r => r.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase) && r.ResourceType == Rp6lResourceTypes.Mesh);
        await using var cache = new Rp6lChunkCache(new Rp6lChunkCacheOptions { CacheDirectory = cachePath });
        byte[] metadata = await archive.ReadItemBytesAsync(mesh.Items[0], cache, cancellationToken: cancellationToken).ConfigureAwait(false);
        byte[] variants = await archive.ReadItemBytesAsync(mesh.Items[1], cache, cancellationToken: cancellationToken).ConfigureAwait(false);
        byte[] vertices = await archive.ReadItemBytesAsync(mesh.Items[3], cache, maximumBytes: 512 * 1024 * 1024, cancellationToken: cancellationToken).ConfigureAwait(false);
        byte[] indices = await archive.ReadItemBytesAsync(mesh.Items[4], cache, maximumBytes: 512 * 1024 * 1024, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = CompiledMeshGeometryDecoder.Decode(metadata, variants, vertices, indices, retailResourceName: resourceName, cancellationToken: cancellationToken);
        if (result.Diagnostics.Any(d => d.Severity == CompactMeshDiagnosticSeverity.Error)) throw new InvalidDataException("The current mesh material feature baseline is invalid.");
        return result;
    }

    private static readonly string[] MaterialDatabaseFileNames = ["local_dx11.mp", "local_dx11_refs.mp", "local_dx11_debug.mp", "local_dx11_refs_debug.mp"];

    internal static void ValidateCompiledMaterialDatabasePreserves(
        string previousDatabasePath,
        string updatedDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedDatabasePath);
        Dictionary<uint, CompiledMaterialRecord> previous;
        Dictionary<uint, CompiledMaterialRecord> updated;
        using (var stream = new FileStream(previousDatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            previous = ReadCompiledMaterialRecords(stream);
        }

        using (var stream = new FileStream(updatedDatabasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            updated = ReadCompiledMaterialRecords(stream);
        }

        ValidateCompiledDatabaseGraphPreserves(previousDatabasePath, updatedDatabasePath);
        uint[] missing = previous.Keys.Where(hash => !updated.ContainsKey(hash)).Take(16).ToArray();
        uint[] changed = previous
            .Where(pair => updated.TryGetValue(pair.Key, out CompiledMaterialRecord? value) &&
                           (!pair.Value.TextureNameHashes.SequenceEqual(value.TextureNameHashes) ||
                            !string.Equals(
                                pair.Value.PayloadSha256,
                                value.PayloadSha256,
                                StringComparison.OrdinalIgnoreCase)))
            .Select(static pair => pair.Key)
            .Take(16)
            .ToArray();
        if (missing.Length > 0 || changed.Length > 0)
        {
            throw new InvalidDataException(
                "Techland's staged material update did not preserve the existing local_dx11.mp inventory " +
                $"({missing.Length} missing and {changed.Length} changed record(s) in the bounded report). " +
                "The Developer Tools project was not modified.");
        }
    }

    private static void ValidateCompiledDatabaseGraphPreserves(string previousPath, string updatedPath)
    {
        var previous = ReadCompiledDatabaseGraph(previousPath);
        var updated = ReadCompiledDatabaseGraph(updatedPath);
        foreach ((string container, Dictionary<uint, string> records) in previous)
            foreach ((uint key, string hash) in records)
                if (!updated.TryGetValue(container, out var target) ||
                    !target.TryGetValue(key, out string? actual) || !string.Equals(hash, actual, StringComparison.Ordinal))
                    throw new InvalidDataException($"The compiled material graph did not preserve {container} record 0x{key:X8}.");
    }

    private static Dictionary<string, Dictionary<uint, string>> ReadCompiledDatabaseGraph(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = ReadExactlyAt(stream, 0, 16);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x4D444241 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != 0)
            throw new InvalidDataException("The compiled material graph has an invalid header.");
        int count = ReadBoundedCompiledCount(header.AsSpan(4), 128, "graph container");
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        ValidateCompiledRange(offset, checked(count * 48), stream.Length, "graph container table");
        byte[] table = ReadExactlyAt(stream, offset, checked(count * 48));
        var result = new Dictionary<string, Dictionary<uint, string>>(StringComparer.OrdinalIgnoreCase);
        int totalRecords = 0;
        long totalLogicalBytes = 0;
        for (int i = 0; i < count; i++)
        {
            var row = table.AsSpan(i * 48, 48);
            int end = row[..32].IndexOf((byte)0);
            if (end < 1 || row[(end + 1)..32].ContainsAnyExcept((byte)0) || !IsPrintableCompiledAscii(row[..end]))
                throw new InvalidDataException("The compiled graph container name is invalid.");
            string name = Encoding.ASCII.GetString(row[..end]);
            int entries = ReadBoundedCompiledCount(row[32..], 1_000_000, "graph record");
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[36..]) != entries || BinaryPrimitives.ReadUInt32LittleEndian(row[44..]) != 0)
                throw new InvalidDataException("The compiled graph container layout is unsupported.");
            totalRecords = checked(totalRecords + entries);
            if (totalRecords > 1_000_000) throw new InvalidDataException("The compiled graph record limit was exceeded.");
            long recordsOffset = BinaryPrimitives.ReadUInt32LittleEndian(row[40..]);
            ValidateCompiledRange(recordsOffset, checked(entries * 16), stream.Length, "graph records");
            byte[] rows = ReadExactlyAt(stream, recordsOffset, checked(entries * 16));
            var inventory = new Dictionary<uint, string>();
            uint previousKey = 0;
            for (int j = 0; j < entries; j++)
            {
                var record = rows.AsSpan(j * 16, 16);
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(record);
                long start = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
                int logical = ReadBoundedCompiledCount(record[8..], 1_048_576, "graph logical byte");
                int stored = ReadBoundedCompiledCount(record[12..], 1_048_576, "graph stored byte");
                if (logical > stored || (j > 0 && key <= previousKey)) throw new InvalidDataException("Invalid compiled graph inventory row.");
                ValidateCompiledRange(start, stored, stream.Length, "graph payload");
                totalLogicalBytes = checked(totalLogicalBytes + logical);
                if (totalLogicalBytes > 512L * 1024 * 1024) throw new InvalidDataException("The compiled graph payload budget was exceeded.");
                byte[] payload = ReadExactlyAt(stream, start, logical);
                ReadOnlySpan<byte> semantic = payload;
                if (name.Equals("input_attributes", StringComparison.OrdinalIgnoreCase))
                {
                    int meaningful = payload.Length == 0 ? 0 : checked(1 + 4 * payload[0]);
                    if (meaningful == 0 || meaningful > payload.Length || (payload.Length != meaningful && payload.Length != ((meaningful + 7) & ~7)) || semantic[meaningful..].ContainsAnyExcept((byte)0))
                        throw new InvalidDataException("The input attribute record has invalid count or nonzero padding.");
                    semantic = semantic[..meaningful];
                }
                if (name.Equals("strings", StringComparison.OrdinalIgnoreCase))
                {
                    int stringEnd = semantic.IndexOf((byte)0);
                    int meaningful = stringEnd + 1;
                    if (stringEnd < 0 || (payload.Length != meaningful && payload.Length != ((meaningful + 3) & ~3)) ||
                        semantic[meaningful..].ContainsAnyExcept((byte)0))
                        throw new InvalidDataException("The native string record has missing termination or invalid padding.");
                    semantic = semantic[..meaningful];
                }
                inventory.Add(key, Convert.ToHexStringLower(SHA256.HashData(semantic)));
                previousKey = key;
            }
            if (!result.TryAdd(name, inventory)) throw new InvalidDataException("The compiled graph repeats a container.");
        }
        return result;
    }

    private static Dictionary<uint, CompiledMaterialRecord> ReadCompiledMaterialRecords(FileStream stream)
    {
        const int headerBytes = 16;
        const int containerRowBytes = 48;
        const int materialRowBytes = 16;
        const int textureRowBytes = 12;
        const int maximumContainers = 128;
        const int maximumMaterials = 1_000_000;
        const int maximumTableBytes = 32 * 1024 * 1024;
        const int maximumMaterialBytes = 1024 * 1024;
        const int maximumTextures = 256;

        if (stream.Length < headerBytes)
        {
            throw new InvalidDataException("The ABDM material database is shorter than its header.");
        }

        byte[] header = ReadExactlyAt(stream, 0, headerBytes);
        ReadOnlySpan<byte> headerData = header;
        if (BinaryPrimitives.ReadUInt32LittleEndian(headerData) != 0x4D44_4241 ||
            BinaryPrimitives.ReadUInt32LittleEndian(headerData[12..]) != 0)
        {
            throw new InvalidDataException("The ABDM material database header is invalid.");
        }

        int containerCount = ReadBoundedCompiledCount(headerData[4..], maximumContainers, "container");
        long containerOffset = BinaryPrimitives.ReadUInt32LittleEndian(headerData[8..]);
        int containerBytes = checked(containerCount * containerRowBytes);
        if (containerBytes > maximumTableBytes)
        {
            throw new InvalidDataException("The ABDM container table exceeds its bounded size.");
        }

        ValidateCompiledRange(containerOffset, containerBytes, stream.Length, "container table");
        byte[] containerTable = ReadExactlyAt(stream, containerOffset, containerBytes);
        int materialCount = -1;
        long materialTableOffset = -1;
        for (int index = 0; index < containerCount; index++)
        {
            ReadOnlySpan<byte> row = containerTable.AsSpan(index * containerRowBytes, containerRowBytes);
            int terminator = row[..32].IndexOf((byte)0);
            if (terminator < 0 ||
                row[(terminator + 1)..32].ContainsAnyExcept((byte)0) ||
                !IsPrintableCompiledAscii(row[..terminator]))
            {
                throw new InvalidDataException($"ABDM container {index} has an invalid name.");
            }

            string name = Encoding.ASCII.GetString(row[..terminator]);
            int count = ReadBoundedCompiledCount(row[32..], maximumMaterials, $"'{name}' entry");
            int declared = ReadBoundedCompiledCount(row[36..], maximumMaterials, $"'{name}' declared entry");
            if (count != declared || BinaryPrimitives.ReadUInt32LittleEndian(row[44..]) != 0)
            {
                throw new InvalidDataException($"ABDM container '{name}' has an unsupported layout.");
            }

            if (!name.Equals("materials", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (materialCount >= 0)
            {
                throw new InvalidDataException("The ABDM database has multiple materials containers.");
            }

            materialCount = count;
            materialTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(row[40..]);
        }

        if (materialCount < 0)
        {
            throw new InvalidDataException("The ABDM database has no materials container.");
        }

        int materialTableBytes = checked(materialCount * materialRowBytes);
        if (materialTableBytes > maximumTableBytes)
        {
            throw new InvalidDataException("The ABDM material table exceeds its bounded size.");
        }

        ValidateCompiledRange(materialTableOffset, materialTableBytes, stream.Length, "material table");
        byte[] materialTable = ReadExactlyAt(stream, materialTableOffset, materialTableBytes);
        var materials = new Dictionary<uint, CompiledMaterialRecord>(materialCount);
        uint previousHash = 0;
        for (int index = 0; index < materialCount; index++)
        {
            ReadOnlySpan<byte> row = materialTable.AsSpan(index * materialRowBytes, materialRowBytes);
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(row);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            int logicalSize = ReadBoundedCompiledCount(row[8..], maximumMaterialBytes, "material byte");
            int storedSize = ReadBoundedCompiledCount(row[12..], maximumMaterialBytes, "stored material byte");
            if (logicalSize > storedSize || (index > 0 && hash <= previousHash))
            {
                throw new InvalidDataException($"ABDM material 0x{hash:X8} has an invalid inventory row.");
            }

            ValidateCompiledRange(offset, storedSize, stream.Length, $"material 0x{hash:X8}");
            byte[] payload = ReadExactlyAt(stream, offset, logicalSize);
            if (payload.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != hash)
            {
                throw new InvalidDataException($"ABDM material 0x{hash:X8} has invalid fixed fields.");
            }

            int textureCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(18));
            if (textureCount > maximumTextures)
            {
                throw new InvalidDataException($"ABDM material 0x{hash:X8} declares too many textures.");
            }

            int textureOffset = checked(22 + BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(22)));
            int textureBytes = checked(textureCount * textureRowBytes);
            if (textureCount > 0 && (textureOffset < 24 || textureOffset > payload.Length - textureBytes))
            {
                throw new InvalidDataException($"ABDM material 0x{hash:X8} has an invalid texture table.");
            }

            var textureHashes = ImmutableArray.CreateBuilder<uint>(textureCount);
            for (int textureIndex = 0; textureIndex < textureCount; textureIndex++)
            {
                int rowOffset = textureOffset + textureIndex * textureRowBytes;
                textureHashes.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(rowOffset + 4)));
            }

            materials.Add(
                hash,
                new CompiledMaterialRecord(
                    textureHashes.ToImmutable(),
                    Convert.ToHexStringLower(SHA256.HashData(payload))));
            previousHash = hash;
        }

        return materials;
    }

    private static bool IsPrintableCompiledAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] ReadExactlyAt(FileStream stream, long offset, int count)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(count);
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static int ReadBoundedCompiledCount(ReadOnlySpan<byte> bytes, int maximum, string field)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (value > maximum)
        {
            throw new InvalidDataException($"The ABDM {field} count {value:N0} exceeds the bounded {maximum:N0} limit.");
        }

        return checked((int)value);
    }

    private static void ValidateCompiledRange(long offset, int size, long length, string field)
    {
        if (offset < 0 || size < 0 || offset > length || size > length - offset)
        {
            throw new InvalidDataException($"The ABDM {field} range is outside the database.");
        }
    }

    private static string NormalizeCompiledResourceName(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        string fileName = resourceName.Replace('\\', '/').Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        if (fileName.Length == 0 || fileName.Contains('\0') || fileName.Any(static value => value > 0x7F))
        {
            throw new InvalidDataException($"DL1 resource name '{resourceName}' is not a safe ASCII filename.");
        }

        return fileName.ToLowerInvariant();
    }

    private static uint ComputeCompiledResourceHash(string resourceName)
    {
        uint crc = 0x811C9DC5 ^ uint.MaxValue;
        foreach (byte value in Encoding.ASCII.GetBytes(NormalizeCompiledResourceName(resourceName)))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320U & mask);
            }
        }

        return crc ^ uint.MaxValue;
    }

    private static string NormalizeCompiledTextureReferenceName(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        string fileName = resourceName.Replace('\\', '/').Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        if (fileName.Length == 0 || fileName.Contains('\0') || fileName.Any(static value => value > 0x7F))
        {
            throw new InvalidDataException($"DL1 texture resource name '{resourceName}' is not a safe ASCII filename.");
        }

        return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : string.Concat(fileName, ".dds");
    }

    private static uint ComputeCompiledTextureReferenceHash(string resourceName)
    {
        uint crc = 0x811C9DC5 ^ uint.MaxValue;
        foreach (byte value in Encoding.ASCII.GetBytes(NormalizeCompiledTextureReferenceName(resourceName)))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320U & mask);
            }
        }

        return crc ^ uint.MaxValue;
    }

    private static void ValidateCompiledResourceNames(
        IReadOnlyList<Rp6lResourceDescriptor> resources,
        IEnumerable<string> sourceFiles,
        short resourceType,
        string kind)
    {
        string[] expectedNames = sourceFiles
            .Select(static path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expectedNames.Length == 0)
        {
            return;
        }

        HashSet<string> actualNames = resources
            .Where(resource => resource.ResourceType == resourceType)
            .Select(static resource => resource.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missing = expectedNames
            .Where(name => !actualNames.Contains(name))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException(
                $"The compiled RP6L is missing {kind} resource(s): {string.Join(", ", missing)}. " +
                "No model RPack was published.");
        }
    }

    private static string CreateCommonOverrideScript() => "sub main()\n{\n}\n";

    private static void ValidateRequest(Dl1OfficialModelCompilerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        request.Model.Package.Document.Validate();
        if (!request.Model.Package.Document.Bones.IsEmpty)
        {
            _ = request.Model.Package.Document
                .CreateDl1AnimationRigDefinition();
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CompilerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputRpackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceName);
        if (request.WorkingDirectoryRoot is not null) ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectoryRoot);
        if (!string.IsNullOrWhiteSpace(request.CharacterId))
        {
            _ = Dl1DeveloperToolsProjectDeployer.NormalizeCharacterId(request.CharacterId);
        }
        if (!string.IsNullOrWhiteSpace(request.ExistingMaterialDatabasePath) &&
            !File.Exists(request.ExistingMaterialDatabasePath))
        {
            throw new FileNotFoundException(
                "The requested existing material database was not found.",
                request.ExistingMaterialDatabasePath);
        }
        if (!string.IsNullOrWhiteSpace(request.ExistingMaterialSourceRoot) &&
            !Directory.Exists(Path.Combine(request.ExistingMaterialSourceRoot, "data")))
        {
            throw new DirectoryNotFoundException(
                "The existing material source root has no Developer Tools data directory.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurfaceName);
        if (!File.Exists(request.CompilerExecutablePath))
        {
            throw new FileNotFoundException("Techland's ResPack compiler was not found.", request.CompilerExecutablePath);
        }

        _ = ResolveMaterialCompilerExecutable(Path.GetFullPath(request.CompilerExecutablePath));

        if (string.IsNullOrWhiteSpace(request.RetailData0PakPath) ||
            !File.Exists(request.RetailData0PakPath))
        {
            throw new FileNotFoundException(
                "The retail DL1 DW\\Data0.pak bootstrap was not found. Index or select a complete Dying Light 1 installation before compiling a model RPack.",
                request.RetailData0PakPath);
        }

        if (!string.Equals(Path.GetExtension(request.OutputRpackPath), ".rpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The compiled model output must use the .rpack extension.", nameof(request));
        }

        if (request.Model.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary)
        {
            if (request.AnimationLibrary is not null)
                throw new ArgumentException("Stock-reference compilation cannot stage an authored animation library.", nameof(request));
            _ = Dl1StockAnimationReferenceValidator.Validate(request.RetailData0PakPath!,
                request.AnimationScriptAlias ?? throw new ArgumentException("Stock-reference compilation requires its animation bank alias.", nameof(request)));
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The compiler timeout must be between zero and one hour.");
        }
    }

    internal static async Task<int> StageCompleteMaterialSourcesAsync(
        string projectRoot,
        string currentSourceDirectory,
        string outputDirectory,
        CancellationToken cancellationToken,
        string? stockMaterialRoot = null)
    {
        const int maximumMaterialSources = 16_384;
        const int maximumMaterialSourceBytes = 1_048_576;
        const long maximumTotalMaterialSourceBytes = 256L * 1024 * 1024;
        string dataDirectory = Path.GetFullPath(Path.Combine(projectRoot, "data"));
        if ((File.GetAttributes(dataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The project material data directory cannot be a linked directory.");
        var sources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        // The Developer Tools default local_dx11.mp contains stock shadow-caster
        // records even in a newly created empty project. Their source .dmt files
        // live beside the compiler, outside the project data directory. Include
        // those exact installed sources before rebuilding from project sources;
        // otherwise Techland's full rebuild drops the default material records.
        if (stockMaterialRoot is not null)
        {
            string stockDirectory = Path.GetFullPath(stockMaterialRoot);
            if (!Directory.Exists(stockDirectory) ||
                (File.GetAttributes(stockDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installed Developer Tools stock material source directory is missing or linked.");
            foreach (string path in Directory.EnumerateFiles(stockDirectory, "*.dmt", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(path);
                if (sources.Count >= maximumMaterialSources)
                    throw new InvalidDataException("The installed Developer Tools contains too many stock material sources for a bounded rebuild.");
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > maximumMaterialSourceBytes)
                    throw new InvalidDataException($"Installed stock material source '{name}' is linked or exceeds the bounded size.");
                byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                totalBytes = checked(totalBytes + bytes.Length);
                if (bytes.Length > maximumMaterialSourceBytes || totalBytes > maximumTotalMaterialSourceBytes)
                    throw new InvalidDataException("Installed stock material sources exceed the bounded full-rebuild size.");
                if (sources.TryGetValue(name, out byte[]? previous) && !previous.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException($"Installed stock material source basename '{name}' is duplicated with different content.");
                sources[name] = bytes;
            }
        }
        foreach (string path in Directory.EnumerateFiles(dataDirectory, "*.dmt", enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (sources.Count >= maximumMaterialSources)
                throw new InvalidDataException("The project contains too many material sources for a bounded full rebuild.");
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > maximumMaterialSourceBytes)
                throw new InvalidDataException($"Project material source '{name}' is linked or exceeds the bounded size.");
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            totalBytes = checked(totalBytes + bytes.Length);
            if (bytes.Length > maximumMaterialSourceBytes || totalBytes > maximumTotalMaterialSourceBytes)
                throw new InvalidDataException("Project material sources exceed the bounded full-rebuild size.");
            if (sources.TryGetValue(name, out byte[]? existing) && !existing.AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException($"Project material source basename '{name}' is duplicated with different content.");
            sources[name] = bytes;
        }

        foreach (string path in Directory.EnumerateFiles(currentSourceDirectory, "*.dmt", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > maximumMaterialSourceBytes)
                throw new InvalidDataException($"Current model material source '{name}' exceeds the bounded size.");
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > maximumTotalMaterialSourceBytes)
                throw new InvalidDataException("Material sources exceed the bounded full-rebuild size.");
            sources[name] = bytes;
        }

        if (sources.Count == 0 || sources.Count > maximumMaterialSources)
            throw new InvalidDataException("The full material rebuild has no bounded source inventory.");
        Directory.CreateDirectory(outputDirectory);
        foreach ((string name, byte[] bytes) in sources.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, name), bytes, cancellationToken)
                .ConfigureAwait(false);
        }
        return sources.Count;
    }

    internal static void ValidateCompiledMorphOutput(
        FbxModelAuthoringImportResult source,
        CompiledMeshGeometryDocument compiled,
        ImmutableArray<Dl1PreparedMorphSurfaceExpectation> prepared = default,
        ImmutableArray<Dl1CompiledSkinVertexCorrespondence> correspondence = default)
    {
        ValidateCompiledSpeechOrder(source.Package.Document.MorphChannels.Select(channel => channel.Name),
            compiled.MorphChannels.Select(channel => channel.Name));
        string[] expectedChannelNames = source.Package.Document.MorphChannels
            .Select(static channel => channel.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualChannelNames = compiled.MorphChannels
            .Select(static channel => channel.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expectedChannelNames.SequenceEqual(
                actualChannelNames,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The official compiler output did not preserve the exact custom-model morph channel names. " +
                $"Expected [{string.Join(", ", expectedChannelNames)}], got " +
                $"[{string.Join(", ", actualChannelNames)}]. No model RPack was published.");
        }

        int count = prepared.IsDefault ? source.Surfaces.Length : prepared.Length;
        for (int surfaceIndex = 0; surfaceIndex < count; surfaceIndex++)
        {
            Dl1PreparedMorphSurfaceExpectation expectedLod = prepared.IsDefault
                ? new(string.Empty, 0, source.Surfaces[surfaceIndex].Vertices.Length,
                    source.Surfaces[surfaceIndex].MorphTargets.Select(target => new Dl1PreparedMorphTargetExpectation(target.Name, target.PositionDeltas)).ToImmutableArray())
                : prepared[surfaceIndex];
            CompiledMeshSurface? compiledSurface = prepared.IsDefault
                ? surfaceIndex < compiled.Surfaces.Count ? compiled.Surfaces[surfaceIndex] : null
                : compiled.Surfaces
                    .Where(surface => surface.LodIndex == expectedLod.LodIndex &&
                        string.Equals(surface.Name, expectedLod.NodeName, StringComparison.OrdinalIgnoreCase))
                    .ToArray() switch
                {
                    [] => null,
                    [var only] => only,
                    _ => throw new InvalidDataException(
                        $"The official compiler emitted ambiguous case-insensitive geometry identity '{expectedLod.NodeName}' LOD {expectedLod.LodIndex}. No model RPack was published."),
                };
            if (compiledSurface is null)
                throw new InvalidDataException($"The official compiler omitted prepared geometry '{expectedLod.NodeName}' LOD {expectedLod.LodIndex}. No model RPack was published.");
            CompiledNodeMorphBinding[] bindings = compiled.MorphBindings
                .Where(binding => binding.EntityIndex == compiledSurface.EntityIndex && binding.LodIndex == compiledSurface.LodIndex).ToArray();
            if (expectedLod.MorphTargets.IsEmpty)
            {
                if (bindings.Any(static binding => binding.TargetDeltas.Count != 0))
                    throw new InvalidDataException($"The official compiler added morph targets to '{compiledSurface.Name}' LOD {compiledSurface.LodIndex}.");
                continue;
            }
            if (bindings.Length != 1)
                throw new InvalidDataException($"The official compiler emitted {bindings.Length} morph bindings for '{compiledSurface.Name}' LOD {compiledSurface.LodIndex}; exactly one is required. No model RPack was published.");
            CompiledNodeMorphBinding binding = bindings[0];
            if (binding.VertexCount != expectedLod.VertexCount || binding.TargetDeltas.Count != expectedLod.MorphTargets.Length)
                throw new InvalidDataException($"The official compiler changed morph dimensions for '{compiledSurface.Name}' LOD {compiledSurface.LodIndex}. No model RPack was published.");
            if (binding.TargetDeltas.Select(target => target.MorphChannelIndex).Distinct().Count() != binding.TargetDeltas.Count)
                throw new InvalidDataException($"The official compiler duplicated a morph target for '{compiledSurface.Name}' LOD {compiledSurface.LodIndex}.");
            if (binding.DeltaFormat != CompiledMorphDeltaFormat.PcHalf4)
            {
                throw new InvalidDataException(
                    $"The official PC compiler emitted unsupported morph encoding " +
                    $"'{binding.DeltaFormat}' for surface '{compiledSurface.Name}'; " +
                    "PC output must use HALF4. No model RPack was published.");
            }

            foreach (CompiledMorphTargetDeltas actualTarget in binding.TargetDeltas)
            {
                if (actualTarget.MorphChannelIndex >= compiled.MorphChannels.Count)
                {
                    throw new InvalidDataException(
                        "The official compiler emitted an out-of-range morph channel index.");
                }

                string targetName = compiled.MorphChannels[
                    actualTarget.MorphChannelIndex].Name;
                Dl1PreparedMorphTargetExpectation expectedTarget = expectedLod.MorphTargets
                    .SingleOrDefault(target => string.Equals(
                        target.Name,
                        targetName,
                        StringComparison.Ordinal)) ??
                    throw new InvalidDataException(
                        $"The official compiler emitted unexpected morph target '{targetName}' " +
                        $"for surface '{compiledSurface.Name}'.");
                if (actualTarget.PositionDeltas.Count !=
                    expectedTarget.PositionDeltas.Length)
                {
                    throw new InvalidDataException(
                        $"The official compiler changed morph target '{targetName}'s vertex count.");
                }

                for (int vertexIndex = 0;
                     vertexIndex < actualTarget.PositionDeltas.Count;
                     vertexIndex++)
                {
                    Vector3 actual = actualTarget.PositionDeltas[vertexIndex];
                    int sourceVertexIndex = vertexIndex;
                    if (!correspondence.IsDefaultOrEmpty)
                    {
                        var match = correspondence.SingleOrDefault(row => row.NodeName == compiledSurface.Name &&
                            row.LodIndex == compiledSurface.LodIndex && row.CompiledVertexIndex == vertexIndex);
                        if (match is null) throw new InvalidDataException("Compiled morph vertex has no verified source correspondence.");
                        sourceVertexIndex = match.SourceVertexIndex;
                    }
                    CoreVector3 expected = expectedTarget.PositionDeltas[sourceVertexIndex];
                    try
                    {
                        ValidateHalfMorphComponent(
                            actual.X,
                            expected.X,
                            targetName,
                            vertexIndex,
                            "X");
                        ValidateHalfMorphComponent(
                            actual.Y,
                            expected.Y,
                            targetName,
                            vertexIndex,
                            "Y");
                        ValidateHalfMorphComponent(
                            actual.Z,
                            expected.Z,
                            targetName,
                            vertexIndex,
                            "Z");
                    }
                    catch (InvalidDataException exception)
                    {
                        string compiledPreview = string.Join(
                            "; ",
                            actualTarget.PositionDeltas.Take(8)
                                .Select((delta, index) =>
                                    $"{index}:({delta.X:R},{delta.Y:R},{delta.Z:R})"));
                        string compiledPositions = string.Join(
                            "; ",
                            compiledSurface.Vertices.Take(8)
                                .Select((vertex, index) =>
                                    $"{index}:({vertex.Position.X:R},{vertex.Position.Y:R},{vertex.Position.Z:R})"));
                        string compiledLayout = compiledSurface.VertexLayout is null
                            ? "unavailable"
                            : string.Join(
                                "; ",
                                compiledSurface.VertexLayout.Elements.Select(element =>
                                    $"format={element.RawFormat},semantic={element.RawSemantic},channel={element.Channel},offset={element.ByteOffset},size={element.ByteSize}"));
                        throw new InvalidDataException(
                            $"{exception.Message} Compiled delta preview [{compiledPreview}]. " +
                            $"Compiled position preview [{compiledPositions}]. " +
                            $"Compiled declaration [{compiledLayout}].",
                            exception);
                    }
                }
            }
        }
    }

    internal static void ValidateCompiledSpeechOrder(IEnumerable<string> sourceNames, IEnumerable<string> compiledNames)
    {
        string[] source = sourceNames.ToArray();
        string[] compiled = compiledNames.ToArray();
        foreach (string label in SpeechLabels)
        {
            string? expected = source.FirstOrDefault(name => name.Equals(label, StringComparison.OrdinalIgnoreCase));
            if (expected is null) continue;
            string? actual = compiled.FirstOrDefault(name => name.StartsWith(label, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Compiled morph inventory binds speech label '{label}' to '{actual ?? "<missing>"}' instead of '{expected}' under Windows Player prefix-first lookup. No model RPack was published.");
        }
    }

    internal static void ValidateHalfMorphComponent(
        float actual,
        double expected,
        string targetName,
        int vertexIndex,
        string component)
    {
        float sourceFloat = (float)expected;
        Half expectedHalf = (Half)sourceFloat;
        float expectedQuantized = (float)expectedHalf;
        if (!float.IsFinite(sourceFloat) ||
            !Half.IsFinite(expectedHalf) ||
            !float.IsFinite(actual) ||
            (float)(Half)actual != actual ||
            // Both adjacent HALF values are valid at an exact midpoint. The
            // native compiler and CLR need not choose the same tie direction.
            Math.Abs((double)actual - sourceFloat) != Math.Abs((double)expectedQuantized - sourceFloat))
        {
            throw new InvalidDataException(
                $"The official compiler did not preserve morph '{targetName}' vertex " +
                $"{vertexIndex} {component} through the PC HALF4 conversion " +
                $"(source {expected:R}, expected half {expectedQuantized:R}, compiled {actual:R}). " +
                "No model RPack was published.");
        }
    }

    private static string ResolveDeveloperToolsDataDirectory(string compilerPath)
    {
        string compilerDirectory = Path.GetDirectoryName(compilerPath)
            ?? throw new InvalidOperationException("The compiler path has no parent directory.");
        string[] candidates =
        [
            Path.Combine(compilerDirectory, "Engine", "Data"),
            Path.Combine(compilerDirectory, "engine", "data"),
            Path.Combine(Directory.GetParent(compilerDirectory)?.FullName ?? compilerDirectory, "Engine", "Data"),
        ];
        return candidates.FirstOrDefault(Directory.Exists)
            ?? throw new DirectoryNotFoundException(
                "The selected compiler has no adjacent Developer Tools Engine/Data bootstrap directory.");
    }

    private static string ResolveMaterialCompilerExecutable(string compilerPath)
    {
        string compilerDirectory = Path.GetDirectoryName(compilerPath)
            ?? throw new InvalidOperationException("The compiler path has no parent directory.");
        string materialCompilerPath = Path.Combine(compilerDirectory, "MEConv_x64_rwdi.exe");
        if (!File.Exists(materialCompilerPath))
        {
            throw new FileNotFoundException(
                "The selected Developer Tools installation has no adjacent MEConv_x64_rwdi.exe material compiler.",
                materialCompilerPath);
        }

        return materialCompilerPath;
    }

    private static async Task StageModelAnimationSourcesAsync(
        Dl1OfficialModelCompilerRequest request,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        PreparedCustomModelAnimationLibrary library = request.AnimationLibrary
            ?? throw new InvalidOperationException("Animation library was not supplied.");
        if (string.IsNullOrWhiteSpace(request.AnimationScriptAlias) ||
            !string.Equals(
                library.AnimationScriptName,
                request.AnimationScriptAlias,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The model compiler animation library must exactly match its ASCR alias identity.");
        }

        CustomModelAnimationLibraryExporter.ValidateLooseScriptCoversInventory(
            library);

        string animationDirectory = Path.Combine(
            projectDirectory,
            "data",
            "characters",
            "animations");
        string scriptDirectory = Path.Combine(animationDirectory, "animscripts");
        Directory.CreateDirectory(scriptDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(scriptDirectory, $"{library.AnimationScriptName}.scr"),
            library.LooseScriptText,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        foreach (PreparedCustomModelAnimation animation in library.Animations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string expectedFileName = Dl1SourceModelWriter.RequireExactResourceName(
                animation.Name,
                63,
                "animation resource name") + ".anm2";
            if (!string.Equals(animation.Anm2FileName, expectedFileName, StringComparison.Ordinal) ||
                animation.Payload is not { Length: > 0 })
            {
                throw new InvalidDataException(
                    $"Prepared animation '{animation.Name}' has an invalid loose source contract.");
            }

            await File.WriteAllBytesAsync(
                Path.Combine(animationDirectory, animation.Anm2FileName),
                animation.Payload,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void CopyCompilerBootstrap(string sourceRoot, string projectDirectory)
    {
        foreach ((string targetRelative, string[] sourceCandidates) in BootstrapFiles)
        {
            string? source = sourceCandidates
                .Select(candidate => Path.Combine(sourceRoot, candidate.Replace('/', Path.DirectorySeparatorChar)))
                .FirstOrDefault(File.Exists);
            if (source is null)
            {
                continue;
            }

            string target = Path.Combine(projectDirectory, targetRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        string engineDefs = Path.Combine(projectDirectory, "data", "enginedefs.mth");
        string resourcePackCfg = Path.Combine(projectDirectory, "data", "resourcepackcfg.scr");
        if (!File.Exists(engineDefs) || !File.Exists(resourcePackCfg))
        {
            throw new InvalidDataException(
                "The selected Developer Tools installation is missing EngineDefs.mth or ResourcePackCfg.scr.");
        }
    }

    private static async Task CopyRetailCompilerBootstrapAsync(
        string data0PakPath,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        await using FileStream packageStream = new(
            Path.GetFullPath(data0PakPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            string lower = normalized.ToLowerInvariant();
            bool exact = lower is
                "data/enginedefs.mth" or
                "data/resourcepackcfg.scr" or
                "data/varlist_descriptions.scr" or
                "data/varlist_descriptions_game.scr" or
                "data/quickaccessvarsdefault.scr" or
                "data/map_creator_varlist.scr";
            int lastSlash = lower.LastIndexOf('/');
            string fileName = lastSlash >= 0 ? lower[(lastSlash + 1)..] : lower;
            bool varlistScript =
                lower.StartsWith("data/scripts/", StringComparison.Ordinal) &&
                fileName.StartsWith("varlist", StringComparison.Ordinal) &&
                (fileName.EndsWith(".scr", StringComparison.Ordinal) ||
                 fileName.EndsWith(".scd", StringComparison.Ordinal));
            if (!exact && !varlistScript)
            {
                continue;
            }

            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or ".."))
            {
                throw new InvalidDataException(
                    $"Retail compiler bootstrap contains an unsafe entry path: {entry.FullName}");
            }

            if (entry.Length < 0 || entry.Length > MaximumBootstrapEntryBytes)
            {
                throw new InvalidDataException(
                    $"Retail compiler bootstrap entry '{entry.FullName}' exceeds the bounded {MaximumBootstrapEntryBytes:N0}-byte limit.");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumBootstrapTotalBytes)
            {
                throw new InvalidDataException(
                    $"Retail compiler bootstrap exceeds the bounded {MaximumBootstrapTotalBytes:N0}-byte aggregate limit.");
            }

            string target = Path.Combine(
                projectDirectory,
                normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                // The installed Developer Tools bootstrap is authoritative.
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using Stream source = entry.Open();
            await using FileStream destination = new(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        StageVarlistAliases(projectDirectory);
        string rootVarlist = Path.Combine(projectDirectory, "varlist.scr");
        string rootVarlistMain = Path.Combine(projectDirectory, "varlist_main.scr");
        if (!File.Exists(rootVarlist) || !File.Exists(rootVarlistMain))
        {
            throw new InvalidDataException(
                "Retail Data0.pak did not provide the varlist.scr/varlist_main.scr compiler bootstrap expected by this DL1 build.");
        }
    }

    private static void StageVarlistAliases(string projectDirectory)
    {
        foreach ((string sourceRelative, string[] targetRelatives) in new[]
                 {
                     (
                         "data/scripts/varlist.scr",
                         new[] { "data/varlist.scr", "varlist.scr" }),
                     (
                         "data/scripts/varlist_main.scr",
                         new[] { "data/varlist_main.scr", "varlist_main.scr" }),
                 })
        {
            string source = Path.Combine(
                projectDirectory,
                sourceRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                continue;
            }

            foreach (string targetRelative in targetRelatives)
            {
                string target = Path.Combine(
                    projectDirectory,
                    targetRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
        }
    }

    private static async Task<ProcessResult> RunCompilerStageAsync(
        string compilerPath,
        ImmutableArray<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = compilerPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Techland's ResPack compiler could not be started.");
        }

        using CancellationTokenSource timeoutCancellation = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        Task<string> stdout = ReadBoundedAsync(process.StandardOutput, linked.Token);
        Task<string> stderr = ReadBoundedAsync(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            string[] streams = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                string.Join("\r\n", streams.Where(static value => !string.IsNullOrWhiteSpace(value))));
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            if (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Techland's model compiler exceeded the {timeout:g} timeout.");
            }

            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        bool truncated = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (output.Length + line.Length + 2 <= MaximumCompilerLogCharacters)
            {
                output.AppendLine(line);
            }
            else
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            output.AppendLine("[additional compiler output omitted after the bounded 4 MiB diagnostic limit]");
        }

        return output.ToString();
    }

    private static string FindCompilerOutput(string root, string fileName)
    {
        string? match = TryFindCompilerOutput(root, fileName);
        if (match is null)
        {
            throw new InvalidDataException(
                $"Techland's compiler completed but did not emit '{fileName}'. No output was published.");
        }

        return match;
    }

    private static string? TryFindCompilerOutput(string root, string fileName) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(
                Path.GetFileName(path),
                fileName,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path.Contains("CompilerOutput", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .FirstOrDefault();

    private static string CreateInputFingerprint(
        Dl1OfficialModelCompilerRequest request,
        string resourceName) =>
        CalculateInputFingerprint(
            request.Model,
            resourceName,
            request.SurfaceName,
            request.AnimationScriptAlias);

    private static async Task PublishFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string temporaryPath = destinationPath + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream source = new(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task PublishBytesAtomicallyAsync(
        byte[] bytes,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string temporaryPath = destinationPath + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AppendBounded(StringBuilder target, string value)
    {
        int available = MaximumCompilerLogCharacters - target.Length;
        if (available <= 0)
        {
            return;
        }

        target.Append(value.AsSpan(0, Math.Min(value.Length, available)));
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteOwnedJobDirectory(string jobContainer, string jobDirectory)
    {
        string container = Path.GetFullPath(jobContainer).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string job = Path.GetFullPath(jobDirectory);
        string relative = Path.GetRelativePath(container, job);
        if (!job.StartsWith(container, StringComparison.OrdinalIgnoreCase) ||
            relative.Contains(Path.DirectorySeparatorChar) ||
            !Guid.TryParseExact(relative, "N", out _))
        {
            return;
        }

        try
        {
            if (Directory.Exists(job))
            {
                Directory.Delete(job, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddProgramFilesCandidate(List<string> candidates, string programFiles)
    {
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(
                programFiles,
                "Steam",
                "steamapps",
                "common",
                "Dying Light Developer Tools",
                "ResPackCompilerConsole_x64_rwdi.exe"));
        }
    }

    private static string EscapeScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record CompiledMaterialRecord(
        ImmutableArray<uint> TextureNameHashes,
        string PayloadSha256);

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed record CompilerResourceUnit(
        string VirtualSourcePath,
        string ExpectedObjectFileName,
        string OutputDirectory);
}




