using FlaUI.Core.AutomationElements;
using FlaUIRecorder.Core.Hooks;
using FlaUIRecorder.Core.Models;
using FlaUIRecorder.Tracking.Special;
using FlaUIRecorder.Tracking.Window;
using Serilog;
using System.Diagnostics;
using System.Windows.Forms;

/// <summary>
/// Recorder that correctly captures Gmail and Outlook Web email workflows:
/// OpenApp → (OWA: navigate URL) → Compose → To → Type → Enter →
/// Subject → Type → Body → Type → Attach → FileSelect → Open → Send → Close
///
/// KEY DESIGN DECISIONS (derived from JSON analysis):
///
/// GMAIL field fingerprints
///   Compose  : ClassName "T-I T-I-KE L3",  Name "Compose"
///   To       : AutomationId ":ud",          ClassName "agP aFw",  ControlType ComboBox
///   Subject  : AutomationId ":qa",          ClassName "aoT",      ControlType Edit
///   Body     : AutomationId ":rr",          ClassName contains "Am aiL Al editable"
///   Attach   : AutomationId ":s9",          ClassName "wG J-Z-I e9", Name "Attach files"
///   Send     : AutomationId ":pz",          ClassName contains "T-I J-J5-Ji aoO"
///
/// OUTLOOK WEB field fingerprints
///   Address  : AutomationId "view_1012",    Name "Address and search bar"  (Omnibox)
///   To       : AutomationId "0",            ClassName contains "EditorClass", Name "To"
///   Body/Scroll: AutomationId "docking_InitVisiblePart_0", ClassName contains "owaMailComposeEditorScrollContainer"
///   Body Edit: ClassName contains "dFCbN" and "customScrollBar", Name "Message body"
///   Attach   : ClassName contains "fui-Button" AND "ms-Button",  Name "Attach file"
///   AttachDrop: AutomationId "docking_InitVisiblePart_0" AFTER attach click (Browse this computer)
///   Send     : AutomationId contains "splitButton", ClassName contains "fui-SplitButton"
/// </summary>
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

    // ── Dialog / file-picker state ───────────────────────────────────────────
    private bool isInDialog = false;
    private DateTime _lastDialogCloseTime = DateTime.MinValue;

    // ── Email-specific state ─────────────────────────────────────────────────
    // Tracks what email "zone" the user is currently in so Type steps
    // get the right TargetAutomationId / TargetName / TargetClassName
    private enum EmailZone { None, To, Subject, Body }
    private EmailZone _emailZone = EmailZone.None;

    // Outlook Web only: after clicking "Attach file" button the next
    // click on docking_InitVisiblePart_0 is "Browse this computer" — a
    // dropdown entry — and must be recorded as a plain Click, not a body
    // click.  This flag marks that state.
    private bool _outlookAttachDropdownPending = false;

    private bool _isAttaching = false;
    private readonly HashSet<int> _monitoredPids = new();

    // =========================================================
    // HELPERS
    // =========================================================

    private static bool IsChrome(string path) =>
        path != null && path.ToLower().Contains("chrome");

    /// <summary>True when the current Chrome tab is showing Gmail.</summary>
    private bool IsGmail() => _currentUrl?.Contains("mail.google") == true;

    /// <summary>True when the current Chrome tab is showing Outlook Web.</summary>
    private bool IsOutlookWeb() =>
        _currentUrl?.Contains("outlook.live") == true ||
        _currentUrl?.Contains("outlook.office") == true ||
        _currentUrl?.Contains("outlook.com") == true;

    // We track the URL typed into the Omnibox to detect Gmail vs OWA.
    // Updated every time the user types into AutomationId "view_1012".
    private string _currentUrl = null;

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

    // ── Gmail field fingerprints ─────────────────────────────────────────────
    private static bool IsGmailCompose(ActionStep s) =>
        s.ControlType == "Button" &&
        (s.ClassName?.Contains("T-I T-I-KE") == true || s.Name == "Compose");

    private static bool IsGmailToField(ActionStep s) =>
        s.AutomationId == ":ud" ||
        s.ClassName == "agP aFw" ||
        (s.ControlType == "ComboBox" && s.Name?.ToLower().Contains("to") == true);

    private static bool IsGmailSubjectField(ActionStep s) =>
        s.AutomationId == ":qa" ||
        s.ClassName == "aoT" ||
        (s.ControlType == "Edit" && s.Name?.ToLower() == "subject");

    private static bool IsGmailBodyField(ActionStep s) =>
        s.AutomationId == ":rr" ||
        (s.ClassName?.Contains("Am aiL Al editable") == true) ||
        (s.ControlType == "Edit" && s.Name?.ToLower().Contains("message body") == true);

    private static bool IsGmailAttachButton(ActionStep s) =>
        s.ControlType == "Button" && (
            s.AutomationId == ":s9" ||
            s.ClassName == "wG J-Z-I e9" ||
            s.Name?.ToLower().Contains("attach") == true);

    private static bool IsGmailSendButton(ActionStep s) =>
        s.ControlType == "Button" && (
            s.AutomationId == ":pz" ||
            s.ClassName?.Contains("T-I J-J5-Ji aoO") == true ||
            (s.Name?.ToLower().Contains("send") == true &&
             s.ClassName?.Contains("T-I") == true));

    // ── Outlook Web field fingerprints ──────────────────────────────────────
    private static bool IsOmnibox(ActionStep s) =>
        s.AutomationId == "view_1012" ||
        s.Name == "Address and search bar" ||
        s.ClassName == "OmniboxViewViews";

    private static bool IsOutlookToField(ActionStep s) =>
        (s.AutomationId == "0" && s.ClassName?.Contains("EditorClass") == true) ||
        (s.Name == "To" && s.ClassName?.Contains("EditorClass") == true);

    private static bool IsOutlookSubjectField(ActionStep s) =>
        (s.ControlType == "Edit" && s.Name?.ToLower().Contains("subject") == true) ||
        (s.AutomationId?.ToLower().Contains("subject") == true);

    /// <summary>
    /// The Outlook Web scroll container hosts BOTH body typing AND the
    /// "Browse this computer" dropdown target — context decides which.
    /// </summary>
    private static bool IsOutlookScrollContainer(ActionStep s) =>
        s.AutomationId == "docking_InitVisiblePart_0" ||
        s.ClassName?.Contains("owaMailComposeEditorScrollContainer") == true;

    private static bool IsOutlookBodyEdit(ActionStep s) =>
        (s.ClassName?.Contains("dFCbN") == true && s.ClassName?.Contains("customScrollBar") == true) ||
        (s.ControlType == "Edit" && s.Name?.ToLower().Contains("message body") == true);

    private static bool IsOutlookAttachButton(ActionStep s) =>
        s.ControlType == "Button" &&
        s.ClassName?.Contains("fui-Button") == true &&
        s.ClassName?.Contains("ms-Button") == true &&
        s.Name?.ToLower().Contains("attach") == true;

    private static bool IsOutlookSendButton(ActionStep s) =>
        s.ControlType == "Button" && (
            s.AutomationId?.Contains("splitButton") == true ||
            s.ClassName?.Contains("fui-SplitButton") == true ||
            (s.Name?.ToLower() == "send" && s.ClassName?.Contains("fui-Button") == true));

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

                if (path.ToLower().Contains("explorer.exe")) return;

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
            Steps.Add(new ActionStep { ActionType = "OpenApp", AppPath = launchPath });
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
                    return;

                // 🔥 INCREASED BUFFER: Browsers like Chrome fire many process-exit events 
                // after a dialog closes. We skip these for 15s to be safe.
                int skipBuffer = IsChrome(path) ? 15000 : 5000;

                if (isInDialog || (DateTime.UtcNow - _lastDialogCloseTime).TotalMilliseconds < skipBuffer)
                {
                    Log.Information($"[SKIP CLOSE] Dialog context active or recently closed: {path}");
                    return;
                }

                // 🔥 NEW: Specifically skip close events during the attachment-to-send window for browsers
                if (_isAttaching && IsChrome(path))
                {
                    Log.Information($"[SKIP CLOSE] Suppressing browser close during attachment phase: {path}");
                    return;
                }

                lock (_stepsLock)
                {
                    Steps.Add(new ActionStep { ActionType = "Close", AppPath = path });
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
        if (string.IsNullOrEmpty(buffer)) return;

        // ── Email typing (Gmail or Outlook Web) ─────────────────────────────
        if (IsChrome(currentApp) && _emailZone != EmailZone.None && lastElement != null)
        {
            lock (_stepsLock)
            {
                Steps.Add(new ActionStep
                {
                    ActionType = "Type",
                    AppPath = TranslateToLaunchPath(currentApp),
                    Value = buffer,
                    TargetAutomationId = lastElement.AutomationId,
                    TargetName = lastElement.Name,
                    TargetClassName = lastElement.ClassName
                });
            }
            Log.Information($"[EMAIL TYPE] Zone={_emailZone} Value='{buffer}' Target='{lastElement.Name}'");
            buffer = "";
            return;
        }

        // ── Default / Excel typing ───────────────────────────────────────────
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
        Log.Information($"[TYPE] '{buffer}'");
        buffer = "";
    }

    private static bool LooksLikeCellAddress(string s)
    {
        s = s?.Trim() ?? "";
        if (s.Length > 10 || string.IsNullOrEmpty(s)) return false;
        int i = 0;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        if (i == 0 || i >= s.Length) return false;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return i == s.Length;
    }

    // =========================================================
    // MOUSE CLICK — main routing logic
    // =========================================================
    private void OnMouseClick(int x, int y)
    {
        Log.Information($"[CLICK] X={x} Y={y} isInDialog={isInDialog}");

        // Flush any buffered typing before processing the new click
        if (!string.IsNullOrEmpty(buffer))
            CommitTyping();

        var window = ForegroundWindowTracker.GetActiveWindow();

        // Ignore clicks inside the recorder UI itself
        if (window.ExecutablePath?.ToLower().Contains("flauirecorder") == true)
            return;

        // Recover currentApp if lost
        if (currentApp == null && window.ExecutablePath != null && !IsShellProcess(window.ExecutablePath))
        {
            currentApp = window.ExecutablePath;
            Log.Information($"[RECOVER APP] {currentApp}");
        }

        // Shell processes: only allow through when a file dialog is open
        if (window.ExecutablePath != null && IsShellProcess(window.ExecutablePath))
        {
            // 🔥 AUTO-DETECT: If the window title looks like a file dialog, enable recording
            if (window.MainWindowTitle?.Contains("Open") == true || window.MainWindowTitle?.Contains("Save") == true)
            {
                if (!isInDialog) Log.Information("[AUTO-DIALOG] Detected dialog by window title");
                isInDialog = true;
            }

            if (!isInDialog) return;
            Log.Information("[DIALOG] Shell dialog interaction allowed");
        }

        // App-switch detection
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
            if (!alreadyRecorded) AddOpenAppStep(currentApp);
        }
        else if (window.ExecutablePath != null)
        {
            currentApp = window.ExecutablePath;
        }

        var step = uiTracker.GetStepFromPoint(x, y);
        if (step == null) return;

        step.AppPath = TranslateToLaunchPath(currentApp);

        Log.Information($"[CLICK RAW] Name='{step.Name}' AutoId='{step.AutomationId}' " +
                        $"Type='{step.ControlType}' Class='{step.ClassName}' " +
                        $"isInDialog={isInDialog} zone={_emailZone}");

        // ────────────────────────────────────────────────────────────────────
        // BRANCH A: We are inside a Windows file-picker dialog
        // ────────────────────────────────────────────────────────────────────
        if (isInDialog)
        {
            HandleDialogClick(step);
            return;
        }

        // ────────────────────────────────────────────────────────────────────
        // BRANCH B: Chrome app — route to email-specific handler
        // ────────────────────────────────────────────────────────────────────
        if (IsChrome(currentApp))
        {
            HandleChromeClick(step);
            return;
        }

        // ────────────────────────────────────────────────────────────────────
        // BRANCH C: Non-email, non-dialog — generic recording (Excel etc.)
        // ────────────────────────────────────────────────────────────────────
        HandleGenericClick(step);
    }

    // =========================================================
    // DIALOG HANDLER (Windows file-picker)
    // =========================================================
    private void HandleDialogClick(ActionStep step)
    {
        Log.Information($"[DIALOG CLICK] Name='{step.Name}' Type='{step.ControlType}' AutoId='{step.AutomationId}'");

        bool hasExt = false;
        try { hasExt = !string.IsNullOrEmpty(step.Name) && System.IO.Path.HasExtension(step.Name); } catch { }
        
        bool isFileItem = step.ControlType == "ListItem" || step.AutomationId == "System.ItemNameDisplay";

        // Record the step if it has a name or is a file item
        if (!string.IsNullOrEmpty(step.Name) || isFileItem)
        {
            // Use SelectFile for items that look like files, otherwise regular Click
            if (hasExt || isFileItem)
                step.ActionType = "SelectFile";
            else
                step.ActionType = "Click";

            RecordClick(step);

            // If it's the "Open" or "Save" button, mark the dialog as closing
            if (step.ControlType == "Button")
            {
                string btnNameLower = step.Name?.ToLower() ?? "";
                if (btnNameLower.Contains("open") || btnNameLower.Contains("ok") || btnNameLower.Contains("save") || step.AutomationId == "1")
                {
                    _lastDialogCloseTime = DateTime.UtcNow;
                    isInDialog = false;
                    Log.Information("[DIALOG] Marked as closed via button click");
                }
            }
        }
    }

    // =========================================================
    // CHROME / EMAIL HANDLER
    // =========================================================
    private void HandleChromeClick(ActionStep step)
    {
        // ── 1. Outlook Omnibox (address bar) ────────────────────────────────
        // Navigating to outlook.com / mail.google.com
        if (IsOmnibox(step))
        {
            _emailZone = EmailZone.None;
            lastElement = step;
            RecordClick(step);
            Log.Information("[EMAIL] Omnibox clicked — will capture URL");
            return;
        }

        // ── 2. Gmail Compose button ──────────────────────────────────────────
        if (IsGmailCompose(step))
        {
            _emailZone = EmailZone.None;
            _isAttaching = false; // Reset on new compose
            lastElement = null;
            RecordClick(step);
            Log.Information("[EMAIL] Gmail Compose clicked");
            return;
        }

        // ── 3. To field ──────────────────────────────────────────────────────
        if (IsGmailToField(step) || IsOutlookToField(step))
        {
            _emailZone = EmailZone.To;
            lastElement = step;
            RecordClick(step);
            Log.Information("[EMAIL] To field clicked");
            return;
        }

        // ── 4. Subject field (Gmail and Outlook) ─────────────────────────────
        if (IsGmailSubjectField(step) || IsOutlookSubjectField(step))
        {
            _emailZone = EmailZone.Subject;
            lastElement = step;
            RecordClick(step);
            Log.Information("[EMAIL] Subject field clicked");
            return;
        }

        // ── 5. Gmail body ────────────────────────────────────────────────────
        if (IsGmailBodyField(step))
        {
            _emailZone = EmailZone.Body;
            lastElement = step;
            RecordClick(step);
            Log.Information("[EMAIL] Gmail Body clicked");
            return;
        }

        // ── 6. Outlook body edit (dFCbN / Message body) ──────────────────────
        if (IsOutlookBodyEdit(step))
        {
            _emailZone = EmailZone.Body;
            lastElement = step;
            RecordClick(step);
            Log.Information("[EMAIL] Outlook Body Edit clicked");
            return;
        }

        // ── 7. Outlook scroll container (docking_InitVisiblePart_0) ──────────
        // This element appears in TWO contexts:
        //   a) After clicking "Attach file" → it is "Browse this computer" dropdown item
        //   b) Normal body / subject area click
        if (IsOutlookScrollContainer(step))
        {
            if (_outlookAttachDropdownPending)
            {
                // This click IS "Browse this computer" — record it as-is and open dialog
                _outlookAttachDropdownPending = false;
                RecordClick(step);
                isInDialog = true;
                Log.Information("[EMAIL] Outlook 'Browse this computer' clicked → dialog opening");
                return;
            }

            // Normal body area click — treat as body zone
            _emailZone = EmailZone.Body;
            lastElement = step;
            RecordClick(step);
            Log.Information("[EMAIL] Outlook scroll container (body area) clicked");
            return;
        }

        // ── 8. Attach button ─────────────────────────────────────────────────
        if (IsGmailAttachButton(step) || IsOutlookAttachButton(step))
        {
            _emailZone = EmailZone.None;
            _isAttaching = true; // 🔥 START ATTACHMENT PHASE
            lastElement = null;
            RecordClick(step);

            if (IsOutlookAttachButton(step))
            {
                // Outlook shows a dropdown; next click on docking_InitVisiblePart_0
                // is "Browse this computer" — flag it
                _outlookAttachDropdownPending = true;
                Log.Information("[EMAIL] Outlook Attach button clicked → dropdown pending");
            }
            else
            {
                // Gmail opens the file picker directly
                isInDialog = true;
                Log.Information("[EMAIL] Gmail Attach button clicked → dialog opening");
            }
            return;
        }

        // ── 9. Send button ───────────────────────────────────────────────────
        if (IsGmailSendButton(step) || IsOutlookSendButton(step))
        {
            _emailZone = EmailZone.None;
            _isAttaching = false; // 🔥 END ATTACHMENT PHASE
            lastElement = null;
            RecordClick(step);
            Log.Information("[EMAIL] Send button clicked");
            return;
        }

        // ── 11. Close button ────────────────────────────────────────────────
        if (step.Name == "Close" || step.AutomationId == "Close" || step.Name?.Contains("Close") == true && step.ControlType == "Button")
        {
            step.ActionType = "Close";
            RecordClick(step);
            Log.Information("[EMAIL] Close action recorded");
            return;
        }

        // ── 12. Everything else in Chrome (generic pane clicks, tab bar…) ────
        lastElement = step;
        RecordClick(step);
        Log.Information($"[EMAIL GENERIC] {step.ControlType} '{step.Name}'");
    }

    // =========================================================
    // GENERIC (non-email) HANDLER
    // =========================================================
    private void HandleGenericClick(ActionStep step)
    {
        bool isSpreadsheetCell =
            currentApp != null && currentApp.ToLower().Contains("excel") &&
            !isInDialog &&
            (step.ControlType == "DataItem" || step.ControlType == "Custom" || step.ControlType == "Unknown") &&
            LooksLikeCellAddress(step.Name ?? "");

        if (step.ControlType == "Button")
        {
            if (step.Name == "Close" || step.AutomationId == "Close")
            {
                step.ActionType = "Close";
                Log.Information("[RECORDER] Close button detected — mapping to Close action");
            }
            lastElement = null;
        }
        else
            lastElement = step;

        RecordClick(step);
        Log.Information($"[CLICK] {step.Name} ({step.ControlType}) | Action={step.ActionType}");
    }

    // =========================================================
    // RECORD CLICK HELPER
    // =========================================================
    private void RecordClick(ActionStep step)
    {
        lock (_stepsLock) { Steps.Add(step); }
    }

    // =========================================================
    // KEYBOARD
    // =========================================================
    private bool isCtrlPressed = false;
    private bool isShiftPressed = false;

    private void OnKeyPressed(string key)
    {
        // ── Modifier tracking ────────────────────────────────────────────────
        if (key == "LControlKeyUp" || key == "RControlKeyUp" || key == "ControlKeyUp")
        { isCtrlPressed = false; return; }

        if (key == "LControlKey" || key == "RControlKey" || key == "ControlKey")
        { isCtrlPressed = true; return; }

        if (key == "LShiftKey" || key == "RShiftKey" || key == "ShiftKey")
        { isShiftPressed = true; return; }

        if (key == "LShiftKeyUp" || key == "RShiftKeyUp" || key == "ShiftKeyUp")
        { isShiftPressed = false; return; }

        if (key.EndsWith("Up")) return;

        // ── Ctrl+C ───────────────────────────────────────────────────────────
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

        // ── Ctrl+V ───────────────────────────────────────────────────────────
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
                    Value = pasteText
                });
            }
            Log.Information($"[PASTE] {pasteText}");
            return;
        }

        // ── Ctrl+S ───────────────────────────────────────────────────────────
        if (isCtrlPressed && key.Equals("S", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(buffer)) CommitTyping();
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

        if (isCtrlPressed) return;

        // ── Non-Ctrl keys ────────────────────────────────────────────────────
        var windowInfo = ForegroundWindowTracker.GetActiveWindow();
        var shell = WindowsShellTracker.Detect(windowInfo.ProcessName);

        if (windowInfo.ExecutablePath?.ToLower().Contains("flauirecorder") == true) return;
        if (shell == WindowsShellType.WindowsSearch) return;
        if (windowInfo.ExecutablePath == null) return;
        if (currentApp != null && currentApp != windowInfo.ExecutablePath) return;

        currentApp = windowInfo.ExecutablePath;

        // ── Letter keys ──────────────────────────────────────────────────────
        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            // Track URL being typed in Omnibox so we know Gmail vs OWA
            if (lastElement != null && IsOmnibox(lastElement))
                _currentUrl = (_currentUrl ?? "") + (isShiftPressed ? key.ToUpper() : key.ToLower());

            buffer += isShiftPressed ? key.ToUpper() : key.ToLower();
            return;
        }

        // ── Digit keys ───────────────────────────────────────────────────────
        if (key.Length == 1 && char.IsDigit(key[0])) { buffer += key; return; }

        // D0-D9 (top-row numbers)
        if (key.StartsWith("D") && key.Length == 2 && char.IsDigit(key[1]))
        {
            if (!isShiftPressed) buffer += key[1];
            else switch (key[1])
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
            return;
        }

        // NumPad0-NumPad9
        if (key.StartsWith("NumPad") && key.Length == 7 && char.IsDigit(key[6]))
        { buffer += key[6]; return; }

        // ── Special characters ───────────────────────────────────────────────
        if (key == "Space") { buffer += " "; return; }
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
            case "Oemtilde": buffer += isShiftPressed ? "~" : "`"; return;
        }

        // ── Backspace ────────────────────────────────────────────────────────
        if (key == "Back")
        {
            if (buffer.Length > 0) buffer = buffer[..^1];
            // Also shorten the tracked URL
            if (lastElement != null && IsOmnibox(lastElement) && _currentUrl?.Length > 0)
                _currentUrl = _currentUrl[..^1];
            return;
        }

        // ── Enter / Return ───────────────────────────────────────────────────
        if (key == "Return" || key == "Enter")
        {
            CommitTyping();

            // After Enter in Omnibox — URL is committed, reset partial tracker
            if (lastElement != null && IsOmnibox(lastElement))
            {
                Log.Information($"[EMAIL] URL committed: {_currentUrl}");
                // Keep _currentUrl so IsGmail() / IsOutlookWeb() still work
            }

            bool isRecipientField =
                _emailZone == EmailZone.To ||
                lastElement?.Name?.ToLower().Contains("recipient") == true ||
                lastElement?.Name?.ToLower() == "to" ||
                lastElement?.ControlType == "ComboBox" ||
                lastElement?.ClassName?.Contains("EditorClass") == true ||
                lastElement?.ClassName == "agP aFw";

            bool isDialogClose = isInDialog &&
                (lastElement?.Name?.Contains("File") == true ||
                 lastElement?.ControlType == "Edit");

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

        // ── Escape ───────────────────────────────────────────────────────────
        if (key == "Escape")
        {
            isInDialog = false;
            _outlookAttachDropdownPending = false;
            _emailZone = EmailZone.None;
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