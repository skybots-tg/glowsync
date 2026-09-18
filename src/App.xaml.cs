using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GlowSync.Platform;

namespace GlowSync;

public partial class App : Application
{
    private const string MutexName = @"Local\GlowSync.SingleInstance";
    private const string ShowEventName = @"Local\GlowSync.ShowSettings";
    private const string ExitEventName = @"Local\GlowSync.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool autostart = e.Args.Any(a => string.Equals(a, Autostart.Argument, StringComparison.OrdinalIgnoreCase));
        bool exit = e.Args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first || exit)
        {
            // Already running: a manual launch just brings up the existing settings window; autostart does nothing;
            // --exit asks the running instance to turn the strip off and quit (used by the install script).
            if ((exit || !autostart) && EventWaitHandle.TryOpenExisting(exit ? ExitEventName : ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        Log.Init();
        Log.Info($"GlowSync {typeof(App).Assembly.GetName().Version} starting (autostart={autostart})");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI exception", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Task exception", args.Exception);
            args.SetObserved();
        };

        _controller = new AppController();
        _controller.Start();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var listener = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    if (WaitHandle.WaitAny(new WaitHandle[] { _showEvent, _exitEvent }) == 1)
                    {
                        Dispatcher.BeginInvoke(() => _controller?.Exit());
                        return;
                    }
                    Dispatcher.BeginInvoke(() => _controller?.ShowSettings());
                }
            }
            catch (ObjectDisposedException)
            {
                // app is exiting
            }
        }) { IsBackground = true, Name = "GlowSync.ShowListener" };
        listener.Start();

        if (!autostart) _controller.ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        Log.Info("GlowSync stopped");
        base.OnExit(e);
    }
}
