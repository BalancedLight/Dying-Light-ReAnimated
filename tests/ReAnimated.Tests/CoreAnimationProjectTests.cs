using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class CoreAnimationProjectTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"ReAnimated-CoreTests-{Guid.NewGuid():N}");

    [Fact]
    public void ClipSamplesRationalFrameTimeAndMorphValues()
    {
        var rig = new RigDefinition(
            "single",
            "Single",
            [new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root)]);
        var clip = new AnimationClip(
            "walk",
            new FrameRate(60, 2),
            31,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0.0, TransformTRS.Identity),
                        new TransformKeyframe(
                            30.0,
                            new TransformTRS(
                                new Vector3D(6.0, 0.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ],
            [
                new ScalarTrack(
                    "jaw_open",
                    [new ScalarKeyframe(0.0, 0.0), new ScalarKeyframe(30.0, 1.0)]),
            ]);

        SkeletonPose pose = clip.SamplePose(rig, 0.5);
        ImmutableDictionary<string, double> morphs = clip.SampleScalars(0.5);

        Assert.Equal(30, clip.FrameRate.Numerator);
        Assert.Equal(1, clip.FrameRate.Denominator);
        Assert.Equal(3.0, pose.LocalTransforms[0].Translation.X, 10);
        Assert.Equal(0.5, morphs["jaw_open"], 10);
        Assert.Equal(1.0, clip.DurationSeconds, 10);
    }

    [Fact]
    public void FreshProjectRoundTripsCamelCaseContractAndAtomicOverwrite()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(_temporaryDirectory, "authoring.dlraproj");
        Guid assetId = Guid.NewGuid();
        Guid propAssetId = Guid.NewGuid();
        BoneEditLayer editLayer = new(
            Guid.NewGuid(),
            "Raise root",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                Vector3D.UnitY,
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ],
                    BoneEditInterpolation.Step),
            ]);
        DlraProject project = DlraProject.Create("DL1 Authoring") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = assetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/walk.fbx",
                    ContentSha256 = new string('B', 64),
                },
                new ProjectAssetReference
                {
                    Id = propAssetId,
                    Kind = ProjectAssetKind.RetailGameResource,
                    RelativePath = "Data0.pak::models/props/pipe.msh",
                    ResourceId = "_MESH_",
                    ContentSha256 = new string('C', 64),
                    RetailIdentity = new ProjectRetailAssetIdentity
                    {
                        InstallFingerprint = "steam-239140-control",
                        ProviderId = "dl1-rpacks",
                        ProviderPack = "DW/Data/common_meshes_pc.rpack",
                        ResourceType = 272,
                        ResourceIndex = 42,
                        ResourceName = "pipe",
                        Precedence = 10_042,
                        ContentSha256 = new string('C', 64),
                    },
                },
            ],
            Dl1Settings = new Dl1ProjectSettings
            {
                InstallFingerprint = "steam-239140-control",
                ValidatedBuildFingerprint = "dl1-win64-1.55",
                AdditionalRpackRoots = ["mods/authoring"],
                ShowCameraHelpers = false,
            },
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "walk",
                    SourceAssetId = assetId,
                    TargetAssetId = propAssetId,
                    TargetRigId = "builtin:male_npc_infected",
                    SourceRigSignature = "source-rig-sha256",
                    TargetRigSignature = "target-rig-sha256",
                    MappingFingerprint = "mapping-sha256",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                    RootMotionMode = Dl1RootMotionMode.Bip01,
                    BoneMappings =
                    [
                        new ProjectBoneMapping
                        {
                            SourceBoneName = "Hips",
                            TargetBoneName = "bip01",
                            Method = "manual",
                            IsLocked = true,
                            IsReviewed = true,
                            MappingKind =
                                RetargetMappingKind.HelperOverride,
                            TransferPolicy =
                                RetargetTransferPolicy.RestRelative,
                            ComponentPolicy =
                                RetargetComponentPolicy.RotationTranslation,
                        },
                    ],
                    TargetBindReviews =
                    [
                        new ProjectTargetBindReview
                        {
                            TargetBoneIndex = 7,
                            TargetBoneName = "RefCamera",
                        },
                    ],
                    EditLayers = [editLayer],
                    MorphBindings =
                    [
                        new ProjectMorphBinding
                        {
                            SourceChannel = "jawOpen",
                            TargetMorph = "jaw_open",
                            TargetDescriptorHash = 0x12345678,
                            Weight = 0.75,
                            IsReviewed = true,
                            IsLocked = true,
                        },
                    ],
                    MorphEditLayers =
                    [
                        new MorphEditLayer(
                            Guid.NewGuid(),
                            "FED smile",
                            MorphEditBlendMode.Additive,
                            MorphEditLayerScope.AuthoredExportable,
                            1,
                            [
                                new MorphEditTrack(
                                    "jaw_open",
                                    [new ScalarKeyframe(0, 0.25)]),
                            ]),
                    ],
                    IkLayers =
                    [
                        new ProjectIkLayer
                        {
                            Name = "Left hand",
                            ChainName = "left_hand",
                            Weight = 0.9,
                            BakeToEditLayer = true,
                            Keyframes =
                            [
                                new ProjectIkKeyframe
                                {
                                    Frame = 0,
                                    Effector = new Vector3D(1, 2, 3),
                                    Pole = new Vector3D(0, 1, 0),
                                    EndOrientation = QuaternionD.Identity,
                                },
                            ],
                        },
                    ],
                    Attachments =
                    [
                        new AttachmentBinding(
                            Guid.NewGuid(),
                            propAssetId,
                            "pipe",
                            0,
                            TransformTRS.Identity,
                            AttachmentScope.AuthoredExportable),
                    ],
                },
            ],
            PreviewMode = ProjectPreviewMode.Raw,
            PreviewProfile = new PreviewProfile(
                "dl1-profile",
                PreviewViewMode.ThirdPerson,
                AuthoringPreviewFidelity.AuthoringAccurate,
                PreviewVisualStyle.MaterialApproximation,
                null,
                CameraLens.Default,
                TransformTRS.Identity,
                PreviewFidelityTier.Dl1Profile,
                Dl1PreviewContext.Dl1Body,
                profileVersion: 2,
                proceduralToggles: ["head_spine_correction"]),
        };

        string savedPath = ProjectSerializer.SaveAtomic(project, path);
        string json = File.ReadAllText(savedPath);
        DlraProject loaded = ProjectSerializer.Load(savedPath);

        Assert.Contains("\"format\": \"dl-reanimated-csharp-project\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"game\": \"dying-light-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"previewMode\": \"raw\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("schema_version", json, StringComparison.Ordinal);
        Assert.Equal("DL1 Authoring", loaded.Name);
        Assert.Equal(BoneEditBlendMode.Additive, loaded.Animations[0].EditLayers[0].BlendMode);
        Assert.Equal(
            BoneEditInterpolation.Step,
            loaded.Animations[0].EditLayers[0].Tracks[0].Interpolation);
        Assert.Equal(Vector3D.UnitY, loaded.Animations[0].EditLayers[0].Tracks[0].Keyframes[0].Value.Translation);
        Assert.Equal("pipe", Assert.Single(loaded.Animations[0].Attachments).Name);
        Assert.Equal(
            "steam-239140-control",
            loaded.Assets[1].RetailIdentity?.InstallFingerprint);
        Assert.Equal(
            0x12345678u,
            Assert.Single(loaded.Animations[0].MorphBindings).TargetDescriptorHash);
        Assert.True(
            Assert.Single(loaded.Animations[0].MorphBindings).IsReviewed);
        Assert.True(
            Assert.Single(loaded.Animations[0].MorphBindings).IsLocked);
        Assert.True(Assert.Single(loaded.Animations[0].IkLayers).BakeToEditLayer);
        ProjectBoneMapping loadedMapping =
            Assert.Single(loaded.Animations[0].BoneMappings);
        Assert.True(loadedMapping.IsLocked);
        Assert.True(loadedMapping.IsReviewed);
        Assert.Equal(
            RetargetMappingKind.HelperOverride,
            loadedMapping.MappingKind);
        Assert.Equal(
            RetargetTransferPolicy.RestRelative,
            loadedMapping.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.RotationTranslation,
            loadedMapping.ComponentPolicy);
        Assert.Contains(
            "\"mappingKind\": \"helperOverride\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"interpolation\": \"step\"",
            json,
            StringComparison.Ordinal);
        ProjectTargetBindReview loadedBindReview =
            Assert.Single(loaded.Animations[0].TargetBindReviews);
        Assert.Equal(7, loadedBindReview.TargetBoneIndex);
        Assert.Equal("RefCamera", loadedBindReview.TargetBoneName);
        Assert.Equal(
            "dl1-win64-1.55",
            loaded.Dl1Settings.ValidatedBuildFingerprint);
        Assert.False(loaded.Dl1Settings.ShowCameraHelpers);
        Assert.Equal(ProjectPreviewMode.Raw, loaded.PreviewMode);
        Assert.Equal(PreviewFidelityTier.Dl1Profile, loaded.PreviewProfile.FidelityTier);
        Assert.Equal(Dl1PreviewContext.Dl1Body, loaded.PreviewProfile.Context);
        Assert.Equal(2, loaded.PreviewProfile.ProfileVersion);

        JsonObject earlierSchemaOne = Assert.IsType<JsonObject>(
            JsonNode.Parse(json));
        JsonArray animations = Assert.IsType<JsonArray>(
            earlierSchemaOne["animations"]);
        JsonObject animation = Assert.IsType<JsonObject>(animations[0]);
        JsonArray editLayers = Assert.IsType<JsonArray>(
            animation["editLayers"]);
        JsonObject storedLayer = Assert.IsType<JsonObject>(editLayers[0]);
        JsonArray tracks = Assert.IsType<JsonArray>(storedLayer["tracks"]);
        JsonObject storedTrack = Assert.IsType<JsonObject>(tracks[0]);
        Assert.True(storedTrack.Remove("interpolation"));
        File.WriteAllText(savedPath, earlierSchemaOne.ToJsonString());

        DlraProject loadedWithoutInterpolation =
            ProjectSerializer.Load(savedPath);
        Assert.Equal(
            BoneEditInterpolation.Linear,
            loadedWithoutInterpolation
                .Animations[0]
                .EditLayers[0]
                .Tracks[0]
                .Interpolation);

        storedTrack["interpolation"] = "cubic";
        File.WriteAllText(savedPath, earlierSchemaOne.ToJsonString());
        ProjectFormatException unsupportedInterpolation =
            Assert.Throws<ProjectFormatException>(
                () => ProjectSerializer.Load(savedPath));
        Assert.Contains(
            "invalid JSON",
            unsupportedInterpolation.Message,
            StringComparison.Ordinal);

        File.WriteAllText(savedPath, json);
        ProjectSerializer.SaveAtomic(project with { Name = "Renamed" }, path);
        Assert.Equal("Renamed", ProjectSerializer.Load(path).Name);
        Assert.Empty(Directory.EnumerateFiles(_temporaryDirectory, "*.tmp"));
    }

    [Fact]
    public void FacialFbxSourceIdentityRoundTripsAndExcludesMimicAnm2()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "facial-source.dlraproj");
        Guid bodyAssetId = Guid.NewGuid();
        Guid facialAssetId = Guid.NewGuid();
        Guid mimicAssetId = Guid.NewGuid();
        ProjectAssetReference bodyAsset = new()
        {
            Id = bodyAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/body.fbx",
            ContentSha256 = new string('1', 64),
        };
        ProjectAssetReference facialAsset = new()
        {
            Id = facialAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/face.fbx",
            ContentSha256 = new string('2', 64),
        };
        ProjectAssetReference mimicAsset = new()
        {
            Id = mimicAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/face.anm2",
            ContentSha256 = new string('3', 64),
        };
        var animation = new ProjectAnimation
        {
            Name = "Body and facial FBX",
            SourceAssetId = bodyAssetId,
            FacialSourceAssetId = facialAssetId,
            FacialSourceValueUnit =
                ProjectMorphSourceValueUnit.Percent,
            TargetRigId = "retail:facial",
            MimicProfileId = "dl1-common46",
            MimicMappingFingerprint = new string('4', 64),
            FrameRate = new FrameRate(30, 1),
            FrameCount = 3,
        };
        DlraProject project =
            DlraProject.Create("Facial source") with
            {
                Assets = [bodyAsset, facialAsset],
                Animations = [animation],
            };

        ProjectSerializer.SaveAtomic(project, path);
        string json = File.ReadAllText(path);
        ProjectAnimation reopened = Assert.Single(
            ProjectSerializer.Load(path).Animations);

        Assert.Contains(
            "\"facialSourceAssetId\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"facialSourceValueUnit\": \"percent\"",
            json,
            StringComparison.Ordinal);
        Assert.Equal(
            facialAssetId,
            reopened.FacialSourceAssetId);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            reopened.FacialSourceValueUnit);

        DlraProject ambiguous = project with
        {
            Assets = project.Assets.Add(mimicAsset),
            Animations =
            [
                animation with
                {
                    MimicAssetId = mimicAssetId,
                },
            ],
        };
        ArgumentException error =
            Assert.Throws<ArgumentException>(
                ambiguous.Validate);
        Assert.Contains(
            "cannot use both",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderRejectsLegacySnakeCaseWithoutModifyingIt()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(_temporaryDirectory, "legacy.dlraproj");
        const string legacy =
            """{"schema_version":10,"name":"legacy","game_id":"dying_light_1"}""";
        File.WriteAllText(path, legacy);

        LegacyProjectFormatException exception = Assert.Throws<LegacyProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Equal(10, exception.DetectedSchemaVersion);
        Assert.Equal(legacy, File.ReadAllText(path));
    }

    [Fact]
    public void SaverRefusesToOverwriteLegacyProjectWithoutModifyingIt()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "legacy-save-target.dlraproj");
        const string legacy =
            """{"schema_version":7,"name":"legacy","game_id":"dying_light_1"}""";
        File.WriteAllText(path, legacy);

        LegacyProjectFormatException exception =
            Assert.Throws<LegacyProjectFormatException>(
                () => ProjectSerializer.SaveAtomic(
                    DlraProject.Create("Fresh C# project"),
                    path));

        Assert.Equal(7, exception.DetectedSchemaVersion);
        Assert.Contains(
            "nor overwrites legacy projects",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(legacy, File.ReadAllText(path));
        Assert.Empty(
            Directory.EnumerateFiles(
                _temporaryDirectory,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SaverRefusesToOverwriteUnknownProjectWithoutModifyingIt()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "unknown-save-target.dlraproj");
        const string unknown =
            """{"schemaVersion":1,"format":"another-application"}""";
        File.WriteAllText(path, unknown);

        ProjectFormatException exception =
            Assert.Throws<ProjectFormatException>(
                () => ProjectSerializer.SaveAtomic(
                    DlraProject.Create("Fresh C# project"),
                    path));

        Assert.Contains(
            "Refusing to overwrite",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(unknown, File.ReadAllText(path));
        Assert.Empty(
            Directory.EnumerateFiles(
                _temporaryDirectory,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void LoaderRejectsUnknownSchemaOneFieldsWithoutModifyingFile()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "unknown-field.dlraproj");
        ProjectSerializer.SaveAtomic(
            DlraProject.Create("Strict project"),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        document["unexpectedRetailPayload"] = true;
        string invalid = document.ToJsonString();
        File.WriteAllText(path, invalid);

        ProjectFormatException exception =
            Assert.Throws<ProjectFormatException>(
                () => ProjectSerializer.Load(path));

        Assert.Contains(
            "invalid JSON",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    [Fact]
    public void LoaderRejectsMissingRequiredSchemaOneFields()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "missing-preview-profile.dlraproj");
        ProjectSerializer.SaveAtomic(
            DlraProject.Create("Strict project"),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        Assert.True(document.Remove("previewProfile"));
        File.WriteAllText(path, document.ToJsonString());

        ProjectFormatException exception =
            Assert.Throws<ProjectFormatException>(
                () => ProjectSerializer.Load(path));

        Assert.Contains(
            "missing required property 'previewProfile'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderRejectsOversizedProjectBeforeJsonAllocation()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(_temporaryDirectory, "oversized.dlraproj");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(ProjectSerializer.MaximumProjectBytes + 1);
        }

        ProjectFormatException exception = Assert.Throws<ProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Contains("cannot exceed", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
