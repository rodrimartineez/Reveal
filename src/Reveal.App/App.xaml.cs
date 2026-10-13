using System.Threading;
using System.Windows;
using Reveal.App.Services;

namespace Reveal.App;

public partial class App : Application
{
    private readonly string[] _args;
    private AppHost? _host;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;
    private volatile bool _exiting;

    public App() : this(Array.Empty<string>())
    {
    }

    public App(string[] args)
    {
        _args = args;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var background = _args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        _host = new AppHost(this);
        _host.Initialize(background);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showListener = new Thread(() =>
        {
            while (!_exiting)
            {
                if (_showEvent.WaitOne(500) && !_exiting) Dispatcher.BeginInvoke(() => _host?.ShowMain());
            }
        })
        { IsBackground = true, Name = "Reveal.ShowListener" };
        _showListener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _host?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
