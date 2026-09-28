using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace MenuPrio
{
    /// <summary>
    /// Watches what is typed while the Start menu / search flyout is in the
    /// foreground. When Enter is pressed it launches the top candidate of the
    /// matching history group. Launches that happen outside MenuPrio (Windows
    /// default action, manually picked result, mouse click) are observed and
    /// recorded into the history as well.
    /// </summary>
    internal sealed class Interceptor
    {
        private static readonly int[] NavKeys = new int[]
        {
            0x21, // page up
            0x22, // page down
            0x23, // end
            0x24, // home
            0x25, // left
            0x26, // up
            0x27, // right
            0x28, // down
            0x09  // tab
        };

        private readonly HistoryStore _store;
        private readonly Config _config;
        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly int _ourPid = Process.GetCurrentProcess().Id;

        private bool _navigated;
        private bool _sessionActive;
        private bool _launcherWasReal;
        private DateTime _pendingLauncherUntil = DateTime.MinValue;
        private DateTime _suppressEnterUntil = DateTime.MinValue;

        private volatile string _sessionTyped = "";
        private volatile bool _suppressObservationOnce;
        private int _watcherBusy;

        /// <summary>When paused, MenuPrio does not touch Enter and does not observe launches.</summary>
        public volatile bool Paused;

        public Interceptor(HistoryStore store, Config config)
        {
            _store = store;
            _config = config;
        }

        public bool HandleKey(KeyboardHook.KeyEvent e)
        {
            if (e.Injected) return false;
            if (Paused) return false;

            bool realActive = IsLauncherForeground();

            // session begins when the launcher actually gets focus
            if (realActive && !_launcherWasReal)
            {
                _launcherWasReal = true;
                _suppressObservationOnce = false;
                _sessionTyped = _buffer.ToString().Trim();
                StartSessionWatcher();
            }
            else if (!realActive && _launcherWasReal)
            {
                _launcherWasReal = false;
            }

            // Pressing the Windows key starts a search session a moment before
            // the Start window actually gets focus, so remember it and treat
            // keys arriving right after as search input.
            if (e.Vk == NativeMethods.VK_LWIN || e.Vk == NativeMethods.VK_RWIN)
            {
                _pendingLauncherUntil = DateTime.UtcNow.AddMilliseconds(1000);
                ResetBuffer();
                _sessionTyped = "";
                _sessionActive = true;
                return false;
            }

            bool pending = DateTime.UtcNow < _pendingLauncherUntil;
            bool effective = realActive || pending;

            if (!effective)
            {
                if (_sessionActive) ResetBuffer();
                _sessionActive = false;
                return false;
            }

            if (!_sessionActive)
            {
                ResetBuffer();
                _sessionActive = true;
            }

            if (!e.Down) return false;

            if (e.Vk == NativeMethods.VK_RETURN)
            {
                if (!realActive) return false;
                if (DateTime.UtcNow < _suppressEnterUntil) return true;
                if (IsCtrlOrAltDown()) return false;
                return HandleEnter();
            }

            if (e.Vk == NativeMethods.VK_BACK)
            {
                if (_buffer.Length > 0) _buffer.Length--;
                _navigated = false;
                _sessionTyped = _buffer.ToString().Trim();
                if (Log.Verbose) Log.Info("key: backspace -> \"" + _buffer + "\"");
                return false;
            }

            if (Array.IndexOf(NavKeys, e.Vk) >= 0)
            {
                _navigated = true;
                return false;
            }

            if (IsModifierKey(e.Vk) || IsCtrlOrAltDown()) return false;

            var text = KeyText(e.Vk);
            if (!string.IsNullOrEmpty(text))
            {
                _buffer.Append(text);
                _navigated = false;
                _sessionTyped = _buffer.ToString().Trim();
                if (Log.Verbose) Log.Info("key: '" + text + "' -> \"" + _buffer + "\"");
            }

            return false;
        }

        private bool HandleEnter()
        {
            var typed = _buffer.ToString().Trim();
            if (typed.Length == 0) return false;

            if (_navigated)
            {
                Log.Info("enter: \"" + typed + "\" (result picked manually, not intercepted)");
                ResetBuffer();
                return false;
            }

            var match = _store.Find(typed);
            if (match == null)
            {
                Log.Info("enter: \"" + typed + "\" -> no candidate, normal action");
                ResetBuffer();
                return false;
            }

            Log.Info("enter: \"" + typed + "\" -> launching [" + match.Top + "]");
            _suppressEnterUntil = DateTime.UtcNow.AddMilliseconds(1500);
            _suppressObservationOnce = true; // we record this one ourselves
            ResetBuffer();

            var m = match;
            var t = typed;
            ThreadPool.QueueUserWorkItem(delegate { CloseLauncherThenLaunch(m, t); });
            return true; // swallow Enter
        }

        private void CloseLauncherThenLaunch(HistoryMatch match, string typed)
        {
            try
            {
                if (IsLauncherForeground())
                {
                    SendEscape();
                    Thread.Sleep(180);
                }
                else
                {
                    Thread.Sleep(50);
                }

                if (Launcher.Launch(match.Top))
                {
                    _store.RecordLaunch(match, typed, "menu");
                }
                else
                {
                    _suppressObservationOnce = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("interceptor: launching failed", ex);
                _suppressObservationOnce = false;
            }
        }

        // ---------------- observation ----------------

        private void StartSessionWatcher()
        {
            if (Interlocked.CompareExchange(ref _watcherBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { WatchSession(); }
                catch (Exception ex) { Log.Error("watcher: failed", ex); }
                finally { Interlocked.Exchange(ref _watcherBusy, 0); }
            });
        }

        /// <summary>
        /// Waits for the launcher to close, then looks at what came to the
        /// foreground. Freshly started processes are recorded under the typed
        /// text; switching back to an already running window is ignored.
        /// </summary>
        private void WatchSession()
        {
            var guard = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < guard && IsLauncherForeground())
                Thread.Sleep(120);
            if (DateTime.UtcNow >= guard) return;

            var typed = _sessionTyped;
            if (string.IsNullOrEmpty(typed)) return;

            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < 2.5)
            {
                var hwnd = NativeMethods.GetForegroundWindow();
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);

                if (pid != 0 && pid != (uint)_ourPid)
                {
                    var info = InspectProcess((int)pid, started);
                    if (info != null && !_config.IsLauncherProcess(info.Name))
                    {
                        if (!info.Fresh) return; // user switched back to an existing window

                        if (_suppressObservationOnce)
                        {
                            _suppressObservationOnce = false;
                            return; // MenuPrio launched it and already recorded it
                        }

                        string name = HistoryStore.FriendlyName(info.Target);
                        if (string.IsNullOrEmpty(name)) name = info.Name;

                        Log.Info("observed: \"" + typed + "\" -> " + name
                            + (info.Target != null ? " (" + info.Target + ")" : ""));
                        _store.RecordObserved(typed, name, info.Target, "", "", "observed");
                        return;
                    }
                }

                Thread.Sleep(100);
            }
        }

        private sealed class ProcessInfo
        {
            public string Name;
            public string Target;
            public bool Fresh;
        }

        private static ProcessInfo InspectProcess(int pid, DateTime started)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    var info = new ProcessInfo();
                    info.Name = p.ProcessName;
                    try { info.Fresh = p.StartTime >= started.AddMilliseconds(-2500); } catch { }
                    try { info.Target = p.MainModule.FileName; } catch { }
                    return info;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------------- helpers ----------------

        private static void SendEscape()
        {
            var inputs = new NativeMethods.INPUT[2];

            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = NativeMethods.VK_ESCAPE;

            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = NativeMethods.VK_ESCAPE;
            inputs[1].U.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            int size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT));
            var sent = NativeMethods.SendInput(2, inputs, size);
            if (sent != 2) Log.Warn("interceptor: SendInput(Escape) sent " + sent + "/2");
        }

        private bool IsLauncherForeground()
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == (uint)_ourPid) return false;

            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    return _config.IsLauncherProcess(p.ProcessName);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCtrlOrAltDown()
        {
            return (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0
                || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        }

        private static bool IsModifierKey(int vk)
        {
            switch (vk)
            {
                case 0x10: // shift
                case 0x11: // ctrl
                case 0x12: // alt
                case 0x14: // caps lock
                case 0x5B: // lwin
                case 0x5C: // rwin
                case 0x90: // num lock
                case 0x91: // scroll lock
                case 0xA0: case 0xA1: // l/r shift
                case 0xA2: case 0xA3: // l/r ctrl
                case 0xA4: case 0xA5: // l/r alt
                    return true;
                default:
                    return false;
            }
        }

        private static string KeyText(int vk)
        {
            // Letters (case does not matter; everything is matched lowercased)
            if (vk >= 0x41 && vk <= 0x5A) return ((char)('a' + (vk - 0x41))).ToString();

            // Digits, top row and numpad
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return ((char)('0' + (vk - 0x60))).ToString();

            // Everything else: let the keyboard layout decide, accept only a
            // single printable character.
            var state = new byte[256];
            if (!NativeMethods.GetKeyboardState(state)) return null;

            var scan = NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
            var sb = new StringBuilder(4);
            int rc = NativeMethods.ToUnicodeEx((uint)vk, scan, state, sb, sb.Capacity, 0, IntPtr.Zero);

            if (rc == 1 && sb.Length >= 1)
            {
                char c = sb[0];
                if (!char.IsControl(c)) return c.ToString();
            }

            return null;
        }

        private void ResetBuffer()
        {
            _buffer.Clear();
            _navigated = false;
        }
    }
}
