using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlaUIRecorder.Tracking.Window
{
    public class ForegroundWindowInfo
    {
        public IntPtr Hwnd { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "";
        public string? MainWindowTitle { get; set; }
        public string? ExecutablePath { get; set; }
    }

    public static class ForegroundWindowTracker
    {
        public static ForegroundWindowInfo GetActiveWindow()
        {
            IntPtr hwnd = GetForegroundWindow();

            if (hwnd == IntPtr.Zero)
                return new ForegroundWindowInfo();

            GetWindowThreadProcessId(hwnd, out uint pid);

            try
            {
                var process = Process.GetProcessById((int)pid);

                return new ForegroundWindowInfo
                {
                    Hwnd = hwnd,
                    ProcessId = (int)pid,
                    ProcessName = process.ProcessName,
                    MainWindowTitle = process.MainWindowTitle,
                    ExecutablePath = SafeGetMainModule(process)
                };
            }
            catch
            {
                return new ForegroundWindowInfo
                {
                    Hwnd = hwnd,
                    ProcessId = (int)pid,
                    ProcessName = "Unknown"
                };
            }
        }

        private static string? SafeGetMainModule(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null; // access denied (system processes)
            }
        }

        #region Win32

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        #endregion
    }
}