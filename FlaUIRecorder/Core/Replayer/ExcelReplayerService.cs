using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using FlaUIRecorder.Core.Models;
using Serilog;
using System.Diagnostics;
using System.Windows.Forms;
using FlaUIApp = FlaUI.Core.Application;

public class ExcelReplayerService
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // =========================================================
    // HELPERS
    // =========================================================
    private static bool IsShellApp(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var p = path.ToLower();
        return FlaUIRecorder.Core.Constants.Shell.Processes.Any(s => p.Contains(s));
    }

    private static void NavigateToCell(AutomationElement window, UIA3Automation automation, string cellAddress)
    {
        if (string.IsNullOrEmpty(cellAddress)) return;

        var cf = automation.ConditionFactory;
        var nameBox = window.FindFirstDescendant(cf.ByAutomationId(FlaUIRecorder.Core.Constants.Excel.NameBoxIds[0]));

        if (nameBox != null)
        {
            FlaUIRecorder.Core.Replayer.VisualIndicator.ShowRectangle(nameBox.BoundingRectangle);
            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
            nameBox.Click();
            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ShortWait);

            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_A);
            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);

            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
            FlaUI.Core.Input.Keyboard.Type(cellAddress.ToUpper());
            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ShortWait);

            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.WindowFocusWait);
        }
    }

    private static bool EnsureCurrentApp(
        string fullLaunchPath,
        Dictionary<string, FlaUIApp> apps,
        UIA3Automation automation,
        ref FlaUIApp currentApp)
    {
        if (string.IsNullOrEmpty(fullLaunchPath) || IsShellApp(fullLaunchPath)) return false;

        // Separate exe path from arguments for matching
        string appPath = fullLaunchPath;
        string args = "";
        if (fullLaunchPath.Contains(" ") && fullLaunchPath.ToLower().Contains(".exe"))
        {
            int idx = fullLaunchPath.ToLower().IndexOf(".exe") + 4;
            appPath = fullLaunchPath.Substring(0, idx).Trim('\"');
            args = fullLaunchPath.Substring(idx).Trim();
        }

        bool isAlive = false;
        try { isAlive = currentApp != null && !currentApp.HasExited; } catch { }

        if (isAlive && apps.TryGetValue(appPath, out var existing) && existing == currentApp)
            return true;

        if (apps.TryGetValue(appPath, out var tracked) && !tracked.HasExited)
        {
            currentApp = tracked;
            return true;
        }

        try
        {
            var processes = Process.GetProcesses().Where(p =>
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe == null) return false;
                    return exe.ToLower() == appPath.ToLower() && p.MainWindowHandle != IntPtr.Zero;
                }
                catch { return false; }
            }).OrderByDescending(p => p.StartTime).ToList();

            if (processes.Count > 0)
            {
                var attached = FlaUIApp.Attach(processes[0]);
                apps[appPath] = attached;
                currentApp = attached;
                Log.Information($"[ATTACH] {appPath}");
                return true;
            }
        }
        catch { }

        try
        {
            FlaUIApp newApp;
            if (appPath.ToLower().Contains(FlaUIRecorder.Core.Constants.Chrome.ProcessKeyword))
            {
                string chromeArgs = FlaUIRecorder.Core.Constants.Chrome.DebugFlags;
                if (!string.IsNullOrEmpty(args)) chromeArgs += " " + args;
                newApp = FlaUIApp.Launch(appPath, chromeArgs);
            }
            else
            {
                newApp = FlaUIApp.Launch(appPath, args);
            }

            apps[appPath] = newApp;
            currentApp = newApp;
            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.AppLaunchWait);
            Log.Information($"[LAUNCHED] {appPath} {args}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Launch failed: {appPath} {args}");
            return false;
        }
    }

    // =========================================================
    // PLAY
    // =========================================================
    public void Play(List<ActionStep> steps)
    {
        var apps = new Dictionary<string, FlaUIApp>();
        FlaUIApp currentApp = null;
        var automation = UIAutomationManager.Automation;
        var closedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool lastClickWasAttachFile = false;
        System.Drawing.Rectangle lastChromeWindowRect = System.Drawing.Rectangle.Empty;

        foreach (var step in steps)
        {
            Log.Information($"[REPLAY] {step.ActionType} | App: {step.AppPath} | Target: {step.Name} | Value: {step.Value}");

            if (IsShellApp(step.AppPath)) continue;

            bool isWebBasedApp = !string.IsNullOrEmpty(step.AppPath) && (
                                 step.AppPath.ToLower().Contains(FlaUIRecorder.Core.Constants.Chrome.ProcessKeyword) ||
                                 step.AppPath.ToLower().Contains(FlaUIRecorder.Core.Constants.Chrome.OutlookKeyword) ||
                                 step.AppPath.ToLower().Contains(FlaUIRecorder.Core.Constants.Chrome.OlkKeyword));

            int retrySeconds = isWebBasedApp ? FlaUIRecorder.Core.Constants.Timeouts.WebRetrySeconds : FlaUIRecorder.Core.Constants.Timeouts.RetrySeconds;

            switch (step.ActionType)
            {
                case "OpenApp":
                    if (!string.IsNullOrEmpty(step.AppPath))
                    {
                        string launchPath = step.AppPath;
                        if (!string.IsNullOrEmpty(step.Value) && step.AppPath.ToLower().Contains("chrome"))
                        {
                            launchPath = $"{step.AppPath} {step.Value}";
                        }
                        EnsureCurrentApp(launchPath, apps, automation, ref currentApp);
                        Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.PageLoadWait); // 🔥 Allow Gmail to load
                    }
                    break;

                case "ExcelInput":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        var win = currentApp.GetMainWindow(automation);
                        if (win == null) break;
                        win.SetForeground();
                        Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.WindowFocusWait);

                        var cell = win.FindFirstDescendant(automation.ConditionFactory.ByName(step.TargetName));
                        if (cell != null)
                        {
                            FlaUIRecorder.Core.Replayer.VisualIndicator.ShowRectangle(cell.BoundingRectangle);
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                            cell.Click();
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_A);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                            FlaUI.Core.Input.Keyboard.Type(step.Value);
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.WindowFocusWait);
                        }
                    }
                    break;

                case "Click":
                    if (step.Name == "Close" || step.Name == "Minimize") break;
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        AutomationElement targetWindow = null;
                        AutomationElement targetElement = null;
                        var cf = automation.ConditionFactory;

                        Retry.WhileTrue(() =>
                        {
                            try
                            {
                                var desktop = automation.GetDesktop();
                                var allWindows = currentApp.GetAllTopLevelWindows(automation)
                                    .Where(w => !w.Properties.IsOffscreen.ValueOrDefault && w.BoundingRectangle.Width > 150)
                                    .OrderByDescending(w => w.Properties.NativeWindowHandle.ValueOrDefault == GetForegroundWindow())
                                    .ToList();

                                var dialogs = desktop.FindAllChildren(cf.ByClassName(FlaUIRecorder.Core.Constants.Automation.Win32DialogClass)).ToList();

                                // 🔥 DROPDOWN HACK: Zero UIA calls for the attach menu
                                // FIX: ONLY use the hack if NO dialog is open. If a dialog is open, we use normal logic.
                                if (lastClickWasAttachFile && lastChromeWindowRect != System.Drawing.Rectangle.Empty && !dialogs.Any())
                                {
                                    int x = lastChromeWindowRect.X + step.X;
                                    int y = lastChromeWindowRect.Y + step.Y;
                                    Log.Information($"[HACK] Clicking '{step.Name}' at recorded offset ({step.X},{step.Y})");
                                    FlaUIRecorder.Core.Replayer.VisualIndicator.ShowClick(x, y);
                                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(x, y));

                                    targetWindow = allWindows.FirstOrDefault() ?? desktop;
                                    lastClickWasAttachFile = false;
                                    return true;
                                }

                                // If a dialog IS open, ensure the hack is disabled for the next step
                                if (dialogs.Any()) lastClickWasAttachFile = false;

                                if (!string.IsNullOrEmpty(step.AutomationId) || !string.IsNullOrEmpty(step.Name))
                                {
                                    // 1. Win32 Dialogs
                                    foreach (var dlg in dialogs)
                                    {
                                        // 🔥 Robust search for Open button variations
                                        if (step.AutomationId == "1" || step.Name?.ToLower().Contains("open") == true)
                                        {
                                            targetElement = dlg.FindFirstDescendant(cf.ByAutomationId("1"))
                                                         ?? dlg.FindFirstDescendant(cf.ByName("Open"))
                                                         ?? dlg.FindFirstDescendant(cf.ByName("&Open"));
                                        }
                                        else if (step.ControlType == "ListItem")
                                        {
                                            // 🔥 NEW: For file items, search by NAME first (not AutomationId)
                                            targetElement = dlg.FindFirstDescendant(cf.ByName(step.Name));

                                            // Fallback: search all list items if name didn't work
                                            if (targetElement == null)
                                            {
                                                var allItems = dlg.FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
                                                targetElement = allItems.FirstOrDefault(item => item.Name?.Contains(step.Name) == true);
                                                if (targetElement != null) Log.Information($"[FILE FOUND] {step.Name} (by name fallback)");
                                            }
                                        }
                                        else
                                        {
                                            if (!string.IsNullOrEmpty(step.Name)) targetElement = dlg.FindFirstDescendant(cf.ByName(step.Name));
                                            if (targetElement == null && !string.IsNullOrEmpty(step.AutomationId)) targetElement = dlg.FindFirstDescendant(cf.ByAutomationId(step.AutomationId));
                                        }

                                        if (targetElement != null)
                                        {
                                            targetWindow = dlg;

                                            // 🔥 Ensure we select the parent row if an inner label was clicked
                                            var actualItem = targetElement;
                                            while (actualItem != null && actualItem.ControlType != FlaUI.Core.Definitions.ControlType.ListItem && actualItem.ControlType != FlaUI.Core.Definitions.ControlType.Window)
                                            {
                                                actualItem = actualItem.Parent;
                                            }

                                            if (actualItem != null && actualItem.ControlType == FlaUI.Core.Definitions.ControlType.ListItem)
                                            {
                                                try
                                                {
                                                    if (actualItem.Patterns.ScrollItem.IsSupported) actualItem.Patterns.ScrollItem.Pattern.ScrollIntoView();
                                                    if (actualItem.Patterns.SelectionItem.IsSupported) actualItem.Patterns.SelectionItem.Pattern.Select();
                                                    else actualItem.Focus();
                                                }
                                                catch { actualItem.Focus(); }
                                            }
                                            return true;
                                        }
                                    }

                                    // 2. Browser Windows - 🔥 IMPROVED with better fallbacks
                                    foreach (var w in allWindows)
                                    {
                                        // For buttons, try Name first
                                        if (step.ControlType == "Button" && !string.IsNullOrEmpty(step.Name))
                                        {
                                            targetElement = w.FindFirstDescendant(cf.ByName(step.Name));
                                        }

                                        // Try AutomationId
                                        if (targetElement == null && !string.IsNullOrEmpty(step.AutomationId))
                                        {
                                            targetElement = w.FindFirstDescendant(cf.ByAutomationId(step.AutomationId));
                                        }

                                        // Try Name as fallback
                                        if (targetElement == null && !string.IsNullOrEmpty(step.Name))
                                        {
                                            targetElement = w.FindFirstDescendant(cf.ByName(step.Name));
                                        }

                                        // 🔥 NEW: Last resort for web apps - search by partial name or control type
                                        if (targetElement == null && isWebBasedApp)
                                        {
                                            if (step.ControlType == "Button")
                                            {
                                                var allButtons = w.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
                                                targetElement = allButtons.FirstOrDefault(b => b.Name?.Contains(step.Name) == true);
                                                if (targetElement != null) Log.Information($"[BUTTON FOUND] {step.Name} (by partial name)");
                                            }
                                            else if (step.ControlType == "Edit" || step.ControlType == "ComboBox")
                                            {
                                                var edits = w.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
                                                var combos = w.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox));
                                                targetElement = edits.Union(combos)
                                                    .FirstOrDefault(el => el.Name?.Contains(step.Name) == true);
                                                if (targetElement != null) Log.Information($"[FIELD FOUND] {step.Name} (by partial name)");
                                            }
                                        }

                                        if (targetElement != null) { targetWindow = w; return true; }
                                    }
                                }

                                if (dialogs.Any()) { targetWindow = dialogs.First(); return true; }
                                if (allWindows.Count > 0) { targetWindow = allWindows.First(); return true; }
                                return false;
                            }
                            catch (Exception ex) { Log.Error(ex, "Click search failed"); return false; }
                        }, TimeSpan.FromSeconds(retrySeconds));

                        if (targetWindow == null) break;

                        targetWindow.SetForeground();
                        Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ShortWait);

                        try
                        {
                            var winRect = targetWindow.BoundingRectangle;
                            int clickX, clickY;
                            bool isListItem = false;

                            if (targetElement != null)
                            {
                                var rect = targetElement.BoundingRectangle;
                                Log.Information($"[ELEMENT CLICK] {step.Name}");
                                FlaUIRecorder.Core.Replayer.VisualIndicator.ShowRectangle(rect);
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);

                                clickX = (int)rect.X + ((int)rect.Width / 2);
                                clickY = (int)rect.Y + ((int)rect.Height / 2);

                                isListItem = targetElement.Properties.ControlType == FlaUI.Core.Definitions.ControlType.ListItem;
                            }
                            else
                            {
                                Log.Information($"[XY CLICK] {step.Name} at {step.X}, {step.Y}");
                                clickX = (int)winRect.X + step.X;
                                clickY = (int)winRect.Y + step.Y;
                                FlaUIRecorder.Core.Replayer.VisualIndicator.ShowClick(clickX, clickY);
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                            }

                            FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(clickX, clickY));
                            Thread.Sleep(500); // 🔥 Give the dialog time to register the selection or click

                            string clickedName = targetElement?.Name ?? step.Name ?? "";
                            bool isOpenClick = step.AutomationId == "1" || clickedName == "Open";
                            bool isAttachClick = clickedName == FlaUIRecorder.Core.Constants.Automation.AttachFileButton || clickedName.ToLower().Contains("attach");
                            bool isBrowseClick = clickedName == FlaUIRecorder.Core.Constants.Automation.BrowseComputerButton || clickedName.ToLower().Contains("browse");

                            if (isOpenClick)
                            {
                                Log.Information("[DIALOG] Open button clicked, waiting for dialog to close...");
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.LongWait);
                                lastClickWasAttachFile = false;
                            }
                            else if (isAttachClick)
                            {
                                Thread.Sleep(800); // 🔥 INCREASED: Gmail's file picker may take longer

                                // 🔥 NEW: Wait for dialog to appear instead of just hacking
                                AutomationElement fileDialog = null;  // 🔥 DECLARE HERE
                                Retry.WhileTrue(() =>
                                {
                                    var dialogs2 = automation.GetDesktop()
                                        .FindAllChildren(cf.ByClassName("#32770")).ToList();
                                    if (dialogs2.Count > 0)
                                    {
                                        fileDialog = dialogs2[0];
                                        return true;  // Found dialog, exit loop
                                    }
                                    return false;  // Dialog not found yet, retry
                                }, TimeSpan.FromSeconds(3));

                                if (fileDialog != null)
                                {
                                    Log.Information("[FILE PICKER] Dialog detected");
                                    lastClickWasAttachFile = true;
                                    lastChromeWindowRect = new System.Drawing.Rectangle((int)winRect.X, (int)winRect.Y,
                                                                                       (int)winRect.Width, (int)winRect.Height);
                                }
                                else
                                {
                                    Log.Warning("[FILE PICKER] No dialog detected");
                                    lastClickWasAttachFile = false;
                                }
                            }
                            else if (isListItem)
                            {
                                Thread.Sleep(500);
                                lastClickWasAttachFile = false;
                            }
                            else if (isBrowseClick)
                            {
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.LongWait);
                                lastClickWasAttachFile = false;
                            }
                            else { lastClickWasAttachFile = false; }
                        }
                        catch (Exception ex) { Log.Error(ex, $"Click failed for {step.Name}"); }
                    }
                    break;


                case "Type":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        AutomationElement targetWindow = null;
                        AutomationElement targetElement = null;
                        var cf = automation.ConditionFactory;

                        Retry.WhileTrue(() =>
                        {
                            try
                            {
                                var allWindows = currentApp.GetAllTopLevelWindows(automation)
                                    .Where(w => !w.Properties.IsOffscreen.ValueOrDefault && w.BoundingRectangle.Width > 150)
                                    .OrderByDescending(w => w.Properties.NativeWindowHandle.ValueOrDefault == GetForegroundWindow())
                                    .ToList();

                                if (allWindows.Count == 0) return false;

                                // 1. Try foreground window first
                                if (!string.IsNullOrEmpty(step.TargetAutomationId) || !string.IsNullOrEmpty(step.TargetName))
                                {
                                    var activeWin = allWindows.FirstOrDefault(w => w.Properties.NativeWindowHandle.ValueOrDefault == GetForegroundWindow());
                                    if (activeWin != null)
                                    {
                                        if (!string.IsNullOrEmpty(step.TargetAutomationId)) targetElement = activeWin.FindFirstDescendant(cf.ByAutomationId(step.TargetAutomationId));
                                        if (targetElement == null && !string.IsNullOrEmpty(step.TargetName)) targetElement = activeWin.FindFirstDescendant(cf.ByName(step.TargetName));

                                        // 🔥 NEW: Fallback search for web elements by partial name
                                        if (targetElement == null && isWebBasedApp && !string.IsNullOrEmpty(step.TargetName))
                                        {
                                            var allEdits = activeWin.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
                                            var allCombos = activeWin.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox));
                                            targetElement = allEdits.Union(allCombos)
                                                .FirstOrDefault(el => el.Name?.Contains(step.TargetName) == true);
                                        }

                                        if (targetElement != null) { targetWindow = activeWin; return true; }
                                    }

                                    foreach (var w in allWindows)
                                    {
                                        if (w == activeWin) continue;
                                        if (!string.IsNullOrEmpty(step.TargetAutomationId)) targetElement = w.FindFirstDescendant(cf.ByAutomationId(step.TargetAutomationId));
                                        if (targetElement == null && !string.IsNullOrEmpty(step.TargetName)) targetElement = w.FindFirstDescendant(cf.ByName(step.TargetName));
                                        if (targetElement != null) { targetWindow = w; return true; }
                                    }
                                }

                                targetWindow = allWindows.First();
                                return true;
                            }
                            catch { return false; }
                        }, TimeSpan.FromSeconds(retrySeconds));

                        if (targetWindow == null) break;

                        targetWindow.SetForeground();

                        try
                        {
                            if (targetElement != null)
                            {
                                var rect = targetElement.BoundingRectangle;
                                FlaUIRecorder.Core.Replayer.VisualIndicator.ShowRectangle(rect);
                                targetElement.Click();
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                            }
                            else if (isWebBasedApp)
                            {
                                var rect = targetWindow.BoundingRectangle;
                                int x = (int)rect.X + step.X;
                                int y = (int)rect.Y + step.Y;
                                FlaUIRecorder.Core.Replayer.VisualIndicator.ShowClick(x, y);
                                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(x, y));
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                            }

                            // 🔥 Handle Save/Open dialogs specifically
                            if (targetWindow.ClassName == FlaUIRecorder.Core.Constants.Automation.Win32DialogClass)
                            {
                                // 🔥 FIX: Use the correct ID for the File Name box (1148 is modern, 1001 is legacy)
                                var edit = targetWindow.FindFirstDescendant(automation.ConditionFactory.ByAutomationId("1148"))
                                        ?? targetWindow.FindFirstDescendant(automation.ConditionFactory.ByAutomationId("1001"));

                                if (edit != null)
                                {
                                    edit.Click();
                                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_A);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                                }
                                if (!string.IsNullOrEmpty(step.Value))
                                {
                                    Clipboard.SetText(step.Value);
                                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_V);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_V);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                                }
                            }
                            else
                            {
                                FlaUI.Core.Input.Keyboard.Type(step.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"Type failed for {step.Value}");
                        }
                        Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                    }
                    break;

                case "Scroll":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp) && int.TryParse(step.Value, out int delta))
                    {
                        var win = currentApp.GetMainWindow(automation);
                        if (win != null)
                        {
                            win.SetForeground();
                            int x = step.X; int y = step.Y;
                            if (!step.AppPath.ToLower().Contains("chrome")) { var r = win.BoundingRectangle; x += (int)r.X; y += (int)r.Y; }
                            FlaUI.Core.Input.Mouse.MoveTo(x, y);
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                            FlaUI.Core.Input.Mouse.Scroll(delta / 120.0);
                        }
                    }
                    break;

                case "Paste":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        var win = currentApp.GetAllTopLevelWindows(automation).FirstOrDefault(w => !w.Properties.IsOffscreen.ValueOrDefault);
                        if (win != null)
                        {
                            win.SetForeground();
                            bool isExcel = step.AppPath.ToLower().Contains("excel");
                            if (isExcel && !string.IsNullOrEmpty(step.TargetName)) NavigateToCell(win, automation, step.TargetName);
                            if (!string.IsNullOrEmpty(step.Value)) { Clipboard.SetText(step.Value); Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay); }
                            if (isExcel) { FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.F2); Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ShortWait); }
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_V);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_V);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                            if (isExcel) { FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN); }
                        }
                    }
                    break;

                case "Copy":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        var win = currentApp.GetMainWindow(automation);
                        if (win != null)
                        {
                            win.SetForeground();
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                            if (!string.IsNullOrEmpty(step.Value)) { Clipboard.SetText(step.Value); Thread.Sleep(500); }
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_C);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_C);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                        }
                    }
                    break;

                case "Save":
                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_S);
                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_S);
                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ExtraLongWait);
                    break;

                case "Enter":
                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                    break;

                case "Tab":
                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.TAB);
                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ClickDelay);
                    break;

                case "DialogConfirm":
                    // 🔥 NEW: Confirm a dialog (file picker, save dialog, etc.)
                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.LongWait);
                    break;

                case "Close":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        var win = currentApp.GetMainWindow(automation);
                        if (win != null)
                        {
                            win.SetForeground();
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.ALT);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.F4);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.F4);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.ALT);
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.CloseWait);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
                        }
                        else { currentApp.Kill(); }
                    }
                    break;
            }
            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
        }
    }
}