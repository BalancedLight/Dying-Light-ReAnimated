using System.Collections.Immutable;
using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class FacialFbxReviewViewModelTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-FacialReview-{Guid.NewGuid():N}");

    [Fact]
    public async Task FacialFbxImportRequiresExplicitUnitAndPersistsReview()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "facial-review.dlraproj");
        string facialPath = Path.Combine(
            _temporaryDirectory,
            "face.fbx");
        await File.WriteAllBytesAsync(facialPath, [0]);

        RigDefinition rig = CreateRig();
        Guid sourceAssetId = Guid.NewGuid();
        ProjectAssetReference targetAsset =
            CreateRetailAsset();
        var animation = new ProjectAnimation
        {
            Name = "Body",
            SourceAssetId = sourceAssetId,
            TargetAssetId = targetAsset.Id,
            TargetRigId = rig.Id,
            TargetRigSignature = RigSignature.Compute(rig),
            FrameRate = new FrameRate(30000, 1001),
            FrameCount = 17,
        };
        DlraProject project = DlraProject.Create("Facial review") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/body.fbx",
                },
                targetAsset,
            ],
            Animations = [animation],
        };
        var importer = new RecordingFacialFbxImporter(rig);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new FacialProjectFileDialogs(
                projectPath,
                facialPath),
            assets,
            new Dl1InstalledBuildFingerprintService(),
            importer)
        {
            ProjectPath = projectPath,
        };
        SetPrivateField(viewModel, "_project", project);
        SetPrivateField(
            viewModel,
            "_activeAnimationId",
            animation.Id);
        SetPrivateField(viewModel, "_targetRig", rig);
        SetPrivateField(
            viewModel,
            "_targetProjectAsset",
            targetAsset);
        SetImportedAnimationSession(
            viewModel,
            rig,
            new AnimationClip(
                "Body",
                animation.FrameRate,
                animation.FrameCount,
                [
                    new TransformTrack(
                        0,
                        [
                            new TransformKeyframe(
                                0,
                                TransformTRS.Identity),
                            new TransformKeyframe(
                                animation.FrameCount - 1,
                                TransformTRS.Identity),
                        ]),
                ]),
            facialPath);

        Assert.False(
            viewModel.ImportFacialFbxCommand.CanExecute(null));
        viewModel.FacialFpp.SelectedFacialSourceValueUnit =
            ProjectMorphSourceValueUnit.Percent;
        Assert.True(
            viewModel.ImportFacialFbxCommand.CanExecute(null));

        await viewModel.ImportFacialFbxCommand.ExecuteAsync(null);

        Assert.True(
            importer.SourceValueUnit.HasValue,
            string.Join(
                Environment.NewLine,
                viewModel.Diagnostics.Select(
                    static diagnostic =>
                        $"{diagnostic.Area}: {diagnostic.Message}: {diagnostic.Detail}")));
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            importer.SourceValueUnit);
        Assert.Same(rig, importer.ExactTargetRig);
        Assert.Equal(animation.FrameRate, importer.BodyAnimation.FrameRate);
        Assert.Equal(animation.FrameCount, importer.BodyAnimation.FrameCount);
        ProjectMorphBinding suggestion = Assert.Single(
            Assert.Single(
                viewModel.CurrentProject.Animations)
            .MorphBindings);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            suggestion.SourceValueUnit);
        Assert.False(suggestion.IsReviewed);
        Assert.False(suggestion.IsLocked);
        Assert.Equal(
            "jaw_unmapped",
            Assert.Single(
                viewModel.FacialFpp.UnmappedFacialChannels));

        FacialMorphBindingReviewViewModel row = Assert.Single(
            viewModel.FacialFpp.FacialMappingReviews);
        row.IsReviewed = true;
        row.IsLocked = true;
        Assert.True(
            viewModel.ApplyFacialMappingReviewCommand
                .CanExecute(null));
        viewModel.ApplyFacialMappingReviewCommand.Execute(null);

        ProjectAnimation reviewedAnimation = Assert.Single(
            viewModel.CurrentProject.Animations);
        Guid facialSourceAssetId = Assert.IsType<Guid>(
            reviewedAnimation.FacialSourceAssetId);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            reviewedAnimation.FacialSourceValueUnit);
        ProjectAssetReference facialSourceAsset =
            Assert.Single(
                viewModel.CurrentProject.Assets,
                asset => asset.Id == facialSourceAssetId);
        Assert.Equal(
            ProjectAssetKind.SourceAnimation,
            facialSourceAsset.Kind);
        Assert.EndsWith(
            ".fbx",
            facialSourceAsset.RelativePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, facialSourceAsset.ContentSha256?.Length);
        ProjectMorphBinding reviewed = Assert.Single(
            reviewedAnimation.MorphBindings);
        Assert.True(reviewed.IsReviewed);
        Assert.True(reviewed.IsLocked);
        Assert.NotEqual(
            RecordingFacialFbxImporter.InitialFingerprint,
            reviewedAnimation.MimicMappingFingerprint);
        Assert.Equal(
            "jaw_unmapped",
            Assert.Single(
                viewModel.FacialFpp.UnmappedFacialChannels));

        viewModel.CurrentProject.Validate();
        ProjectSerializer.SaveAtomic(
            viewModel.CurrentProject,
            projectPath);
        DlraProject reopened =
            ProjectSerializer.Load(projectPath);
        ProjectMorphBinding persisted = Assert.Single(
            Assert.Single(reopened.Animations).MorphBindings);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            persisted.SourceValueUnit);
        Assert.True(persisted.IsReviewed);
        Assert.True(persisted.IsLocked);
        ProjectAnimation reopenedAnimation =
            Assert.Single(reopened.Animations);
        Assert.Equal(
            facialSourceAssetId,
            reopenedAnimation.FacialSourceAssetId);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            reopenedAnimation.FacialSourceValueUnit);

        SetPrivateField(
            viewModel,
            "_facialFbxAnimation",
            null);
        SetPrivateField(
            viewModel,
            "_synchronizedAnimation",
            null);
        SetPrivateField(
            viewModel,
            "_pendingFacialFbxSourcePath",
            Path.Combine(
                _temporaryDirectory,
                facialSourceAsset.RelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        SetPrivateField(
            viewModel,
            "_pendingFacialFbxAssetId",
            facialSourceAssetId);
        await InvokePrivateTaskAsync(
            viewModel,
            "LoadPendingFacialFbxSourceAsync",
            CancellationToken.None);

        Assert.Equal(1, importer.DecodeSourceCallCount);
        AnimationClip synchronized = GetPrivateField<AnimationClip>(
            viewModel,
            "_synchronizedAnimation");
        Assert.Equal(
            0.5,
            synchronized.SampleScalars(
                animation.FrameRate.SecondsForFrame(8))["Smile"],
            12);
    }

    [Fact]
    public void ReviewRowCannotLockBeforeExplicitReview()
    {
        var row = new FacialMorphBindingReviewViewModel(
            new ProjectMorphBinding
            {
                SourceChannel = "Smile",
                SourceValueUnit =
                    ProjectMorphSourceValueUnit.Normalized,
                TargetMorph = "smile",
                IsReviewed = false,
                IsLocked = false,
            });

        row.IsLocked = true;

        Assert.False(row.IsLocked);
        row.IsReviewed = true;
        row.IsLocked = true;
        Assert.True(row.BuildBinding().IsLocked);
        row.IsReviewed = false;
        Assert.False(row.IsLocked);
        Assert.False(row.BuildBinding().IsReviewed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(
                _temporaryDirectory,
                recursive: true);
        }
    }

    private static RigDefinition CreateRig()
    {
        const string morphName = "mimic.smile";
        return new RigDefinition(
            "facial-target",
            "Facial target",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash:
                        Dl1NameHash.Compute("root")),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    morphName,
                    Dl1NameHash.Compute(morphName),
                    morphName),
            ]);
    }

    private static ProjectAssetReference CreateRetailAsset() =>
        new()
        {
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath = "retail/272/0",
            ContentSha256 = new string('A', 64),
            RetailIdentity = new ProjectRetailAssetIdentity
            {
                InstallFingerprint = "install",
                ProviderId = "base",
                ProviderPack = "data/common.rpack",
                ResourceType = 272,
                ResourceIndex = 0,
                ResourceName = "facial-target",
                ContentSha256 = new string('A', 64),
            },
        };

    private static void SetPrivateField(
        MainWindowViewModel viewModel,
        string name,
        object? value)
    {
        FieldInfo field =
            typeof(MainWindowViewModel).GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"View-model field '{name}' was not found.");
        field.SetValue(viewModel, value);
    }

    private static T GetPrivateField<T>(
        MainWindowViewModel viewModel,
        string name)
    {
        FieldInfo field =
            typeof(MainWindowViewModel).GetField(
                name,
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"View-model field '{name}' was not found.");
        return Assert.IsType<T>(field.GetValue(viewModel));
    }

    private static async Task InvokePrivateTaskAsync(
        MainWindowViewModel viewModel,
        string name,
        params object?[] arguments)
    {
        MethodInfo method =
            typeof(MainWindowViewModel).GetMethod(
                name,
                BindingFlags.Instance |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"View-model method '{name}' was not found.");
        Task task = Assert.IsAssignableFrom<Task>(
            method.Invoke(viewModel, arguments));
        await task;
    }

    private static void SetImportedAnimationSession(
        MainWindowViewModel viewModel,
        RigDefinition rig,
        AnimationClip clip,
        string sourcePath)
    {
        Type sessionType =
            typeof(MainWindowViewModel).GetNestedType(
                "ImportedAnimationSession",
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Imported animation session type was not found.");
        object session = Activator.CreateInstance(
                sessionType,
                rig,
                clip,
                sourcePath,
                "FBX")
            ?? throw new InvalidOperationException(
                "Imported animation session could not be created.");
        SetPrivateField(
            viewModel,
            "_sourceAnimation",
            session);
    }

    private sealed class FacialProjectFileDialogs(
        string projectPath,
        string facialPath) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(
            string? initialPath) =>
            projectPath;

        public string? ShowOpenFacialFbxDialog(
            string? initialPath) =>
            facialPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;
    }

    private sealed class RecordingFacialFbxImporter(
        RigDefinition expectedRig) :
        IFacialFbxProjectReviewImporter
    {
        public const string InitialFingerprint =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        public ProjectMorphSourceValueUnit? SourceValueUnit
        {
            get;
            private set;
        }

        public ProjectAnimation BodyAnimation
        {
            get;
            private set;
        } = new();

        public RigDefinition? ExactTargetRig
        {
            get;
            private set;
        }

        public int DecodeSourceCallCount { get; private set; }

        public Task<FacialFbxProjectReviewImportResult>
            ImportAsync(
                string sourcePath,
                ProjectMorphSourceValueUnit sourceValueUnit,
                ProjectAnimation bodyAnimation,
                RigDefinition exactTargetRig,
                CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedRig.Id, exactTargetRig.Id);
            SourceValueUnit = sourceValueUnit;
            BodyAnimation = bodyAnimation;
            ExactTargetRig = exactTargetRig;
            MorphChannelDefinition target =
                Assert.Single(exactTargetRig.MorphChannels);
            ProjectAnimation updated = bodyAnimation with
            {
                MimicProfileId =
                    Dl1MimicProfile.BuiltInCommon46Id,
                MimicMappingFingerprint =
                    InitialFingerprint,
                MorphBindings =
                [
                    new ProjectMorphBinding
                    {
                        SourceChannel = "Smile",
                        SourceValueUnit = sourceValueUnit,
                        TargetMorph = target.Name,
                        TargetDescriptorHash =
                            target.DescriptorHash,
                        Confidence = 0.95,
                        Method = "test suggestion",
                        IsReviewed = false,
                        IsLocked = false,
                    },
                ],
            };
            AnimationClip sourceClip = CreateFacialClip(
                bodyAnimation);
            return Task.FromResult(
                new FacialFbxProjectReviewImportResult(
                    updated,
                    sourceClip,
                    SourceChannelCount: 2,
                    SuggestedBindingCount: 1,
                    ImmutableArray.Create("jaw_unmapped")));
        }

        public Task<AnimationClip> DecodeSourceAsync(
            string sourcePath,
            ProjectMorphSourceValueUnit sourceValueUnit,
            ProjectAnimation bodyAnimation,
            CancellationToken cancellationToken = default)
        {
            DecodeSourceCallCount++;
            return Task.FromResult(
                CreateFacialClip(bodyAnimation));
        }

        private static AnimationClip CreateFacialClip(
            ProjectAnimation bodyAnimation) =>
            new(
                "Face",
                bodyAnimation.FrameRate,
                bodyAnimation.FrameCount,
                scalarTracks:
                [
                    new ScalarTrack(
                        "Smile",
                        [
                            new ScalarKeyframe(0, 0),
                            new ScalarKeyframe(
                                bodyAnimation.FrameCount - 1,
                                1),
                        ]),
                ]);
    }
}
