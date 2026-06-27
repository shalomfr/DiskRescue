using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskRescue
{
    /// <summary>Applies a dark title bar (DWM immersive dark mode) so the OS chrome matches the dark UI.</summary>
    public static class ThemeHelper
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void UseDarkTitleBar(Window w)
        {
            w.SourceInitialized += (s, e) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    int on = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                }
                catch { /* older Windows — ignore */ }
            };
        }
    }
}
