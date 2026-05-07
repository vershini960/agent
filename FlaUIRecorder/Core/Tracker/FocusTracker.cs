using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA3;

public class FocusTracker
{
    private UIA3Automation automation;
    private FocusChangedEventHandlerBase handler;

    
    public event Action<int> OnFocusChanged; // pass PID instead
    public FocusTracker(UIA3Automation automation)
    {
        this.automation = automation;
    }

    public void Start()
    {
        handler = automation.RegisterFocusChangedEvent(element =>
        {
            if (element == null) return;

            try
            {
                var pid = element.Properties.ProcessId.ValueOrDefault;
                if (pid == 0 || pid == Environment.ProcessId)
                    return;

                var controlType = element.Properties.ControlType.ValueOrDefault;
                var name = element.Properties.Name.ValueOrDefault;

                // 🔥 FILTER NOISE (CRITICAL)
                if (controlType == FlaUI.Core.Definitions.ControlType.Pane ||
                    controlType == FlaUI.Core.Definitions.ControlType.Text)
                    return;

                if (string.IsNullOrWhiteSpace(name))
                    return;

                OnFocusChanged?.Invoke(pid);
            }
            catch
            {
                // ignore invalid element
            }
        });
    }

    public void Stop()
    {
        if (handler != null)
            automation.UnregisterFocusChangedEvent(handler);
    }
}