using FlaUI.Core.AutomationElements;
using FlaUIRecorder.Core.Hooks;
using FlaUIRecorder.Core.Models;
using FlaUIRecorder.Tracking.Special;
using FlaUIRecorder.Tracking.Window;
using Serilog;
using System.Diagnostics;
using System.Windows.Forms;

public class ExcelRecorderService
{
    private FocusTracker tracker;
    private UITracker uiTracker = new UITracker();

    public List<ActionStep> Steps = new();
    private readonly object _stepsLock = new object();

    private ActionStep lastElement = null;
    private string currentApp = null;
    private string buffer = "";
    private bool isRecording = false;
    private string currentExcelCell = null;
    private bool isInDialog = false; // true when a Save/Open dialog is active
    private DateTime _lastDialogCloseTime = DateTime.MinValue;

    private readonly HashSet<int> _monitoredPids = new();

    // =========================================================
    // HELPERS
    // =========================================================
    private static bool IsSpreadsheet(string path) =>
        path != null &&
        path.ToLower().Contains("excel");

    /// <summary>
    /// Returns true for Windows shell processes that should never appear
    /// as recorded app targets (Explorer, Search, WebView, etc.).
    /// </summary>
    private static bool IsShellProcess(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var p = path.ToLower();
        return p.Contains("explorer.exe") ||
               p.Contains("searchhost") ||
               p.Contains("startmenu") ||
               p.Contains("webview") ||
               p.Contains("shellhost") ||
               p.Contains("sihost");
    }

    // =========================================================
    // START
    // =========================================================
    public void Start()
    {
        isRecording = true;
        GlobalKeyboardHook.KeyPressed += OnKeyPressed;
        GlobalKeyboardHook.Start();
        lock (_stepsLock) { Steps.Clear(); }
        _monitoredPids.Clear();
        Log.Information("Recording started");

        var automation = UIAutomationManager.Automation;
        tracker = new FocusTracker(automation);

        tracker.OnFocusChanged += (pid) =>
        {
            try
            {
                var process = Process.GetProcessById(pid);
                var path = process.MainModule.FileName;

                if (path.ToLower().Contains("explorer.exe"))
                    return;

                MonitorProcessForClose(process, path);

                if (currentApp == null)
                {
                    currentApp = path;
                    AddOpenAppStep(currentApp);
                    return;
                }

                if (path.ToLower().Contains("searchhost") ||
                    path.ToLower().Contains("webview") ||
                    path.ToLower().Contains("explorer"))
                    return;

                if (path != currentApp)
                {
                    currentApp = path;
                    AddOpenAppStep(currentApp);
                }
            }
            catch { }
        };

        tracker.Start();
        GlobalMouseHook.MouseClick += OnMouseClick;
        GlobalMouseHook.Start();
    }

    private void AddOpenAppStep(string path)
    {
        string launchPath = TranslateToLaunchPath(path);

        lock (_stepsLock)
        {
            Steps.Add(new ActionStep
            {
                ActionType = "OpenApp",
                AppPath = launchPath
            });
        }
        Log.Information($"[OPEN] {launchPath}");
    }

    private static string TranslateToLaunchPath(string path)
    {
        if (path == null) return path;

        if (path.ToLower().EndsWith("soffice.bin"))
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            return System.IO.Path.Combine(dir, "soffice.exe");
        }

