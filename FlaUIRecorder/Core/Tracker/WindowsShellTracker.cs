using System;

namespace FlaUIRecorder.Tracking.Special
{
    public enum WindowsShellType
    {
        None,
        WindowsSearch,
        StartMenu,
        TaskSwitcher
    }

    public static class WindowsShellTracker
    {
        public static WindowsShellType Detect(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return WindowsShellType.None;

            processName = processName.ToLower();

            return processName switch
            {
                "searchhost" => WindowsShellType.WindowsSearch,
                "startmenuexperiencehost" => WindowsShellType.StartMenu,
                "explorer" => WindowsShellType.StartMenu, // fallback case
                _ => WindowsShellType.None
            };
        }

        public static bool IsShell(WindowsShellType type)
        {
            return type != WindowsShellType.None;
        }
    }
}