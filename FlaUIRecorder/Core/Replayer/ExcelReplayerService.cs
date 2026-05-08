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

    private static bool IsSelf(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.ToLower();
        // Check for common process names of this app
        return p.Contains("flauirecorder") || p.Contains("vshost");
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
        ref FlaUIApp currentApp,
        bool launchIfMissing = true)
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

        // 🔥 FIX: If we are just trying to CLOSE the app, don't launch it if it's already gone!
        if (!launchIfMissing) return false;

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
            // 🔥 SAFETY: Never replay actions on our own recorder UI (prevents loops)
            if (IsSelf(step.AppPath))
            {
                Log.Warning($"[REPLAY SKIP] Skipping action on self: {step.ActionType}");
                continue;
            }

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
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_A);
                            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                            Thread.Sleep(100);
                            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.BACK);
                            Thread.Sleep(200);
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
                                        // 🔥 SPECIAL: Handle the 'Open' button in file dialogs
                                        if (step.AutomationId == "1" || step.Name?.ToLower().Contains("open") == true)
                                        {
                                            targetElement = dlg.FindFirstDescendant(cf.ByAutomationId("1"))
                                                         ?? dlg.FindFirstDescendant(cf.ByName("Open"))
                                                         ?? dlg.FindFirstDescendant(cf.ByName("&Open"));
                                            
                                            // 🔥 Wait for button to be enabled (Windows lag)
                                            if (targetElement != null)
                                            {
                                                Retry.WhileFalse(() => targetElement.Properties.IsEnabled.ValueOrDefault, TimeSpan.FromSeconds(2));
                                            }
                                        }
                                        else if (step.ControlType == "ListItem")
                                        {
                                            // 🔥 ENHANCED FILE SELECTION: Support both Names and Numeric IDs
                                            // 1. Try numeric AutomationId directly
                                            if (!string.IsNullOrEmpty(step.AutomationId))
                                                targetElement = dlg.FindFirstDescendant(cf.ByAutomationId(step.AutomationId));

                                            // 2. Try Name directly
                                            if (targetElement == null && !string.IsNullOrEmpty(step.Name))
                                                targetElement = dlg.FindFirstDescendant(cf.ByName(step.Name));

                                            // 3. Deep search fallback
                                            if (targetElement == null)
                                            {
                                                var allItems = dlg.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                                                    .Union(dlg.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))).ToList();

                                                targetElement = allItems.FirstOrDefault(el => 
                                                    el.AutomationId == step.AutomationId || 
                                                    el.Name == step.Name || 
                                                    (el.Name != null && el.Name.Contains(step.Name)));

                                                // 4. Index Fallback (if AutomationId is a number like '6')
                                                if (targetElement == null && int.TryParse(step.AutomationId, out int index) && index > 0 && index <= allItems.Count)
                                                {
                                                    targetElement = allItems[index - 1];
                                                    Log.Information($"[INDEX SELECT] Selected item at index {index}");
                                                }
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
                            int clickX = 0, clickY = 0;
                            bool isListItem = false;
                            bool elementFound = targetElement != null;

                            if (elementFound)
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
                                // 🔥 SPECIAL FALLBACK: If we're looking for 'Open' and didn't find it, just press ENTER
                                if (targetWindow.ClassName == FlaUIRecorder.Core.Constants.Automation.Win32DialogClass && 
                                   (step.Name?.ToLower().Contains("open") == true || step.AutomationId == "1"))
                                {
                                    Log.Information("[FALLBACK] Button not found, pressing ENTER to attach");
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
                                    Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.WindowFocusWait);
                                    return; 
                                }

                                Log.Information($"[XY CLICK] {step.Name} at {step.X}, {step.Y}");
                                clickX = (int)winRect.X + step.X;
                                clickY = (int)winRect.Y + step.Y;
                                FlaUIRecorder.Core.Replayer.VisualIndicator.ShowClick(clickX, clickY);
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                            }

                            FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(clickX, clickY));
                            Thread.Sleep(1000); 

                            // 🔥 Handle "Replace File?" dialogs (like the one in your screenshot)
                            try {
                                var dlg = automation.GetDesktop().FindAllChildren(automation.ConditionFactory.ByClassName("#32770")).FirstOrDefault();
                                if (dlg != null && (dlg.Name.Contains("Save") || dlg.Name.Contains("Excel"))) {
                                    var btn = dlg.FindFirstDescendant(automation.ConditionFactory.ByName("OK")) 
                                           ?? dlg.FindFirstDescendant(automation.ConditionFactory.ByName("Yes"))
                                           ?? dlg.FindFirstDescendant(automation.ConditionFactory.ByAutomationId("1"));
                                    if (btn != null) { btn.Click(); Thread.Sleep(1000); }
                                }
                            } catch {}

                            string clickedName = targetElement?.Name ?? step.Name ?? "";
                            bool isOpenClick = step.AutomationId == "1" || clickedName == "Open";
                            bool isAttachClick = clickedName == FlaUIRecorder.Core.Constants.Automation.AttachFileButton || clickedName.ToLower().Contains("attach");
                            bool isBrowseClick = clickedName == FlaUIRecorder.Core.Constants.Automation.BrowseComputerButton || clickedName.ToLower().Contains("browse");

                            if (isOpenClick)
                            {
                                Log.Information("[DIALOG] Open button clicked, waiting for upload/process...");
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.ExtraLongWait); // Increased to ExtraLongWait (8s)
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
                                    // 🔥 OUTLOOK HACK: If no dialog, it might be a dropdown. Still set flag for next click.
                                    Log.Information("[FILE PICKER] No dialog detected - assuming dropdown phase");
                                    lastClickWasAttachFile = true; 
                                    lastChromeWindowRect = new System.Drawing.Rectangle((int)winRect.X, (int)winRect.Y,
                                                                                       (int)winRect.Width, (int)winRect.Height);
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

                case "SelectFile":
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp))
                    {
                        AutomationElement targetElement = null;
                        var cf = automation.ConditionFactory;

                        Retry.WhileTrue(() =>
                        {
                            try
                            {
                                Thread.Sleep(500); // Wait for dialog to fully open
                                var desktop = automation.GetDesktop();

                                // Look for file picker dialogs - try multiple window classes
                                var dialogs = desktop.FindAllChildren(cf.ByClassName(FlaUIRecorder.Core.Constants.Automation.Win32DialogClass)).ToList();

                                if (!dialogs.Any())
                                {
                                    // Try finding any window containing files
                                    dialogs = currentApp.GetAllTopLevelWindows(automation)
                                        .Cast<AutomationElement>()
                                        .Where(w => !w.Properties.IsOffscreen.ValueOrDefault)
                                        .ToList();
                                    Log.Information($"[SELECT FILE] Found {dialogs.Count} windows in app");
                                }

                                if (dialogs.Any())
                                {
                                    // Search for exact filename match in list items
                                    foreach (var dlg in dialogs)
                                    {
                                        var listItems = dlg.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem));
                                        targetElement = listItems.FirstOrDefault(el => el.Name == step.Name);

                                        if (targetElement == null)
                                        {
                                            var dataItems = dlg.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem));
                                            targetElement = dataItems.FirstOrDefault(el => el.Name == step.Name);
                                        }

                                        if (targetElement != null)
                                        {
                                            Log.Information($"[SELECT FILE] Found in dialog: '{step.Name}'");
                                            break;
                                        }
                                    }

                                    if (targetElement != null)
                                    {
                                        Log.Information($"[SELECT FILE] Selecting: '{step.Name}'");
                                        
                                        // 🔥 Ensure the item is visible and selected
                                        try
                                        {
                                            if (targetElement.Patterns.ScrollItem.IsSupported) 
                                                targetElement.Patterns.ScrollItem.Pattern.ScrollIntoView();
                                            
                                            if (targetElement.Patterns.SelectionItem.IsSupported)
                                                targetElement.Patterns.SelectionItem.Pattern.Select();
                                            else
                                                targetElement.Focus();
                                        }
                                        catch { targetElement.Focus(); }

                                        var rect = targetElement.BoundingRectangle;
                                        FlaUIRecorder.Core.Replayer.VisualIndicator.ShowRectangle(rect);
                                        Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                                        
                                        targetElement.Click();
                                        Thread.Sleep(500); // Give UI time to update selection
                                        return true;
                                    }
                                    else
                                    {
                                        Log.Warning($"[SELECT FILE] File '{step.Name}' not found in any window - using position click");
                                        // Fallback: click at recorded position
                                        var dlgWindow = dialogs.FirstOrDefault();
                                        if (dlgWindow != null)
                                        {
                                            var rect = dlgWindow.BoundingRectangle;
                                            int x = (int)rect.X + step.X;
                                            int y = (int)rect.Y + step.Y;
                                            Log.Information($"[SELECT FILE] Fallback click at {x},{y}");
                                            FlaUIRecorder.Core.Replayer.VisualIndicator.ShowClick(x, y);
                                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.InteractionDelay);
                                            FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(x, y));
                                            return true;
                                        }
                                        return false;
                                    }
                                }
                                else
                                {
                                    Log.Warning("[SELECT FILE] No dialog detected");
                                    return false;
                                }
                            }
                            catch (Exception ex) { Log.Error(ex, $"SelectFile failed for {step.Name}"); return false; }
                        },
                        TimeSpan.FromSeconds(isWebBasedApp ? FlaUIRecorder.Core.Constants.Timeouts.WebRetrySeconds : FlaUIRecorder.Core.Constants.Timeouts.RetrySeconds));
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

                            // 🔥 Check for any save dialog (modern Office panel OR classic Win32)
                            var allWinsForType = currentApp.GetAllTopLevelWindows(automation);
                            var saveDialog = allWinsForType.FirstOrDefault(w =>
                                (w.Name != null && w.Name.ToLower().Contains("save")) ||
                                w.ClassName == "#32770");

                            if (saveDialog != null)
                            {
                                // 🔥 Modern Office "Save this file" dialog OR classic Win32 Save dialog
                                saveDialog.SetForeground();
                                Thread.Sleep(300);

                                var cfType = automation.ConditionFactory;

                                // Find filename Edit box — try Win32 IDs first, then any Edit control
                                var fileNameBox =
                                    saveDialog.FindFirstDescendant(cfType.ByAutomationId("1148")) ?? // modern Win32
                                    saveDialog.FindFirstDescendant(cfType.ByAutomationId("1001")) ?? // legacy Win32
                                    saveDialog.FindFirstDescendant(cfType.ByControlType(FlaUI.Core.Definitions.ControlType.Edit)); // modern Office panel

                                if (fileNameBox != null)
                                {
                                    FlaUIRecorder.Core.Replayer.VisualIndicator.ShowRectangle(fileNameBox.BoundingRectangle);
                                    fileNameBox.Click();
                                    Thread.Sleep(200);

                                    // 🔥 Select ALL existing text and delete it (clears default like "Book1verds")
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_A);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                                    Thread.Sleep(100);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.DELETE);
                                    Thread.Sleep(100);
                                    Log.Information("[TYPE/SAVE] Cleared existing filename");
                                }
                                else
                                {
                                    Log.Warning("[TYPE/SAVE] File name edit box not found");
                                }

                                // 🔥 Paste new filename via clipboard (avoids keyboard layout issues)
                                if (!string.IsNullOrEmpty(step.Value))
                                {
                                    Clipboard.SetText(step.Value);
                                    Thread.Sleep(100);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_V);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_V);
                                    FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                                    Thread.Sleep(200);
                                    Log.Information($"[TYPE/SAVE] Filename set to: {step.Value}");
                                }
                            }
                            else if (targetWindow.ClassName == FlaUIRecorder.Core.Constants.Automation.Win32DialogClass)
                            {
                                // 🔥 Fallback: Win32 dialog detected via targetWindow (not allWindows scan)
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
                                    Thread.Sleep(100);
                                    FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.DELETE);
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
                                // 🔥 NEW: Clear field before typing (prevents appending)
                                FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
                                FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
                                FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.KEY_A);
                                FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
                                Thread.Sleep(100);
                                FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.BACK);
                                Thread.Sleep(200);
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

                    // 🔥 Auto-dismiss "Replace file?" dialog (Excel shows OK/Cancel)
                    try
                    {
                        var cf2 = automation.ConditionFactory;
                        var allDesktopDialogs = automation.GetDesktop()
                            .FindAllChildren(cf2.ByClassName("#32770")).ToList();

                        var replaceDialog = allDesktopDialogs.FirstOrDefault(d =>
                            d.Name?.Contains("Save As") == true ||
                            d.Name?.Contains("Confirm") == true ||
                            d.Name?.Contains("Replace") == true ||
                            d.Name?.Contains("Excel") == true)
                            ?? allDesktopDialogs.FirstOrDefault(); // fallback: any dialog

                        if (replaceDialog != null)
                        {
                            var okBtn = replaceDialog.FindFirstDescendant(cf2.ByName("OK"))
                                     ?? replaceDialog.FindFirstDescendant(cf2.ByName("Yes"))
                                     ?? replaceDialog.FindFirstDescendant(cf2.ByName("Replace"))
                                     ?? replaceDialog.FindFirstDescendant(cf2.ByAutomationId("6"))  // "Yes" AutomationId
                                     ?? replaceDialog.FindFirstDescendant(cf2.ByAutomationId("1")); // "OK" AutomationId

                            if (okBtn != null)
                            {
                                Log.Information("[SAVE] Replace dialog detected — clicking OK");
                                okBtn.Click();
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.LongWait);
                            }
                            else
                            {
                                Log.Information("[SAVE] Replace dialog detected — pressing Enter fallback");
                                FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);
                                Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.LongWait);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[SAVE] Replace dialog check failed");
                    }
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
                    // 🔥 FIX: Pass launchIfMissing: false so we don't restart the app just to close it!
                    if (EnsureCurrentApp(step.AppPath, apps, automation, ref currentApp, launchIfMissing: false))
                    {
                        var win = currentApp.GetMainWindow(automation);
                        if (win != null)
                        {
                            win.SetForeground();
                            Thread.Sleep(FlaUIRecorder.Core.Constants.Timeouts.LongWait); // Give time for final actions
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