        return path;
    }

    // =========================================================
    // CLOSE DETECTION
    // =========================================================
    private void MonitorProcessForClose(Process process, string path)
    {
        if (_monitoredPids.Contains(process.Id)) return;
        if (!isRecording) return;

        _monitoredPids.Add(process.Id);

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) =>
            {
                if (!isRecording) return;

                string lowerPath = path.ToLower();
                if (lowerPath.Contains("explorer.exe") ||
                    lowerPath.Contains("comdlg32") ||
                    lowerPath.Contains("oleaut32") ||
                    lowerPath.Contains("searchhost") ||
                    lowerPath.Contains("rundll32"))
                {
                    Log.Information($"[SKIP CLOSE] Ignored system/dialog process: {path}");
                    return;
                }

                if (isInDialog || (DateTime.UtcNow - _lastDialogCloseTime).TotalMilliseconds < 2500)
                {
                    // 🔥 Aggressively skip any closure (even Chrome) if we just handled a dialog
                    Log.Information($"[SKIP CLOSE] Dialog context active - ignored exit of {path}");
                    return;
                }

                lock (_stepsLock)
                {
                    Steps.Add(new ActionStep
                    {
                        ActionType = "Close",
                        AppPath = path
                    });
                }
                Log.Information($"[CLOSE] {path}");
            };
        }
        catch (Exception ex)
        {
            Log.Warning($"MonitorProcessForClose failed: {ex.Message}");
        }
    }

    // =========================================================
    // COMMIT TYPING BUFFER
    // =========================================================
    private void CommitTyping()
    {
        if (string.IsNullOrEmpty(buffer))
            return;

        if (currentApp != null && IsSpreadsheet(currentApp) && !isInDialog)
        {
            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "ExcelInput",
                    AppPath = TranslateToLaunchPath(currentApp),
                    Value = buffer,
                    TargetName = currentExcelCell ?? "A1",
                    TargetAutomationId = lastElement?.AutomationId
                });
            }
            Log.Information($"[EXCEL INPUT] Cell: {currentExcelCell}, Value: {buffer}");
        }
        else
        {
            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Type",
                    AppPath = TranslateToLaunchPath(currentApp),
                    Value = buffer,
                    TargetAutomationId = lastElement?.AutomationId,
                    TargetName = lastElement?.Name ?? "Editor",
                    TargetClassName = lastElement?.ClassName
                });
            }
            Log.Information($"[TYPE] {buffer}");
        }

        buffer = "";
    }

    private static bool LooksLikeCellAddress(string s)
    {
        s = s.Trim();
        if (s.Length > 10 || string.IsNullOrEmpty(s)) return false;
        int i = 0;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        if (i == 0 || i >= s.Length) return false;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return i == s.Length;
    }

    // =========================================================
    // MOUSE CLICK
    // =========================================================
    private void OnMouseClick(int x, int y)
    {
        if (!string.IsNullOrEmpty(buffer))
        {
            CommitTyping();
        }

        var window = ForegroundWindowTracker.GetActiveWindow();

        if (window.ExecutablePath != null &&
            window.ExecutablePath.ToLower().Contains("flauirecorder"))
            return;
        if (window.ExecutablePath != null && IsShellProcess(window.ExecutablePath))
        {
            // ✅ Allow explorer events when file dialog is active
            if (!isInDialog)
                return;

            Log.Information("[DIALOG] Allowing shell dialog interaction");
        }

        if (window.ExecutablePath != null &&
            window.ExecutablePath != currentApp &&
            !IsShellProcess(window.ExecutablePath))
        {
            currentApp = window.ExecutablePath;

            string translated = TranslateToLaunchPath(currentApp);
            bool alreadyRecorded;
            lock (_stepsLock)
            {
                alreadyRecorded = Steps.Count > 0 &&
                                  Steps[^1].ActionType == "OpenApp" &&
                                  Steps[^1].AppPath == translated;
            }
            if (!alreadyRecorded)
                AddOpenAppStep(currentApp);
        }
        else if (window.ExecutablePath != null)
        {
            currentApp = window.ExecutablePath;
        }

        var step = uiTracker.GetStepFromPoint(x, y);
        if (step == null)
            return;
        if (isInDialog)
        {
            Log.Information($"[DIALOG CONTROL] Name='{step.Name}', Type='{step.ControlType}', Class='{step.ClassName}'");
        }
        // ✅ FIX: Early filter when inside a file dialog
        if (isInDialog)
        {
            // ✅ Skip Edit/UIProperty controls (column headers, date modified, etc.)
            if (step.ControlType == "Edit" || step.ControlType == "UIProperty")
            {
                Log.Information($"[SKIP EARLY] Ignored {step.ControlType} '{step.Name}' in dialog");
                return;
            }

            if (step.ControlType == "ListItem" ||
     step.ControlType == "DataItem" ||
     step.ControlType == "TreeItem")
            {
                lock (_stepsLock)
                {
                    Steps.Add(new ActionStep
                    {
                        ActionType = "Click",
                        AppPath = TranslateToLaunchPath(currentApp),
                        AutomationId = step.AutomationId,
                        Name = step.Name,
                        ControlType = step.ControlType,
                        ClassName = step.ClassName,
                        Value = null,
                        X = step.X,
                        Y = step.Y,
                        TargetAutomationId = null,
                        TargetName = null,
                        TargetClassName = null
                    });
                }

                Log.Information($"[FILE SELECTED] '{step.Name}' ({step.ControlType})");
                return;
            }

            // ✅ Only Button (Open/Cancel/Save) passes through from a dialog
            if (step.ControlType != "Button")
            {
                Log.Information($"[SKIP EARLY] Ignored {step.ControlType} '{step.Name}' in dialog");
                return;
            }
        }

        // Detect if we entered a dialog via File Name edit box
        if (step.ControlType == "Edit" && step.Name?.Contains("File name") == true)
            isInDialog = true;

        // ✅ FIX: Detect Attach Files button — record the click FIRST, then mark dialog mode
        if (step.Name?.ToLower().Contains("attach") == true && step.ControlType == "Button")
        {
            // Record the Attach button click explicitly before entering dialog mode
            step.AppPath = TranslateToLaunchPath(currentApp);
            lock (_stepsLock) { Steps.Add(step); }
            Log.Information($"[CLICK] {step.Name} ({step.ControlType})");

            isInDialog = true;
            Log.Information("[ATTACH CLICKED] File picker opening - marking dialog mode");
            Thread.Sleep(300);
            return; // Skip the generic add at the bottom
        }

        // ✅ FIX: Detect Open button — record the click FIRST, then reset dialog state
        if (step.Name == "Open" && step.ControlType == "Button" && isInDialog)
        {
            // Record the Open button click explicitly
            step.AppPath = TranslateToLaunchPath(currentApp);
            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Click",
                    AppPath = TranslateToLaunchPath(currentApp),
                    AutomationId = step.AutomationId,
                    Name = step.Name,
                    ControlType = step.ControlType,
                    ClassName = step.ClassName,
                    X = step.X,
                    Y = step.Y
                });
            }
            Log.Information($"[CLICK] {step.Name} ({step.ControlType})");

            // THEN reset dialog state with cooldown timestamp
            isInDialog = false;
            _lastDialogCloseTime = DateTime.UtcNow;
            Log.Information("[DIALOG CLOSED] File picker dismissed via Open button");
            return; // Skip the generic add at the bottom
        }

        // ✅ HANDLE SAVE CLICK
        if (step.Name == "Save" && isInDialog)
        {
            if (!string.IsNullOrEmpty(buffer))
            {
                CommitTyping();
            }
            try
            {
                var automation = UIAutomationManager.Automation;
                var cf = automation.ConditionFactory;

                var dialog = automation.GetDesktop()
                    .FindAllChildren(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window))
                    .FirstOrDefault(w =>
                        w.Name != null &&
                        (w.Name.ToLower().Contains("save") || w.ClassName == "#32770")
                    );

                if (dialog != null)
                {
                    var fileNameBox = dialog.FindFirstDescendant(cf.ByAutomationId("1001"));
                    string fileName = fileNameBox?.AsTextBox()?.Text;

                    if (string.IsNullOrEmpty(fileName))
                        fileName = "NewFile.xlsx";

                    if (!fileName.EndsWith(".xlsx"))
                        fileName += ".xlsx";

                    string fullPath;

                    if (System.IO.Path.IsPathRooted(fileName))
                    {
                        fullPath = fileName;
                    }
                    else
                    {
                        string folder = null;

                        var toolBars = dialog.FindAllDescendants(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ToolBar));
                        foreach (var tb in toolBars)
                        {
                            if (tb.Name != null && tb.Name.StartsWith("Address: "))
                            {
                                folder = tb.Name.Substring(9).Trim();
                                break;
                            }
                        }

                        if (string.IsNullOrEmpty(folder))
                        {
                            var addressBar = dialog.FindFirstDescendant(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ComboBox));
                            if (addressBar != null && !string.IsNullOrEmpty(addressBar.Name) && addressBar.Name.Contains("\\"))
                            {
                                folder = addressBar.Name;
                            }
                        }

                        if (!string.IsNullOrEmpty(folder))
                        {
                            fullPath = System.IO.Path.Combine(folder, fileName);
                        }
                        else
                        {
                            fullPath = fileName;
                        }
                    }

                    lock (_stepsLock)
                    {
                        Steps.Add(new ActionStep
                        {
                            ActionType = "SavePath",
                            AppPath = TranslateToLaunchPath(currentApp),
                            Value = fullPath
                        });
                    }
                    buffer = "";
                    Log.Information($"[SAVE PATH] {fullPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Save capture failed: {ex.Message}");
            }

            isInDialog = false;
            _lastDialogCloseTime = DateTime.UtcNow;
            // Deliberately do NOT return — let the Save button click still be recorded
        }

        // Determine if we are clicking a spreadsheet cell
        bool isSpreadsheetCell =
            IsSpreadsheet(currentApp) &&
            !isInDialog &&
            (step.ControlType == "DataItem" || step.ControlType == "Custom" ||
             step.ControlType == "Unknown") &&
            LooksLikeCellAddress(step.Name ?? "");

        if (isSpreadsheetCell)
        {
            currentExcelCell = step.Name;
            Log.Information($"[CELL] {currentExcelCell}");
        }

        if (step.ControlType == "Button")
            lastElement = null;
        else
            lastElement = step;

        step.AppPath = TranslateToLaunchPath(currentApp);
        if (step != null)
        {
            lock (_stepsLock)
            {
                Steps.Add(step);
            }
        }

        Log.Information($"[CLICK] {step.Name} ({step.ControlType})");
    }

    // =========================================================
    // KEYBOARD
    // =========================================================
    private bool isCtrlPressed = false;
    private bool isShiftPressed = false;

    private void OnKeyPressed(string key)
    {
        // Modifier UP
        if (key == "LControlKeyUp" || key == "RControlKeyUp" || key == "ControlKeyUp")
        {
            isCtrlPressed = false;
            return;
        }

        // Modifier DOWN
        if (key == "LControlKey" || key == "RControlKey" || key == "ControlKey")
        {
            isCtrlPressed = true;
            return;
        }

        // SHIFT DOWN
        if (key == "LShiftKey" || key == "RShiftKey" || key == "ShiftKey")
        {
            isShiftPressed = true;
            return;
        }

        // SHIFT UP
        if (key == "LShiftKeyUp" || key == "RShiftKeyUp" || key == "ShiftKeyUp")
        {
            isShiftPressed = false;
            return;
        }

        // Ignore non-modifier key-up events
        if (key.EndsWith("Up"))
            return;

        // Ctrl+C
        if (isCtrlPressed && key.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            Thread.Sleep(100);
            string copiedText = null;
            try { if (Clipboard.ContainsText()) copiedText = Clipboard.GetText(); } catch { }

            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Copy",
                    AppPath = TranslateToLaunchPath(currentApp),
                    Value = copiedText
                });
            }
            Log.Information($"[COPY] {copiedText}");
            return;
        }

        // Ctrl+V
        if (isCtrlPressed && key.Equals("V", StringComparison.OrdinalIgnoreCase))
        {
            string pasteText = null;
            try { if (Clipboard.ContainsText()) pasteText = Clipboard.GetText(); } catch { }

            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Paste",
                    AppPath = TranslateToLaunchPath(currentApp),
                    Value = pasteText,
                    TargetName = currentExcelCell
                });
            }
            Log.Information($"[PASTE] {pasteText} in {currentExcelCell}");
            return;
        }

        // Ctrl+S
        if (isCtrlPressed && key.Equals("S", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(buffer))
                CommitTyping();

            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Save",
                    AppPath = TranslateToLaunchPath(currentApp)
                });
            }

            Log.Information("[SAVE]");
            return;
        }

        if (isCtrlPressed)
            return;

        // ---- Non-Ctrl keys ----
        var windowInfo = ForegroundWindowTracker.GetActiveWindow();
        var shell = WindowsShellTracker.Detect(windowInfo.ProcessName);

        if (windowInfo.ExecutablePath != null &&
            windowInfo.ExecutablePath.ToLower().Contains("flauirecorder"))
            return;

        if (shell == WindowsShellType.WindowsSearch)
            return;

        if (windowInfo.ExecutablePath == null)
            return;

        if (currentApp != null && currentApp != windowInfo.ExecutablePath)
            return;

        currentApp = windowInfo.ExecutablePath;

        // LETTERS
        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            buffer += isShiftPressed ? key.ToUpper() : key.ToLower();
            return;
        }

        // NUMBERS
        if (key.Length == 1 && char.IsDigit(key[0]))
        {
            buffer += key;
            return;
        }

        // Map top row numbers (D0-D9)
        if (key.StartsWith("D") && key.Length == 2 && char.IsDigit(key[1]))
        {
            if (!isShiftPressed)
            {
                buffer += key[1];
            }
            else
            {
                switch (key[1])
                {
                    case '1': buffer += "!"; break;
                    case '2': buffer += "@"; break;
                    case '3': buffer += "#"; break;
                    case '4': buffer += "$"; break;
                    case '5': buffer += "%"; break;
                    case '6': buffer += "^"; break;
                    case '7': buffer += "&"; break;
                    case '8': buffer += "*"; break;
                    case '9': buffer += "("; break;
                    case '0': buffer += ")"; break;
                }
            }
            return;
        }

        // Map numpad numbers (NumPad0-NumPad9)
        if (key.StartsWith("NumPad") && key.Length == 7 && char.IsDigit(key[6]))
        {
            buffer += key[6];
            return;
        }

        // SPACE
        if (key == "Space")
        {
            buffer += " ";
            return;
        }

        // SPECIAL CHARACTERS
        switch (key)
        {
            case "OemPeriod": buffer += "."; return;
            case "Oemcomma": buffer += ","; return;
            case "OemMinus": buffer += isShiftPressed ? "_" : "-"; return;
            case "Oemplus": buffer += isShiftPressed ? "+" : "="; return;
            case "OemQuestion": buffer += isShiftPressed ? "?" : "/"; return;
            case "OemSemicolon": buffer += isShiftPressed ? ":" : ";"; return;
            case "OemQuotes": buffer += isShiftPressed ? "\"" : "'"; return;
            case "OemOpenBrackets": buffer += isShiftPressed ? "{" : "["; return;
            case "OemCloseBrackets": buffer += isShiftPressed ? "}" : "]"; return;
            case "OemPipe": buffer += isShiftPressed ? "|" : "\\"; return;
        }

        if (key == "Back")
        {
            if (buffer.Length > 0)
                buffer = buffer.Substring(0, buffer.Length - 1);
            return;
        }

        if (key == "Return" || key == "Enter")
        {
            CommitTyping();

            var activeWindow = ForegroundWindowTracker.GetActiveWindow();
            bool isWebApp = activeWindow?.ExecutablePath?.ToLower().Contains("chrome") == true;

            // ✅ FIX: Improved recipient detection — check name and control type
            bool isRecipientField = lastElement?.Name?.ToLower().Contains("recipient") == true ||
                                    lastElement?.Name?.ToLower().Contains("to") == true ||
                                    lastElement?.ControlType == "ComboBox";

            bool isDialogClose = isInDialog && (lastElement?.Name?.Contains("File") == true || lastElement?.ControlType == "Edit");

            string contextName = null;
            if (isRecipientField) contextName = "RecipientConfirm";
            else if (isDialogClose) contextName = "DialogClose";

            isInDialog = false;

            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Enter",
                    AppPath = TranslateToLaunchPath(currentApp),
                    TargetName = contextName
                });
            }
            Log.Information($"[ENTER] Context: {contextName ?? "Generic"}");
            return;
        }

        if (key == "Escape")
        {
            isInDialog = false;
            buffer = "";
            return;
        }
    }

    // =========================================================
    // STOP
    // =========================================================
    public void Stop()
    {
        isRecording = false;

        if (!string.IsNullOrEmpty(buffer))
            CommitTyping();
        GlobalKeyboardHook.KeyPressed -= OnKeyPressed;
        GlobalMouseHook.MouseClick -= OnMouseClick;

        tracker?.Stop();
        GlobalMouseHook.Stop();
        GlobalKeyboardHook.Stop();

        Log.Information("Recording stopped");
    }
}