using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Mapping;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class UserReportedRetargetAcceptanceTests
{
    private const string HipHopSha256 =
        "92aa6b028f20c12c6f00f39b90e824f19ac93fadffc198deca9e249afd382129";
    private const string StandingGreetingSha256 =
        "6630d8a502c078134ce1448ffe993774116bd20f8d33c70109af6bae2af74d6c";
    private const string TauntSha256 =
        "8f00711acdcf8c89b0dad1632b940ced9d3b1666f3f194d268f394fce5df9d08";

    private readonly ITestOutputHelper _output;

    public UserReportedRetargetAcceptanceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    [Trait("Gate", "ExternalUserRetarget")]
    public async Task HipHopPoseTransfersToInstalledPlayerAtReportedFrame()
    {
        const int sampleFrame = 74;
        string sourcePath = Path.Combine(
            Environment.GetEnvironmentVariable(
                "DLR_FBX_ANIMATION_CORPUS_ROOT")
                ?? @"F:\Fbx\AnimationTests",
            "Hip Hop Dancing.fbx");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        string actualSourceHash =
            await ReAnimated.App.Infrastructure.ProjectSourceImporter
                .ComputeSha256Async(
                    sourcePath,
                    CancellationToken.None);
        if (!string.Equals(
                actualSourceHash,
                HipHopSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string packPath = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(packPath))
        {
            return;
        }

        FbxCoreAnimationImportResult imported =
            await new FbxAnimationDecoder().DecodeFileAsync(
                sourcePath);
        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(
                            temporaryDirectory,
                            "cache"),
                    MaximumMemoryBytes =
                        128L * 1024 * 1024,
                    MaximumMemoryEntryBytes =
                        32 * 1024 * 1024,
                    MaximumDiskBytes =
                        512L * 1024 * 1024,
                });
            Rp6lArchive archive =
                await Rp6lArchive.OpenAsync(packPath);
            Rp6lResourceDescriptor resource =
                Assert.IsType<Rp6lResourceDescriptor>(
                    archive.FindResource(
                        Rp6lResourceTypes.Mesh,
                        "player_1_tpp"));
            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            RigDefinition target =
                Assert.IsType<RigDefinition>(mesh.Rig);
            RetargetMap mapping =
                RetargetMapBuilder.CreateSuggested(
                    imported.Rig,
                    target);
            Dl1AuthoringPolicy policy =
                Dl1AuthoringPolicy.Create(
                    imported.Rig,
                    target,
                    mapping,
                    AnimationRootMode.InPlace);
            double seconds =
                imported.Clip.FrameRate.SecondsForFrame(
                    sampleFrame);
            EvaluationFrame evaluated =
                new AnimationEvaluator().Evaluate(
                    new EvaluationRequest(
                        imported.Rig,
                        target,
                        imported.Clip,
                        seconds,
                        PreviewProfile.RawAuthoring,
                        mapping,
                        playbackMode:
                            PlaybackMode.Clamp,
                        purpose:
                            EvaluationPurpose.Preview,
                        dl1AuthoringPolicy:
                            policy));
            SkeletonPose sourcePose =
                imported.Clip.SamplePose(
                    imported.Rig,
                    seconds,
                    PlaybackMode.Clamp);

            _output.WriteLine(
                $"source={imported.Rig.BoneCount}, target={target.BoneCount}, mappings={mapping.Entries.Length}, frame={evaluated.SampleFrame}");
            WriteBonePair(
                imported.Rig,
                sourcePose,
                "mixamorig:Hips",
                target,
                evaluated.DisplayPose,
                "pelvis");
            WriteBonePair(
                imported.Rig,
                sourcePose,
                "mixamorig:LeftUpLeg",
                target,
                evaluated.DisplayPose,
                "l_thigh");
            WriteBonePair(
                imported.Rig,
                sourcePose,
                "mixamorig:LeftLeg",
                target,
                evaluated.DisplayPose,
                "l_calf");
            WriteBonePair(
                imported.Rig,
                sourcePose,
                "mixamorig:RightUpLeg",
                target,
                evaluated.DisplayPose,
                "r_thigh");
            WriteBonePair(
                imported.Rig,
                sourcePose,
                "mixamorig:RightLeg",
                target,
                evaluated.DisplayPose,
                "r_calf");
            WriteBonePair(
                imported.Rig,
                sourcePose,
                "mixamorig:LeftArm",
                target,
                evaluated.DisplayPose,
                "l_upperarm");

            double leftKneeChange = RotationDeltaDegrees(
                target.CreateBindPose().LocalTransforms[
                    target.GetBoneIndex("l_calf")].Rotation,
                evaluated.DisplayPose.LocalTransforms[
                    target.GetBoneIndex("l_calf")].Rotation);
            double rightKneeChange = RotationDeltaDegrees(
                target.CreateBindPose().LocalTransforms[
                    target.GetBoneIndex("r_calf")].Rotation,
                evaluated.DisplayPose.LocalTransforms[
                    target.GetBoneIndex("r_calf")].Rotation);
            _output.WriteLine(
                $"target local knee deltas: left={leftKneeChange:F3} degrees, right={rightKneeChange:F3} degrees");

            Assert.Equal(sampleFrame, evaluated.SampleFrame);
            Assert.True(
                Math.Max(leftKneeChange, rightKneeChange) > 15.0,
                "The reported seated/dancing frame remained effectively in the target bind pose.");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    [Fact(Timeout = 180_000)]
    [Trait("Gate", "ExternalUserRetarget")]
    public async Task StandingGreetingRaisedForearmKeepsItsModelSpaceMotionOnDl1Player()
    {
        const int sampleFrame = 93;
        string sourcePath = Path.Combine(
            Environment.GetEnvironmentVariable(
                "DLR_FBX_ANIMATION_CORPUS_ROOT")
                ?? @"F:\Fbx\AnimationTests",
            "Standing Greeting.fbx");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        string actualSourceHash =
            await ProjectSourceImporter.ComputeSha256Async(
                sourcePath,
                CancellationToken.None);
        if (!string.Equals(
                actualSourceHash,
                StandingGreetingSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string packPath = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(packPath))
        {
            return;
        }

        FbxCoreAnimationImportResult imported =
            await new FbxAnimationDecoder().DecodeFileAsync(
                sourcePath);
        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(
                            temporaryDirectory,
                            "cache"),
                    MaximumMemoryBytes =
                        128L * 1024 * 1024,
                    MaximumMemoryEntryBytes =
                        32 * 1024 * 1024,
                    MaximumDiskBytes =
                        512L * 1024 * 1024,
                });
            Rp6lArchive archive =
                await Rp6lArchive.OpenAsync(packPath);
            Rp6lResourceDescriptor resource =
                Assert.IsType<Rp6lResourceDescriptor>(
                    archive.FindResource(
                        Rp6lResourceTypes.Mesh,
                        "player_1_tpp"));
            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            RigDefinition target =
                Assert.IsType<RigDefinition>(mesh.Rig);
            RetargetMap mapping =
                RetargetMapBuilder.CreateSuggested(
                    imported.Rig,
                    target);
            Dl1AuthoringPolicy policy =
                Dl1AuthoringPolicy.Create(
                    imported.Rig,
                    target,
                    mapping,
                    AnimationRootMode.InPlace);
            WriteLimbDirections(
                "source bind",
                imported.Rig.CreateBindPose(),
                "mixamorig:RightArm",
                "mixamorig:RightForeArm",
                "mixamorig:RightHand");
            WriteLimbDirections(
                "target bind",
                target.CreateBindPose(),
                "r_upperarm",
                "r_forearm",
                "r_hand");
            double worstDirectionErrorDegrees = 0.0;
            foreach (int diagnosticFrame in
                     new[] { 0, 70, sampleFrame })
            {
                double diagnosticSeconds =
                    imported.Clip.FrameRate.SecondsForFrame(
                        diagnosticFrame);
                SkeletonPose diagnosticSource =
                    imported.Clip.SamplePose(
                        imported.Rig,
                        diagnosticSeconds,
                        PlaybackMode.Clamp);
                SkeletonPose diagnosticTarget =
                    PoseRetargeter.RetargetBody(
                        diagnosticSource,
                        target,
                        mapping,
                        policy.TargetBindBoneIndices);
                SkeletonPose diagnosticPipelineTarget =
                    new AnimationEvaluator().Evaluate(
                        new EvaluationRequest(
                            imported.Rig,
                            target,
                            imported.Clip,
                            diagnosticSeconds,
                            PreviewProfile.RawAuthoring,
                            mapping,
                            playbackMode:
                                PlaybackMode.Clamp,
                            purpose:
                                EvaluationPurpose.Preview,
                            dl1AuthoringPolicy:
                                policy))
                    .AuthoredPose;
                WriteLimbDirections(
                    $"source frame {diagnosticFrame}",
                    diagnosticSource,
                    "mixamorig:RightArm",
                    "mixamorig:RightForeArm",
                    "mixamorig:RightHand");
                WriteLimbDirections(
                    $"target frame {diagnosticFrame}",
                    diagnosticTarget,
                    "r_upperarm",
                    "r_forearm",
                    "r_hand");
                WriteLimbDirections(
                    $"pipeline target frame {diagnosticFrame}",
                    diagnosticPipelineTarget,
                    "r_upperarm",
                    "r_forearm",
                    "r_hand");
                worstDirectionErrorDegrees = Math.Max(
                    worstDirectionErrorDegrees,
                    LimbDirectionErrorDegrees(
                        diagnosticSource,
                        diagnosticPipelineTarget,
                        "mixamorig:RightArm",
                        "mixamorig:RightForeArm",
                        "mixamorig:RightHand",
                        "r_upperarm",
                        "r_forearm",
                        "r_hand"));
                worstDirectionErrorDegrees = Math.Max(
                    worstDirectionErrorDegrees,
                    LimbDirectionErrorDegrees(
                        diagnosticSource,
                        diagnosticPipelineTarget,
                        "mixamorig:LeftArm",
                        "mixamorig:LeftForeArm",
                        "mixamorig:LeftHand",
                        "l_upperarm",
                        "l_forearm",
                        "l_hand"));
            }
            double seconds =
                imported.Clip.FrameRate.SecondsForFrame(
                    sampleFrame);
            SkeletonPose sourcePose =
                imported.Clip.SamplePose(
                    imported.Rig,
                    seconds,
                    PlaybackMode.Clamp);

            int sourceForearm =
                imported.Rig.GetBoneIndex(
                    "mixamorig:RightForeArm");
            int targetForearm =
                target.GetBoneIndex("r_forearm");
            BoneMapEntry forearmMap = Assert.Single(
                mapping.Entries,
                entry =>
                    entry.SourceBoneIndex == sourceForearm &&
                    entry.TargetBoneIndex == targetForearm);
            Assert.Equal(
                RetargetTransferPolicy.AnatomicalDirection,
                forearmMap.TransferPolicy);

            double sourceMotionDegrees =
                DirectionErrorDegrees(
                    JointDirection(
                        imported.Rig.CreateBindPose(),
                        "mixamorig:RightForeArm",
                        "mixamorig:RightHand"),
                    JointDirection(
                        sourcePose,
                        "mixamorig:RightForeArm",
                        "mixamorig:RightHand"));

            _output.WriteLine(
                $"Standing Greeting frames 0/70/{sampleFrame}: source forearm direction motion={sourceMotionDegrees:F6} degrees, worst authored DL1 arm-direction error={worstDirectionErrorDegrees:F6} degrees.");
            Assert.True(
                sourceMotionDegrees > 30.0,
                "The exact reported raised-arm frame did not contain the expected forearm motion.");
            Assert.InRange(
                worstDirectionErrorDegrees,
                0.0,
                8.0);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    [Fact(Timeout = 180_000)]
    [Trait("Gate", "ExternalUserRetarget")]
    public async Task TauntFistAndHeadAxesTransferToInstalledVolatile()
    {
        const int sampleFrame = 85;
        string corpusRoot =
            Environment.GetEnvironmentVariable(
                "DLR_FBX_ANIMATION_CORPUS_ROOT")
            ?? @"F:\Fbx\AnimationTests";
        string sourcePath = Path.Combine(
            corpusRoot,
            "Sources",
            "Taunt.fbx");
        if (!File.Exists(sourcePath))
        {
            sourcePath = Path.Combine(corpusRoot, "Taunt.fbx");
        }

        if (!File.Exists(sourcePath))
        {
            return;
        }

        string actualSourceHash =
            await ProjectSourceImporter.ComputeSha256Async(
                sourcePath,
                CancellationToken.None);
        if (!string.Equals(
                actualSourceHash,
                TauntSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string packPath = Path.Combine(
            install.DataPath,
            "common_meshes_PC.rpack");
        if (!File.Exists(packPath))
        {
            return;
        }

        FbxCoreAnimationImportResult imported =
            await new FbxAnimationDecoder().DecodeFileAsync(
                sourcePath);
        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(
                            temporaryDirectory,
                            "cache"),
                    MaximumMemoryBytes =
                        128L * 1024 * 1024,
                    MaximumMemoryEntryBytes =
                        32 * 1024 * 1024,
                    MaximumDiskBytes =
                        512L * 1024 * 1024,
                });
            Rp6lArchive archive =
                await Rp6lArchive.OpenAsync(packPath);
            Rp6lResourceDescriptor resource =
                Assert.IsType<Rp6lResourceDescriptor>(
                    archive.FindResource(
                        Rp6lResourceTypes.Mesh,
                        "zombie_voleteile_blue"));
            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            RigDefinition target =
                Assert.IsType<RigDefinition>(mesh.Rig);
            RetargetMap mapping =
                RetargetMapBuilder.CreateSuggested(
                    imported.Rig,
                    target);
            Dl1AuthoringPolicy policy =
                Dl1AuthoringPolicy.Create(
                    imported.Rig,
                    target,
                    mapping,
                    AnimationRootMode.InPlace);
            double seconds =
                imported.Clip.FrameRate.SecondsForFrame(
                    sampleFrame);
            SkeletonPose sourcePose =
                imported.Clip.SamplePose(
                    imported.Rig,
                    seconds,
                    PlaybackMode.Clamp);
            EvaluationFrame evaluated =
                new AnimationEvaluator().Evaluate(
                    new EvaluationRequest(
                        imported.Rig,
                        target,
                        imported.Clip,
                        seconds,
                        PreviewProfile.RawAuthoring,
                        mapping,
                        playbackMode:
                            PlaybackMode.Clamp,
                        purpose:
                            EvaluationPurpose.Preview,
                        dl1AuthoringPolicy:
                            policy));
            SkeletonPose targetPose = evaluated.AuthoredPose;

            string[] anatomicalTargets =
            [
                "head",
                "l_hand",
                "r_hand",
                .. FingerTargetNames(),
            ];
            int mappedAnatomicalTargets = 0;
            foreach (string targetName in anatomicalTargets)
            {
                int targetIndex = target.GetBoneIndex(targetName);
                if (targetIndex < 0)
                {
                    continue;
                }

                BoneMapEntry? row = mapping.Entries
                    .SingleOrDefault(
                        entry =>
                            entry.TargetBoneIndex ==
                            targetIndex);
                if (row is null)
                {
                    string? role =
                        HumanoidBoneSemanticClassifier.Classify(
                            target.Bones[targetIndex].SemanticRole ??
                            target.Bones[targetIndex].Name)?.Role;
                    string sourceCandidates = string.Join(
                        ", ",
                        imported.Rig.Bones
                            .Where(bone =>
                                HumanoidBoneSemanticClassifier.Classify(
                                    bone.SemanticRole ??
                                    bone.Name)?.Role ==
                                role)
                            .Select(static bone =>
                                $"{bone.Index}:{bone.Name}:{bone.Kind}"));
                    string targetCandidates = string.Join(
                        ", ",
                        target.Bones
                            .Where(bone =>
                                HumanoidBoneSemanticClassifier.Classify(
                                    bone.SemanticRole ??
                                    bone.Name)?.Role ==
                                role)
                            .Select(static bone =>
                                $"{bone.Index}:{bone.Name}:{bone.Kind}"));
                    _output.WriteLine(
                        $"Target anatomical row is not mapped: {targetName}; role={role}; source=[{sourceCandidates}]; target=[{targetCandidates}]");
                    continue;
                }

                Assert.Equal(
                    RetargetTransferPolicy.AnatomicalDirection,
                    row.TransferPolicy);
                mappedAnatomicalTargets++;
            }
            Assert.True(mappedAnatomicalTargets >= 20);

            double worstFingerError =
                FingerPalmDirectionErrorDegrees(
                    sourcePose,
                    target.CreateBindPose(),
                    targetPose,
                    mapping);
            double headError =
                HeadDirectionErrorDegrees(
                    sourcePose,
                    targetPose);
            _output.WriteLine(
                $"Taunt frame {sampleFrame}: worst finger palm-direction error={worstFingerError:F6} degrees; head body-frame direction error={headError:F6} degrees.");

            Assert.Equal(sampleFrame, evaluated.SampleFrame);
            Assert.InRange(worstFingerError, 0.0, 0.01);
            Assert.InRange(headError, 0.0, 5.0);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    [Fact(Timeout = 60_000)]
    [Trait("Gate", "ExternalUserRetarget")]
    public async Task ImportingHipHopDropsPreviouslyDisplayedRetailMeshFromSourceScene()
    {
        string sourcePath = ResolveHipHopSourcePath();
        if (!await IsReportedHipHopSourceAsync(sourcePath))
        {
            return;
        }

        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(
                temporaryDirectory,
                "mesh-binding-regression.dlraproj");
            await using var workspace =
                new Dl1AssetWorkspace(
                    Path.Combine(
                        temporaryDirectory,
                        "assets.sqlite3"),
                    Path.Combine(
                        temporaryDirectory,
                        "asset-cache"));
            await using var viewModel = new MainWindowViewModel(
                new JsonWorkspaceStateStore(
                    Path.Combine(
                        temporaryDirectory,
                        "workspace.json")),
                new UserRetargetFileDialogs(
                    projectPath,
                    sourcePath),
                workspace);
            SkeletonRenderData retailSkeleton = new(
                Enumerable.Range(0, 87)
                    .Select(index =>
                        new BoneRenderData(
                            $"retail_{index}",
                            index - 1,
                            Matrix4x4.Identity,
                            Matrix4x4.Identity,
                            false))
                    .ToArray(),
                Matrix4x4.Identity);
            MeshRenderData retailMesh = new(
                "player_1_tpp/flashlight/lod0/part0",
                new MeshVertex[]
                {
                    new(
                        Vector3.Zero,
                        Vector3.UnitZ,
                        Vector2.Zero,
                        Vector4.UnitX,
                        Vector4.Zero),
                    new(
                        Vector3.UnitX,
                        Vector3.UnitZ,
                        Vector2.UnitX,
                        Vector4.UnitX,
                        Vector4.Zero),
                    new(
                        Vector3.UnitY,
                        Vector3.UnitZ,
                        Vector2.UnitY,
                        Vector4.UnitX,
                        Vector4.Zero),
                },
                new uint[] { 0, 1, 2 },
                Matrix4x4.Identity,
                new Matrix4x4[] { Matrix4x4.Identity },
                IsSkinned: true)
            {
                SkinBoneIndices = new int[] { 74 },
            };
            viewModel.SetSourcePreviewScene(
                [retailMesh],
                retailSkeleton);

            await viewModel.ImportAnimationCommand
                .ExecuteAsync(null);

            RenderFrameSnapshot sourceFrame =
                viewModel.SourceViewport.SceneSource
                    .CaptureFrame();
            Assert.Empty(sourceFrame.Meshes);
            Assert.Equal(
                65,
                Assert.IsType<SkeletonRenderData>(
                        sourceFrame.Skeleton)
                    .Bones
                    .Count);
            Assert.DoesNotContain(
                viewModel.Diagnostics,
                static diagnostic =>
                    diagnostic.Detail?.Contains(
                        "outside 65 rows",
                        StringComparison.OrdinalIgnoreCase) ==
                    true);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    private static string ResolveHipHopSourcePath() =>
        Path.Combine(
            Environment.GetEnvironmentVariable(
                "DLR_FBX_ANIMATION_CORPUS_ROOT")
                ?? @"F:\Fbx\AnimationTests",
            "Hip Hop Dancing.fbx");

    private static async Task<bool> IsReportedHipHopSourceAsync(
        string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        string hash = await ProjectSourceImporter
            .ComputeSha256Async(
                sourcePath,
                CancellationToken.None);
        return string.Equals(
            hash,
            HipHopSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FingerTargetNames()
    {
        foreach (string side in new[] { "l", "r" })
        {
            foreach (string digit in new[] { "0", "1", "2", "3", "4" })
            {
                for (int segment = 1; segment <= 3; segment++)
                {
                    yield return
                        $"{side}_finger{digit}{segment}";
                }
            }
        }
    }

    private double FingerPalmDirectionErrorDegrees(
        SkeletonPose source,
        SkeletonPose targetBind,
        SkeletonPose target,
        RetargetMap mapping)
    {
        double worst = 0.0;
        foreach ((string side, string sourceSide, string targetSide)
                 in new[]
                 {
                     ("left", "Left", "l"),
                     ("right", "Right", "r"),
                 })
        {
            string[] targetDigits = ["0", "1", "2", "3", "4"];
            var mappedDigits = new List<(
                string TargetDigit,
                BoneMapEntry[] Segments,
                int SourceTerminal)>();
            foreach (string targetDigit in targetDigits)
            {
                var segments = new BoneMapEntry[3];
                bool complete = true;
                for (int segment = 1; segment <= 3; segment++)
                {
                    int targetIndex = target.Rig.GetBoneIndex(
                        $"{targetSide}_finger{targetDigit}{segment}");
                    BoneMapEntry? row = targetIndex < 0
                        ? null
                        : mapping.Entries.SingleOrDefault(
                            entry =>
                                entry.TargetBoneIndex == targetIndex);
                    if (row is null)
                    {
                        complete = false;
                        break;
                    }

                    segments[segment - 1] = row;
                }

                if (!complete ||
                    !TryGetFingerRole(
                        source.Rig,
                        segments[0].SourceBoneIndex,
                        out string sourceFingerSide,
                        out string sourceDigit,
                        out int sourceSegment) ||
                    sourceFingerSide != side ||
                    sourceSegment != 1)
                {
                    continue;
                }

                int sourceTerminal = source.Rig.Bones
                    .Where(bone =>
                        HumanoidBoneSemanticClassifier.Classify(
                            bone.SemanticRole ??
                            bone.Name)?.Role ==
                        $"finger.{side}.{sourceDigit}.4")
                    .Select(static bone => bone.Index)
                    .SingleOrDefault(-1);
                if (sourceTerminal >= 0)
                {
                    mappedDigits.Add((
                        targetDigit,
                        segments,
                        sourceTerminal));
                }
            }

            Assert.Equal(5, mappedDigits.Count);
            var palmDigits = mappedDigits
                .Where(static digit =>
                    digit.TargetDigit != "0")
                .DistinctBy(static digit =>
                    digit.Segments[0].SourceBoneIndex)
                .ToArray();
            Assert.True(palmDigits.Length >= 2);
            QuaternionD sourcePalm =
                PalmFrame(
                    source,
                    $"mixamorig:{sourceSide}Hand",
                    palmDigits
                        .Select(digit =>
                            source.Rig.Bones[
                                digit.Segments[0].SourceBoneIndex].Name)
                        .ToArray());
            QuaternionD targetBindPalm =
                PalmFrame(
                    targetBind,
                    $"{targetSide}_hand",
                    palmDigits
                        .Select(digit =>
                            target.Rig.Bones[
                                digit.Segments[0].TargetBoneIndex].Name)
                        .ToArray());
            int targetHand =
                target.Rig.GetBoneIndex(
                    $"{targetSide}_hand");
            QuaternionD targetPalm = (
                ComputeGlobalRotation(target, targetHand) *
                ComputeGlobalRotation(
                    targetBind,
                    targetHand).Inverse() *
                targetBindPalm
            ).Normalized();

            foreach ((string targetDigit,
                      BoneMapEntry[] segments,
                      int sourceTerminal)
                     in mappedDigits)
            {
                for (int segment = 1; segment <= 3; segment++)
                {
                    BoneMapEntry current = segments[segment - 1];
                    int sourceEnd = segment < 3
                        ? segments[segment].SourceBoneIndex
                        : sourceTerminal;
                    Vector3D sourceDirection = (
                        source.GlobalMatrices[sourceEnd].Translation -
                        source.GlobalMatrices[
                            current.SourceBoneIndex].Translation
                    ).Normalized();
                    string targetBone = target.Rig.Bones[
                        current.TargetBoneIndex].Name;
                    Vector3D targetDirection;
                    if (segment < 3)
                    {
                        targetDirection = (
                            target.GlobalMatrices[
                                segments[segment].TargetBoneIndex].Translation -
                            target.GlobalMatrices[
                                current.TargetBoneIndex].Translation
                        ).Normalized();
                    }
                    else
                    {
                        string previous = target.Rig.Bones[
                            segments[segment - 2].TargetBoneIndex].Name;
                        Vector3D bindDirection =
                            JointDirection(
                                targetBind,
                                previous,
                                targetBone);
                        int targetIndex =
                            target.Rig.GetBoneIndex(
                                targetBone);
                        Vector3D localAxis =
                            ComputeGlobalRotation(
                                targetBind,
                                targetIndex)
                            .Inverse()
                            .Rotate(bindDirection);
                        targetDirection =
                            ComputeGlobalRotation(
                                target,
                                targetIndex)
                            .Rotate(localAxis)
                            .Normalized();
                    }

                    double error = DirectionErrorDegrees(
                        sourcePalm
                            .Inverse()
                            .Rotate(sourceDirection),
                        targetPalm
                            .Inverse()
                            .Rotate(targetDirection));
                    _output.WriteLine(
                        $"{side} target digit {targetDigit} segment {segment}: palm-direction error={error:F6} degrees");
                    worst = Math.Max(worst, error);
                }
            }
        }

        return worst;
    }

    private static bool TryGetFingerRole(
        RigDefinition rig,
        int boneIndex,
        out string side,
        out string digit,
        out int segment)
    {
        side = string.Empty;
        digit = string.Empty;
        segment = 0;
        if (boneIndex < 0 ||
            boneIndex >= rig.Bones.Length)
        {
            return false;
        }

        string? role = HumanoidBoneSemanticClassifier.Classify(
            rig.Bones[boneIndex].SemanticRole ??
            rig.Bones[boneIndex].Name)?.Role;
        string[]? parts = role?.Split('.');
        if (parts is not { Length: 4 } ||
            parts[0] != "finger" ||
            !int.TryParse(parts[3], out segment))
        {
            return false;
        }

        side = parts[1];
        digit = parts[2];
        return true;
    }

    private static double HeadDirectionErrorDegrees(
        SkeletonPose source,
        SkeletonPose target)
    {
        SkeletonPose sourceBind =
            source.Rig.CreateBindPose();
        SkeletonPose targetBind =
            target.Rig.CreateBindPose();
        QuaternionD sourceBody =
            BodyFrame(source, source: true);
        QuaternionD sourceBindBody =
            BodyFrame(sourceBind, source: true);
        QuaternionD targetBindBody =
            BodyFrame(targetBind, source: false);
        QuaternionD targetBody = (
            targetBindBody *
            sourceBindBody.Inverse() *
            sourceBody
        ).Normalized();
        Vector3D sourceHeadDirection =
            JointDirection(
                source,
                "mixamorig:Head",
                "mixamorig:HeadTop_End");
        string? targetHeadEnd =
            FindHeadEndName(
                target.Rig,
                "head");
        Vector3D targetHeadDirection;
        if (targetHeadEnd is not null)
        {
            targetHeadDirection =
                JointDirection(
                    target,
                    "head",
                    targetHeadEnd);
        }
        else
        {
            int targetHead =
                target.Rig.GetBoneIndex("head");
            Vector3D bindIncoming =
                JointDirection(
                    targetBind,
                    "neck",
                    "head");
            Vector3D localAxis =
                ComputeGlobalRotation(
                    targetBind,
                    targetHead)
                .Inverse()
                .Rotate(bindIncoming);
            targetHeadDirection =
                ComputeGlobalRotation(
                    target,
                    targetHead)
                .Rotate(localAxis)
                .Normalized();
        }
        return DirectionErrorDegrees(
            sourceBody
                .Inverse()
                .Rotate(sourceHeadDirection),
            targetBody
                .Inverse()
                .Rotate(targetHeadDirection));
    }

    private static QuaternionD BodyFrame(
        SkeletonPose pose,
        bool source)
    {
        Vector3D right = source
            ? pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "mixamorig:RightShoulder")].Translation -
              pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "mixamorig:LeftShoulder")].Translation
            : pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "r_clavicle")].Translation -
              pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "l_clavicle")].Translation;
        Vector3D up = source
            ? pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "mixamorig:Spine2")].Translation -
              pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "mixamorig:Hips")].Translation
            : pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "spine2")].Translation -
              pose.GlobalMatrices[
                  pose.Rig.GetBoneIndex(
                      "pelvis")].Translation;
        return FrameFromPrimarySecondary(right, up);
    }

    private static string? FindHeadEndName(
        RigDefinition rig,
        string headName)
    {
        int head = rig.GetBoneIndex(headName);
        return rig.Bones
            .Where(bone =>
                {
                    if (bone.ParentIndex != head)
                    {
                        return false;
                    }

                    string compact = new(
                        bone.Name
                            .Where(char.IsLetterOrDigit)
                            .Select(char.ToLowerInvariant)
                            .ToArray());
                    return compact.Contains(
                               "headtopend",
                               StringComparison.Ordinal) ||
                           compact.Contains(
                               "headend",
                               StringComparison.Ordinal);
                })
            .Select(static bone => bone.Name)
            .SingleOrDefault();
    }

    private static QuaternionD PalmFrame(
        SkeletonPose pose,
        string handName,
        IReadOnlyList<string> rootNames)
    {
        Vector3D hand =
            pose.GlobalMatrices[
                pose.Rig.GetBoneIndex(
                    handName)].Translation;
        Vector3D[] roots = rootNames
            .Select(name =>
                pose.GlobalMatrices[
                    pose.Rig.GetBoneIndex(
                        name)].Translation)
            .ToArray();
        Vector3D forward =
            (roots.Aggregate(
                 Vector3D.Zero,
                 static (sum, value) => sum + value) /
             roots.Length) -
            hand;
        return FrameFromPrimarySecondary(
            forward,
            roots[0] - roots[^1]);
    }

    private static QuaternionD FrameFromPrimarySecondary(
        Vector3D primary,
        Vector3D secondary)
    {
        Vector3D x = primary.Normalized();
        Vector3D y = (
            secondary -
            (Vector3D.Dot(secondary, x) * x)
        ).Normalized();
        Vector3D z =
            Vector3D.Cross(x, y).Normalized();
        y = Vector3D.Cross(z, x).Normalized();
        return QuaternionD.FromRotationMatrix(
            new TransformMatrix(
                x.X, y.X, z.X, 0.0,
                x.Y, y.Y, z.Y, 0.0,
                x.Z, y.Z, z.Z, 0.0,
                0.0, 0.0, 0.0, 1.0));
    }

    private void WriteBonePair(
        RigDefinition sourceRig,
        SkeletonPose sourcePose,
        string sourceName,
        RigDefinition targetRig,
        SkeletonPose targetPose,
        string targetName)
    {
        int sourceIndex = sourceRig.GetBoneIndex(sourceName);
        int targetIndex = targetRig.GetBoneIndex(targetName);
        TransformTRS sourceBind =
            sourceRig.Bones[sourceIndex].LocalBindPose;
        TransformTRS targetBind =
            targetRig.Bones[targetIndex].LocalBindPose;
        TransformTRS sourceLocal =
            sourcePose.LocalTransforms[sourceIndex];
        TransformTRS targetLocal =
            targetPose.LocalTransforms[targetIndex];
        _output.WriteLine(
            $"{sourceName}->{targetName}: " +
            $"sourceLocalDelta={RotationDeltaDegrees(sourceBind.Rotation, sourceLocal.Rotation):F3}, " +
            $"targetLocalDelta={RotationDeltaDegrees(targetBind.Rotation, targetLocal.Rotation):F3}, " +
            $"sourceGlobal={Format(sourcePose.GlobalMatrices[sourceIndex].Translation)}, " +
            $"targetGlobal={Format(targetPose.GlobalMatrices[targetIndex].Translation)}");
    }

    private void WriteLimbDirections(
        string label,
        SkeletonPose pose,
        string upperArmName,
        string forearmName,
        string handName)
    {
        Vector3D upperDirection = JointDirection(
            pose,
            upperArmName,
            forearmName);
        Vector3D forearmDirection = JointDirection(
            pose,
            forearmName,
            handName);
        _output.WriteLine(
            $"{label}: upper={Format(upperDirection)}, forearm={Format(forearmDirection)}");
    }

    private static Vector3D JointDirection(
        SkeletonPose pose,
        string parentName,
        string childName)
    {
        int parent = pose.Rig.GetBoneIndex(parentName);
        int child = pose.Rig.GetBoneIndex(childName);
        return (
            pose.GlobalMatrices[child].Translation -
            pose.GlobalMatrices[parent].Translation
        ).Normalized();
    }

    private static double LimbDirectionErrorDegrees(
        SkeletonPose source,
        SkeletonPose target,
        string sourceRoot,
        string sourceMid,
        string sourceEnd,
        string targetRoot,
        string targetMid,
        string targetEnd) =>
        Math.Max(
            DirectionErrorDegrees(
                JointDirection(
                    source,
                    sourceRoot,
                    sourceMid),
                JointDirection(
                    target,
                    targetRoot,
                    targetMid)),
            DirectionErrorDegrees(
                JointDirection(
                    source,
                    sourceMid,
                    sourceEnd),
                JointDirection(
                    target,
                    targetMid,
                    targetEnd)));

    private static double DirectionErrorDegrees(
        Vector3D first,
        Vector3D second) =>
        Math.Acos(
            Math.Clamp(
                Vector3D.Dot(
                    first.Normalized(),
                    second.Normalized()),
                -1.0,
                1.0)) *
        (180.0 / Math.PI);

    private static double RotationDeltaDegrees(
        QuaternionD first,
        QuaternionD second)
    {
        QuaternionD a = first.Normalized();
        QuaternionD b = second.Normalized();
        double dot = Math.Clamp(
            Math.Abs(
                (a.X * b.X) +
                (a.Y * b.Y) +
                (a.Z * b.Z) +
                (a.W * b.W)),
            0.0,
            1.0);
        return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
    }

    private static QuaternionD ComputeGlobalRotation(
        SkeletonPose pose,
        int boneIndex)
    {
        var chain = new Stack<int>();
        int current = boneIndex;
        while (current >= 0)
        {
            chain.Push(current);
            current = pose.Rig.Bones[current].ParentIndex;
        }

        QuaternionD rotation = QuaternionD.Identity;
        while (chain.TryPop(out int index))
        {
            rotation =
                (
                    rotation *
                    pose.LocalTransforms[index].Rotation
                ).Normalized();
        }

        return rotation;
    }

    private static string Format(Vector3D value) =>
        FormattableString.Invariant(
            $"({value.X:F4},{value.Y:F4},{value.Z:F4})");

    private sealed class UserRetargetFileDialogs(
        string projectPath,
        string animationPath) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(
            string? initialPath) =>
            projectPath;

        public string? ShowOpenAnimationDialog(
            string? initialPath) =>
            animationPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;
    }
}
