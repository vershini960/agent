using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class GlobalKeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP   = 0x0101;  // 🔥 FIX: also capture key-up

    private static IntPtr _hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc _proc = HookCallback;

    /// <summary>
    /// Fired on key-down with the key name (e.g. "A", "Enter", "LControlKey").
    /// Fired on key-up  with the key name suffixed by "Up" (e.g. "LControlKeyUp").
    /// </summary>
    public static event Action<string>? KeyPressed;

    public static void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;

        _hookId = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _proc,
            GetModuleHandle(module.ModuleName),
            0);
    }

    public static void Stop()
    {
        if (_hookId == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            string key  = ((Keys)vkCode).ToString();

            if ((int)wParam == WM_KEYDOWN)
            {
                // Fire e.g. "LControlKey", "A", "Enter" …
                KeyPressed?.Invoke(key);
            }
            else if ((int)wParam == WM_KEYUP)
            {
                // 🔥 FIX: fire e.g. "LControlKeyUp" so modifier releases work
                KeyPressed?.Invoke(key + "Up");
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
}