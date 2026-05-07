namespace FlaUIRecorder.Core
{
    public static class Constants
    {
        public static class App
        {
            public const string Name = "FlaUIRecorder";
            public const string DefaultStepsFile = "steps.json";
        }

        public static class Shell
        {
            public static readonly string[] Processes = { "explorer.exe", "searchhost", "startmenu", "webview", "shellhost", "sihost" };
            public const string Explorer = "explorer.exe";
            public const string SearchHost = "searchhost";
            public const string WebView = "webview";
            public const string Taskbar = "Taskbar";
            public const string CoreComponentInputSource = "CoreComponentInputSource";
        }

        public static class Excel
        {
            public const string ProcessKeyword = "excel";
            public static readonly string[] NameBoxIds = { "Box", "NameBox", "Name Box" };
            public const string DefaultCell = "A1";
            public const string FileExtension = ".xlsx";
            public const string AddressPrefix = "Address: ";
        }

        public static class Chrome
        {
            public const string ProcessKeyword = "chrome";
            public const string OutlookKeyword = "outlook";
            public const string OlkKeyword = "olk";
            public const string DebugFlags = "--force-renderer-accessibility --remote-debugging-port=9222 --user-data-dir=\"C:\\ChromeDebugProfile\"";
            public const string LaunchFlags = "--force-renderer-accessibility --remote-debugging-port=9222 --user-data-dir=\"C:\\ChromeDebugProfile\" --start-maximized";
        }

        public static class LibreOffice
        {
            public const string SOfficeBin = "soffice.bin";
            public const string SOfficeExe = "soffice.exe";
            public const string CalcButton = "Calc Spreadsheet";
            public const string WriterButton = "Writer Document";
            public const string ImpressButton = "Impress Presentation";
            public const string SalFrame = "SALFRAME";
        }

        public static class Automation
        {
            public const string Win32DialogClass = "#32770";
            public const string SaveKeyword = "save";
            public const string ReplaceKeyword = "replace";
            public const string ConfirmKeyword = "confirm";
            public const string AlreadyExistsKeyword = "already exists";
            public const string YesButton = "Yes";
            public const string NoButton = "No";
            public const string OkButton = "OK";
            public const string FileNameEditId = "1001";
            public const string FileNameLabel = "File name";
            public const string OutlookWebAttachId = "docking_InitVisiblePart_0";
            public const string AttachFileButton = "Attach files";
            public const string BrowseComputerButton = "Browse this computer";
            public const int SplitButtonOffset = 15;
            public const int DefaultRetrySeconds = 2;
            public const int WebRetrySeconds = 5;
        }

        public static class Timeouts
        {
            public const int DefaultInterval = 1000;
            public const int StartDelay = 2000;
            public const int AppLaunchWait = 3000;
            public const int WindowFocusWait = 300;
            public const int InteractionDelay = 100;
            public const int DropdownWait = 400;
            public const int ClickDelay = 200;
            public const int LongWait = 3000;
            public const int ExtraLongWait = 8000;
            public const int PageLoadWait = 4000;
            public const int CloseWait = 1500;
            public const int ShortWait = 150;
            public const int MediumWait = 500;
            public const int RetrySeconds = 10;
            public const int WebRetrySeconds = 5;
        }

        public static class UI
        {
            public const int VisualIndicatorDuration = 400;
            public const int VisualIndicatorSize = 50;
            public const int VisualIndicatorPadding = 4;
            public const string VisualIndicatorColor = "Red";
            public const string VisualIndicatorTransparencyKey = "Magenta";
        }
    }
}
