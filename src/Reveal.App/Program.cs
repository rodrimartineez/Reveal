using System.Threading;

namespace Reveal.App;

public static class Program
{
    public const string ShowEventName = @"Local\Reveal.ShowMainWindow";
    private const string MutexName = @"Local\Reveal.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask the running instance to bring up its window and leave.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch
            {
                // the first instance is still starting; nothing to do
            }

            return 0;
        }

        var app = new App(args);
        app.InitializeComponent();
        return app.Run();
    }
}
