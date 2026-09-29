using FlowMonitor.Host;
using FlowMonitor.Interop;

namespace FlowMonitor;

internal static class Program
{
    const string MutexName = "FlowMonitor.SingleInstance.v1";

    /// <summary>
    /// Waits until no other instance owns the widgets. True when the mutex is free
    /// (or was abandoned by a dead owner, which also transfers ownership to us).
    /// </summary>
    static bool WaitForPreviousInstance(TimeSpan timeout)
    {
        DesktopHost.SignalRunningInstanceToExit();
        try
        {
            using var m = new Mutex(false, MutexName);
            try
            {
                if (!m.WaitOne(timeout)) return false;
            }
            catch (AbandonedMutexException)
            {
                // Dead owner: we hold the mutex now.
            }
            m.ReleaseMutex();
            return true;
        }
        catch
        {
            return true;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // The self test deliberately runs before the single-instance mutex and before the
        // DPI call: it owns no desktop state, so it must work even while widgets are live.
        if (args.Contains("--selftest"))
        {
            int i = Array.IndexOf(args, "--capture");
            string dir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
            return SelfTest.Run(dir);
        }

        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // --kill must work even when the mutex is held by the instance we want to stop, so it
        // is handled before the single-instance check and never takes ownership of anything.
        if (args.Contains("--kill"))
        {
            bool signalled = DesktopHost.SignalRunningInstanceToExit();
            Log.Info(signalled ? "--kill: shutdown signal delivered." : "--kill: no running instance found.");
            return signalled ? 0 : 2;
        }

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Another instance owns the widgets. The desktop verb's whole job is to add a
            // widget to an already-running app, so a second launch is a request, not an error.
            // The request rides the same named event the kill signal uses, so there is no pipe
            // to keep alive and nothing to go stale if a widget closes.
            if (args.Contains("--new-widget"))
            {
                bool ok = DesktopHost.SignalNewWidgetRequest();
                Log.Info(ok ? "--new-widget: request delivered to the running instance." : "--new-widget: no running instance.");
                return ok ? 0 : 2;
            }
            return 0;
        }

        // Tray Restart: the old instance was signalled to exit; wait for its mutex so
        // two owners never exist, then start normally. A missing old instance falls
        // through immediately and this degrades to a plain start.
        if (args.Contains("--replace") && !WaitForPreviousInstance(TimeSpan.FromSeconds(15)))
        {
            Log.Warn("--replace: previous instance did not exit in time.");
            return 3;
        }

        try
        {
            StartMenuLink.Ensure();
            using var host = new DesktopHost();
            host.Run(args);
            return 0;
        }
        catch (Exception ex)
        {
            // Never surface a dialog: the product has no windows other than widgets and
            // context menus. Log instead so a crash is diagnosable after the fact.
            Log.Fatal(ex);
            return 1;
        }
    }
}
