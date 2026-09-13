using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CornDownloader
{
    static class Program
    {
        private const string MutexName = @"Global\CornSystems.CornDownloader.SingleInstance";
        private const string ActivateEventName = @"Global\CornSystems.CornDownloader.Activate";

        [STAThread]
        static int Main(string[] args)
        {
            AppPaths.MigrateLegacyData();
            AppPaths.EnsureDataDir();

            var opts = CliOptions.Parse(args);
            if (args.Length > 0) HeadlessRunner.AttachParentConsole();   // print to the launching cmd/PowerShell if there is one
            if (opts.ShowHelp)    { HeadlessRunner.Out(CliOptions.HelpText); ShowInfo(CliOptions.HelpText); return 0; }
            if (opts.ShowVersion) { HeadlessRunner.Out(AppInfo.Version); ShowInfo($"{AppInfo.Name} {AppInfo.Version}"); return 0; }
            if (opts.Error != null)
            {
                HeadlessRunner.Out("error: " + opts.Error);
                if (!opts.Headless) MessageBox.Show(opts.Error + "\n\n" + CliOptions.HelpText, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return HeadlessRunner.ExitBadArgs;
            }

            // Single instance — two copies would race on settings.json and, worse, on winget.
            using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
            if (!isFirst)
            {
                if (opts.Headless)
                {
                    HeadlessRunner.Out($"{AppInfo.Name} is already running — refusing to start a second instance.");
                    return HeadlessRunner.ExitAlreadyRunning;
                }
                try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { /* other instance is mid-startup */ }
                return 0;
            }

            if (opts.Headless)
                return HeadlessRunner.Run(opts);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Catch async-void handler exceptions instead of letting Windows' crash dialog
            // take the app down with no trail.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => HandleUnhandled(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => HandleUnhandled(e.ExceptionObject as Exception);

            SessionLog.SessionHeader("gui");

            var form = new MainForm(opts);
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var watcher = new Thread(() =>
            {
                while (activate.WaitOne())
                {
                    try { form.BeginInvoke((Action)form.BringToFrontFromOtherInstance); }
                    catch (Exception ex) { SessionLog.Write("ACTIVATE", ex); }
                }
            }) { IsBackground = true, Name = "SingleInstanceWatcher" };
            watcher.Start();

            Application.Run(form);
            return 0;
        }

        private static void ShowInfo(string text)
        {
            // Only pop a box when there's no console to print to.
            if (Environment.UserInteractive && !HeadlessRunner.ConsoleAttached)
                MessageBox.Show(text, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void HandleUnhandled(Exception ex)
        {
            if (ex == null) return;

            try
            {
                AppPaths.EnsureDataDir();
                File.AppendAllText(AppPaths.CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { /* best-effort — never throw from the crash handler */ }
            SessionLog.Write("CRASH", ex);

            try
            {
                MessageBox.Show(
                    $"{AppInfo.Name} hit an unexpected error:\n\n{ex.Message}\n\n" +
                    $"Details were saved to:\n{AppPaths.CrashLog}\n\n" +
                    "You can usually keep working — if something looks wrong, restart the app.",
                    "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }
    }
}
