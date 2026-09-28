using System;
using System.Runtime.InteropServices;

namespace MenuPrio
{
    internal sealed class KeyboardHook : IDisposable
    {
        public struct KeyEvent
        {
            public int Vk;
            public bool Down;
            public bool Injected;
        }

        /// <summary>Return true to swallow the key.</summary>
        public Func<KeyEvent, bool> Handler;

        private NativeMethods.LowLevelKeyboardProc _proc;
        private IntPtr _hook = IntPtr.Zero;

        public bool Install()
        {
            if (_hook != IntPtr.Zero) return true;

            _proc = Callback;
            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                NativeMethods.GetModuleHandle(null),
                0);

            if (_hook == IntPtr.Zero)
            {
                Log.Error("keyboard hook: SetWindowsHookEx failed (win32 error " + Marshal.GetLastWin32Error() + ")");
                return false;
            }

            Log.Info("keyboard hook: installed");
            return true;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Handler != null)
            {
                int msg = wParam.ToInt32();
                bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                if (down || up)
                {
                    try
                    {
                        var data = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));

                        var ev = new KeyEvent
                        {
                            Vk = (int)data.vkCode,
                            Down = down,
                            Injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0
                        };

                        if (Handler(ev)) return (IntPtr)1;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("keyboard hook: handler failed", ex);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                Log.Info("keyboard hook: removed");
            }
        }
    }
}
