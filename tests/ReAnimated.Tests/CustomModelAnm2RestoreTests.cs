using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class CustomModelAnm2RestoreTests : IDisposable
{
    private readonly string _directory =
        RpackTestData.CreateTemporaryDirectory();

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task OpeningLocalAnm2OnItsCustomModelRestoresPlayableMeshesWithoutCatalog()
    {
        string path = CreateProject();
        byte[] original = File.ReadAllBytes(path);
        await using var assets = CreateAssets();
        var dialogs = new NoDialogs();
        await using var viewModel = CreateViewModel(dialogs, assets);

        await viewModel.OpenWorkspaceAsync(path);

        Assert.Empty(dialogs.Failures);
        Assert.Null(assets.Catalog);
        Assert.Equal(path, viewModel.ProjectPath);
        Assert.False(viewModel.IsTargetPlaybackBlocked);
        Assert.True(viewModel.Timeline.IsPlaybackEnabled);
        Assert.NotEmpty(viewModel.Timeline.Tracks);
        Assert.NotEmpty(viewModel.SourceViewport.SceneSource.CaptureFrame().Meshes);
        Assert.NotEmpty(viewModel.TargetViewport.SceneSource.CaptureFrame().Meshes);
        var initial = viewModel.TargetViewport.SceneSource.CaptureFrame()
            .Skeleton!.Bones.ToArray();

        viewModel.Timeline.CurrentFrame = 1;

        var advanced = viewModel.TargetViewport.SceneSource.CaptureFrame()
            .Skeleton!.Bones.ToArray();
        Assert.False(initial.SequenceEqual(advanced));
        Assert.False(viewModel.IsBusy);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SourcePaneUsesThePreparedMeshBoneDomainDuringPlayback()
    {
        string path = CreateProject(multipleRoots: true);
        await using var assets = CreateAssets();
        var dialogs = new NoDialogs();
        await using var viewModel = CreateViewModel(dialogs, assets);
        await viewModel.OpenWorkspaceAsync(path);
        Assert.Empty(dialogs.Failures);
        var imported = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(Path.Combine(_directory, "model.dlrmodel")));
        var raw = Anm2Reader.Read(File.ReadAllBytes(Path.Combine(_directory, "clip.anm2")), "control");
        var clip = Anm2TrackPartitioner.Partition(raw, imported.Rig!, new FrameRate(30, 1)).BodyClip;
        viewModel.Timeline.IsLooping = false;
        bool rawBoneDomainWouldDeform = false;
        foreach (int frame in new[] { 0, 1, 2 })
        {
            viewModel.Timeline.CurrentFrame = frame;
            RenderFrameSnapshot actual = viewModel.SourceViewport.SceneSource.CaptureFrame();
            CustomModelPreviewPayload reference = CustomModelPreviewAdapter.Create(
                imported, clip, frame, mode: CustomModelPreviewMode.SourceFbx);
            Assert.Equal(reference.Meshes.Count, actual.Meshes.Count);
            for (int mesh = 0; mesh < actual.Meshes.Count; mesh++)
            {
                CpuDeformedVertex[] expectedVertices = CpuMeshDeformationEvaluator.Evaluate(
                    reference.Meshes[mesh], reference.Skeleton, []);
                CpuDeformedVertex[] actualVertices = CpuMeshDeformationEvaluator.Evaluate(
                    actual.Meshes[mesh], actual.Skeleton, []);
                CpuDeformedVertex[] wrongDomain = CpuMeshDeformationEvaluator.Evaluate(actual.Meshes[mesh],
                    CorePreviewAdapter.ToRenderSkeleton(clip.SamplePose(imported.Rig!, frame / 30.0)), []);
                Assert.Equal(expectedVertices.Length, actualVertices.Length);
                for (int vertex = 0; vertex < actualVertices.Length; vertex++)
                {
                    Assert.True(System.Numerics.Vector3.Distance(expectedVertices[vertex].Position,
                        actualVertices[vertex].Position) <= 1e-5f,
                        $"Source skin differs at frame {frame}, mesh {mesh}, vertex {vertex}: expected {expectedVertices[vertex].Position}, actual {actualVertices[vertex].Position}.");
                    Assert.InRange(System.Numerics.Vector3.Distance(expectedVertices[vertex].Normal,
                        actualVertices[vertex].Normal), 0, 1e-5f);
                    rawBoneDomainWouldDeform |= System.Numerics.Vector3.Distance(expectedVertices[vertex].Position,
                        wrongDomain[vertex].Position) > 1e-4f;
                }
            }
        }
        Assert.True(rawBoneDomainWouldDeform, "The fixture must expose the invalid raw-pose/prepared-mesh pairing.");
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task CorruptLocalPayloadStillFailsClosed()
    {
        string path = CreateProject();
        await File.AppendAllTextAsync(
            Path.Combine(_directory, "clip.anm2"),
            "changed");
        await using var assets = CreateAssets();
        var dialogs = new NoDialogs();
        await using var viewModel = CreateViewModel(dialogs, assets);
        Guid previousProjectId = viewModel.CurrentProject.ProjectId;
        bool previousPlaybackEnabled = viewModel.Timeline.IsPlaybackEnabled;

        await viewModel.OpenWorkspaceAsync(path);

        Assert.Contains(dialogs.Failures, detail =>
            detail.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(path, viewModel.ProjectPath);
        Assert.Equal(previousProjectId, viewModel.CurrentProject.ProjectId);
        Assert.Equal(previousPlaybackEnabled, viewModel.Timeline.IsPlaybackEnabled);
        Assert.Empty(viewModel.TargetViewport.SceneSource.CaptureFrame().Meshes);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void LocalCustomSourceDoesNotRemoveRetailTargetRequirement()
    {
        DlraProject project = ProjectSerializer.Load(CreateProject());
        ProjectAnimation animation = Assert.Single(project.Animations);
        Guid unavailableTarget = Guid.NewGuid();
        var target = new ProjectAssetReference
        {
            Id = unavailableTarget,
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath = "retail/unavailable-model",
            ResourceId = "unavailable-model",
        };

        Assert.True(MainWindowViewModel.CanRestoreAnimationFromLoadedCatalog(
            animation, project.Assets, []));
        Assert.False(MainWindowViewModel.CanRestoreAnimationFromLoadedCatalog(
            animation with { TargetAssetId = unavailableTarget },
            project.Assets.Add(target), []));
        Assert.False(MainWindowViewModel.CanRestoreAnimationFromLoadedCatalog(
            animation,
            project.Assets.Where(asset =>
                asset.Kind != ProjectAssetKind.CustomModelSource).ToArray(),
            []));
    }

    public void Dispose() =>
        RpackTestData.DeleteTemporaryDirectory(_directory);

    private string CreateProject(bool multipleRoots = false)
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            multipleRoots ? BlenderFbxStrictValidationTests.CreateMultiRootModelFixture()
                : BlenderFbxStrictValidationTests.CreateValidModelFixture(),
            "generic-model.fbx");
        RigDefinition rig = Assert.IsType<RigDefinition>(imported.Rig);
        string packagePath = Path.Combine(_directory, "model.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(imported.Package, packagePath);
        Guid modelId = imported.Package.Document.ModelId;
        var modelAsset = new ProjectAssetReference
        {
            Id = modelId,
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "model.dlrmodel",
            ResourceId = $"custom-model:{modelId:N}:generic-model",
            ContentSha256 = Hash(File.ReadAllBytes(packagePath)),
        };
        byte[] bytes = CreateMotion(rig);
        File.WriteAllBytes(Path.Combine(_directory, "clip.anm2"), bytes);
        var clipAsset = new ProjectAssetReference
        {
            Id = Guid.NewGuid(),
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "clip.anm2",
            ContentSha256 = Hash(bytes),
        };
        var rate = new FrameRate(30, 1);
        Anm2PartitionedImportResult partitioned = Anm2TrackPartitioner.Partition(
            Anm2Reader.Read(bytes, "generated-motion"), rig, rate);
        Assert.False(partitioned.Partition.RequiresReview);
        string signature = RigSignature.Compute(rig);
        string skeletonSignature = AnimationSkeletonSignature.Compute(rig);
        var source = new ProjectAnimationSource
        {
            Id = Guid.NewGuid(),
            Name = "Generated motion",
            SourceAssetId = clipAsset.Id,
            FrameRate = rate,
            FrameCount = 3,
            SourceAnimationSkeletonSignature = skeletonSignature,
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalAnm2,
                AssetId = clipAsset.Id,
                Roles = AnimationSourceRoles.Body,
                SourceRigSignature = signature,
                RetailSourceModelAssetId = modelAsset.Id,
                TimingProvenance = AnimationTimingProvenance.UserSpecified,
                Partition = partitioned.Partition,
            },
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = source.Id,
            Name = "Generated motion on owning model",
            TargetModelId = modelId,
            TargetRigId = rig.Id,
            TargetRigSignature = signature,
            TargetAnimationSkeletonSignature = skeletonSignature,
            BindingMode = ProjectAnimationBindingMode.ExactDirect,
            RootBoneName = multipleRoots ? "Root" : null,
            IncludeInPackage = false,
        };
        DlraProject project = DlraProject.Create("Local model motion") with
        {
            Assets = [modelAsset, clipAsset],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = modelAsset.Id,
                    Name = "Generic model",
                    RigSignature = signature,
                    AuthoringRigContractSignature =
                        imported.Package.Document.RigSignature,
                    AnimationSkeletonSignature = skeletonSignature,
                    MorphSignature = imported.Package.Document.MorphSignature,
                },
            ],
            AnimationSources = [source],
            AnimationVariants = [variant],
            ModelsWorkspace = new ProjectModelsWorkspaceState
            {
                PackageAssetId = modelAsset.Id,
                PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
            },
            Workflow = new ProjectWorkflowState
            {
                ActiveTab = ProjectWorkflowTab.Playback,
                SelectedModelId = modelId,
                SelectedAnimationSourceId = source.Id,
                SelectedAnimationVariantId = variant.Id,
            },
        };
        string path = Path.Combine(_directory, "workspace.dlraproj");
        ProjectSerializer.SaveAtomic(project, path);
        return path;
    }

    private static byte[] CreateMotion(RigDefinition rig)
    {
        ImmutableArray<uint> descriptors = rig.Bones
            .Select(bone => bone.DescriptorHash!.Value).ToImmutableArray();
        ImmutableArray<Anm2Frame> frames = Enumerable.Range(0, 3)
            .Select(frame => new Anm2Frame(rig.Bones.Select(_ =>
                new Anm2TrackFrame(0, 0, 0, frame * 0.1f, 0, 0, 1, 1, 1))
                .ToImmutableArray())).ToImmutableArray();
        return Anm2PayloadWriter.Build(
            new Anm2Header(Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion, 3,
                checked((ushort)descriptors.Length), 0, 0, 0, 1, 0, 0),
            descriptors, frames,
            Enumerable.Repeat(Anm2PackedComponents.TranslationX,
                descriptors.Length).ToImmutableArray());
    }

    private Dl1AssetWorkspace CreateAssets() => new(
        Path.Combine(_directory, "assets.sqlite3"),
        Path.Combine(_directory, "asset-cache"));

    private MainWindowViewModel CreateViewModel(
        NoDialogs dialogs, Dl1AssetWorkspace assets) => new(
        new JsonWorkspaceStateStore(Path.Combine(_directory, "state.json")),
        dialogs, assets);

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public List<string> Failures { get; } = [];
        public string? ShowOpenProjectDialog(string? initialPath) =>
            throw new InvalidOperationException("Unexpected project picker.");
        public string? ShowSaveProjectDialog(string suggestedName,
            string? currentPath) =>
            throw new InvalidOperationException("Unexpected save picker.");
        public void ShowOperationFailure(string title, string summary,
            string details) => Failures.Add(details);
    }
}
