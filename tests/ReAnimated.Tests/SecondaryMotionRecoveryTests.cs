using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class SecondaryMotionRecoveryTests
{
    [Fact]
    public void PendingSecondaryMotionRoundTripsWithExactModelIdentity()
    {
        var key = new SecondaryMotionModelKey(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), new string('b', 64), new string('c', 64));
        var cache = new SecondaryMotionModelStateCache();
        SecondaryMotionDefinition baseline = new();
        cache.Select(key, baseline, baseline);
        SecondaryMotionDefinition edited = baseline with { PreviewActorScale = 1.75 };

        ImmutableArray<PendingSecondaryMotionEdit> captured = cache.CapturePendingEdits(edited);
        var restored = new SecondaryMotionModelStateCache();
        restored.RestorePendingEdits(captured);
        SecondaryMotionModelSelection selection = restored.Select(key, baseline, baseline);

        Assert.True(selection.HasPendingEdits);
        Assert.Equal(edited.PreviewActorScale, selection.Definition.PreviewActorScale);
        ImmutableArray<PendingSecondaryMotionEdit> recaptured = restored.CapturePendingEdits(selection.Definition);
        Assert.Single(recaptured);
        Assert.Equal(edited.PreviewActorScale, recaptured[0].Definition.PreviewActorScale);
    }

    [Fact]
    public void FailedPendingSecondaryMotionRestoreLeavesExistingCacheIntact()
    {
        var existingKey = new SecondaryMotionModelKey(Guid.NewGuid(), Guid.NewGuid(), new string('c', 64), new string('d', 64), new string('e', 64));
        SecondaryMotionDefinition baseline = new();
        SecondaryMotionDefinition edited = baseline with { PreviewActorScale = 2 };
        var cache = new SecondaryMotionModelStateCache();
        cache.RestorePendingEdits([PendingSecondaryMotionEdit.Create(existingKey, edited)]);
        PendingSecondaryMotionEdit invalid = PendingSecondaryMotionEdit.Create(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('e', 64), new string('f', 64), new string('1', 64)), edited) with
        {
            DefinitionSha256 = new string('0', 64),
        };

        Assert.Throws<InvalidDataException>(() => cache.RestorePendingEdits([invalid]));
        SecondaryMotionModelSelection selection = cache.Select(existingKey, baseline, baseline);

        Assert.True(selection.HasPendingEdits);
        Assert.Equal(2, selection.Definition.PreviewActorScale);
    }

    [Fact]
    public void PendingDefinitionFollowsPackageMetadataChangesWhenSourceAndRigAreStable()
    {
        Guid projectId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        string sourceHash = new('1', 64);
        string rigSignature = new('2', 64);
        var original = new SecondaryMotionModelKey(projectId, modelId, sourceHash, new string('3', 64), rigSignature);
        var updated = original with { PackageHash = new string('4', 64) };
        SecondaryMotionDefinition baseline = new();
        SecondaryMotionDefinition edited = baseline with { PreviewActorScale = 1.5 };
        var cache = new SecondaryMotionModelStateCache();
        cache.Select(original, baseline, baseline);
        cache.CapturePendingEdits(edited);

        SecondaryMotionModelSelection selection = cache.Select(updated, baseline, edited);

        Assert.True(selection.IdentityChanged);
        Assert.True(selection.HasPendingEdits);
        Assert.Equal(edited.PreviewActorScale, selection.Definition.PreviewActorScale);
    }

    [Fact]
    public void PendingDefinitionDoesNotFollowChangedRig()
    {
        Guid projectId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        var original = new SecondaryMotionModelKey(projectId, modelId, new string('1', 64), new string('2', 64), new string('3', 64));
        var updated = original with { PackageHash = new string('4', 64), RigSignature = new string('5', 64) };
        SecondaryMotionDefinition baseline = new();
        SecondaryMotionDefinition edited = baseline with { PreviewActorScale = 2 };
        var cache = new SecondaryMotionModelStateCache();
        cache.Select(original, baseline, baseline);
        cache.CapturePendingEdits(edited);

        SecondaryMotionModelSelection selection = cache.Select(updated, baseline, edited);

        Assert.False(selection.HasPendingEdits);
        Assert.Equal(baseline.PreviewActorScale, selection.Definition.PreviewActorScale);
        Assert.Single(cache.CapturePendingEdits(baseline));
    }

    [Fact]
    public void PendingDefinitionDoesNotOverwriteAnIndependentlyChangedSecondaryBaseline()
    {
        var original = new SecondaryMotionModelKey(
            Guid.NewGuid(), Guid.NewGuid(), new string('1', 64), new string('2', 64), new string('3', 64));
        var updated = original with { PackageHash = new string('4', 64) };
        SecondaryMotionDefinition baseline = new();
        SecondaryMotionDefinition edited = baseline with { PreviewActorScale = 1.5 };
        var cache = new SecondaryMotionModelStateCache();
        cache.Select(original, baseline, baseline);
        cache.CapturePendingEdits(edited);
        SecondaryMotionDefinition independentEdit = baseline with { PreviewActorScale = 1.25 };

        SecondaryMotionModelSelection selection = cache.Select(updated, independentEdit, edited);

        Assert.False(selection.HasPendingEdits);
        Assert.Equal(1.25, selection.Definition.PreviewActorScale);
        PendingSecondaryMotionEdit pending = Assert.Single(cache.CapturePendingEdits(independentEdit));
        Assert.Equal(1.5, pending.Definition.PreviewActorScale);
        Assert.Equal(PendingSecondaryMotionEdit.ComputeDefinitionSha256(baseline), pending.BaselineDefinitionSha256);
    }

    [Fact]
    public void WorkspaceSnapshotRoundTripsPendingSecondaryMotionDefinitions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "reanimated-secondary-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var key = new SecondaryMotionModelKey(Guid.NewGuid(), Guid.NewGuid(), new string('1', 64), new string('2', 64), new string('3', 64));
            PendingSecondaryMotionEdit edit = PendingSecondaryMotionEdit.Create(
                key, new SecondaryMotionDefinition { PreviewActorScale = 0.75 });
            var snapshot = new WorkspaceSnapshot(
                WorkspaceSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, null,
                string.Empty, null, null, 0, false, 0, 0, "Animate")
            {
                PendingSecondaryMotionEdits = ImmutableArray.Create(edit),
            };
            var store = new JsonWorkspaceStateStore(Path.Combine(directory, "workspace.json"));

            store.Save(snapshot);

            WorkspaceSnapshot restored = Assert.IsType<WorkspaceSnapshot>(store.Load());
            Assert.Single(restored.PendingSecondaryMotionEdits);
            Assert.Equal(key.ModelId, restored.PendingSecondaryMotionEdits[0].ModelId);
            Assert.Equal(0.75, restored.PendingSecondaryMotionEdits[0].Definition.PreviewActorScale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SnapshotRejectsPendingDefinitionHashMismatch()
    {
        var key = new SecondaryMotionModelKey(Guid.NewGuid(), Guid.NewGuid(), new string('3', 64), new string('4', 64), new string('5', 64));
        PendingSecondaryMotionEdit edit = PendingSecondaryMotionEdit.Create(key, new()) with
        {
            DefinitionSha256 = new string('0', 64),
        };

        Assert.Throws<InvalidDataException>(() =>
            PendingSecondaryMotionEdit.ValidateSnapshotEdits([edit]));
    }
}
