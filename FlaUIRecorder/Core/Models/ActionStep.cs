using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FlaUIRecorder.Core.Models
{
    public class ActionStep
    {
        public string ActionType { get; set; }   // Open, Click, Type, Close
        public string AppPath { get; set; }

        public string AutomationId { get; set; }
        public string Name { get; set; }
        public string ControlType { get; set; }

        public string ClassName { get; set; }

        public string Value { get; set; }

        public int X { get; set; }
        public int Y { get; set; }

        public string TargetAutomationId { get; set; }
        public string TargetName { get; set; }
        public string TargetClassName { get; set; }
    }
}
