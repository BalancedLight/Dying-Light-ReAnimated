using System.Runtime.ExceptionServices;
using System.Windows;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.App.Views;

namespace ReAnimated.Tests;

/// <summary>
/// Proves the conformance wizard's XAML loads, resolves the application's
/// shared brushes, and lays out at every stage. A markup or resource mistake in
/// a tab that is only reached by hand would otherwise surface as a crash in
/// front of the user.
/// </summary>
/// <remarks>
/// WPF allows one <see cref="Application"/> per AppDomain and gives its brushes
/// thread affinity, so this is deliberately a single test on a single STA
/// thread rather than one test per stage.
/// </remarks>
public sealed class RigConformanceViewTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void EveryWizardStageLoadsItsMarkupAndLaysOut()
    {
        RunOnStaThread(() =>
        {
            using var application = new ReAnimated.App.App();
            application.InitializeComponent();

            var wizard = new RigConformanceWizardViewModel(
                (profile, _) => Task.FromResult(
                    Dl1RigTemplateResolution.Failed(profile, "No installation indexed.")),
                static _ => { });
            var view = new RigConformanceView { DataContext = wizard };

            foreach (RigConformanceStage stage in Enum.GetValues<RigConformanceStage>())
            {
                wizard.Stage = stage;
                view.Measure(new Size(360, 900));
                view.Arrange(new Rect(0, 0, 360, 900));
                view.UpdateLayout();
                Assert.Equal(stage, wizard.Stage);
            }

            Assert.NotNull(view.Content);

            // The guided sequence and the scale policy list are populated from
            // the view model's own state, so they must be bindable before any
            // template resolves.
            Assert.NotEmpty(wizard.Landmarks);
            Assert.NotNull(wizard.SelectedScaleModeChoice);
            Assert.NotEmpty(wizard.ScaleModeChoices);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                captured = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        captured?.Throw();
    }
}
