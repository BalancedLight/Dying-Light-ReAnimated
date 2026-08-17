using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.Tests;

public sealed class RetailAnimationBrowserTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-RetailAnimationBrowser-{Guid.NewGuid():N}");

    [Fact]
    public void OnlyFingerprintedRetailAnimationsAreListed()
    {
        var browser = new RetailAnimationBrowserViewModel();

        browser.ReplaceAssets(
        [
            CreateItem("fpp_fireball", AssetKind.Animation),
            CreateItem("player_1_fpp", AssetKind.Mesh),
            CreateItem("player_animations", AssetKind.AnimationScript),
            new AssetItemViewModel(
                "loose:anim",
                "loose_clip",
                AssetKind.Animation,
                "dl1-loose-0",
                "loose/loose_clip",
                retailAsset: null),
        ]);

        AssetItemViewModel listed = Assert.Single(browser.VisibleAssets);
        Assert.Equal("fpp_fireball", listed.Name);
        Assert.Equal(1, browser.IndexedAssetCount);
    }

    [Fact]
    public void SearchAndProviderFiltersNarrowTheList()
    {
        var browser = new RetailAnimationBrowserViewModel();
        browser.ReplaceAssets(
        [
            CreateItem("fpp_fireball", AssetKind.Animation),
            CreateItem("fpp_sprint", AssetKind.Animation),
            CreateItem(
                "dlc_climb",
                AssetKind.Animation,
                provider: "dl1-fed-paks"),
        ]);

        browser.SearchText = "sprint";
        Assert.Equal("fpp_sprint", Assert.Single(browser.VisibleAssets).Name);
        Assert.Contains(
            "1 matching animations",
            browser.ResultSummary,
            StringComparison.Ordinal);

        browser.SearchText = string.Empty;
        browser.SelectedProviderFilter = "dl1-fed-paks";
        Assert.Equal("dlc_climb", Assert.Single(browser.VisibleAssets).Name);

        browser.SelectedProviderFilter =
            RetailAnimationBrowserViewModel.AllProviders;
        Assert.Equal(3, browser.VisibleAssets.Count);
    }

    [Fact]
    public void NoMatchesReportsAnEmptyStateRatherThanAnEmptyPane()
    {
        var browser = new RetailAnimationBrowserViewModel();
        browser.ReplaceAssets(
            [CreateItem("fpp_fireball", AssetKind.Animation)]);

        browser.SearchText = "nothing-matches-this";

        Assert.False(browser.HasFilteredAssets);
        Assert.Contains(
            "No base-game animations match",
            browser.EmptyResultMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AnimationBrowserIsIsolatedFromTheSharedMeshBrowser()
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
        AssetItemViewModel animation =
            CreateItem("fpp_fireball", AssetKind.Animation);
        AssetItemViewModel mesh =
            CreateItem("player_1_fpp", AssetKind.Mesh);
        viewModel.AssetBrowser.ReplaceAssets([animation, mesh]);
        viewModel.AnimationBrowser.ReplaceAssets([animation, mesh]);
        viewModel.AnimationBrowser.SelectedAsset = animation;
        viewModel.AnimationBrowser.SearchText = "fire";

        // The two browsers must not steer each other: the Models tab shares
        // the mesh browser, and several animation flows mutate its filters.
        viewModel.AssetBrowser.SearchText = "player";
        viewModel.AssetBrowser.SelectedKindFilter = nameof(AssetKind.Mesh);
        viewModel.AssetBrowser.SelectedAsset = mesh;

        Assert.Equal("fire", viewModel.AnimationBrowser.SearchText);
        Assert.Same(animation, viewModel.AnimationBrowser.SelectedAsset);
        Assert.Same(
            animation,
            Assert.Single(viewModel.AnimationBrowser.VisibleAssets));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AddingWithoutASourceModelOpensThePickerAndKeepsTheSelection()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "add-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "add-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "add-workspace.json")),
            new NoOpProjectFileDialogs(),
            assets);
        AssetItemViewModel animation =
            CreateItem("prime_4leg_sprint", AssetKind.Animation);
        viewModel.AnimationBrowser.ReplaceAssets([animation]);

        Assert.False(
            viewModel.AddSelectedRetailAnimationCommand.CanExecute(null));

        viewModel.AnimationBrowser.SelectedAsset = animation;
        viewModel.AnimationBrowser.SearchText = "sprint";

        Assert.True(
            viewModel.AddSelectedRetailAnimationCommand.CanExecute(null));
        await viewModel.AddSelectedRetailAnimationCommand.ExecuteAsync(null);

        // The prompt is hosted on the animation surface, so the row and the
        // search that led to it have to survive the flow that asks for a
        // source model.
        Assert.True(viewModel.IsExplorerSourceModelPickerActive);
        Assert.Same(animation, viewModel.AnimationBrowser.SelectedAsset);
        Assert.Equal("sprint", viewModel.AnimationBrowser.SearchText);
        Assert.Contains(
            "exact fingerprinted source model",
            viewModel.ExplorerSourceModelPickerPrompt,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class NoOpProjectFileDialogs :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }

    private static AssetItemViewModel CreateItem(
        string name,
        AssetKind kind,
        string provider = "dl1-rpacks")
    {
        short resourceType = kind switch
        {
            AssetKind.Animation => Rp6lResourceTypes.Animation,
            AssetKind.AnimationScript => Rp6lResourceTypes.AnimationScript,
            _ => Rp6lResourceTypes.Mesh,
        };
        RetailAssetId id = RetailAssetId.Create(
            RetailAssetLogicalId.Rpack(resourceType, name),
            "test-install",
            provider,
            sourceIndex: 42,
            precedence: 100,
            sourceFingerprint: new string('a', 64));
        var retail = new RetailAssetRecord(
            id,
            name,
            new RetailAssetSource(
                provider,
                RetailAssetSourceKind.Rpack,
                100,
                TestPaths.Combine("retail", "common_anims_PC.rpack"),
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
        return new AssetItemViewModel(
            retail.Id.StableKey,
            retail.DisplayName,
            kind,
            provider,
            retail.Id.LogicalId.StableKey,
            retail);
    }
}
