using FlaUI.UIA3;

public static class UIAutomationManager
{
    public static UIA3Automation Automation { get; private set; }

    public static void Initialize()
    {
        Automation = new UIA3Automation();
    }
    public static void Dispose()
    {
        if (Automation != null)
        {
            Automation.Dispose();
            Automation = null;
        }
    }
}