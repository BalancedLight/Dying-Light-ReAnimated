using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ReAnimated.Tests;

/// <summary>
/// Gives WPF tests the same process-lifetime STA/dispatcher model as the app.
/// The background dispatcher and Application intentionally remain alive for
/// the testhost lifetime; AvalonDock routes focus work through Application.Current.Dispatcher.
/// </summary>
internal static class WpfTestDispatcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim InvocationGate = new(1, 1);
    private static readonly Lazy<Dispatcher> Shared = new(StartDispatcher, LazyThreadSafetyMode.ExecutionAndPublication);
    private static Thread? _dispatcherThread;
    private static Application? _application;

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher dispatcher = Shared.Value;
        if (dispatcher.CheckAccess())
        {
            EnsureApplication(dispatcher);
            action();
            return;
        }

        if (!InvocationGate.Wait(Timeout))
            throw new TimeoutException("Timed out waiting for the shared WPF test dispatcher to become available.");
        try
        {
            var completed = new TaskCompletionSource<ExceptionDispatchInfo?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherOperation operation = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                ExceptionDispatchInfo? captured = null;
                try
                {
                    EnsureApplication(dispatcher);
                    action();
                }
                catch (Exception exception)
                {
                    captured = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    completed.TrySetResult(captured);
                }
            }));

            if (!completed.Task.Wait(Timeout))
            {
                if (operation.Status == DispatcherOperationStatus.Pending)
                    operation.Abort();
                throw new TimeoutException("Timed out waiting for a WPF test action on the shared STA dispatcher.");
            }
            completed.Task.GetAwaiter().GetResult()?.Throw();
        }
        finally
        {
            InvocationGate.Release();
        }
    }

    public static T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        T result = default!;
        Run(() => { result = action(); });
        return result;
    }

    private static Dispatcher StartDispatcher()
    {
        using var initialized = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            }
            catch (Exception exception)
            {
                captured = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                initialized.Set();
            }

            if (dispatcher is not null)
                Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "ReAnimated shared WPF test dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!initialized.Wait(Timeout))
            throw new TimeoutException("The shared WPF test STA did not initialize.");
        captured?.Throw();
        _dispatcherThread = thread;
        return dispatcher ?? throw new InvalidOperationException("The shared WPF dispatcher did not initialize.");
    }

    private static void EnsureApplication(Dispatcher dispatcher)
    {
        if (_application is not null)
        {
            if (!ReferenceEquals(_application.Dispatcher, dispatcher))
                throw new InvalidOperationException("The WPF test Application belongs to another dispatcher.");
            _application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            return;
        }

        if (Application.Current is { } current)
        {
            if (!ReferenceEquals(current.Dispatcher, dispatcher))
                throw new InvalidOperationException("Application.Current belongs to a different or stopped WPF test dispatcher.");
            _application = current;
            current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            return;
        }

        var application = new ReAnimated.App.App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _application = application;
    }
}