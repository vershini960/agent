using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlaUIRecorder.Core.Hooks
{
    public static class GlobalMouseHook
    {
        private const int WH_MOUSE_LL = 14;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MOUSEWHEEL = 0x020A;

        private static IntPtr _hookId = IntPtr.Zero;
        private static LowLevelMouseProc _proc = HookCallback;

        // 🔥 Events
        public static event Action<int, int>? MouseMove;
        public static event Action<int, int>? MouseClick;       // Left click DOWN
        public static event Action<int, int>? MouseClickUp;     // Left click UP
        public static event Action<int, int>? RightClick;
        public static event Action<int, int, int>? MouseScroll;           // Mouse Wheel (x, y, delta)

        // =====================================================
        // 🔥 START / STOP
        // =====================================================
        public static void Start()
        {
            if (_hookId != IntPtr.Zero)
                return;

            _hookId = SetHook(_proc);
        }

        public static void Stop()
        {
            if (_hookId == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        // =====================================================
        // 🔥 SET HOOK
        // =====================================================
        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule!;

            return SetWindowsHookEx(
                WH_MOUSE_LL,
                proc,
                GetModuleHandle(curModule.ModuleName),
                0
            );
        }

        // =====================================================
        // 🔥 CALLBACK
        // =====================================================
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                int x = data.pt.x;
                int y = data.pt.y;

                switch ((int)wParam)
                {
                    case WM_MOUSEMOVE:
                        MouseMove?.Invoke(x, y);
                        break;

                    case WM_LBUTTONDOWN:
                        MouseClick?.Invoke(x, y);
                        break;

                    case WM_LBUTTONUP:
                        MouseClickUp?.Invoke(x, y);
                        break;

                    case WM_RBUTTONDOWN:
                        RightClick?.Invoke(x, y);
                        break;

                    case WM_MOUSEWHEEL:
                        short delta = (short)((data.mouseData >> 16) & 0xffff);
                        MouseScroll?.Invoke(x, y, delta);
                        break;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // =====================================================
        // 🔥 WIN32 API
        // =====================================================
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            LowLevelMouseProc lpfn,
            IntPtr hMod,
            uint dwThreadId
        );

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk,
            int nCode,
            IntPtr wParam,
            IntPtr lParam
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelMouseProc(
            int nCode,
            IntPtr wParam,
            IntPtr lParam
        );

        // =====================================================
        // 🔥 STRUCTS
        // =====================================================
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public int mouseData;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }
    }
}