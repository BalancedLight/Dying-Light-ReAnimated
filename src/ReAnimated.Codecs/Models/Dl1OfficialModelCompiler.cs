using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

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

    public string SurfaceName { get; init; } = "default";

    public string? AnimationScriptAlias { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
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
    string CompilerLog);

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
    private const int MaximumCompilerLogCharacters = 4 * 1024 * 1024;
    private const long MaximumBootstrapEntryBytes = 16L * 1024L * 1024L;
    private const long MaximumBootstrapTotalBytes = 64L * 1024L * 1024L;
    private const int WindowsAccessViolationExitCode = unchecked((int)0xC0000005);
    private const string CompilerEnvironmentVariable = "DLR_DL1_RESPACK_COMPILER";

    private static readonly string[] UnsupportedCompiledOutputs = [".chr", ".skn"];

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
        string compilerFingerprint = await Sha256FileAsync(compilerPath, cancellationToken).ConfigureAwait(false);
        string materialCompilerFingerprint = await Sha256FileAsync(
            materialCompilerPath,
            cancellationToken).ConfigureAwait(false);
        string jobContainer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLReAnimated",
            "ModelCompiler",
            "Jobs");
        string jobId = Guid.NewGuid().ToString("N");
        string jobDirectory = Path.Combine(jobContainer, jobId);
        string workshopDirectory = Path.Combine(jobDirectory, "Workshop");
        string projectName = $"_DLReAnimatedModelImporter_{jobId[..8]}";
        string projectDirectory = Path.Combine(workshopDirectory, projectName);
        string virtualDirectory = $"data/characters/dl_reanimated/imported/{resourceName}";
        string virtualMshPath = $"{virtualDirectory}/{resourceName}.msh";
        string stagedSourceDirectory = Path.Combine(
            projectDirectory,
            virtualDirectory.Replace('/', Path.DirectorySeparatorChar));
        string rsrcPath = Path.Combine(projectDirectory, "model_resources.rsrc");
        string rulesPath = Path.Combine(projectDirectory, "model_resource.rules");
        string outputName = $"{resourceName}_pc.rpack";
        var compilerLog = new StringBuilder();
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
                },
                cancellationToken).ConfigureAwait(false);

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
            string? compiledMaterialDatabase = null;
            if (locallyAuthoredMaterialReferences.Length > 0)
            {
                AppendBounded(compilerLog, "Material compiler stage\r\n");
                ProcessResult materialProcess = await RunCompilerStageAsync(
                    materialCompilerPath,
                    CreateMaterialCompilerCommand(
                        projectDirectory,
                        workshopDirectory,
                        virtualDirectory),
                    projectDirectory,
                    request.Timeout,
                    cancellationToken).ConfigureAwait(false);
                AppendBounded(compilerLog, materialProcess.Output);
                if (materialProcess.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        $"Techland material compiler exited with code {materialProcess.ExitCode}. " +
                        "No model bundle was published.");
                }

                compiledMaterialDatabase = Path.Combine(
                    projectDirectory,
                    "Assets_PC",
                    "local_dx11.mp");
                ValidateCompiledMaterialDatabase(
                    compiledMaterialDatabase,
                    projectDirectory,
                    locallyAuthoredMaterialReferences);
            }

            ImmutableArray<CompilerResourceUnit> units = CreateCompilerResourceUnits(
                resourceName,
                virtualMshPath,
                virtualDirectory,
                sourceBuild.TextureSourceFiles,
                jobDirectory);
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
                    if (process.ExitCode == WindowsAccessViolationExitCode &&
                        emittedObject is not null)
                    {
                        AppendBounded(
                            compilerLog,
                            $"Techland compiler exited with access violation 0xC0000005 after emitting {Path.GetFileName(emittedObject)}. " +
                            "The isolated object will be admitted only if bounded linking and ordinary RP6L validation both pass.\r\n");
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"Techland model compiler stage {stage + 1} exited with code {process.ExitCode}. " +
                            $"It did not emit the expected '{unit.ExpectedObjectFileName}'. No model bundle was published.");
                    }
                }

                compiledObjects.Add(emittedObject ?? FindCompilerOutput(
                    unit.OutputDirectory,
                    unit.ExpectedObjectFileName));
            }

            string compiledMeshObject = compiledObjects[0];
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
            int verifiedIndexCount;
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

                verifiedEntityCount = hierarchy.Entities.Count;
                verifiedSurfaceCount = geometry.Surfaces.Count;
                verifiedVertexCount = geometry.VertexCount;
                verifiedIndexCount = geometry.IndexCount;
            }

            string outputDirectory = Path.GetDirectoryName(outputRpackPath)
                ?? throw new InvalidOperationException("The model RPack output path has no parent directory.");
            Directory.CreateDirectory(outputDirectory);
            string outputObjectPath = Path.Combine(outputDirectory, $"{resourceName}.msh_obj");
            string? outputMaterialDatabasePath = compiledMaterialDatabase is null
                ? null
                : Path.Combine(outputDirectory, "local_dx11.mp");
            await PublishFileAtomicallyAsync(compiledRpack, outputRpackPath, cancellationToken).ConfigureAwait(false);
            await PublishFileAtomicallyAsync(compiledMeshObject, outputObjectPath, cancellationToken).ConfigureAwait(false);
            if (compiledMaterialDatabase is not null && outputMaterialDatabasePath is not null)
            {
                await PublishFileAtomicallyAsync(
                    compiledMaterialDatabase,
                    outputMaterialDatabasePath,
                    cancellationToken).ConfigureAwait(false);
            }

            string outputRpackSha256 = await Sha256FileAsync(outputRpackPath, cancellationToken).ConfigureAwait(false);
            string outputObjectSha256 = await Sha256FileAsync(outputObjectPath, cancellationToken).ConfigureAwait(false);
            string? outputMaterialDatabaseSha256 = outputMaterialDatabasePath is null
                ? null
                : await Sha256FileAsync(
                    outputMaterialDatabasePath,
                    cancellationToken).ConfigureAwait(false);
            string outputManifestFingerprint = Sha256(
                Encoding.UTF8.GetBytes(
                    $"rpack={outputRpackSha256}\nmsh_obj={outputObjectSha256}\nlocal_dx11.mp={outputMaterialDatabaseSha256 ?? "none"}\n"));
            string inputFingerprint = CreateInputFingerprint(request, resourceName);
            string toolFingerprint = Sha256(Encoding.UTF8.GetBytes(
                "dl-reanimated-csharp-model-compiler-material-texture-link-v4"));
            CustomModelBuildReceipt buildReceipt = new()
            {
                InputFingerprint = inputFingerprint,
                ToolFingerprint = toolFingerprint,
                CompilerFingerprint = compilerFingerprint,
                OutputManifestFingerprint = outputManifestFingerprint,
                State = CustomModelBuildState.GameReady,
                CompletedUtc = DateTimeOffset.UtcNow,
                BlockingReasons =
                [
                    ".chr and .skn remain unsupported until an evidence-backed writer is validated.",
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
                    state = CustomModelBuildState.GameReady.ToString(),
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
                        toolFingerprint,
                        normalization.ConvertedResourceCount,
                    },
                    outputs = new[]
                    {
                        new { path = Path.GetFileName(outputRpackPath), sha256 = outputRpackSha256 },
                        new { path = Path.GetFileName(outputObjectPath), sha256 = outputObjectSha256 },
                        outputMaterialDatabasePath is null
                            ? null
                            : new
                            {
                                path = Path.GetFileName(outputMaterialDatabasePath),
                                sha256 = outputMaterialDatabaseSha256!,
                            },
                    }.Where(static output => output is not null),
                    resources = archive.Resources.Select(static resource => new
                    {
                        resource.Name,
                        resource.ResourceType,
                        resource.ItemCount,
                    }),
                    verification = new
                    {
                        entities = verifiedEntityCount,
                        surfaces = verifiedSurfaceCount,
                        vertices = verifiedVertexCount,
                        indices = verifiedIndexCount,
                        customMaterials = sourceBuild.CustomMaterialReferences.Length,
                        textures = sourceBuild.TextureSourceFiles.Length,
                    },
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
                compilerLog.ToString());
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
        string virtualDirectory)
    {
        string projectName = Path.GetFileName(
            projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException(
                "The material compiler project directory must end in a project folder name.",
                nameof(projectDirectory));
        }

        return
        [
            "-u",
            "local",
            "-p",
            "dx11",
            "-savedeps",
            "-rebuild",
            "-logfile",
            "Assets_PC/dl-reanimated-materials.log",
            "-game",
            projectName,
            "-gamedir",
            workshopDirectory.Replace('\\', '/').TrimEnd('/') + "/",
            $"{virtualDirectory.TrimEnd('/')}/*.dmt",
        ];
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
        string projectDirectory,
        IEnumerable<string> customMaterialReferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(customMaterialReferences);
        if (!File.Exists(materialDatabasePath))
        {
            throw new InvalidDataException(
                "Techland's material compiler did not emit Assets_PC/local_dx11.mp. No model bundle was published.");
        }

        using (FileStream stream = new(
                   materialDatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            Span<byte> magic = stackalloc byte[4];
            if (stream.Length < 16 || stream.Read(magic) != magic.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(magic) != 0x4D44_4241)
            {
                throw new InvalidDataException(
                    "Techland's material compiler emitted an invalid ABDM local_dx11.mp. No model bundle was published.");
            }
        }

        string materialDependencyRoot = Path.Combine(projectDirectory, "Assets_PC");
        string[] missing = customMaterialReferences
            .Select(static reference => $"{Path.GetFileNameWithoutExtension(reference.Replace('\\', '/'))}.dmt_obj_dep")
            .Where(name => Directory.GetFiles(
                materialDependencyRoot,
                name,
                SearchOption.AllDirectories).Length == 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException(
                $"Techland's material compiler did not compile material source(s): {string.Join(", ", missing)}. " +
                "No model bundle was published.");
        }
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CompilerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputRpackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceName);
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

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The compiler timeout must be between zero and one hour.");
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
        Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .OrderByDescending(path => path.Contains("CompilerOutput", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .FirstOrDefault();

    private static string CreateInputFingerprint(
        Dl1OfficialModelCompilerRequest request,
        string resourceName)
    {
        CustomModelPackage fingerprintPackage = new(
            request.Model.Package.Document with { LastBuildReceipt = null },
            request.Model.Package.SourceFbx,
            request.Model.Package.TexturePayloads);
        ImmutableArray<byte> packageBytes =
            CustomModelPackageSerializer.Serialize(fingerprintPackage);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("dl-reanimated-model-compiler-input-v2\0"u8);
        hash.AppendData(packageBytes.AsSpan());
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"\0resource={resourceName}\0surface={request.SurfaceName}\0animationAlias={request.AnimationScriptAlias ?? string.Empty}"));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

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

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed record CompilerResourceUnit(
        string VirtualSourcePath,
        string ExpectedObjectFileName,
        string OutputDirectory);
}
