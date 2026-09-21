using System.Threading;
using System.Windows.Forms;

namespace DS4WindowsMonitor;

internal static class Program
{
    private const string MutexName = @"Local\DS4WindowsMonitor.Singleton.v1";
    private const string StopEventName = @"Local\DS4WindowsMonitor.Stop.v1";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--stop", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using EventWaitHandle stop = EventWaitHandle.OpenExisting(StopEventName);
                stop.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Monitor is not running.
            }

            return;
        }

        using Mutex mutex = new(false, MutexName, out bool createdNew);
        if (!createdNew)
            return;

        using EventWaitHandle stopEvent = new(false, EventResetMode.ManualReset, StopEventName);

        ApplicationConfiguration.Initialize();

        using MonitorContext context = new(stopEvent);
        Application.Run(context);
    }
}
