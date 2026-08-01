using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class AssetProfileFilterViewModelTests
{
    [Fact]
    public void ConjunctiveFiltersUseOnlyDecodedPositiveEvidence()
    {
        AssetItemViewModel player = CreateMeshItem(
            1,
            "player_1_fpp",
            CreateProfile(
                1,
                "player_1_fpp",
                Dl1MeshGeometryKind.Skinned,
                "rig-player",
                Dl1RigFamily.Player,
                Dl1MeshPerspective.FirstPerson,
                Dl1FacialSupport.DecodedMorphDeltas,
                Dl1RetailSourceScope.BaseGame,
                null,
                ["default", "bloody"]));
        AssetItemViewModel staticDlc = CreateMeshItem(
            2,
            "traffic_sign",
            CreateProfile(
                2,
                "traffic_sign",
                Dl1MeshGeometryKind.Static,
                null,
                Dl1RigFamily.Unknown,
                Dl1MeshPerspective.Unknown,
                Dl1FacialSupport.None,
                Dl1RetailSourceScope.Dlc,
                "dlc10",
                []));
        AssetItemViewModel undecoded = CreateMeshItem(
            3,
            "player_unknown");
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets([player, staticDlc, undecoded]);

        browser.SelectedGeometryFilter = Find(
            browser.GeometryFilters,
            Dl1MeshGeometryKind.Skinned.ToString());
        browser.SelectedRigFamilyFilter = Find(
            browser.RigFamilyFilters,
            Dl1RigFamily.Player.ToString());
        browser.SelectedPerspectiveFilter = Find(
            browser.PerspectiveFilters,
            Dl1MeshPerspective.FirstPerson.ToString());
        browser.SelectedFacialFilter = Find(
            browser.FacialFilters,
            AssetBrowserViewModel.HasValueFilterKey);
        browser.SelectedSourceScopeFilter = Find(
            browser.SourceScopeFilters,
            Dl1RetailSourceScope.BaseGame.ToString());
        browser.SelectedVariantFilter = FindValue(
            browser.VariantFilters,
            "bloody");

        Assert.Same(player, Assert.Single(browser.VisibleAssets));
        Assert.Equal(1, browser.FilteredAssetCount);
        Assert.DoesNotContain(undecoded, browser.VisibleAssets);
    }

    [Fact]
    public void NegativeCapabilityFilterDoesNotTreatUndecodedAsNone()
    {
        AssetItemViewModel noFacial = CreateMeshItem(
            4,
            "prop",
            CreateProfile(
                4,
                "prop",
                Dl1MeshGeometryKind.Static,
                null,
                Dl1RigFamily.Unknown,
                Dl1MeshPerspective.Unknown,
                Dl1FacialSupport.None,
                Dl1RetailSourceScope.BaseGame,
                null,
                []));
        AssetItemViewModel undecoded = CreateMeshItem(
            5,
            "undecoded");
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets([noFacial, undecoded]);

        browser.SelectedFacialFilter = Find(
            browser.FacialFilters,
            AssetBrowserViewModel.NoValueFilterKey);

        Assert.Same(noFacial, Assert.Single(browser.VisibleAssets));
        Assert.DoesNotContain(undecoded, browser.VisibleAssets);
    }

    [Fact]
    public void ExplicitUnknownFilterIncludesUndecodedAndUnknownEvidence()
    {
        AssetItemViewModel classifiedUnknown = CreateMeshItem(
            6,
            "ambiguous",
            CreateProfile(
                6,
                "ambiguous",
                Dl1MeshGeometryKind.Unknown,
                null,
                Dl1RigFamily.Unknown,
                Dl1MeshPerspective.Unknown,
                Dl1FacialSupport.Unknown,
                Dl1RetailSourceScope.Unknown,
                null,
                []));
        AssetItemViewModel undecoded = CreateMeshItem(
            7,
            "undecoded");
        AssetItemViewModel known = CreateMeshItem(
            8,
            "known",
            CreateProfile(
                8,
                "known",
                Dl1MeshGeometryKind.Static,
                null,
                Dl1RigFamily.Unknown,
                Dl1MeshPerspective.Unknown,
                Dl1FacialSupport.None,
                Dl1RetailSourceScope.BaseGame,
                null,
                []));
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets([classifiedUnknown, undecoded, known]);

        browser.SelectedGeometryFilter = Find(
            browser.GeometryFilters,
            AssetBrowserViewModel.UnknownFilterKey);

        Assert.Equal(
            [classifiedUnknown, undecoded],
            browser.VisibleAssets);
        Assert.DoesNotContain(known, browser.VisibleAssets);
    }

    [Fact]
    public void DynamicSignatureDlcAndVariantOptionsRemainSelectable()
    {
        AssetItemViewModel item = CreateMeshItem(
            9,
            "dlc_mesh",
            CreateProfile(
                9,
                "dlc_mesh",
                Dl1MeshGeometryKind.Skinned,
                "0123456789abcdef0123456789abcdef",
                Dl1RigFamily.GenericNpc,
                Dl1MeshPerspective.ThirdPerson,
                Dl1FacialSupport.MorphChannels,
                Dl1RetailSourceScope.Dlc,
                "dlc42",
                ["winter"]));
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets([item]);

        browser.SelectedRigSignatureFilter = FindValue(
            browser.RigSignatureFilters,
            "0123456789abcdef0123456789abcdef");
        browser.SelectedDlcFilter = FindValue(
            browser.DlcFilters,
            "dlc42");
        browser.SelectedVariantFilter = FindValue(
            browser.VariantFilters,
            "winter");

        Assert.Same(item, Assert.Single(browser.VisibleAssets));
        Assert.Contains(
            browser.RigSignatureFilters,
            option => option.Label.EndsWith(
                "...",
                StringComparison.Ordinal));
    }

    [Fact]
    public void BoundedProfileScanUsesGeneralFiltersAndCanBeCanceled()
    {
        AssetBrowserViewModel browser = new();
        AssetItemViewModel failed = CreateMeshItem(
            99,
            "candidate_failed");
        failed.MarkProfileFailed("Unsupported layout");
        browser.ReplaceAssets(
        [
            failed,
            .. Enumerable.Range(0, 140)
                .Select(index => CreateMeshItem(
                    index + 100,
                    $"candidate_{index:D3}")),
            CreateAnimationItem(999, "candidate_idle"),
        ]);
        browser.SearchText = "candidate";
        browser.SelectedGeometryFilter = Find(
            browser.GeometryFilters,
            Dl1MeshGeometryKind.Skinned.ToString());
        Assert.Empty(browser.VisibleAssets);
        Assert.Equal(140, browser.PendingProfileCount);

        AssetProfileScanRequestedEventArgs? request = null;
        bool cancellationRequested = false;
        browser.ProfileScanRequested += (_, args) => request = args;
        browser.ProfileScanCancellationRequested +=
            (_, _) => cancellationRequested = true;

        browser.ClassifyFilteredMeshesCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal(
            AssetBrowserViewModel.ProfileScanBatchSize,
            request.Assets.Count);
        Assert.All(
            request.Assets,
            static item => Assert.Equal(AssetKind.Mesh, item.Kind));
        Assert.DoesNotContain(failed, request.Assets);
        Assert.Equal(
            "candidate_000",
            request.Assets[0].Name);
        Assert.Equal(
            "candidate_127",
            request.Assets[^1].Name);
        Assert.True(browser.IsProfileScanRunning);
        browser.CancelProfileScanCommand.Execute(null);
        Assert.True(cancellationRequested);

        browser.SetProfileScanRunning(false, "Canceled");
        Assert.False(browser.IsProfileScanRunning);
        Assert.Equal("Canceled", browser.ProfileScanStatus);
    }

    [Fact]
    public void ReplacingRowsPreservesSelectionByPhysicalIdentity()
    {
        AssetItemViewModel original = CreateMeshItem(500, "selected");
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets([original]);
        browser.SelectedAsset = original;
        AssetItemViewModel replacement = CreateMeshItem(500, "selected");

        browser.ReplaceAssets([replacement]);

        Assert.Same(replacement, browser.SelectedAsset);
    }

    [Fact]
    public void ProfileRefreshPreservesSelectionWhenRowStillMatches()
    {
        AssetItemViewModel selected = CreateMeshItem(
            501,
            "selected");
        AssetItemViewModel sibling = CreateMeshItem(
            502,
            "sibling");
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets([selected, sibling]);
        browser.SelectedAsset = selected;
        selected.ApplyMeshProfile(
            CreateProfile(
                501,
                "selected",
                Dl1MeshGeometryKind.Skinned,
                "rig-selected",
                Dl1RigFamily.Player,
                Dl1MeshPerspective.ThirdPerson,
                Dl1FacialSupport.MorphChannels,
                Dl1RetailSourceScope.BaseGame,
                null,
                ["default"]));

        browser.NotifyProfileChanged(selected);

        Assert.Same(selected, browser.SelectedAsset);
        Assert.Equal(
            [selected, sibling],
            browser.VisibleAssets);
    }

    private static AssetItemViewModel CreateMeshItem(
        int sourceIndex,
        string name,
        Dl1RetailMeshProfile? profile = null)
    {
        RetailAssetRecord asset = CreateRetailAsset(
            sourceIndex,
            name,
            Rp6lResourceTypes.Mesh);
        return new AssetItemViewModel(
            asset.Id.StableKey,
            name,
            AssetKind.Mesh,
            asset.Source.ProviderId,
            asset.Id.LogicalId.StableKey,
            asset,
            profile);
    }

    private static AssetItemViewModel CreateAnimationItem(
        int sourceIndex,
        string name)
    {
        RetailAssetRecord asset = CreateRetailAsset(
            sourceIndex,
            name,
            Rp6lResourceTypes.Animation);
        return new AssetItemViewModel(
            asset.Id.StableKey,
            name,
            AssetKind.Animation,
            asset.Source.ProviderId,
            asset.Id.LogicalId.StableKey,
            asset);
    }

    private static RetailAssetRecord CreateRetailAsset(
        int sourceIndex,
        string name,
        short resourceType)
    {
        RetailAssetId id = RetailAssetId.Create(
            RetailAssetLogicalId.Rpack(resourceType, name),
            "test-install",
            "dl1-rpack",
            sourceIndex,
            100,
            $"snapshot-{sourceIndex}");
        return new RetailAssetRecord(
            id,
            name,
            new RetailAssetSource(
                "dl1-rpack",
                RetailAssetSourceKind.Rpack,
                100,
                @"C:\retail\data0.pak",
                name,
                sourceIndex,
                128,
                1_024,
                new DateTime(
                    2026,
                    7,
                    30,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc)));
    }

    private static Dl1RetailMeshProfile CreateProfile(
        int sourceIndex,
        string name,
        Dl1MeshGeometryKind geometry,
        string? rigSignature,
        Dl1RigFamily family,
        Dl1MeshPerspective perspective,
        Dl1FacialSupport facial,
        Dl1RetailSourceScope sourceScope,
        string? dlcIdentifier,
        IReadOnlyList<string> variants)
    {
        RetailAssetRecord asset = CreateRetailAsset(
            sourceIndex,
            name,
            Rp6lResourceTypes.Mesh);
        return new Dl1RetailMeshProfile(
            asset.Id,
            geometry,
            rigSignature,
            family,
            family == Dl1RigFamily.Unknown
                ? Dl1ClassificationConfidence.None
                : Dl1ClassificationConfidence.High,
            perspective,
            perspective == Dl1MeshPerspective.Unknown
                ? Dl1ClassificationConfidence.None
                : Dl1ClassificationConfidence.High,
            facial,
            asset.Source.ProviderId,
            Path.GetFileName(asset.Source.ContainerPath),
            sourceScope,
            dlcIdentifier,
            variants,
            []);
    }

    private static AssetFilterOption Find(
        IEnumerable<AssetFilterOption> options,
        string key) =>
        Assert.Single(options, option =>
            string.Equals(
                option.Key,
                key,
                StringComparison.Ordinal));

    private static AssetFilterOption FindValue(
        IEnumerable<AssetFilterOption> options,
        string value) =>
        Find(options, $"value:{value}");
}
