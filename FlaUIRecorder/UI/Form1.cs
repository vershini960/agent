using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace FlaUIRecorder.UI  
{
    public partial class Form1 : Form
    {
        private ExcelRecorderService recorder = new ExcelRecorderService();
        private JsonStorageService storage = new JsonStorageService();
        private ExcelReplayerService replayer = new ExcelReplayerService();
        private System.Windows.Forms.Timer recordingTimer;
        private DateTime recordStartTime;
        private System.Windows.Forms.Timer replayingTimer;
        private DateTime replayStartTime;

        public Form1()
        {
            InitializeComponent();
            recordingTimer = new System.Windows.Forms.Timer();
            recordingTimer.Interval = FlaUIRecorder.Core.Constants.Timeouts.DefaultInterval;
            recordingTimer.Tick += RecordingTimer_Tick;

            replayingTimer = new System.Windows.Forms.Timer();
            replayingTimer.Interval = FlaUIRecorder.Core.Constants.Timeouts.DefaultInterval;
            replayingTimer.Tick += ReplayingTimer_Tick;
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - recordStartTime;
            lblRecordingTime.Text = $"Recording Time: {elapsed.ToString(@"hh\:mm\:ss")}";
        }

        private void ReplayingTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - replayStartTime;
            lblReplayTime.Text = $"Replay Time: {elapsed.ToString(@"hh\:mm\:ss")}";
        }
        private bool isRecording = false;
        private bool isReplaying = false;

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (isRecording) return;

            isRecording = true;

            this.WindowState = FormWindowState.Minimized;

            await Task.Delay(FlaUIRecorder.Core.Constants.Timeouts.StartDelay);

            recordStartTime = DateTime.Now;
            lblRecordingTime.Text = "Recording Time: 00:00:00";
            recordingTimer.Start();
            recorder.Start();
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            if (!isRecording) return;

            recorder.Stop();
            recordingTimer.Stop();
            isRecording = false;

            this.Invoke(() =>
            {
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            });

            storage.Save(recorder.Steps);

            MessageBox.Show($"Recording saved ({recorder.Steps.Count} steps)");
        }

        private void btnReplay_Click(object sender, EventArgs e)
        {
            if (isReplaying) return;

            // 🔥 CRITICAL: If we are recording, STOP IT before replaying.
            // Otherwise, the recorder will capture the replay, leading to a loop of steps.
            if (isRecording)
            {
                btnStop_Click(sender, e);
            }

            var steps = storage.Load(FlaUIRecorder.Core.Constants.App.DefaultStepsFile);

            if (steps == null || steps.Count == 0)
            {
                MessageBox.Show("No steps to replay");
                return;
            }

            isReplaying = true;
            button3.Enabled = false;
            replayStartTime = DateTime.Now;
            lblReplayTime.Text = "Replay Time: 00:00:00";
            replayingTimer.Start();

            var thread = new Thread(() =>
            {
                try
                {
                    replayer.Play(steps);
                }
                finally
                {
                    this.Invoke((MethodInvoker)delegate {
                        replayingTimer.Stop();
                        isReplaying = false;
                        button3.Enabled = true;
                        MessageBox.Show("Replay finished");
                    });
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private void btnLoad_Click(object sender, EventArgs e)
        {
            var steps = storage.Load(FlaUIRecorder.Core.Constants.App.DefaultStepsFile);
            // FIX: null-safe count
            MessageBox.Show($"Loaded {steps?.Count ?? 0} steps");
        }
    }
}