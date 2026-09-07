using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class SecondaryMotionModelStateCacheTests
{
    [Fact]
    public void SameModelFreshDocumentAndClipActivationRetainPendingTuning()
    {
        var cache = new SecondaryMotionModelStateCache();
        var key = Key();
        var packaged = SecondaryMotionTests.Definition();
        var vm = new SecondaryMotionViewModel();
        vm.Load(cache.Select(key, packaged, vm.Definition).Definition);
        vm.Damping = 13;
        SecondaryMotionDefinition edited = vm.Definition;
        var decodedAgain = SecondaryMotionSetupSerializer.Deserialize(SecondaryMotionSetupSerializer.Serialize(packaged));
        Assert.NotSame(packaged, decodedAgain);

        var sameModel = cache.Select(key, decodedAgain, edited);
        Assert.False(sameModel.IdentityChanged);
        Assert.True(sameModel.HasPendingEdits);
        Assert.Same(edited, sameModel.Definition);
        Assert.Equal(13, sameModel.Definition.Groups[0].Preview.Damping);

        // Activation may temporarily clear the target before publishing its fresh decode.
        var cleared = cache.Select(null, new(), edited);
        var restored = cache.Select(key, decodedAgain, cleared.Definition);
        Assert.True(restored.HasPendingEdits);
        Assert.Same(edited, restored.Definition);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("source")]
    [InlineData("package")]
    [InlineData("project")]
    public void DifferentIdentityDoesNotReuseAnotherModelsPendingDefinition(string changedPart)
    {
        var cache = new SecondaryMotionModelStateCache();
        var key = Key();
        var packaged = SecondaryMotionTests.Definition();
        _ = cache.Select(key, packaged, new());
        var edited = packaged with { Groups = [packaged.Groups[0] with { Preview = new() { Damping = 13 } }] };
        SecondaryMotionModelKey other = changedPart switch
        {
            "model" => key with { ModelId = Guid.NewGuid() },
            "source" => key with { SourceHash = new string('c', 64) },
            "package" => key with { PackageHash = new string('d', 64) },
            _ => key with { ProjectId = Guid.NewGuid() },
        };
        var changed = cache.Select(other, packaged, edited);
        Assert.True(changed.IdentityChanged);
        Assert.False(changed.HasPendingEdits);
        Assert.Same(packaged, changed.Definition);
        Assert.Equal(3, changed.Definition.Groups[0].Preview.Damping);
        var originalAgain = cache.Select(key, packaged, changed.Definition);
        Assert.Same(edited, originalAgain.Definition);
        Assert.True(originalAgain.HasPendingEdits);
    }

    private static SecondaryMotionModelKey Key() => new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), new string('b', 64));
}
