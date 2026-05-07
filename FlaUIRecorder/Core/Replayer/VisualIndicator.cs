using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace FlaUIRecorder.Core.Replayer
{
    public static class VisualIndicator
    {
        public static void ShowClick(int x, int y, int durationMs = FlaUIRecorder.Core.Constants.UI.VisualIndicatorDuration)
        {
            Thread t = new Thread(() =>
            {
                Form overlay = new Form();
                overlay.FormBorderStyle = FormBorderStyle.None;
                overlay.StartPosition = FormStartPosition.Manual;
                overlay.TopMost = true;
                overlay.ShowInTaskbar = false;
                overlay.BackColor = Color.FromName(FlaUIRecorder.Core.Constants.UI.VisualIndicatorTransparencyKey);
                overlay.TransparencyKey = Color.FromName(FlaUIRecorder.Core.Constants.UI.VisualIndicatorTransparencyKey);

                int size = FlaUIRecorder.Core.Constants.UI.VisualIndicatorSize;
                overlay.Bounds = new Rectangle(x - size / 2, y - size / 2, size, size);

                overlay.Paint += (s, e) =>
                {
                    using (Pen pen = new Pen(Color.FromName(FlaUIRecorder.Core.Constants.UI.VisualIndicatorColor), 5))
                    {
                        e.Graphics.DrawRectangle(pen, 2, 2, size - 5, size - 5);
                    }
                };

                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = durationMs;
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    overlay.Close();
                };
                timer.Start();

                Application.Run(overlay);
            });
            
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        public static void ShowRectangle(System.Drawing.Rectangle rect, int durationMs = FlaUIRecorder.Core.Constants.UI.VisualIndicatorDuration)
        {
            Thread t = new Thread(() =>
            {
                Form overlay = new Form();
                overlay.FormBorderStyle = FormBorderStyle.None;
                overlay.StartPosition = FormStartPosition.Manual;
                overlay.TopMost = true;
                overlay.ShowInTaskbar = false;
                overlay.BackColor = Color.FromName(FlaUIRecorder.Core.Constants.UI.VisualIndicatorTransparencyKey);
                overlay.TransparencyKey = Color.FromName(FlaUIRecorder.Core.Constants.UI.VisualIndicatorTransparencyKey);

                // Add padding so the border draws outside or directly on the element edges
                int padding = FlaUIRecorder.Core.Constants.UI.VisualIndicatorPadding;
                overlay.Bounds = new Rectangle(
                    rect.X - padding, 
                    rect.Y - padding, 
                    rect.Width + (padding * 2), 
                    rect.Height + (padding * 2)
                );

                overlay.Paint += (s, e) =>
                {
                    using (Pen pen = new Pen(Color.FromName(FlaUIRecorder.Core.Constants.UI.VisualIndicatorColor), 4))
                    {
                        e.Graphics.DrawRectangle(pen, 2, 2, overlay.Width - 4, overlay.Height - 4);
                    }
                };

                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = durationMs;
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    overlay.Close();
                };
                timer.Start();

                Application.Run(overlay);
            });
            
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }
}
