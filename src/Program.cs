using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MenuPrio
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (HasArg(args, "--console")) NativeMethods.AllocConsole();
            if (HasArg(args, "--verbose")) Log.Verbose = true;

            Log.Init(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MenuPrio"));

            Log.Info("MenuPrio starting (pid " + Process.GetCurrentProcess().Id + ")");

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

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log.Error("unhandled UI exception", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Error("unhandled exception", e.ExceptionObject as Exception);
                };
                Application.Run(new TrayContext(HasArg(args, "--ui")));
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal static class AppIcons
    {
        public static Icon Get()
        {
            try
            {
                var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) return ic;
            }
            catch
            {
                // fall through to the default icon
            }
            return SystemIcons.Application;
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MenuPrio";

        private readonly NotifyIcon _icon;
        private readonly KeyboardHook _hook;
        private readonly Config _config;
        private readonly HistoryStore _store;
        private readonly Interceptor _interceptor;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _startupItem;

        private PriorityForm _form;

        public TrayContext(bool showUiOnStart)
        {
            _config = Config.Load(Config.DefaultPath, HistoryStore.RulesPath);
            _store = new HistoryStore(HistoryStore.DefaultPath, HistoryStore.RulesPath);
            _interceptor = new Interceptor(_store, _config);

            _hook = new KeyboardHook();
            _hook.Handler = _interceptor.HandleKey;
            if (!_hook.Install())
                Log.Error("keyboard hook could not be installed; MenuPrio will not intercept anything");

            var menu = new ContextMenuStrip();
            menu.Items.Add("Priorities...", null, delegate { ShowPriorities(); });

            _pauseItem = new ToolStripMenuItem("Pause interception");
            _pauseItem.CheckOnClick = false;
            _pauseItem.Click += delegate
            {
                _interceptor.Paused = !_interceptor.Paused;
                _pauseItem.Checked = _interceptor.Paused;
            };
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripSeparator());

            _startupItem = new ToolStripMenuItem("Start with Windows");
            _startupItem.Click += delegate
            {
                ToggleStartup();
                _startupItem.Checked = IsStartupEnabled();
            };
            menu.Items.Add(_startupItem);

            menu.Items.Add("Import rules.ini", null, delegate { ImportRules(); });
            menu.Items.Add("Open log", null, delegate { OpenPath(Log.Path); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            menu.Opening += delegate
            {
                _pauseItem.Checked = _interceptor.Paused;
                _startupItem.Checked = IsStartupEnabled();
            };

            _icon = new NotifyIcon();
            _icon.Icon = AppIcons.Get();
            _icon.Text = "MenuPrio - Enter in Start follows your priorities";
            _icon.ContextMenuStrip = menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { ShowPriorities(); };

            Log.Info("ready. history: " + _store.Path);
            if (System.IO.File.Exists(HistoryStore.RulesPath))
                Log.Info("note: rules.ini was imported into history.json; manage priorities in the Priorities window");

            if (showUiOnStart)
            {
                var t = new System.Windows.Forms.Timer();
                t.Interval = 300;
                t.Tick += delegate
                {
                    t.Stop();
                    t.Dispose();
                    ShowPriorities();
                };
                t.Start();
            }
        }

        private void ShowPriorities()
        {
            if (_form == null || _form.IsDisposed)
                _form = new PriorityForm(_store, _interceptor);

            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;
            _form.BringToFront();
            _form.Activate();
        }

        private void ImportRules()
        {
            int added = _store.ImportRulesFile(HistoryStore.RulesPath);
            string msg = added > 0
                ? added + " app(s) imported from rules.ini."
                : "No new apps found in rules.ini.";

            Log.Info("rules import: " + msg);
            _icon.BalloonTipTitle = "MenuPrio";
            _icon.BalloonTipText = msg;
            _icon.ShowBalloonTip(3000);
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("could not open " + path, ex);
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void ToggleStartup()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (key.GetValue(RunValue) != null)
                    {
                        key.DeleteValue(RunValue, false);
                        Log.Info("start with Windows: disabled");
                    }
                    else
                    {
                        key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                        Log.Info("start with Windows: enabled (" + Application.ExecutablePath + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("start with Windows: toggle failed", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_form != null) { _form.Dispose(); _form = null; }
                _hook.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
