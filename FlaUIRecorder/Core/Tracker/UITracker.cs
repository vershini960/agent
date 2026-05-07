using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System;
using System.Diagnostics;
using System.Windows;
using Serilog;
using System.Windows.Forms;
using FlaUIRecorder.Core.Models;

public class UITracker
{
    // 🔥 USE GLOBAL UIA (VERY IMPORTANT)
    private UIA3Automation Automation => UIAutomationManager.Automation;

    // =========================================================
    // 🔥 1. FOCUS TRACKING → AutomationElement → ActionStep
    // =========================================================
    public ActionStep ConvertElementToStep(AutomationElement element)
    {
        if (element == null)
        {
            Log.Warning("ConvertElementToStep: element is null");
            return null;
        }

        string automationId = Safe(() => element.AutomationId);
        string name = Safe(() => element.Name);
        string controlType = Safe(() => element.ControlType.ToString());
        string className = Safe(() => element.ClassName);

        int processId = 0;
        string appPath = null;

        try
        {
            processId = element.Properties.ProcessId.Value;
        }
        catch { }

        try
        {
            var process = Process.GetProcessById(processId);
            appPath = process.MainModule.FileName;
        }
        catch { }
        var pos = Cursor.Position;

        var window = element?.Parent;
        AutomationElement topLevelWindow = null;
        while (window != null)
        {
            if (window.ControlType == FlaUI.Core.Definitions.ControlType.Window)
                topLevelWindow = window;
            window = window.Parent;
        }

        int relX = pos.X;
        int relY = pos.Y;

        if (topLevelWindow != null)
        {
            var rect = topLevelWindow.BoundingRectangle;

            relX = pos.X - (int)rect.X;
            relY = pos.Y - (int)rect.Y;
        }
        return new ActionStep
        {
            ActionType = "Click", // or "Focus"
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            ClassName = className,
            AppPath = appPath,
            X = relX,
            Y = relY
        };
    }

    // =========================================================
    // 🔥 2. MOUSE FALLBACK → (x,y) → ActionStep
    // =========================================================
    public ActionStep GetStepFromPoint(int x, int y)
    {
        var automation = Automation;

        AutomationElement element = null;

        try
        {
            element = automation.FromPoint(new Point(x, y));
        }
        catch (Exception ex)
        {
            Log.Warning($"UIA FromPoint failed: {ex.Message}");
            return null;
        }

        if (element == null)
        {
            Log.Warning("No element found at given coordinates");
            return null;
        }

        string automationId = Safe(() => element.AutomationId);
        string name = Safe(() => element.Name);

        // 🔥 FIX: If the name is generic (like "Name" or "System.ItemNameDisplay"), 
        // try to get the real filename from the parent ListItem or the Value property.
        if (string.IsNullOrEmpty(name) || name == "Name" || automationId?.Contains("ItemName") == true)
        {
            var parent = element.Parent;
            if (parent != null && (parent.ControlType == FlaUI.Core.Definitions.ControlType.ListItem || parent.ControlType == FlaUI.Core.Definitions.ControlType.DataItem))
            {
                name = Safe(() => parent.Name);
            }
        }

        var ct = element.Properties.ControlType.ValueOrDefault;
        string controlType = ct == FlaUI.Core.Definitions.ControlType.Unknown
            ? "Unknown"
            : ct.ToString();
        string className = Safe(() => element.ClassName);

        int processId = 0;
        string appPath = null;

        try { processId = element.Properties.ProcessId.Value; } catch { }

        try
        {
            var process = Process.GetProcessById(processId);
            appPath = process.MainModule.FileName;
        }
        catch { }

        // 🔥 CALCULATE RELATIVE POSITION
        var pos = Cursor.Position;

        var window = element;
        AutomationElement topLevelWindow = null;
        while (window != null)
        {
            var windowCt = window.Properties.ControlType.ValueOrDefault;

            if (windowCt == FlaUI.Core.Definitions.ControlType.Window)
                topLevelWindow = window;

            window = window.Parent;
        }

        int relX = pos.X;
        int relY = pos.Y;

        if (topLevelWindow != null)
        {
            var rect = topLevelWindow.BoundingRectangle;

            relX = pos.X - (int)rect.X;
            relY = pos.Y - (int)rect.Y;
        }
        if (className?.Contains(FlaUIRecorder.Core.Constants.Shell.CoreComponentInputSource) == true ||
    className?.Contains(FlaUIRecorder.Core.Constants.Shell.Taskbar) == true)
        {
            return null;
        }

        return new ActionStep
        {
            ActionType = "Click",
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
            ClassName = className,
            AppPath = appPath,
            X = relX,
            Y = relY
        };
    }

    // =========================================================
    // 🔥 SAFE PROPERTY ACCESS (AVOIDS CRASHES)
    // =========================================================
    private T Safe<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }
}