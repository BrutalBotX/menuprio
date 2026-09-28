using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MenuPrio
{
    /// <summary>
    /// Code-built WinForms layouts do not scale reliably on their own (and
    /// Control.DeviceDpi can report 96 even when the window is at 192), so the
    /// 96-dpi layout is scaled explicitly using the window's real DPI.
    /// </summary>
    internal class ScaledForm : Form
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private float _uiScale = 1f;

        /// <summary>Converts a 96-dpi design value to device pixels.</summary>
        public int ToDevice(int value)
        {
            return (int)Math.Round(value * _uiScale);
        }

        protected override void OnLoad(EventArgs e)
        {
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                _uiScale = dpi / 96f;

                if (_uiScale > 1.01f)
                {
                    var client = ClientSize;
                    var minimum = MinimumSize;

                    SuspendLayout();
                    Scale(new SizeF(_uiScale, _uiScale));

                    // Form.ScaleControl does not always touch the form itself
                    ClientSize = new Size(
                        (int)Math.Round(client.Width * _uiScale),
                        (int)Math.Round(client.Height * _uiScale));
                    MinimumSize = new Size(
                        (int)Math.Round(minimum.Width * _uiScale),
                        (int)Math.Round(minimum.Height * _uiScale));
                    ResumeLayout(true);
                }

                Log.Info("ui: dpi=" + dpi + " scale=" + _uiScale.ToString("0.##")
                    + " client=" + ClientSize.Width + "x" + ClientSize.Height);
            }
            catch (Exception ex)
            {
                Log.Error("ui: scaling failed", ex);
            }

            base.OnLoad(e);
        }
    }
}
