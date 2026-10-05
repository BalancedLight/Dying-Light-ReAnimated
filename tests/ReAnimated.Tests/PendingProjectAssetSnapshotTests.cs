using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class PendingProjectAssetSnapshotTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task SnapshotIncludesCurrentReceiptsAndDropsUnreferencedReceipts()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var store = new JsonWorkspaceStateStore(
                Path.Combine(root, "recovery.json"));
            await using var viewModel = new MainWindowViewModel(store);

            Guid currentAssetId = Guid.NewGuid();
            Guid staleAssetId = Guid.NewGuid();
            const string relativePath = "Sources/shared.dlrmodel";
            ProjectAssetReference currentAsset = new()
            {
                Id = currentAssetId,
                Kind = ProjectAssetKind.CustomModelSource,
                RelativePath = relativePath,
                ContentSha256 = new string('a', 64),
            };
            viewModel.RestoreSnapshot(new WorkspaceSnapshot(
                WorkspaceSnapshot.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                null,
                string.Empty,
                null,
                null,
                0,
                true,
                75.0f,
                0.01f,
                "Models",
                DlraProject.Create("Synthetic") with
                {
                    Assets = [currentAsset],
                }));

            PendingProjectAssetReceipt currentReceipt = CreateReceipt(
                currentAssetId,
                relativePath,
                currentAsset.ContentSha256!);
            PendingProjectAssetReceipt staleReceipt = CreateReceipt(
                staleAssetId,
                relativePath,
                new string('b', 64));
            FieldInfo field = typeof(MainWindowViewModel).GetField(
                "_pendingProjectAssets",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = Assert.IsType<Dictionary<Guid,
                PendingProjectAssetReceipt>>(field.GetValue(viewModel));
            pending[currentReceipt.AssetId] = currentReceipt;
            pending[staleReceipt.AssetId] = staleReceipt;

            WorkspaceSnapshot snapshot = viewModel.CreateSnapshot();

            PendingProjectAssetReceipt persisted = Assert.Single(
                snapshot.PendingAssets);
            Assert.Equal(currentReceipt, persisted);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void ValidatorRejectsDistinctReferencedReceiptsWithTheSamePath()
    {
        PendingProjectAssetReceipt first = CreateReceipt(
            Guid.NewGuid(),
            "Sources/shared.dlrmodel",
            new string('c', 64));
        PendingProjectAssetReceipt second = CreateReceipt(
            Guid.NewGuid(),
            first.RelativePath,
            new string('d', 64));

        Assert.Throws<InvalidDataException>(() =>
            PendingProjectAssetStore.ValidateSnapshotReceipts(
                [first, second]));
    }

    private static PendingProjectAssetReceipt CreateReceipt(
        Guid assetId,
        string relativePath,
        string contentSha256) => new()
        {
            AssetId = assetId,
            RelativePath = relativePath,
            ContentSha256 = contentSha256,
            Length = 1,
            StagedFileName = $"{assetId:N}-staged.dlrstage",
        };
}
