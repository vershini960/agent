namespace FlaUIRecorder.UI
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            button1 = new Button();
            button2 = new Button();
            button3 = new Button();
            button4 = new Button();
            lblRecordingTime = new Label();
            lblReplayTime = new Label();
            SuspendLayout();
            // 
            // lblRecordingTime
            // 
            lblRecordingTime.AutoSize = true;
            lblRecordingTime.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            lblRecordingTime.ForeColor = Color.Red;
            lblRecordingTime.Location = new Point(274, 9);
            lblRecordingTime.Name = "lblRecordingTime";
            lblRecordingTime.Size = new Size(160, 28);
            lblRecordingTime.TabIndex = 4;
            lblRecordingTime.Text = "Ready to record";
            // 
            // lblReplayTime
            // 
            lblReplayTime.AutoSize = true;
            lblReplayTime.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            lblReplayTime.ForeColor = Color.Blue;
            lblReplayTime.Location = new Point(410, 227);
            lblReplayTime.Name = "lblReplayTime";
            lblReplayTime.Size = new Size(160, 28);
            lblReplayTime.TabIndex = 5;
            lblReplayTime.Text = "Ready to replay";
            // 
            // button1
            // 
            button1.Location = new Point(274, 42);
            button1.Name = "button1";
            button1.Size = new Size(147, 29);
            button1.TabIndex = 0;
            button1.Text = "Start Recording";
            button1.UseVisualStyleBackColor = true;
            button1.Click += btnStart_Click;
            // 
            // button2
            // 
            button2.Location = new Point(274, 141);
            button2.Name = "button2";
            button2.Size = new Size(137, 29);
            button2.TabIndex = 1;
            button2.Text = "Stop Recording";
            button2.UseVisualStyleBackColor = true;
            button2.Click += btnStop_Click;
            // 
            // button3
            // 
            button3.Location = new Point(298, 227);
            button3.Name = "button3";
            button3.Size = new Size(94, 29);
            button3.TabIndex = 2;
            button3.Text = "Replay";
            button3.UseVisualStyleBackColor = true;
            button3.Click += btnReplay_Click;
            // 
            // button4
            // 
            button4.Location = new Point(298, 330);
            button4.Name = "button4";
            button4.Size = new Size(94, 29);
            button4.TabIndex = 3;
            button4.Text = "Load JSON";
            button4.UseVisualStyleBackColor = true;
            button4.Click += btnLoad_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(button4);
            Controls.Add(button3);
            Controls.Add(button2);
            Controls.Add(button1);
            Controls.Add(lblRecordingTime);
            Controls.Add(lblReplayTime);
            Name = "Form1";
            Text = "Form1";
            ResumeLayout(false);
        }

        #endregion

        private Button button1;
        private Button button2;
        private Button button3;
        private Button button4;
        private Label lblRecordingTime;
        private Label lblReplayTime;
    }
}