using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.Tests;

public sealed class AnimationExplorerViewModelTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-AnimationExplorer-{Guid.NewGuid():N}");

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ExplicitPlayOpensFingerprintSourceModelPicker()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new NoOpProjectFileDialogs(),
            assets);
        RetailAssetRecord retail = CreateAnimation("prime_4leg_sprint");
        var item = new AssetItemViewModel(
            retail.Id.StableKey,
            retail.DisplayName,
            AssetKind.Animation,
            retail.Source.ProviderId,
            retail.Id.LogicalId.StableKey,
            retail);
        viewModel.AssetBrowser.ReplaceAssets([item]);
        viewModel.AssetBrowser.SelectedAsset = item;

        Assert.True(
            viewModel.PlaySelectedExplorerAnimationCommand.CanExecute(null));
        await viewModel.PlaySelectedExplorerAnimationCommand
            .ExecuteAsync(null);

        Assert.True(viewModel.IsExplorerSourceModelPickerActive);
        Assert.Equal(
            nameof(AssetKind.Mesh),
            viewModel.AssetBrowser.SelectedKindFilter);
        Assert.Contains(
            "exact retail source model",
            viewModel.ExplorerSourceModelPickerPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            viewModel.CancelExplorerSourceModelPickerCommand.CanExecute(null));

        viewModel.CancelExplorerSourceModelPickerCommand.Execute(null);

        Assert.False(viewModel.IsExplorerSourceModelPickerActive);
        Assert.False(
            viewModel.CancelExplorerSourceModelPickerCommand.CanExecute(null));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ConflictingExactTimingRequiresAnExplicitChoice()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "timing-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "timing-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "timing-workspace.json")),
            new NoOpProjectFileDialogs(),
            assets);
        RetailAssetRecord retail = CreateAnimation("conflict");
        var item = new AssetItemViewModel(
            retail.Id.StableKey,
            retail.DisplayName,
            AssetKind.Animation,
            retail.Source.ProviderId,
            retail.Id.LogicalId.StableKey,
            retail);
        Dl1RetailAnimationTiming[] choices =
        [
            new(
                new FrameRate(30, 1),
                0,
                29,
                AnimationTimingProvenance.ExactRetailAnimationScript,
                "anims_a"),
            new(
                new FrameRate(60, 1),
                3,
                63,
                AnimationTimingProvenance.ExactRetailAnimationScript,
                "anims_b"),
        ];

        viewModel.BeginExplorerAnimationTimingPicker(item, choices);

        Assert.True(viewModel.IsExplorerAnimationTimingPickerActive);
        Assert.Equal(choices, viewModel.ExplorerAnimationTimingChoices);
        Assert.Same(
            choices[0],
            viewModel.SelectedExplorerAnimationTiming);
        Assert.True(
            viewModel.ConfirmExplorerAnimationTimingCommand.CanExecute(null));
        Assert.Contains(
            "multiple exact AnimationScr",
            viewModel.ExplorerAnimationTimingPrompt,
            StringComparison.Ordinal);

        viewModel.CancelExplorerAnimationTimingCommand.Execute(null);

        Assert.False(viewModel.IsExplorerAnimationTimingPickerActive);
        Assert.Empty(viewModel.ExplorerAnimationTimingChoices);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void RetailProjectEntryIsReusedOnlyForTheSameAnimationAndModelFingerprint()
    {
        RetailAssetRecord retail = CreateAnimation("prime_4leg_sprint");
        Guid animationAssetId = Guid.NewGuid();
        Guid modelAssetId = Guid.NewGuid();
        Guid animationId = Guid.NewGuid();
        ProjectAssetReference animationAsset = CreateProjectRetailAsset(
            animationAssetId,
            retail,
            new string('a', 64));
        ProjectAssetReference modelAsset = CreateProjectRetailAsset(
            modelAssetId,
            retail with
            {
                Id = RetailAssetId.Create(
                    RetailAssetLogicalId.Rpack(
                        Rp6lResourceTypes.Mesh,
                        "zombie_prime"),
                    "test-install",
                    "dl1-rpacks",
                    77,
                    100,
                    new string('b', 64)),
                DisplayName = "zombie_prime",
                Source = retail.Source with
                {
                    EntryPath = "zombie_prime",
                    ResourceIndex = 77,
                },
            },
            new string('b', 64));
        var project = DlraProject.Create("reuse") with
        {
            Assets = [animationAsset, modelAsset],
            Animations =
            [
                new ProjectAnimation
                {
                    Id = animationId,
                    Name = retail.DisplayName,
                    SourceAssetId = animationAssetId,
                    SourceBinding = new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.RetailAnm2,
                        AssetId = animationAssetId,
                        Roles = AnimationSourceRoles.Body,
                        SourceRigSignature = new string('c', 64),
                        RetailSourceModelAssetId = modelAssetId,
                        TimingProvenance =
                            AnimationTimingProvenance.Manual30FpsFallback,
                    },
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                },
            ],
        };

        ProjectAnimation reused = Assert.IsType<ProjectAnimation>(
            MainWindowViewModel.FindReusableRetailAnimation(
                project,
                retail,
                modelAsset));
        Assert.Equal(animationId, reused.Id);

        ProjectAssetReference changedFingerprint = modelAsset with
        {
            ContentSha256 = new string('d', 64),
        };
        Assert.Null(
            MainWindowViewModel.FindReusableRetailAnimation(
                project,
                retail,
                changedFingerprint));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void TargetSwitchPreservesMapOnlyForTheExactRetailFingerprint()
    {
        RetailAssetRecord retail = CreateAnimation("target");
        ProjectAssetReference target = CreateProjectRetailAsset(
            Guid.NewGuid(),
            retail,
            new string('a', 64));
        string signature = new('b', 64);
        var animation = new ProjectAnimation
        {
            Id = Guid.NewGuid(),
            Name = "mapped",
            SourceAssetId = Guid.NewGuid(),
            TargetAssetId = target.Id,
            TargetRigSignature = signature,
            BoneMappings =
            [
                new ProjectBoneMapping
                {
                    SourceBoneName = "source",
                    TargetBoneName = "target",
                },
            ],
        };

        Assert.True(MainWindowViewModel.ShouldPreserveTargetMapping(
            animation,
            target,
            target,
            signature));
        Assert.False(MainWindowViewModel.ShouldPreserveTargetMapping(
            animation,
            target,
            target with { ContentSha256 = new string('c', 64) },
            signature));
        Assert.False(MainWindowViewModel.ShouldPreserveTargetMapping(
            animation,
            target,
            target,
            new string('d', 64)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static RetailAssetRecord CreateAnimation(string name)
    {
        RetailAssetId id = RetailAssetId.Create(
            RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Animation,
                name),
            "test-install",
            "dl1-rpacks",
            sourceIndex: 42,
            precedence: 100,
            sourceFingerprint: new string('a', 64));
        return new RetailAssetRecord(
            id,
            name,
            new RetailAssetSource(
                "dl1-rpacks",
                RetailAssetSourceKind.Rpack,
                100,
                @"C:\retail\common_anims_PC.rpack",
                name,
                42,
                128,
                1024,
                new DateTime(
                    2026,
                    8,
                    1,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc)));
    }

    private static ProjectAssetReference CreateProjectRetailAsset(
        Guid id,
        RetailAssetRecord retail,
        string contentSha256) =>
        new()
        {
            Id = id,
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath =
                $"retail/{retail.Id.ResourceType}/{retail.Source.ResourceIndex}",
            ResourceId = retail.Id.LogicalId.StableKey,
            ContentSha256 = contentSha256,
            RetailIdentity = new ProjectRetailAssetIdentity
            {
                InstallFingerprint = retail.Id.InstallId,
                ProviderId = retail.Id.ProviderId,
                ProviderPack = Path.GetFileName(
                    retail.Source.ContainerPath),
                ResourceType = retail.Id.ResourceType,
                ResourceIndex = retail.Source.ResourceIndex ?? -1,
                ResourceName = retail.DisplayName,
                Precedence = retail.Id.Precedence,
                ContentSha256 = contentSha256,
            },
        };

    private sealed class NoOpProjectFileDialogs :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}
