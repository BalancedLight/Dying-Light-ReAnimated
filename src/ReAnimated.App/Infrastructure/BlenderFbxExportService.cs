using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public sealed class BlenderFbxExportService :
    IBlenderFbxExportService
{
    public const string JobFormat =
        "dl-reanimated-csharp-blender-fbx-job";
    public const int JobSchemaVersion = 1;
    public const string HandoffFormat =
        "dl-reanimated-csharp-fbx-handoff";
    public const int HandoffSchemaVersion = 1;
    public const string FidelityLabel =
        "DL1 retail mesh; base-color-only material";
    public const string RedistributionWarning =
        "This export contains local Dying Light retail mesh and texture data. Keep it local and do not redistribute it.";
    internal const string BundleCommitFormat =
        "dl-reanimated-blender-bundle-commit";
    internal const int BundleCommitSchemaVersion = 1;
    internal const string BundleCommitPreparedPhase = "prepared";
    internal const string BundleCommitInstalledPhase = "installed";

    private const uint MotionAccumulatorDescriptor = 0xCCC3CDDF;
    private const int MaximumClipCount = 64;
    private const int MaximumMeshCount = 8_192;
    private const long MaximumOutputTransforms = 1_000_000;
    private const long MaximumAggregateVertexCount = 2_000_000;
    private const long MaximumAggregateIndexCount = 6_000_000;
    private const long MaximumTexturePayloadBytes =
        64L * 1024 * 1024;
    private const long MaximumTemporaryPayloadBytes =
        192L * 1024 * 1024;
    private const int ClipBinaryHeaderSize = 16;
    private const int MeshVertexStrideFloats = 16;
    private const int MaximumCommitJournalBytes =
        16 * 1024 * 1024;
    private const int MaximumCommitJournalDepth = 16;
    private const int MaximumCommitJournalEntries =
        MaximumMeshCount + MaximumClipCount;
    private static readonly JsonSerializerOptions JobSerializerOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.SnakeCaseLower,
        };
    private static readonly JsonSerializerOptions ManifestSerializerOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };
    private static readonly JsonSerializerOptions
        CommitJournalSerializerOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            MaxDepth = MaximumCommitJournalDepth,
        };

    private readonly IBlenderProcessRunner _processRunner;
    private readonly BlenderHelperResource _helperResource;
    private readonly IBlenderFbxOutputValidator _outputValidator;
    private readonly IBlenderBundleFileSystem _bundleFileSystem;
    private readonly TimeSpan _timeout;

    public BlenderFbxExportService(
        IBlenderProcessRunner? processRunner = null,
        BlenderHelperResource? helperResource = null,
        IBlenderFbxOutputValidator? outputValidator = null,
        TimeSpan? timeout = null)
        : this(
            processRunner,
            helperResource,
            outputValidator,
            PhysicalBlenderBundleFileSystem.Instance,
            timeout)
    {
    }

    internal BlenderFbxExportService(
        IBlenderProcessRunner? processRunner,
        BlenderHelperResource? helperResource,
        IBlenderFbxOutputValidator? outputValidator,
        IBlenderBundleFileSystem bundleFileSystem,
        TimeSpan? timeout = null)
    {
        _processRunner =
            processRunner ?? new BlenderProcessRunner();
        _helperResource =
            helperResource ?? new BlenderHelperResource();
        _outputValidator =
            outputValidator ??
            new BlenderFbxOutputValidator();
        _bundleFileSystem = bundleFileSystem ??
            throw new ArgumentNullException(
                nameof(bundleFileSystem));
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout));
        }
    }

    public async Task<BlenderFbxExportResult> ExportAsync(
        BlenderFbxExportRequest request,
        IProgress<BlenderFbxExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        string outputPath = Path.GetFullPath(
            request.OutputFbxPath);
        string outputDirectory =
            Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException(
                "The FBX output has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        using BundleTargetLock bundleLock =
            BundleTargetLock.Acquire(outputPath);
        var warnings = new List<string>();
        if (RecoverInterruptedBundle(
                outputPath,
                _bundleFileSystem))
        {
            warnings.Add(
                "Recovered an interrupted Blender bundle transaction before starting this export.");
        }

        string stageDirectory = Path.Combine(
            outputDirectory,
            $".dlr-blender-{Guid.NewGuid():N}");
        string workDirectory = Path.Combine(
            stageDirectory,
            "work");
        Directory.CreateDirectory(workDirectory);
        BlenderFbxExportResult? completedResult = null;
        bool preserveStageDirectory = false;
        try
        {
            bool hasAnimations = request.Anm2Paths.Count > 0;
            PreparedClipInfo[] clipInfo;
            if (hasAnimations)
            {
                progress?.Report(new BlenderFbxExportProgress(
                    "Reading ANM2",
                    3.0,
                    "Hash-checking clips and timing provenance"));
                clipInfo = await InspectClipsAsync(
                        request.Anm2Paths,
                        request.Rig!,
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                progress?.Report(new BlenderFbxExportProgress(
                    "Preparing mesh",
                    3.0,
                    "Preparing a mesh-only FBX with no ANM2 Actions"));
                clipInfo = [];
            }

            double outputFps = hasAnimations
                ? clipInfo[0].Timing.SourceFbxFps
                : 30.0;
            BlenderFbxJobBone[] bones = request.Rig is { } rig
                ? BuildJobBones(rig)
                : [];
            HashSet<uint> rigDescriptors = bones
                .Where(static bone =>
                    bone.Descriptor.HasValue)
                .Select(static bone =>
                    bone.Descriptor!.Value)
                .ToHashSet();
            long transformCount = clipInfo.Sum(info =>
            {
                int unresolvedCount = info.Descriptors
                    .Distinct()
                    .Count(descriptor =>
                        !rigDescriptors.Contains(descriptor));
                Anm2TemporalResamplePlan resamplePlan =
                    Anm2TemporalResampler.CreatePlan(
                        info.SourceFrameCount,
                        info.Timing.SampleFps,
                        outputFps);
                return checked(
                    (long)resamplePlan.OutputFrameCount *
                    bones.Length +
                    (long)info.SourceFrameCount *
                    unresolvedCount);
            });
            if (transformCount > MaximumOutputTransforms)
            {
                throw new InvalidDataException(
                    $"The selected clips require {transformCount:N0} exported transforms; the first-pass safety limit is {MaximumOutputTransforms:N0}.");
            }

            ValidateAggregateBounds(
                request.Meshes,
                transformCount);

            string stageFbxPath = Path.Combine(
                stageDirectory,
                Path.GetFileName(outputPath));
            BlenderFbxJobTexture[] textures = WriteTextures(
                    request.Meshes,
                    stageDirectory,
                    cancellationToken)
                .Select(texture => texture with
                {
                    EmbeddedInFbx = request.EmbedTextures,
                })
                .ToArray();
            BlenderFbxJobMesh[] meshes =
                WriteMeshes(
                    request.Meshes,
                    textures,
                    workDirectory,
                    cancellationToken);
            progress?.Report(new BlenderFbxExportProgress(
                hasAnimations ? "Preparing actions" : "Preparing mesh",
                28.0,
                hasAnimations
                    ? $"Decoding {clipInfo.Length:N0} ANM2 clip(s)"
                    : "Writing rig, geometry, skin weights, and textures"));
            IReadOnlyList<BlenderFbxJobClip> clips;
            if (hasAnimations)
            {
                clips = await WriteClipsAsync(
                        clipInfo,
                        request.Rig!,
                        bones,
                        outputFps,
                        workDirectory,
                        stageDirectory,
                        Path.GetFileName(stageFbxPath),
                        progress,
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                clips = [];
            }
            string helperPath =
                await _helperResource.ExtractAsync(
                    workDirectory,
                    cancellationToken)
                    .ConfigureAwait(false);
            var job = new BlenderFbxJob(
                JobFormat,
                JobSchemaVersion,
                stageFbxPath,
                outputFps,
                FidelityLabel,
                new BlenderFbxJobAsset(
                    request.Asset.StableKey,
                    request.Asset.ProviderId,
                    request.Asset.ResourceName,
                    request.Asset.ContentFingerprint),
                bones,
                meshes,
                textures,
                clips,
                warnings.ToArray())
            {
                EmbedTextures = request.EmbedTextures,
            };
            string jobPath = Path.Combine(
                workDirectory,
                "blender-fbx-job.json");
            await WriteJsonAsync(
                    jobPath,
                    job,
                    JobSerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new BlenderFbxExportProgress(
                "Starting Blender",
                55.0,
                hasAnimations
                    ? "Creating retail mesh, BindPose, and animation Actions"
                    : request.Rig is null
                        ? request.EmbedTextures
                            ? "Creating a static retail mesh with embedded textures"
                            : "Creating a static retail mesh"
                        : request.EmbedTextures
                            ? "Creating retail mesh and BindPose with embedded textures"
                            : "Creating retail mesh and BindPose"));
            BlenderProcessResult processResult =
                await _processRunner.RunAsync(
                    new BlenderProcessRequest(
                        request.BlenderExecutablePath,
                        helperPath,
                        jobPath,
                        _timeout),
                    line => ReportBlenderProgress(
                        line,
                        progress),
                    cancellationToken)
                    .ConfigureAwait(false);
            string log = processResult.CombinedLog;
            ValidateBlenderResult(
                processResult,
                stageFbxPath,
                clips,
                bones.Length);
            progress?.Report(new BlenderFbxExportProgress(
                "Validating FBX",
                94.0,
                "Reading written stacks, BindPose, retail geometry, and textures"));
            await _outputValidator.ValidateAsync(
                    stageFbxPath,
                    bones,
                    clips,
                    meshes,
                    textures,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            string manifestFileName =
                Path.GetFileName(outputPath) +
                ".dlrahandoff.json";
            string stageManifestPath = Path.Combine(
                stageDirectory,
                manifestFileName);
            string[] textureFileNames = textures
                .Select(static texture => texture.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyList<string> externalTextureFiles =
                request.EmbedTextures
                    ? []
                    : textureFileNames;
            string[] limitations =
            [
                hasAnimations
                    ? "Each Action is an inspection/editing take. A multi-action FBX cannot use the legacy one-clip .fbx.dlrroundtrip.json contract."
                    : "This mesh-only FBX contains no ANM2 Actions.",
                hasAnimations
                    ? "Unresolved transform tracks are preserved in hash-validated, frame-major .dlrtracks sidecars referenced by this manifest. Blender does not expose those unresolved tracks as editable armature bones."
                    : "No animation-track sidecars are present in a mesh-only FBX.",
                request.EmbedTextures
                    ? "Each decoded base-color texture is embedded in the binary FBX; no loose DDS texture dependencies are committed."
                    : "Only the decoded base-color texture is emitted; DL1 shader techniques, normal/specular/mask maps, cloth, and physics are not reproduced.",
                "Morph targets are not exported in this first Blender handoff.",
            ];
            var manifest = new BlenderFbxHandoffManifest(
                HandoffFormat,
                HandoffSchemaVersion,
                FidelityLabel,
                RedistributionWarning,
                job.Asset,
                Path.GetFileName(outputPath),
                externalTextureFiles,
                clips.Select(static clip =>
                    new BlenderFbxHandoffClip(
                        clip.ActionName,
                        clip.SourceFileName,
                        clip.SourceSha256,
                        clip.TimingMetadataStatus,
                        clip.Anm2InputFps,
                        clip.FbxOutputFps,
                        clip.SourceFrameCount,
                        clip.FbxFrameCount,
                        clip.SourceDescriptors,
                        clip.HelperTracks,
                        clip.MotionAccumulator))
                    .ToArray(),
                "child_pivot_display_v1",
                "armature_edit_rest_with_roundtrip_guard",
                limitations)
            {
                TexturesEmbedded = request.EmbedTextures,
                EmbeddedTextureFiles = request.EmbedTextures
                    ? textureFileNames
                    : [],
            };
            await WriteJsonAsync(
                    stageManifestPath,
                    manifest,
                    ManifestSerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new BlenderFbxExportProgress(
                "Committing output",
                96.0,
                request.EmbedTextures
                    ? "Moving the validated self-contained FBX into place"
                    : "Moving the validated FBX bundle into place"));
            IReadOnlyList<BlenderFbxJobTexture> bundleTextures =
                request.EmbedTextures
                    ? []
                    : textures;
            CommitBundle(
                stageFbxPath,
                stageManifestPath,
                bundleTextures,
                GetHelperSidecarFiles(clips),
                outputPath,
                _bundleFileSystem,
                cancellationToken);
            string finalManifestPath = Path.Combine(
                outputDirectory,
                manifestFileName);
            string[] sidecarFiles =
                GetHelperSidecarFiles(clips);
            string[] outputTexturePaths = request.EmbedTextures
                ? []
                : textureFileNames
                    .Select(file =>
                        Path.Combine(
                            outputDirectory,
                            file))
                    .ToArray();
            completedResult = new BlenderFbxExportResult(
                outputPath,
                finalManifestPath,
                outputTexturePaths,
                clips.Select(static clip =>
                        clip.ActionName)
                    .ToArray(),
                bones.Length,
                meshes.Length,
                warnings,
                log)
            {
                HelperSidecarPaths = sidecarFiles
                    .Select(file =>
                        Path.Combine(
                            outputDirectory,
                            file))
                    .ToArray(),
                TexturesEmbedded = request.EmbedTextures,
                EmbeddedTextureFileNames = request.EmbedTextures
                    ? textureFileNames
                    : [],
            };
        }
        catch (BlenderBundleRecoveryException)
        {
            preserveStageDirectory = true;
            throw;
        }
        finally
        {
            if (!preserveStageDirectory)
            {
                string? cleanupWarning =
                    TryDeleteDirectory(stageDirectory);
                if (cleanupWarning is not null)
                {
                    warnings.Add(cleanupWarning);
                }
            }
        }

        progress?.Report(new BlenderFbxExportProgress(
            "Complete",
            100.0,
            Path.GetFileName(outputPath)));
        return completedResult! with
        {
            Warnings = warnings.ToArray(),
        };
    }

    private static void ValidateRequest(
        BlenderFbxExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BlenderExecutableResolver.TryValidate(
                request.BlenderExecutablePath,
                out _))
        {
            throw new FileNotFoundException(
                "A valid blender.exe is required.",
                request.BlenderExecutablePath);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(
            request.OutputFbxPath);
        if (!string.Equals(
                Path.GetExtension(request.OutputFbxPath),
                ".fbx",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Blender handoff output must use the .fbx extension.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Asset);
        ArgumentNullException.ThrowIfNull(request.Meshes);
        ArgumentNullException.ThrowIfNull(request.Anm2Paths);
        if (request.Meshes.Count == 0 ||
            request.Meshes.Count > MaximumMeshCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Select between 1 and {MaximumMeshCount:N0} decoded mesh parts.");
        }

        if (request.Anm2Paths.Count > MaximumClipCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Select at most {MaximumClipCount:N0} ANM2 clips.");
        }

        if (request.Anm2Paths.Count > 0 &&
            request.Rig is null)
        {
            throw new ArgumentException(
                "ANM2 Actions require a decoded retail rig.",
                nameof(request));
        }

        if (request.Rig is null &&
            request.Meshes.Any(static mesh => mesh.IsSkinned))
        {
            throw new ArgumentException(
                "A skinned retail mesh requires its decoded skeleton.",
                nameof(request));
        }
    }

    private static void ValidateAggregateBounds(
        IReadOnlyList<MeshRenderData> meshes,
        long transformCount)
    {
        long vertexCount = 0;
        long indexCount = 0;
        long textureBytes = 0;
        var textureIds = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (MeshRenderData mesh in meshes)
        {
            vertexCount = checked(
                vertexCount + mesh.Vertices.Length);
            indexCount = checked(
                indexCount + mesh.Indices.Length);
            if (mesh.Vertices.IsEmpty ||
                mesh.Indices.IsEmpty ||
                mesh.Indices.Length % 3 != 0)
            {
                throw new InvalidDataException(
                    $"Decoded mesh part '{mesh.Id}' has no complete triangle topology.");
            }

            if (mesh.BaseColorTexture is { } texture &&
                textureIds.Add(texture.Id))
            {
                textureBytes = checked(
                    textureBytes +
                    texture.BaseMipBytes.Length +
                    128L);
            }
        }

        if (vertexCount > MaximumAggregateVertexCount)
        {
            throw new InvalidDataException(
                $"The selected retail mesh has {vertexCount:N0} vertices; the Blender handoff limit is {MaximumAggregateVertexCount:N0}.");
        }

        if (indexCount > MaximumAggregateIndexCount)
        {
            throw new InvalidDataException(
                $"The selected retail mesh has {indexCount:N0} indices; the Blender handoff limit is {MaximumAggregateIndexCount:N0}.");
        }

        if (textureBytes > MaximumTexturePayloadBytes)
        {
            throw new InvalidDataException(
                $"Decoded base-color payloads require {textureBytes:N0} bytes; the Blender handoff texture limit is {MaximumTexturePayloadBytes:N0}.");
        }

        long meshBytes = checked(
            vertexCount *
                MeshVertexStrideFloats *
                sizeof(float) +
            indexCount * sizeof(uint));
        long clipBytes = checked(
            transformCount * 10 * sizeof(float));
        long temporaryBytes = checked(
            meshBytes +
            textureBytes +
            clipBytes);
        if (temporaryBytes > MaximumTemporaryPayloadBytes)
        {
            throw new InvalidDataException(
                $"The Blender handoff would require at least {temporaryBytes:N0} temporary payload bytes; the bounded limit is {MaximumTemporaryPayloadBytes:N0}.");
        }
    }

    private static void ValidateClipCompatibility(
        string path,
        ImmutableArray<uint> descriptors,
        RigDefinition rig)
    {
        var matchedDescriptors = rig.Bones
            .Where(static bone =>
                bone.DescriptorHash.HasValue)
            .ToDictionary(
                static bone =>
                    bone.DescriptorHash!.Value);
        uint[] bodyDescriptors = descriptors
            .Where(static descriptor =>
                descriptor != MotionAccumulatorDescriptor)
            .Distinct()
            .ToArray();
        int matchedCount = bodyDescriptors.Count(
            matchedDescriptors.ContainsKey);
        int matchedAnimatedBoneCount = bodyDescriptors.Count(
            descriptor =>
                matchedDescriptors.TryGetValue(
                    descriptor,
                    out BoneDefinition? bone) &&
                bone.Kind is BoneKind.Root or
                    BoneKind.Deform);
        bool allDescriptorsKnown =
            bodyDescriptors.Length > 0 &&
            matchedCount == bodyDescriptors.Length;
        if (allDescriptorsKnown)
        {
            return;
        }

        bool rootMatched = bodyDescriptors.Any(
            descriptor =>
                matchedDescriptors.TryGetValue(
                    descriptor,
                    out BoneDefinition? bone) &&
                bone.Kind == BoneKind.Root);
        bool sufficientOverlap =
            bodyDescriptors.Length > 0 &&
            (long)matchedCount * 4 >=
                (long)bodyDescriptors.Length * 3;
        const int minimumCharacterBoneMatches = 12;
        if (!rootMatched ||
            !sufficientOverlap ||
            matchedAnimatedBoneCount <
                minimumCharacterBoneMatches)
        {
            throw new InvalidDataException(
                $"ANM2 '{Path.GetFileName(path)}' is not compatible with the selected retail rig: {matchedCount:N0} of {bodyDescriptors.Length:N0} non-motion descriptors match, with {matchedAnimatedBoneCount:N0} Root/Deform matches and root match {rootMatched}. Clips containing unresolved descriptors require a strong character-rig signature: the selected root, at least 75% descriptor overlap, and at least {minimumCharacterBoneMatches:N0} matched Root/Deform tracks. Fully known partial, helper, and camera clips remain supported; Mimic-only and wrong-family clips cannot be exported as body Actions.");
        }
    }

    private static async Task<PreparedClipInfo[]>
        InspectClipsAsync(
            IReadOnlyList<string> paths,
            RigDefinition rig,
            List<string> warnings,
            CancellationToken cancellationToken)
    {
        var actionNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var results = new PreparedClipInfo[paths.Count];
        for (int index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(paths[index]);
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".anm2",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"'{path}' is not an .anm2 file.");
            }

            Anm2Clip clip =
                await Anm2Reader.ReadFileAsync(
                    path,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            if (clip.TrackDescriptors.Distinct().Count() !=
                clip.TrackDescriptors.Length)
            {
                throw new InvalidDataException(
                    $"ANM2 '{path}' contains duplicate track descriptors.");
            }

            Anm2ProvenanceLoadResult provenance =
                Anm2ProvenanceCodec.Load(
                    path,
                    clip.Sha256,
                    clip.Header.FrameCount,
                    cancellationToken);
            Anm2Timing timing =
                Anm2Timing.From(provenance);
            ValidateClipCompatibility(
                path,
                clip.TrackDescriptors,
                rig);
            warnings.AddRange(timing.Warnings);
            string actionName = CreateUniqueActionName(
                Path.GetFileNameWithoutExtension(path),
                actionNames);
            results[index] = new PreparedClipInfo(
                path,
                actionName,
                clip.Sha256,
                clip.Header.FrameCount,
                clip.TrackDescriptors,
                timing);
        }

        return results;
    }

    private static BlenderFbxJobBone[] BuildJobBones(
        RigDefinition rig)
    {
        var result =
            new BlenderFbxJobBone[rig.BoneCount];
        foreach (BoneDefinition bone in rig.Bones)
        {
            result[bone.Index] = ToJobBone(bone);
        }

        return result;
    }

    private static BlenderFbxJobBone ToJobBone(
        BoneDefinition bone)
    {
        TransformTRS bind = bone.LocalBindPose;
        return new BlenderFbxJobBone(
            bone.Index,
            bone.Name,
            bone.ParentIndex,
            bone.DescriptorHash,
            [
                bind.Translation.X,
                bind.Translation.Y,
                bind.Translation.Z,
            ],
            [
                bind.Rotation.W,
                bind.Rotation.X,
                bind.Rotation.Y,
                bind.Rotation.Z,
            ],
            [
                bind.Scale.X,
                bind.Scale.Y,
                bind.Scale.Z,
            ],
            bone.Kind == BoneKind.Root,
            bone.Kind is BoneKind.Root or
                BoneKind.Deform,
            bone.Kind is BoneKind.Helper or
                BoneKind.Camera or
                BoneKind.Prop,
            bone.SemanticRole ?? string.Empty);
    }

    private static BlenderFbxJobTexture[]
        WriteTextures(
            IReadOnlyList<MeshRenderData> meshes,
            string stageDirectory,
            CancellationToken cancellationToken)
    {
        var byKey =
            new Dictionary<string, BlenderFbxJobTexture>(
                StringComparer.Ordinal);
        var fingerprintByKey =
            new Dictionary<string, string>(
                StringComparer.Ordinal);
        var writtenFingerprints = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (MeshRenderData mesh in meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mesh.BaseColorTexture is not { } texture)
            {
                continue;
            }

            string digest = ComputeTextureFingerprint(
                texture,
                cancellationToken);
            if (fingerprintByKey.TryGetValue(
                    texture.Id,
                    out string? previousDigest))
            {
                if (!string.Equals(
                        digest,
                        previousDigest,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Decoded texture key '{texture.Id}' resolves to inconsistent base-color payloads within the selected retail mesh.");
                }

                continue;
            }

            string fileName =
                $"DLR_BaseColor_{digest[..16]}.dds";
            string path = Path.Combine(
                stageDirectory,
                fileName);
            if (writtenFingerprints.Add(digest))
            {
                WriteDds(
                    path,
                    texture,
                    texture.BaseMipBytes,
                    cancellationToken);
            }
            byKey.Add(
                texture.Id,
                new BlenderFbxJobTexture(
                    texture.Id,
                    texture.Id,
                    path,
                    fileName,
                    texture.Width,
                    texture.Height,
                    texture.Format.ToString()));
            fingerprintByKey.Add(
                texture.Id,
                digest);
        }

        return byKey.Values.ToArray();
    }

    private static BlenderFbxJobMesh[]
        WriteMeshes(
            IReadOnlyList<MeshRenderData> meshes,
            BlenderFbxJobTexture[] textures,
            string workDirectory,
            CancellationToken cancellationToken)
    {
        HashSet<string> textureKeys = textures
            .Select(static texture => texture.Key)
            .ToHashSet(StringComparer.Ordinal);
        var usedMeshNames = new HashSet<string>(
            StringComparer.Ordinal);
        var result =
            new BlenderFbxJobMesh[meshes.Count];
        for (int meshIndex = 0;
             meshIndex < meshes.Count;
             meshIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MeshRenderData mesh = meshes[meshIndex];
            string binaryPath = Path.Combine(
                workDirectory,
                $"mesh-{meshIndex:D4}.bin");
            using FileStream stream = new(
                binaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new BinaryWriter(
                stream,
                Encoding.UTF8,
                leaveOpen: false);
            int vertexNumber = 0;
            foreach (MeshVertex vertex in mesh.Vertices.Span)
            {
                if ((vertexNumber++ & 0x3FFF) == 0)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                }

                WriteVector3(writer, vertex.Position);
                WriteVector3(writer, vertex.Normal);
                writer.Write(vertex.TextureCoordinate.X);
                writer.Write(vertex.TextureCoordinate.Y);
                WriteVector4(writer, vertex.BoneWeights);
                WriteVector4(writer, vertex.BoneIndices);
            }

            int indexNumber = 0;
            foreach (uint index in mesh.Indices.Span)
            {
                if ((indexNumber++ & 0x3FFF) == 0)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                }

                if (index >= mesh.Vertices.Length)
                {
                    throw new InvalidDataException(
                        $"Decoded mesh part '{mesh.Id}' contains index {index:N0} outside its {mesh.Vertices.Length:N0}-vertex buffer.");
                }

                writer.Write(index);
            }

            string? textureKey =
                mesh.BaseColorTexture is { } texture &&
                textureKeys.Contains(texture.Id)
                    ? texture.Id
                    : null;
            result[meshIndex] = new BlenderFbxJobMesh(
                CreateUniqueName(
                    mesh.Id,
                    $"Mesh_{meshIndex:D4}",
                    usedMeshNames),
                binaryPath,
                mesh.Vertices.Length,
                mesh.Indices.Length,
                MeshVertexStrideFloats,
                mesh.IsSkinned,
                ToColumnMatrixArray(mesh.LocalToWorld),
                textureKey);
        }

        return result;
    }

    private static async Task<IReadOnlyList<BlenderFbxJobClip>>
        WriteClipsAsync(
            IReadOnlyList<PreparedClipInfo> clipInfo,
            RigDefinition rig,
            BlenderFbxJobBone[] bones,
            double outputFps,
            string workDirectory,
            string stageDirectory,
            string outputFbxFileName,
            IProgress<BlenderFbxExportProgress>? progress,
            List<string> warnings,
            CancellationToken cancellationToken)
    {
        var results =
            new BlenderFbxJobClip[clipInfo.Count];
        BoneDefinition[] primaryRoots = rig.Bones
            .Where(static bone =>
                bone.Kind == BoneKind.Root &&
                bone.ParentIndex < 0)
            .ToArray();
        if (primaryRoots.Length != 1)
        {
            throw new InvalidDataException(
                $"The selected retail rig must contain exactly one parentless Root bone for motion-accumulator baking; found {primaryRoots.Length:N0}.");
        }

        int primaryRootIndex =
            primaryRoots[0].Index;
        for (int clipIndex = 0;
             clipIndex < clipInfo.Count;
             clipIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedClipInfo info = clipInfo[clipIndex];
            Anm2Clip clip =
                await Anm2Reader.ReadFileAsync(
                    info.Path,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            if (!string.Equals(
                    clip.Sha256,
                    info.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"ANM2 changed while export was preparing: {info.Path}");
            }

            var uniqueDescriptors = new HashSet<uint>();
            for (int index = 0;
                 index < clip.TrackDescriptors.Length;
                 index++)
            {
                if (!uniqueDescriptors.Add(
                        clip.TrackDescriptors[index]))
                {
                    throw new InvalidDataException(
                        $"ANM2 '{info.Path}' contains duplicate descriptor 0x{clip.TrackDescriptors[index]:X8}.");
                }
            }

            DescriptorDecodePlan decodePlan =
                BuildDescriptorDecodePlan(
                    clip.TrackDescriptors,
                    bones.Select(static bone =>
                        bone.Descriptor));
            HelperSidecarInfo? helperSidecar =
                decodePlan.SidecarDescriptors.IsEmpty
                    ? null
                    : WriteHelperSidecar(
                        info,
                        clip,
                        decodePlan.SidecarDescriptors,
                        stageDirectory,
                        outputFbxFileName,
                        clipInfo.Count > 1,
                        cancellationToken);
            Anm2BulkDecodeResult actionDecode =
                Anm2SemanticDecoder.DecodeFrames(
                    clip,
                    decodePlan.ActionDescriptors,
                    cancellationToken:
                        cancellationToken);
            ImmutableArray<Anm2Frame> frames =
                actionDecode.Frames;
            Dictionary<uint, int> trackByDescriptor =
                actionDecode.TrackDescriptors
                    .Select(static (
                        descriptor,
                        index) => new
                        {
                            Descriptor = descriptor,
                            Index = index,
                        })
                    .ToDictionary(
                        static row => row.Descriptor,
                        static row => row.Index);
            bool motionPresent =
                trackByDescriptor.TryGetValue(
                    MotionAccumulatorDescriptor,
                    out int motionTrackIndex);
            bool motionActive = motionPresent &&
                IsMotionAccumulatorActive(
                    frames,
                    motionTrackIndex);
            Anm2TemporalResamplePlan resamplePlan =
                Anm2TemporalResampler.CreatePlan(
                frames.Length,
                info.Timing.SampleFps,
                outputFps);
            int outputFrameCount =
                resamplePlan.OutputFrameCount;
            string binaryPath = Path.Combine(
                workDirectory,
                $"clip-{clipIndex:D4}.bin");
            using FileStream stream = new(
                binaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new BinaryWriter(
                stream,
                Encoding.UTF8,
                leaveOpen: false);
            writer.Write("DLRANM1\0"u8);
            writer.Write(outputFrameCount);
            writer.Write(bones.Length);
            TransformTRS[]? previousLocals = null;
            for (int outputFrame = 0;
                 outputFrame < outputFrameCount;
                 outputFrame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sourcePosition =
                    resamplePlan.GetSourcePosition(outputFrame);
                TransformTRS[] locals = BuildLocalTransforms(
                    bones,
                    frames,
                    trackByDescriptor,
                    sourcePosition);
                if (motionActive)
                {
                    BakeMotionAccumulator(
                        locals,
                        rig,
                        primaryRootIndex,
                        SampleTrack(
                            frames,
                            motionTrackIndex,
                            sourcePosition));
                }

                for (int boneIndex = 0;
                     boneIndex < locals.Length;
                     boneIndex++)
                {
                    QuaternionD reference =
                        previousLocals is null
                            ? locals[boneIndex].Rotation
                            : previousLocals[boneIndex].Rotation;
                    locals[boneIndex] =
                        Anm2TemporalResampler
                            .AlignRotationHemisphere(
                                locals[boneIndex],
                                reference);
                    WriteTransform(
                        writer,
                        locals[boneIndex]);
                }

                previousLocals = locals;
            }

            BlenderFbxJobHelperTrack[] helperTracks =
                clip.TrackDescriptors
                    .Select(descriptor =>
                    {
                        BlenderFbxJobBone? node =
                            bones.FirstOrDefault(bone =>
                                bone.Descriptor == descriptor);
                        if (node?.Helper == true)
                        {
                            return new BlenderFbxJobHelperTrack(
                                descriptor,
                                node.Name,
                                node.Semantic);
                        }

                        return node is null
                            ? new BlenderFbxJobHelperTrack(
                                descriptor,
                                HelperName(descriptor),
                                descriptor ==
                                    MotionAccumulatorDescriptor
                                    ? "motion_accumulator_sidecar"
                                    : "unresolved_transform_sidecar",
                                helperSidecar!.FileName,
                                helperSidecar.Sha256,
                                decodePlan
                                    .SidecarDescriptors
                                    .IndexOf(descriptor),
                                frames.Length,
                                info.Timing.SampleFps,
                                "dlr-helper-anm2-trs-f32-wxyz-v1")
                            : null;
                    })
                    .Where(static helper =>
                        helper is not null)
                    .Select(static helper => helper!)
                    .ToArray();
            if (Math.Abs(
                    info.Timing.SourceFbxFps -
                    outputFps) > 1.0e-9)
            {
                warnings.Add(
                    $"{Path.GetFileName(info.Path)} requested {info.Timing.SourceFbxFps:G9} FBX fps; it was resampled to the shared multi-action rate {outputFps:G9}.");
            }

            results[clipIndex] = new BlenderFbxJobClip(
                info.ActionName,
                Path.GetFileName(info.Path),
                info.Sha256,
                info.Timing.Status,
                info.Timing.SampleFps,
                outputFps,
                frames.Length,
                outputFrameCount,
                binaryPath,
                clip.TrackDescriptors,
                helperTracks,
                new BlenderFbxJobMotionAccumulator(
                    motionPresent,
                    motionActive,
                    motionActive,
                    motionActive
                        ? rig.Bones[primaryRootIndex].Name
                        : null));
            progress?.Report(new BlenderFbxExportProgress(
                "Preparing actions",
                28.0 +
                (22.0 * (clipIndex + 1) /
                 clipInfo.Count),
                $"{info.ActionName}: {outputFrameCount:N0} frames"));
        }

        return results;
    }

    internal static DescriptorDecodePlan
        BuildDescriptorDecodePlan(
            ImmutableArray<uint> clipDescriptors,
            IEnumerable<uint?> boneDescriptors)
    {
        ArgumentNullException.ThrowIfNull(
            boneDescriptors);
        HashSet<uint> rigDescriptors =
            boneDescriptors
                .Where(static descriptor =>
                    descriptor.HasValue)
                .Select(static descriptor =>
                    descriptor!.Value)
                .ToHashSet();
        ImmutableArray<uint> actionDescriptors =
            clipDescriptors
                .Where(descriptor =>
                    rigDescriptors.Contains(descriptor) ||
                    descriptor ==
                    MotionAccumulatorDescriptor)
                .ToImmutableArray();
        ImmutableArray<uint> sidecarDescriptors =
            clipDescriptors
                .Where(descriptor =>
                    !rigDescriptors.Contains(descriptor))
                .ToImmutableArray();
        return new DescriptorDecodePlan(
            actionDescriptors,
            sidecarDescriptors);
    }

    private static HelperSidecarInfo WriteHelperSidecar(
        PreparedClipInfo clip,
        Anm2Clip source,
        ImmutableArray<uint> descriptors,
        string stageDirectory,
        string outputFbxFileName,
        bool multipleActions,
        CancellationToken cancellationToken)
    {
        Anm2BulkDecodeResult selected =
            Anm2SemanticDecoder.DecodeFrames(
                source,
                descriptors,
                cancellationToken:
                    cancellationToken);
        ImmutableArray<Anm2Frame> frames =
            selected.Frames;
        Dictionary<uint, int> trackByDescriptor =
            selected.TrackDescriptors
                .Select(static (
                    descriptor,
                    index) => new
                    {
                        Descriptor = descriptor,
                        Index = index,
                    })
                .ToDictionary(
                    static row => row.Descriptor,
                    static row => row.Index);
        string temporaryPath = Path.Combine(
            stageDirectory,
            $".dlrtracks-{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new BinaryWriter(
                       stream,
                       Encoding.UTF8,
                       leaveOpen: false))
            {
                writer.Write("DLRHLPR1"u8);
                writer.Write(1);
                writer.Write(frames.Length);
                writer.Write(clip.Timing.SampleFps);
                writer.Write(descriptors.Length);
                byte[] sourceSha256 =
                    Convert.FromHexString(clip.Sha256);
                if (sourceSha256.Length != 32)
                {
                    throw new InvalidDataException(
                        $"ANM2 '{clip.Path}' has an invalid source fingerprint.");
                }

                writer.Write(sourceSha256);
                foreach (uint descriptor in descriptors)
                {
                    writer.Write(descriptor);
                }

                for (int frameIndex = 0;
                     frameIndex < frames.Length;
                     frameIndex++)
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    foreach (uint descriptor in descriptors)
                    {
                        WriteTransform(
                            writer,
                            FromAnm2(
                                frames[frameIndex].Tracks[
                                    trackByDescriptor[
                                        descriptor]]));
                    }
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            string sha256 = ComputeFileSha256Hex(
                temporaryPath,
                cancellationToken);
            string fileName = multipleActions
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileNameWithoutExtension(outputFbxFileName)}.{MakeSafeName(clip.ActionName, "Action")}.{sha256}.dlrtracks")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileNameWithoutExtension(outputFbxFileName)}.{sha256}.dlrtracks");
            string path = Path.Combine(
                stageDirectory,
                fileName);
            File.Move(temporaryPath, path);
            return new HelperSidecarInfo(
                fileName,
                sha256);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static TransformTRS[] BuildLocalTransforms(
        BlenderFbxJobBone[] bones,
        ImmutableArray<Anm2Frame> frames,
        Dictionary<uint, int> trackByDescriptor,
        double sourcePosition)
    {
        int lower = Math.Clamp(
            (int)Math.Floor(sourcePosition),
            0,
            frames.Length - 1);
        int upper = Math.Min(
            lower + 1,
            frames.Length - 1);
        double amount = sourcePosition - lower;
        var result = new TransformTRS[bones.Length];
        for (int boneIndex = 0;
             boneIndex < bones.Length;
             boneIndex++)
        {
            BlenderFbxJobBone bone = bones[boneIndex];
            TransformTRS bind = FromJobBind(bone);
            if (bone.Descriptor is not { } descriptor ||
                !trackByDescriptor.TryGetValue(
                    descriptor,
                    out int trackIndex))
            {
                result[boneIndex] = bind;
                continue;
            }

            TransformTRS from = FromAnm2(
                frames[lower].Tracks[trackIndex]);
            result[boneIndex] = upper == lower
                ? from
                : TransformTRS.Interpolate(
                    from,
                    FromAnm2(
                        frames[upper].Tracks[trackIndex]),
                    amount);
        }

        return result;
    }

    private static TransformTRS SampleTrack(
        ImmutableArray<Anm2Frame> frames,
        int trackIndex,
        double sourcePosition)
    {
        int lower = Math.Clamp(
            (int)Math.Floor(sourcePosition),
            0,
            frames.Length - 1);
        int upper = Math.Min(
            lower + 1,
            frames.Length - 1);
        TransformTRS from = FromAnm2(
            frames[lower].Tracks[trackIndex]);
        return upper == lower
            ? from
            : TransformTRS.Interpolate(
                from,
                FromAnm2(
                    frames[upper].Tracks[trackIndex]),
                sourcePosition - lower);
    }

    private static void BakeMotionAccumulator(
        TransformTRS[] locals,
        RigDefinition rig,
        int rootIndex,
        TransformTRS accumulator)
    {
        ImmutableArray<TransformMatrix> globals =
            rig.ComputeGlobalMatrices(
                locals.Take(rig.BoneCount));
        TransformMatrix desiredGlobal =
            accumulator.ToMatrix() *
            globals[rootIndex];
        int parentIndex = rig.Bones[rootIndex].ParentIndex;
        TransformMatrix desiredLocal =
            parentIndex < 0
                ? desiredGlobal
                : globals[parentIndex]
                    .InvertedAffine() *
                  desiredGlobal;
        locals[rootIndex] = desiredLocal.Decompose(1.0e-7);
    }

    private static bool IsMotionAccumulatorActive(
        ImmutableArray<Anm2Frame> frames,
        int trackIndex)
    {
        TransformTRS first = FromAnm2(
            frames[0].Tracks[trackIndex]);
        foreach (Anm2Frame frame in frames)
        {
            TransformTRS value = FromAnm2(
                frame.Tracks[trackIndex]);
            Vector3D translation =
                value.Translation - first.Translation;
            Vector3D scale =
                value.Scale - first.Scale;
            double dot = Math.Abs(
                QuaternionD.Dot(
                    value.Rotation,
                    first.Rotation));
            double angle = 2.0 * Math.Acos(
                Math.Clamp(dot, 0.0, 1.0)) *
                180.0 / Math.PI;
            if (translation.Length > 1.0e-6 ||
                scale.Length > 1.0e-6 ||
                angle > 1.0e-4)
            {
                return true;
            }
        }

        return false;
    }

    private static TransformTRS FromAnm2(
        Anm2TrackFrame frame) =>
        new(
            new Vector3D(
                frame.TranslationX,
                frame.TranslationY,
                frame.TranslationZ),
            Anm2DomainAdapter.QuaternionFromCayley(
                frame.RotationX,
                frame.RotationY,
                frame.RotationZ),
            new Vector3D(
                frame.ScaleX,
                frame.ScaleY,
                frame.ScaleZ));

    private static TransformTRS FromJobBind(
        BlenderFbxJobBone bone) =>
        new(
            new Vector3D(
                bone.BindTranslation[0],
                bone.BindTranslation[1],
                bone.BindTranslation[2]),
            new QuaternionD(
                bone.BindRotationWxyz[1],
                bone.BindRotationWxyz[2],
                bone.BindRotationWxyz[3],
                bone.BindRotationWxyz[0]),
            new Vector3D(
                bone.BindScale[0],
                bone.BindScale[1],
                bone.BindScale[2]));

    private static void WriteTransform(
        BinaryWriter writer,
        TransformTRS value)
    {
        writer.Write(checked((float)value.Translation.X));
        writer.Write(checked((float)value.Translation.Y));
        writer.Write(checked((float)value.Translation.Z));
        writer.Write(checked((float)value.Rotation.W));
        writer.Write(checked((float)value.Rotation.X));
        writer.Write(checked((float)value.Rotation.Y));
        writer.Write(checked((float)value.Rotation.Z));
        writer.Write(checked((float)value.Scale.X));
        writer.Write(checked((float)value.Scale.Y));
        writer.Write(checked((float)value.Scale.Z));
    }

    private static void WriteDds(
        string path,
        TextureRenderData texture,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        int blockBytes = texture.Format ==
            TextureRenderFormat.Bc1Unorm
                ? 8
                : 16;
        int expected = checked(
            Math.Max(1, (texture.Width + 3) / 4) *
            Math.Max(1, (texture.Height + 3) / 4) *
            blockBytes);
        if (texture.Width <= 0 ||
            texture.Height <= 0 ||
            payload.Length != expected)
        {
            throw new InvalidDataException(
                $"Base-color texture '{texture.Id}' has an invalid bounded BC payload.");
        }

        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: false);
        writer.Write("DDS "u8);
        writer.Write(124);
        writer.Write(0x000A1007);
        writer.Write(texture.Height);
        writer.Write(texture.Width);
        writer.Write(payload.Length);
        writer.Write(0);
        writer.Write(1);
        for (int index = 0; index < 11; index++)
        {
            writer.Write(0);
        }

        writer.Write(32);
        writer.Write(0x00000004);
        writer.Write(texture.Format switch
        {
            TextureRenderFormat.Bc1Unorm =>
                FourCc("DXT1"),
            TextureRenderFormat.Bc2Unorm =>
                FourCc("DXT3"),
            TextureRenderFormat.Bc3Unorm =>
                FourCc("DXT5"),
            _ => throw new InvalidDataException(
                $"Unsupported base-color format '{texture.Format}'."),
        });
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0x00001000);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (int offset = 0;
             offset < payload.Length;
             offset += 1024 * 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(
                1024 * 1024,
                payload.Length - offset);
            writer.Write(
                payload.Span.Slice(
                    offset,
                    count));
        }
    }

    private static string ComputeTextureFingerprint(
        TextureRenderData texture,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        byte[] metadata = Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{texture.Width}|{texture.Height}|{texture.Format}|"));
        hash.AppendData(metadata);
        ReadOnlySpan<byte> payload =
            texture.BaseMipBytes.Span;
        for (int offset = 0;
             offset < payload.Length;
             offset += 1024 * 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(
                1024 * 1024,
                payload.Length - offset);
            hash.AppendData(payload.Slice(offset, count));
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static string ComputeFileSha256Hex(
        string path,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(
                buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static uint FourCc(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        return (uint)(
            bytes[0] |
            (bytes[1] << 8) |
            (bytes[2] << 16) |
            (bytes[3] << 24));
    }

    private static void CommitBundle(
        string stageFbxPath,
        string stageManifestPath,
        IReadOnlyList<BlenderFbxJobTexture> textures,
        IReadOnlyList<string> helperSidecarFiles,
        string outputPath,
        IBlenderBundleFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        string directory =
            Path.GetDirectoryName(outputPath)!;
        string finalManifest = Path.Combine(
            directory,
            Path.GetFileName(stageManifestPath));
        string stageDirectory =
            Path.GetDirectoryName(stageFbxPath)!;
        BlenderBundleCommitFile[] createdBundleFiles =
            PrepareBundleDependencies(
                textures,
                helperSidecarFiles,
                stageDirectory,
                directory,
                fileSystem,
                cancellationToken);
        BlenderBundleCommitFile fbx =
            CreatePrimaryCommitFile(
                stageFbxPath,
                outputPath,
                fileSystem,
                cancellationToken);
        BlenderBundleCommitFile manifest =
            CreatePrimaryCommitFile(
                stageManifestPath,
                finalManifest,
                fileSystem,
                cancellationToken);
        var journal = new BlenderBundleCommitJournal(
            BundleCommitFormat,
            BundleCommitSchemaVersion,
            BundleCommitPreparedPhase,
            Path.GetFullPath(outputPath),
            Path.GetFullPath(stageDirectory),
            fbx,
            manifest,
            createdBundleFiles);
        WriteBundleCommitJournal(
            journal,
            cancellationToken);
        bool installedPhasePersisted = false;
        try
        {
            foreach (BlenderBundleCommitFile dependency in
                     journal.CreatedBundleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureExpectedFile(
                    dependency.StagedPath,
                    dependency.ExpectedSha256,
                    "staged bundle dependency");
                if (fileSystem.FileExists(
                        dependency.DestinationPath))
                {
                    throw new IOException(
                        $"Bundle dependency destination appeared while the transaction was preparing: '{dependency.DestinationPath}'.");
                }

                fileSystem.MoveFile(
                    dependency.StagedPath,
                    dependency.DestinationPath);
            }

            InstallPrimaryCommitFile(
                journal.Manifest,
                fileSystem);
            cancellationToken.ThrowIfCancellationRequested();
            InstallPrimaryCommitFile(
                journal.Fbx,
                fileSystem);

            journal = journal with
            {
                Phase = BundleCommitInstalledPhase,
            };
            WriteBundleCommitJournal(
                journal,
                CancellationToken.None);
            installedPhasePersisted = true;
            CompleteInstalledBundle(
                journal,
                fileSystem);
        }
        catch (Exception commitFailure)
        {
            try
            {
                RecoverInterruptedBundle(
                    outputPath,
                    fileSystem);
            }
            catch (Exception recoveryFailure)
            {
                throw new BlenderBundleRecoveryException(
                    stageDirectory,
                    commitFailure,
                    [recoveryFailure]);
            }

            if (!installedPhasePersisted)
            {
                throw;
            }
        }
    }

    private static BlenderBundleCommitFile[]
        PrepareBundleDependencies(
            IReadOnlyList<BlenderFbxJobTexture> textures,
            IReadOnlyList<string> helperSidecarFiles,
            string stageDirectory,
            string outputDirectory,
            IBlenderBundleFileSystem fileSystem,
            CancellationToken cancellationToken)
    {
        var sourcesByName =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        foreach (BlenderFbxJobTexture texture in textures)
        {
            AddBundleDependencySource(
                sourcesByName,
                texture.FileName,
                texture.FilePath,
                fileSystem);
        }

        foreach (string fileName in helperSidecarFiles)
        {
            AddBundleDependencySource(
                sourcesByName,
                fileName,
                Path.Combine(
                    stageDirectory,
                    fileName),
                fileSystem);
        }

        var created =
            new List<BlenderBundleCommitFile>();
        foreach ((string fileName, string source) in
                 sourcesByName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(
                outputDirectory,
                fileName);
            if (fileSystem.FileExists(destination))
            {
                if (!fileSystem.FilesEqual(
                        source,
                        destination))
                {
                    throw new IOException(
                        $"A different file already owns bundle dependency name '{fileName}'.");
                }

                fileSystem.DeleteFile(source);
                continue;
            }

            created.Add(new BlenderBundleCommitFile(
                Path.GetFullPath(source),
                Path.GetFullPath(destination),
                null,
                false,
                ComputeFileSha256Hex(
                    source,
                    cancellationToken),
                null));
        }

        return created.ToArray();
    }

    private static void AddBundleDependencySource(
        Dictionary<string, string> sourcesByName,
        string fileName,
        string source,
        IBlenderBundleFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(
                Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Bundle dependency name is not a safe file name: '{fileName}'.");
        }

        string fullSource = Path.GetFullPath(source);
        if (!fileSystem.FileExists(fullSource))
        {
            throw new FileNotFoundException(
                "A staged Blender bundle dependency is missing.",
                fullSource);
        }

        if (sourcesByName.TryGetValue(
                fileName,
                out string? existing))
        {
            if (PathsEqual(existing, fullSource))
            {
                return;
            }

            if (!fileSystem.FilesEqual(
                    existing,
                    fullSource))
            {
                throw new IOException(
                    $"Staged Blender bundle dependencies disagree for '{fileName}'.");
            }

            fileSystem.DeleteFile(fullSource);
            return;
        }

        sourcesByName.Add(
            fileName,
            fullSource);
    }

    private static BlenderBundleCommitFile
        CreatePrimaryCommitFile(
            string stagedPath,
            string destinationPath,
            IBlenderBundleFileSystem fileSystem,
            CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(stagedPath);
        string destination =
            Path.GetFullPath(destinationPath);
        if (!fileSystem.FileExists(source))
        {
            throw new FileNotFoundException(
                "A staged Blender bundle primary file is missing.",
                source);
        }

        bool destinationExisted =
            fileSystem.FileExists(destination);
        return new BlenderBundleCommitFile(
            source,
            destination,
            source + ".previous",
            destinationExisted,
            ComputeFileSha256Hex(
                source,
                cancellationToken),
            destinationExisted
                ? ComputeFileSha256Hex(
                    destination,
                    cancellationToken)
                : null);
    }

    private static void InstallPrimaryCommitFile(
        BlenderBundleCommitFile file,
        IBlenderBundleFileSystem fileSystem)
    {
        EnsureExpectedFile(
            file.StagedPath,
            file.ExpectedSha256,
            "staged primary bundle file");
        if (!file.DestinationExisted)
        {
            if (fileSystem.FileExists(
                    file.DestinationPath))
            {
                throw new IOException(
                    $"Bundle destination appeared while the transaction was preparing: '{file.DestinationPath}'.");
            }

            fileSystem.MoveFile(
                file.StagedPath,
                file.DestinationPath);
            return;
        }

        if (!fileSystem.FileExists(
                file.DestinationPath))
        {
            throw new IOException(
                $"The existing bundle destination disappeared while the transaction was preparing: '{file.DestinationPath}'.");
        }

        EnsureExpectedFile(
            file.DestinationPath,
            file.OriginalSha256!,
            "existing primary bundle file");
        fileSystem.ReplaceFile(
            file.StagedPath,
            file.DestinationPath,
            file.BackupPath!);
    }

    internal static string GetBundleCommitJournalPath(
        string outputPath) =>
        Path.GetFullPath(outputPath) +
        ".dlrcommit.json";

    internal static bool RecoverInterruptedBundle(
        string outputPath) =>
        RecoverInterruptedBundle(
            outputPath,
            PhysicalBlenderBundleFileSystem.Instance);

    private static bool RecoverInterruptedBundle(
        string outputPath,
        IBlenderBundleFileSystem fileSystem)
    {
        string journalPath =
            GetBundleCommitJournalPath(outputPath);
        if (!File.Exists(journalPath))
        {
            return false;
        }

        BlenderBundleCommitJournal journal =
            ReadBundleCommitJournal(
                journalPath,
                outputPath);
        if (journal.Phase ==
            BundleCommitInstalledPhase)
        {
            CompleteInstalledBundle(
                journal,
                fileSystem);
            return true;
        }

        var recoveryFailures =
            new List<Exception>();
        AttemptRollback(
            "restore the previous FBX",
            () => RestorePreparedPrimaryFile(
                journal.Fbx,
                fileSystem),
            recoveryFailures);
        AttemptRollback(
            "restore the previous handoff manifest",
            () => RestorePreparedPrimaryFile(
                journal.Manifest,
                fileSystem),
            recoveryFailures);
        // Content-addressed dependencies are safe orphans. Retain them during
        // rollback because another output transaction may have installed the
        // same bytes after this journal's preflight but before its move.

        if (recoveryFailures.Count == 0)
        {
            AttemptRollback(
                "remove the interrupted staging directory",
                () => DeleteRecoveryStage(
                    journal.StageDirectory),
                recoveryFailures);
            AttemptRollback(
                "remove the completed recovery journal",
                () => File.Delete(journalPath),
                recoveryFailures);
        }

        if (recoveryFailures.Count > 0)
        {
            throw new BlenderBundleRecoveryException(
                journal.StageDirectory,
                new IOException(
                    "An interrupted Blender bundle transaction was detected."),
                recoveryFailures);
        }

        return true;
    }

    internal static void WriteBundleCommitJournal(
        BlenderBundleCommitJournal journal,
        CancellationToken cancellationToken = default)
    {
        ValidateBundleCommitJournal(
            journal,
            journal.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            CommitJournalSerializerOptions);
        if (bytes.Length > MaximumCommitJournalBytes)
        {
            throw new InvalidDataException(
                $"The Blender bundle commit journal exceeds {MaximumCommitJournalBytes:N0} bytes.");
        }

        string destination =
            GetBundleCommitJournalPath(
                journal.OutputPath);
        string directory =
            Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                cancellationToken
                    .ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination))
            {
                File.Replace(
                    temporaryPath,
                    destination,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(
                    temporaryPath,
                    destination);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static BlenderBundleCommitJournal
        ReadBundleCommitJournal(
            string journalPath,
            string outputPath)
    {
        byte[] bytes = ReadBoundedFile(
            journalPath,
            MaximumCommitJournalBytes);
        BlenderBundleCommitJournal? journal =
            JsonSerializer.Deserialize<
                BlenderBundleCommitJournal>(
                bytes,
                CommitJournalSerializerOptions);
        if (journal is null)
        {
            throw new InvalidDataException(
                "The Blender bundle commit journal is empty.");
        }

        ValidateBundleCommitJournal(
            journal,
            outputPath);
        return journal;
    }

    private static void ValidateBundleCommitJournal(
        BlenderBundleCommitJournal journal,
        string expectedOutputPath)
    {
        if (journal.Format != BundleCommitFormat ||
            journal.SchemaVersion !=
            BundleCommitSchemaVersion ||
            journal.Phase is not
                (BundleCommitPreparedPhase or
                BundleCommitInstalledPhase))
        {
            throw new InvalidDataException(
                "The Blender bundle commit journal contract is not supported.");
        }

        string outputPath =
            RequireCanonicalFullPath(
                journal.OutputPath,
                "output");
        string expected = Path.GetFullPath(
            expectedOutputPath);
        if (!PathsEqual(outputPath, expected))
        {
            throw new InvalidDataException(
                "The Blender bundle commit journal targets another FBX.");
        }

        string outputDirectory =
            Path.GetDirectoryName(outputPath)!;
        string stageDirectory =
            RequireCanonicalFullPath(
                journal.StageDirectory,
                "staging directory");
        if (!PathsEqual(
                Path.GetDirectoryName(stageDirectory)!,
                outputDirectory) ||
            !TryParseStageDirectoryName(
                Path.GetFileName(stageDirectory)))
        {
            throw new InvalidDataException(
                "The Blender bundle commit journal staging directory is outside the output directory.");
        }

        if (Directory.Exists(stageDirectory) &&
            (File.GetAttributes(stageDirectory) &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The Blender bundle recovery staging directory cannot be a reparse point.");
        }

        ValidatePrimaryJournalFile(
            journal.Fbx,
            Path.Combine(
                stageDirectory,
                Path.GetFileName(outputPath)),
            outputPath,
            stageDirectory);
        string manifestDestination =
            outputPath + ".dlrahandoff.json";
        ValidatePrimaryJournalFile(
            journal.Manifest,
            Path.Combine(
                stageDirectory,
                Path.GetFileName(
                    manifestDestination)),
            manifestDestination,
            stageDirectory);

        if (journal.CreatedBundleFiles is null ||
            journal.CreatedBundleFiles.Length >
            MaximumCommitJournalEntries)
        {
            throw new InvalidDataException(
                "The Blender bundle commit journal contains too many dependencies.");
        }

        var destinations = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            outputPath,
            manifestDestination,
        };
        foreach (BlenderBundleCommitFile dependency in
                 journal.CreatedBundleFiles)
        {
            ValidateCommitHash(
                dependency.ExpectedSha256,
                "dependency hash");
            if (dependency.DestinationExisted ||
                dependency.BackupPath is not null ||
                dependency.OriginalSha256 is not null)
            {
                throw new InvalidDataException(
                    "A created Blender bundle dependency has invalid recovery metadata.");
            }

            string source = RequireCanonicalFullPath(
                dependency.StagedPath,
                "dependency source");
            string destination =
                RequireCanonicalFullPath(
                    dependency.DestinationPath,
                    "dependency destination");
            if (!PathsEqual(
                    Path.GetDirectoryName(source)!,
                    stageDirectory) ||
                !PathsEqual(
                    Path.GetDirectoryName(destination)!,
                    outputDirectory) ||
                !destinations.Add(destination))
            {
                throw new InvalidDataException(
                    "A Blender bundle dependency escapes or duplicates the transaction paths.");
            }
        }
    }

    private static void ValidatePrimaryJournalFile(
        BlenderBundleCommitFile file,
        string expectedStagedPath,
        string expectedDestinationPath,
        string stageDirectory)
    {
        ArgumentNullException.ThrowIfNull(file);
        string staged = RequireCanonicalFullPath(
            file.StagedPath,
            "staged primary file");
        string destination = RequireCanonicalFullPath(
            file.DestinationPath,
            "primary destination");
        string backup = RequireCanonicalFullPath(
            file.BackupPath,
            "primary backup");
        if (!PathsEqual(staged, expectedStagedPath) ||
            !PathsEqual(
                destination,
                expectedDestinationPath) ||
            !PathsEqual(backup, staged + ".previous") ||
            !PathsEqual(
                Path.GetDirectoryName(backup)!,
                stageDirectory))
        {
            throw new InvalidDataException(
                "A Blender bundle primary journal path is outside its transaction.");
        }

        ValidateCommitHash(
            file.ExpectedSha256,
            "primary expected hash");
        if (file.DestinationExisted)
        {
            ValidateCommitHash(
                file.OriginalSha256,
                "primary original hash");
        }
        else if (file.OriginalSha256 is not null)
        {
            throw new InvalidDataException(
                "A new Blender bundle primary file cannot have an original hash.");
        }
    }

    private static void CompleteInstalledBundle(
        BlenderBundleCommitJournal journal,
        IBlenderBundleFileSystem fileSystem)
    {
        EnsureExpectedFile(
            journal.Fbx.DestinationPath,
            journal.Fbx.ExpectedSha256,
            "installed FBX");
        EnsureExpectedFile(
            journal.Manifest.DestinationPath,
            journal.Manifest.ExpectedSha256,
            "installed handoff manifest");
        foreach (BlenderBundleCommitFile dependency in
                 journal.CreatedBundleFiles)
        {
            EnsureExpectedFile(
                dependency.DestinationPath,
                dependency.ExpectedSha256,
                "installed bundle dependency");
        }

        DeleteIfPresent(
            journal.Fbx.BackupPath,
            fileSystem);
        DeleteIfPresent(
            journal.Manifest.BackupPath,
            fileSystem);
        DeleteRecoveryStage(
            journal.StageDirectory);
        File.Delete(
            GetBundleCommitJournalPath(
                journal.OutputPath));
    }

    private static void RestorePreparedPrimaryFile(
        BlenderBundleCommitFile file,
        IBlenderBundleFileSystem fileSystem)
    {
        if (!file.DestinationExisted)
        {
            if (fileSystem.FileExists(
                    file.DestinationPath))
            {
                EnsureExpectedFile(
                    file.DestinationPath,
                    file.ExpectedSha256,
                    "interrupted new primary file");
                fileSystem.DeleteFile(
                    file.DestinationPath);
            }

            return;
        }

        string backup = file.BackupPath!;
        if (!fileSystem.FileExists(backup))
        {
            EnsureExpectedFile(
                file.DestinationPath,
                file.OriginalSha256!,
                "unmodified original primary file");
            return;
        }

        EnsureExpectedFile(
            backup,
            file.OriginalSha256!,
            "primary recovery backup");
        if (!fileSystem.FileExists(
                file.DestinationPath))
        {
            fileSystem.MoveFile(
                backup,
                file.DestinationPath);
            return;
        }

        string currentHash = ComputeFileSha256Hex(
            file.DestinationPath,
            CancellationToken.None);
        if (!string.Equals(
                currentHash,
                file.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                currentHash,
                file.OriginalSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The interrupted bundle destination was modified outside the transaction: '{file.DestinationPath}'.");
        }

        if (string.Equals(
                currentHash,
                file.OriginalSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            fileSystem.DeleteFile(backup);
            return;
        }

        string displaced =
            backup + ".interrupted-new";
        DeleteIfPresent(
            displaced,
            fileSystem);
        fileSystem.ReplaceFile(
            backup,
            file.DestinationPath,
            displaced);
        DeleteIfPresent(
            displaced,
            fileSystem);
    }

    private static void DeleteIfPresent(
        string? path,
        IBlenderBundleFileSystem fileSystem)
    {
        if (path is not null &&
            fileSystem.FileExists(path))
        {
            fileSystem.DeleteFile(path);
        }
    }

    private static void DeleteRecoveryStage(
        string stageDirectory)
    {
        if (Directory.Exists(stageDirectory))
        {
            Directory.Delete(
                stageDirectory,
                recursive: true);
        }
    }

    private static void EnsureExpectedFile(
        string path,
        string expectedSha256,
        string description)
    {
        if (!File.Exists(path))
        {
            throw new IOException(
                $"The {description} is missing: '{path}'.");
        }

        string actual = ComputeFileSha256Hex(
            path,
            CancellationToken.None);
        if (!string.Equals(
                actual,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"The {description} changed outside the bundle transaction: '{path}'.");
        }
    }

    private static byte[] ReadBoundedFile(
        string path,
        int maximumBytes)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 ||
            stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The Blender bundle commit journal must be between 1 and {maximumBytes:N0} bytes.");
        }

        using var output = new MemoryStream(
            checked((int)stream.Length));
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int remaining = checked(
                maximumBytes -
                (int)output.Length);
            if (remaining == 0)
            {
                if (stream.ReadByte() >= 0)
                {
                    throw new InvalidDataException(
                        $"The Blender bundle commit journal exceeds {maximumBytes:N0} bytes.");
                }

                break;
            }

            int count = stream.Read(
                buffer,
                0,
                Math.Min(buffer.Length, remaining));
            if (count == 0)
            {
                break;
            }

            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    private static string RequireCanonicalFullPath(
        string? path,
        string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                $"The Blender bundle commit journal {description} path is missing.");
        }

        string fullPath = Path.GetFullPath(path);
        if (!PathsEqual(path, fullPath))
        {
            throw new InvalidDataException(
                $"The Blender bundle commit journal {description} path is not canonical.");
        }

        return fullPath;
    }

    private static bool TryParseStageDirectoryName(
        string name)
    {
        const string prefix = ".dlr-blender-";
        return name.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(
                name[prefix.Length..],
                "N",
                out _);
    }

    private static void ValidateCommitHash(
        string? value,
        string description)
    {
        if (value is null ||
            value.Length != 64 ||
            value.Any(static character =>
                !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                $"The Blender bundle commit journal {description} is not a SHA-256 digest.");
        }
    }

    private static bool PathsEqual(
        string left,
        string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static void AttemptRollback(
        string description,
        Action operation,
        List<Exception> failures)
    {
        try
        {
            operation();
        }
        catch (Exception exception)
        {
            failures.Add(
                new IOException(
                    $"Failed to {description}.",
                    exception));
        }
    }

    private static bool FilesEqual(
        string left,
        string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using FileStream leftStream = File.OpenRead(left);
        using FileStream rightStream = File.OpenRead(right);
        byte[] leftHash = SHA256.HashData(leftStream);
        byte[] rightHash = SHA256.HashData(rightStream);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private sealed class PhysicalBlenderBundleFileSystem :
        IBlenderBundleFileSystem
    {
        public static PhysicalBlenderBundleFileSystem Instance
        {
            get;
        } = new();

        private PhysicalBlenderBundleFileSystem()
        {
        }

        public bool FileExists(string path) =>
            File.Exists(path);

        public void MoveFile(
            string source,
            string destination) =>
            File.Move(source, destination);

        public void ReplaceFile(
            string source,
            string destination,
            string backup) =>
            File.Replace(
                source,
                destination,
                backup,
                ignoreMetadataErrors: true);

        public void DeleteFile(string path) =>
            File.Delete(path);

        public bool FilesEqual(
            string left,
            string right) =>
            BlenderFbxExportService.FilesEqual(
                left,
                 right);
    }

    private sealed class BundleTargetLock :
        IDisposable
    {
        private readonly FileStream _stream;
        private readonly string _path;
        private bool _disposed;

        private BundleTargetLock(
            FileStream stream,
            string path)
        {
            _stream = stream;
            _path = path;
        }

        public static BundleTargetLock Acquire(
            string outputPath)
        {
            string path =
                Path.GetFullPath(outputPath) +
                ".dlrcommit.lock";
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                return new BundleTargetLock(
                    stream,
                    path);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    $"Another Blender bundle transaction is already using '{Path.GetFullPath(outputPath)}'.",
                    exception);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                // A stale empty lock file is harmless. FileShare.None on the
                // open handle, not the directory entry, owns exclusivity.
            }
        }
    }

    private static string[] GetHelperSidecarFiles(
        IReadOnlyList<BlenderFbxJobClip> clips) =>
        clips.SelectMany(static clip =>
                clip.HelperTracks)
            .Select(static helper =>
                helper.SidecarFile)
            .Where(static file =>
                !string.IsNullOrWhiteSpace(file))
            .Select(static file => file!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void ValidateBlenderResult(
        BlenderProcessResult process,
        string outputPath,
        IReadOnlyList<BlenderFbxJobClip> clips,
        int boneCount)
    {
        string log = process.CombinedLog;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Blender FBX export failed with exit code {process.ExitCode}:{Environment.NewLine}{Tail(log, 30)}");
        }

        if (!log.Contains(
                "DLR_EXPORT_COMPLETE:",
                StringComparison.Ordinal) ||
            !File.Exists(outputPath) ||
            new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException(
                "Blender exited without producing and confirming the FBX output.");
        }

        string[] actions = ReadJsonMarker<string[]>(
            log,
            "DLR_ACTION_STACKS:");
        string[] expected = clips
            .Select(static clip => clip.ActionName)
            .ToArray();
        if (!actions.SequenceEqual(
                expected,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Blender did not confirm every requested armature Action/FBX take.");
        }

        BindPoseConfirmation bind = ReadJsonMarker<
            BindPoseConfirmation>(
            log,
            "DLR_BIND_POSE:");
        if (boneCount == 0)
        {
            if (bind.Exported || bind.BoneCount != 0)
            {
                throw new InvalidOperationException(
                    "Blender unexpectedly reported an armature BindPose for a static mesh export.");
            }
        }
        else if (!bind.Exported ||
                 bind.BoneCount != boneCount)
        {
            throw new InvalidOperationException(
                "Blender did not confirm a complete armature BindPose.");
        }

        RootParityConfirmation parity = ReadJsonMarker<
            RootParityConfirmation>(
            log,
            "DLR_ROOT_PARITY:");
        if (parity.MaxAngularErrorDegrees > 0.05 ||
            parity.MaxTranslationErrorM > 1.0e-5)
        {
            throw new InvalidOperationException(
                "Blender child-pivot animation parity exceeded the validated tolerance.");
        }
    }

    private static T ReadJsonMarker<T>(
        string log,
        string marker)
    {
        string? line = log
            .Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(value =>
                value.StartsWith(
                    marker,
                    StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidOperationException(
                $"Blender did not emit required marker '{marker}'.");
        }

        return JsonSerializer.Deserialize<T>(
                line[marker.Length..],
                JobSerializerOptions)
            ?? throw new InvalidOperationException(
                $"Blender emitted an empty '{marker}' payload.");
    }

    private static void ReportBlenderProgress(
        string line,
        IProgress<BlenderFbxExportProgress>? progress)
    {
        const string marker = "DLR_PROGRESS:";
        if (progress is null ||
            !line.StartsWith(
                marker,
                StringComparison.Ordinal))
        {
            return;
        }

        string[] parts = line[marker.Length..]
            .Split('|');
        if (parts.Length != 3 ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double current) ||
            !double.TryParse(
                parts[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double total))
        {
            return;
        }

        double ratio = total > 0.0
            ? Math.Clamp(current / total, 0.0, 1.0)
            : 0.0;
        progress.Report(new BlenderFbxExportProgress(
            parts[0],
            55.0 + (40.0 * ratio),
            $"{current:N0}/{total:N0}"));
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(
                stream,
                value,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreateUniqueActionName(
        string source,
        HashSet<string> used)
        => CreateUniqueName(
            source,
            "ANM2_Action",
            used);

    private static string CreateUniqueName(
        string source,
        string fallback,
        HashSet<string> used)
    {
        string basis = MakeSafeName(
            source,
            fallback);
        string candidate = basis;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{basis}_{suffix++}";
        }

        return candidate;
    }

    private static string MakeSafeName(
        string source,
        string fallback)
    {
        char[] rendered = source
            .Select(character =>
                char.IsLetterOrDigit(character) ||
                character is '_' or '-' or '.'
                    ? character
                    : '_')
            .ToArray();
        string value = new string(rendered)
            .Trim('_', '.', ' ');
        if (string.IsNullOrWhiteSpace(value))
        {
            value = fallback;
        }

        return value.Length <= 96
            ? value
            : value[..96];
    }

    private static string HelperName(uint descriptor) =>
        descriptor == MotionAccumulatorDescriptor
            ? "DLR_OffsetHelper_CCC3CDDF"
            : $"DLR_Track_{descriptor:X8}";

    private static IReadOnlyList<float> ToColumnMatrixArray(
        Matrix4x4 value) =>
        [
            value.M11, value.M21, value.M31, value.M41,
            value.M12, value.M22, value.M32, value.M42,
            value.M13, value.M23, value.M33, value.M43,
            value.M14, value.M24, value.M34, value.M44,
        ];

    private static void WriteVector3(
        BinaryWriter writer,
        Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static void WriteVector4(
        BinaryWriter writer,
        Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static string Tail(
        string value,
        int maximumLines) =>
        string.Join(
            Environment.NewLine,
            value.Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(maximumLines));

    private static string? TryDeleteDirectory(
        string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(
                    directory,
                    recursive: true);
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return
                $"Temporary Blender handoff cleanup requires attention: {directory} ({exception.Message})";
        }
    }

    private sealed record PreparedClipInfo(
        string Path,
        string ActionName,
        string Sha256,
        int SourceFrameCount,
        ImmutableArray<uint> Descriptors,
        Anm2Timing Timing);

    internal sealed record DescriptorDecodePlan(
        ImmutableArray<uint> ActionDescriptors,
        ImmutableArray<uint> SidecarDescriptors);

    private sealed record HelperSidecarInfo(
        string FileName,
        string Sha256);

    private sealed record Anm2Timing(
        string Status,
        double SampleFps,
        double SourceFbxFps,
        IReadOnlyList<string> Warnings)
    {
        public static Anm2Timing Default(
            string status,
            IReadOnlyList<string> warnings) =>
            new(
                status,
                30.0,
                30.0,
                warnings);

        public static Anm2Timing From(
            Anm2ProvenanceLoadResult result) =>
            result.Document is { } document &&
            result.Status == Anm2ProvenanceStatus.Valid
                ? new Anm2Timing(
                    result.StatusName,
                    document.SampleFps,
                    document.SourceFbxFps,
                    result.Warnings)
                : Default(
                    result.StatusName,
                    result.Warnings);
    }

    private sealed record BindPoseConfirmation(
        bool Exported,
        int BoneCount);

    private sealed record RootParityConfirmation(
        double MaxAngularErrorDegrees,
        double MaxTranslationErrorM);
}

internal interface IBlenderBundleFileSystem
{
    bool FileExists(string path);

    void MoveFile(
        string source,
        string destination);

    void ReplaceFile(
        string source,
        string destination,
        string backup);

    void DeleteFile(string path);

    bool FilesEqual(
        string left,
        string right);
}

internal sealed record BlenderBundleCommitFile(
    string StagedPath,
    string DestinationPath,
    string? BackupPath,
    bool DestinationExisted,
    string ExpectedSha256,
    string? OriginalSha256);

internal sealed record BlenderBundleCommitJournal(
    string Format,
    int SchemaVersion,
    string Phase,
    string OutputPath,
    string StageDirectory,
    BlenderBundleCommitFile Fbx,
    BlenderBundleCommitFile Manifest,
    BlenderBundleCommitFile[] CreatedBundleFiles);

internal sealed class BlenderBundleRecoveryException :
    IOException
{
    public BlenderBundleRecoveryException(
        string recoveryDirectory,
        Exception commitFailure,
        IReadOnlyList<Exception> rollbackFailures)
        : base(
            $"The Blender FBX bundle could not be committed and automatic rollback was incomplete. Recovery files were preserved at '{recoveryDirectory}'. Do not delete that directory until its .previous files have been restored.",
            new AggregateException(
                "The bundle commit and one or more rollback operations failed.",
                new[] { commitFailure }
                    .Concat(rollbackFailures)))
    {
        RecoveryDirectory = recoveryDirectory;
        RollbackFailures = rollbackFailures;
    }

    public string RecoveryDirectory
    {
        get;
    }

    public IReadOnlyList<Exception> RollbackFailures
    {
        get;
    }
}
