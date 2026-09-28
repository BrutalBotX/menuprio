using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace MenuPrio
{
    /// <summary>
    /// Native tray icon + menu and the application message loop.
    /// System.Windows.Forms is only loaded when the Priorities window is opened,
    /// which keeps the idle process small.
    /// </summary>
    internal sealed class TrayApp : IDisposable
    {
        private const string WindowClass = "MenuPrioTrayWindow";
        private const uint TrayMessage = NativeMethods.WM_APP + 1;
        private const uint TrimTimerId = 1;
        private const int TrimIntervalMs = 60 * 1000;

        private const int CmdPriorities = 1;
        private const int CmdPause = 2;
        private const int CmdStartup = 3;
        private const int CmdImport = 4;
        private const int CmdLog = 5;
        private const int CmdExit = 6;

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MenuPrio";

        private readonly Config _config;
        private readonly HistoryStore _store;
        private readonly Interceptor _interceptor;
        private readonly KeyboardHook _hook;
        private readonly NativeMethods.WndProcDelegate _wndProc;
        private readonly string _exePath;
        private readonly bool _showUiOnStart;

        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _trayIcon = IntPtr.Zero;
        private bool _trayAdded;
        private int _formRunning;
        private volatile PriorityForm _form;

        public TrayApp(bool showUiOnStart)
        {
            _showUiOnStart = showUiOnStart;
            _wndProc = WndProc; // keep the delegate alive for the native window

            try
            {
                var module = Process.GetCurrentProcess().MainModule;
                _exePath = module != null ? module.FileName : null;
            }
            catch
            {
                _exePath = null;
            }

            _config = Config.Load(Config.DefaultPath, HistoryStore.RulesPath);
            _store = new HistoryStore(HistoryStore.DefaultPath, HistoryStore.RulesPath);
            _interceptor = new Interceptor(_store, _config);
            _hook = new KeyboardHook();
            _hook.Handler = _interceptor.HandleKey;
        }

        public bool Start()
        {
            var hInstance = NativeMethods.GetModuleHandle(null);

            var wc = new NativeMethods.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
            wc.hInstance = hInstance;
            wc.lpszClassName = WindowClass;

            if (NativeMethods.RegisterClassEx(ref wc) == 0)
            {
                Log.Error("tray: RegisterClassEx failed (win32 error " + Marshal.GetLastWin32Error() + ")");
                return false;
            }

            _hwnd = NativeMethods.CreateWindowEx(0, WindowClass, "MenuPrio", 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Log.Error("tray: CreateWindowEx failed (win32 error " + Marshal.GetLastWin32Error() + ")");
                return false;
            }

            _trayIcon = LoadTrayIcon();

            var data = new NativeMethods.NOTIFYICONDATA();
            data.cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATA));
            data.hWnd = _hwnd;
            data.uID = 1;
            data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
            data.uCallbackMessage = TrayMessage;
            data.hIcon = _trayIcon;
            data.szTip = "MenuPrio - Enter in Start follows your priorities";

            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
            {
                Log.Error("tray: Shell_NotifyIcon(NIM_ADD) failed");
                return false;
            }
            _trayAdded = true;

            if (!_hook.Install())
                Log.Error("keyboard hook could not be installed; MenuPrio will not intercept anything");

            NativeMethods.SetTimer(_hwnd, new UIntPtr(TrimTimerId), TrimIntervalMs, IntPtr.Zero);
            TrimWorkingSet();

            Log.Info("ready. history: " + _store.Path);
            if (System.IO.File.Exists(HistoryStore.RulesPath))
                Log.Info("note: rules.ini was imported into history.json; manage priorities in the Priorities window");

            if (_showUiOnStart) ShowPriorities();
            return true;
        }

        public void RunMessageLoop()
        {
            NativeMethods.MSG msg;
            int result;
            while ((result = NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            if (result < 0) Log.Error("tray: GetMessage failed");
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case TrayMessage:
                        OnTrayMessage(lParam);
                        return IntPtr.Zero;
                    case NativeMethods.WM_TIMER:
                        if (wParam.ToInt64() == TrimTimerId) TrimWorkingSet();
                        return IntPtr.Zero;
                    case NativeMethods.WM_DESTROY:
                        NativeMethods.PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Error("tray: window procedure failed", ex);
            }

            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void OnTrayMessage(IntPtr lParam)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev == (int)NativeMethods.WM_RBUTTONUP || ev == (int)NativeMethods.WM_CONTEXTMENU)
                ShowMenu();
            else if (ev == (int)NativeMethods.WM_LBUTTONDBLCLK)
                ShowPriorities();
        }

        private void ShowMenu()
        {
            IntPtr menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            try
            {
                NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (IntPtr)CmdPriorities, "Priorities...");
                NativeMethods.AppendMenu(menu,
                    NativeMethods.MF_STRING | (_interceptor.Paused ? NativeMethods.MF_CHECKED : 0),
                    (IntPtr)CmdPause, "Pause interception");
                NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                NativeMethods.AppendMenu(menu,
                    NativeMethods.MF_STRING | (IsStartupEnabled() ? NativeMethods.MF_CHECKED : 0),
                    (IntPtr)CmdStartup, "Start with Windows");
                NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (IntPtr)CmdImport, "Import rules.ini");
                NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (IntPtr)CmdLog, "Open log");
                NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
                NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (IntPtr)CmdExit, "Exit");

                NativeMethods.POINT pt;
                NativeMethods.GetCursorPos(out pt);

                NativeMethods.SetForegroundWindow(_hwnd);
                uint cmd = NativeMethods.TrackPopupMenuEx(menu,
                    NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                    pt.x, pt.y, _hwnd, IntPtr.Zero);
                NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (cmd != 0) HandleCommand((int)cmd);
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        private void HandleCommand(int cmd)
        {
            switch (cmd)
            {
                case CmdPriorities:
                    ShowPriorities();
                    break;
                case CmdPause:
                    _interceptor.Paused = !_interceptor.Paused;
                    Log.Info("pause: " + (_interceptor.Paused ? "on" : "off"));
                    break;
                case CmdStartup:
                    ToggleStartup();
                    break;
                case CmdImport:
                    ImportRules();
                    break;
                case CmdLog:
                    OpenPath(Log.Path);
                    break;
                case CmdExit:
                    NativeMethods.PostQuitMessage(0);
                    break;
            }
        }

        private void ShowPriorities()
        {
            if (Interlocked.CompareExchange(ref _formRunning, 1, 0) != 0)
            {
                var existing = _form;
                if (existing != null)
                {
                    try { existing.BeginInvoke((Action)delegate { existing.Activate(); }); }
                    catch { }
                }
                return;
            }

            var thread = new Thread(FormThread);
            thread.IsBackground = true;
            thread.Name = "MenuPrio UI";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void FormThread()
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                using (var form = new PriorityForm(_store, _interceptor))
                {
                    _form = form;
                    System.Windows.Forms.Application.Run(form);
                }
            }
            catch (Exception ex)
            {
                Log.Error("priorities window failed", ex);
            }
            finally
            {
                _form = null;
                Interlocked.Exchange(ref _formRunning, 0);
                TrimWorkingSet(); // give the UI's memory back right away
            }
        }

        private void ImportRules()
        {
            int added = _store.ImportRulesFile(HistoryStore.RulesPath);
            string msg = added > 0
                ? added + " app(s) imported from rules.ini."
                : "No new apps found in rules.ini.";

            Log.Info("rules import: " + msg);
            ShowBalloon(msg);
        }

        private void ShowBalloon(string text)
        {
            var data = new NativeMethods.NOTIFYICONDATA();
            data.cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATA));
            data.hWnd = _hwnd;
            data.uID = 1;
            data.uFlags = NativeMethods.NIF_INFO;
            data.szInfoTitle = "MenuPrio";
            data.szInfo = text;
            data.dwInfoFlags = NativeMethods.NIIF_INFO;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }

        private IntPtr LoadTrayIcon()
        {
            try
            {
                if (!string.IsNullOrEmpty(_exePath))
                {
                    IntPtr large, small;
                    if (NativeMethods.ExtractIconEx(_exePath, 0, out large, out small, 1) > 0)
                    {
                        if (small != IntPtr.Zero)
                        {
                            if (large != IntPtr.Zero) NativeMethods.DestroyIcon(large);
                            return small;
                        }
                        if (large != IntPtr.Zero) return large;
                    }
                }
            }
            catch
            {
                // fall through to the default icon
            }
            return NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
        }

        private static void TrimWorkingSet()
        {
            try
            {
                // collect first, then let Windows page out what is no longer needed
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true);
                NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
            }
            catch
            {
            }
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

        private void ToggleStartup()
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
                        var exe = _exePath ?? System.Reflection.Assembly.GetEntryAssembly().Location;
                        key.SetValue(RunValue, "\"" + exe + "\"");
                        Log.Info("start with Windows: enabled (" + exe + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("start with Windows: toggle failed", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_trayAdded)
                {
                    var data = new NativeMethods.NOTIFYICONDATA();
                    data.cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATA));
                    data.hWnd = _hwnd;
                    data.uID = 1;
                    NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
                    _trayAdded = false;
                }

                if (_hwnd != IntPtr.Zero)
                {
                    NativeMethods.KillTimer(_hwnd, new UIntPtr(TrimTimerId));
                    NativeMethods.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }

                if (_trayIcon != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(_trayIcon);
                    _trayIcon = IntPtr.Zero;
                }

                var form = _form;
                if (form != null)
                {
                    try { form.BeginInvoke((Action)delegate { form.Close(); }); }
                    catch { }
                }

                _hook.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("tray: dispose failed", ex);
            }
        }
    }
}
