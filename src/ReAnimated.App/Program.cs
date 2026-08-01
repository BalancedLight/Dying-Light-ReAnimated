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

        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
