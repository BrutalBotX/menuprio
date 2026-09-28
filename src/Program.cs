using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace MenuPrio
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // crisp UI on scaled displays instead of a bitmap-stretched window
            try { NativeMethods.SetProcessDPIAware(); }
            catch { }

            if (HasArg(args, "--console")) NativeMethods.AllocConsole();
            if (HasArg(args, "--verbose")) Log.Verbose = true;

            Log.Init(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MenuPrio"));

            Log.Info("MenuPrio " + Version() + " starting (pid " + Process.GetCurrentProcess().Id + ")");

            if (HasArg(args, "--selftest"))
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "MenuPrio_SingleInstance", out created))
            {
                if (!created)
                {
                    Log.Warn("another MenuPrio instance is already running; exiting");
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Error("unhandled exception", e.ExceptionObject as Exception);
                };

                var app = new TrayApp(HasArg(args, "--ui"));
                if (!app.Start())
                {
                    app.Dispose();
                    Log.Error("MenuPrio could not start");
                    return;
                }

                try { app.RunMessageLoop(); }
                finally { app.Dispose(); }
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string Version()
        {
            try
            {
                var attrs = Assembly.GetExecutingAssembly().GetCustomAttributes(
                    typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                    return ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;

                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch
            {
                return "?";
            }
        }
    }
}
