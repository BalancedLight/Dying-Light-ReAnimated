using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ReAnimated.App.Infrastructure;
using ReAnimated.Cli;

namespace ReAnimated.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (WpfStartupSmoke.IsRequested(args))
        {
            WpfStartupSmoke? smoke = null;
            try
            {
                smoke = WpfStartupSmoke.Create(args);
                var smokeApplication = new App(smoke);
                smokeApplication.InitializeComponent();
                int exitCode = smokeApplication.Run();
                return exitCode == 0 && smoke.IsComplete
                    ? 0
                    : 1;
            }
            catch (Exception exception)
            {
                smoke?.TryWriteStartupFailure(
                    exception,
                    "Program.Main");
                return 1;
            }
        }

        if (PackageSelfTest.IsRequested(args))
        {
            try
            {
                PackageSelfTest.RunAsync(
                        args,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return 0;
            }
            catch
            {
                // The package script treats a nonzero process exit as a
                // failed candidate. Keep this validation path independent
                // from normal app paths, logging, recovery, and WPF.
                return 1;
            }
        }

        if (CliApplication.IsInvocation(args))
        {
            return CliProcess.RunAsync(
                    args,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        DesktopStartupOptions startupOptions;
        try
        {
            startupOptions = DesktopStartupOptions.Parse(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            MessageBox.Show(
                $"{exception.Message}\n\n{DesktopStartupOptions.Usage}",
                "Dying Light ReAnimated",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return 2;
        }

        if (startupOptions.SoftwareUi)
        {
            // A process-local WPF diagnostic override. Native D3D viewports
            // keep their existing renderer and no machine settings are saved.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        var application = new App(startupOptions: startupOptions);
        application.InitializeComponent();
        return application.Run();
    }
}
