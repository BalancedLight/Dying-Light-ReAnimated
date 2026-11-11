using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed partial class RigChannelPolicyWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticPoliciesUseWarningFallbackWhenExactSourceCannotBeRead(bool denied)
    {
        var fixture = StockPreviewFixture();
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(
            new Dl1RigTemplateResolution(fixture.Template, fixture.Template.ProfileName,
                fixture.Template.SourceResourceName, fixture.Template.SourceFingerprint, "test reference")), static _ => { });
        wizard.SetModel(fixture.Model);
        await wizard.ResolveTemplateCommand.ExecuteAsync(null);
        wizard.SetStockPolicySourcePicker((_, _) => Task.FromException<Dl1MeshPreviewPayload?>(
            denied ? new UnauthorizedAccessException("stock source blocked") : new NotSupportedException("stock source blocked")));
        RiggingSession? applied = null;
        wizard.RigPoliciesApplyRequested += (_, args) => applied = args.Session;
        await wizard.ApplyAutomaticChannelPoliciesAsync(CancellationToken.None);
        Assert.NotNull(applied);
        Assert.Contains("stock source blocked", wizard.ChannelPolicyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticPoliciesUseFallbackWithAWarningWhenReferenceLoadingFails()
    {
        var fixture = StockPreviewFixture();
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromException<Dl1RigTemplateResolution>(
            new IOException("reference drive unavailable")), static _ => { });
        wizard.SetModel(fixture.Model);
        wizard.SetStockPolicySourcePicker((_, _) => Task.FromResult<Dl1MeshPreviewPayload?>(fixture.Payload));
        RiggingSession? applied = null;
        wizard.RigPoliciesApplyRequested += (_, args) => applied = args.Session;
        await wizard.ApplyAutomaticChannelPoliciesAsync(CancellationToken.None);
        Assert.NotNull(applied);
        Assert.All(applied.Recipe.ComponentPolicies, policy => Assert.Equal(RigAnimationLod.Off, policy.AnimationLod));
        Assert.Contains("reference drive unavailable", wizard.ChannelPolicyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticPoliciesDiscardAPickerResultForAChangedReference()
    {
        var fixture = StockPreviewFixture();
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(
            new Dl1RigTemplateResolution(fixture.Template, fixture.Template.ProfileName,
                fixture.Template.SourceResourceName, fixture.Template.SourceFingerprint, "test reference")), static _ => { });
        wizard.SetModel(fixture.Model);
        await wizard.ResolveTemplateCommand.ExecuteAsync(null);
        var pending = new TaskCompletionSource<Dl1MeshPreviewPayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wizard.SetStockPolicySourcePicker((_, _) => pending.Task);
        RiggingSession? applied = null;
        wizard.RigPoliciesApplyRequested += (_, args) => applied = args.Session;
        var command = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)wizard.GetType()
            .GetProperty("ApplyAutomaticChannelPoliciesCommand")!.GetValue(wizard)!;
        Task operation = command.ExecuteAsync(null);
        wizard.TemplateProfileName = "another-reference";
        pending.SetResult(fixture.Payload);
        await operation;
        Assert.Null(applied);
    }
}
