using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RetailReferenceSelectionTests
{
    [Fact]
    public void ExactResourceIdentityRoundTripsAndMalformedRevisionsFailClosed()
    {
        string hash = new('a', 64);
        string selected = Dl1RigTemplateProvider.PinnedProfileName("generic_reference", hash.ToUpperInvariant());
        Assert.Equal("mesh:generic_reference@sha256:" + hash, selected);
        Assert.True(Dl1RigTemplateProvider.TryParsePinnedProfile(selected, out string name, out string revision));
        Assert.Equal("generic_reference", name);
        Assert.Equal(hash, revision);
        Assert.False(Dl1RigTemplateProvider.TryParsePinnedProfile("mesh:generic_reference", out _, out _));
        Assert.False(Dl1RigTemplateProvider.TryParsePinnedProfile("mesh:generic_reference@sha256:" + new string('0', 63), out _, out _));
        Assert.Throws<ArgumentException>(() => Dl1RigTemplateProvider.PinnedProfileName("generic_reference", new string('x', 64)));
    }

    [Fact]
    public async Task SelectedReferenceUsesPinnedIdentityAndCanBeSavedWithTheModel()
    {
        var original = CameraTemplateCreationTests.Template();
        string id = Dl1RigTemplateProvider.PinnedProfileName("generic_reference", original.SourceFingerprint);
        var selected = new Dl1RigTemplate(id, original.SourceResourceName, original.SourceFingerprint, original.Entities);
        var wizard = new RigConformanceWizardViewModel((_, _) =>
            Task.FromResult(Dl1RigTemplateResolution.Failed("player", "No default reference needed")), _ => { });
        wizard.SetModel(CameraTemplateCreationTests.Target());
        wizard.SetRetailReferencePicker(_ => Task.FromResult(new Dl1RigTemplateResolution(
            selected, id, selected.SourceResourceName, selected.SourceFingerprint, "Exact selected reference; family capabilities unverified.")));
        await wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        Assert.True(wizard.HasTemplate);
        Assert.Equal(id, wizard.TemplateProfileName);
        Assert.Contains("Exact selected", wizard.TemplateStatus, StringComparison.Ordinal);
        CustomModelRigConformance settings = Assert.IsType<CustomModelRigConformance>(wizard.CreateSettings());
        Assert.Equal(id, settings.TemplateProfileName);
        Assert.Equal(original.SourceFingerprint, settings.TemplateFingerprint);
    }

    [Fact]
    public async Task ChangedModelCannotAdoptInFlightSelection()
    {
        var release = new TaskCompletionSource<Dl1RigTemplateResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wizard = new RigConformanceWizardViewModel((_, _) =>
            Task.FromResult(Dl1RigTemplateResolution.Failed("player", "No default reference needed")), _ => { });
        wizard.SetModel(CameraTemplateCreationTests.Target());
        wizard.SetRetailReferencePicker(_ => release.Task);
        Task selecting = wizard.UseSelectedRetailMeshCommand.ExecuteAsync(null);
        wizard.SetModel(CameraTemplateCreationTests.Target());
        var template = CameraTemplateCreationTests.Template();
        release.SetResult(new(template, template.ProfileName, template.SourceResourceName, template.SourceFingerprint, "stale selection"));
        await selecting;
        Assert.False(wizard.HasTemplate);
        Assert.DoesNotContain("stale selection", wizard.TemplateStatus, StringComparison.Ordinal);
    }
}
