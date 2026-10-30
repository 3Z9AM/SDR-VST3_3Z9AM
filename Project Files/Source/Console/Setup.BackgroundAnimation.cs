using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // Animated spectral-area background (PNG series in the skin, Display.BackgroundAnimation.cs).
    // Controls live on Setup > Appearance > General; created before getOptions() so the
    // generic control walk saves/restores them like designer controls.
    public partial class Setup
    {
        private GroupBoxTS grpBackgroundAnimation;
        private CheckBoxTS chkBackgroundAnimation;
        private LabelTS lblBackgroundAnimationFps;
        private TrackBarTS tbBackgroundAnimationFps;
        private LabelTS lblBackgroundAnimationCache;
        private TrackBarTS tbBackgroundAnimationCache;
        private const int BgCacheStepMB = 32;   // trackbar units -> MB

        private GroupBoxTS grpBackgroundAnimationMeter;
        private CheckBoxTS chkBackgroundAnimationMeter;
        private LabelTS lblBackgroundAnimationTxMax;
        private NumericUpDownTS udBackgroundAnimationTxMax;
        private LabelTS lblBackgroundAnimationSmooth;
        private TrackBarTS tbBackgroundAnimationSmooth;

        private GroupBoxTS grpBackgroundAnimationRange;
        private LabelTS lblBackgroundAnimationMinDbm;
        private TrackBarTS tbBackgroundAnimationMinDbm;
        private LabelTS lblBackgroundAnimationMaxDbm;
        private TrackBarTS tbBackgroundAnimationMaxDbm;

        // meter mode range: RX first .. last frame in dBm (default S5 .. S9+30), TX 0 .. max W
        private volatile int _bgMeterLowDbm = -97;
        private volatile int _bgMeterHighDbm = -43;
        private volatile int _bgAnimTxMaxW = 100;

        private void initBackgroundAnimationControls()
        {
            grpBackgroundAnimation = new GroupBoxTS();
            grpBackgroundAnimation.Text = "Animated Background (PNG series)";
            grpBackgroundAnimation.Location = new Point(8, 345);
            grpBackgroundAnimation.Size = new Size(294, 62);
            grpBackgroundAnimation.Name = "grpBackgroundAnimation";

            chkBackgroundAnimation = new CheckBoxTS();
            chkBackgroundAnimation.Text = "Animate";
            chkBackgroundAnimation.Location = new Point(6, 16);
            chkBackgroundAnimation.Size = new Size(64, 17);
            chkBackgroundAnimation.Checked = true;
            chkBackgroundAnimation.Name = "chkBackgroundAnimation";
            chkBackgroundAnimation.CheckedChanged += chkBackgroundAnimation_CheckedChanged;

            lblBackgroundAnimationFps = new LabelTS();
            lblBackgroundAnimationFps.Location = new Point(70, 18);
            lblBackgroundAnimationFps.Size = new Size(72, 16);
            lblBackgroundAnimationFps.Name = "lblBackgroundAnimationFps";

            tbBackgroundAnimationFps = new TrackBarTS();
            tbBackgroundAnimationFps.AutoSize = false;
            tbBackgroundAnimationFps.Location = new Point(142, 14);
            tbBackgroundAnimationFps.Size = new Size(146, 22);
            tbBackgroundAnimationFps.Minimum = 1;
            tbBackgroundAnimationFps.Maximum = 60;
            tbBackgroundAnimationFps.SmallChange = 1;
            tbBackgroundAnimationFps.LargeChange = 5;
            tbBackgroundAnimationFps.Value = 25;
            tbBackgroundAnimationFps.TickStyle = TickStyle.None;
            tbBackgroundAnimationFps.Name = "tbBackgroundAnimationFps";
            tbBackgroundAnimationFps.ValueChanged += tbBackgroundAnimationFps_ValueChanged;

            lblBackgroundAnimationCache = new LabelTS();
            lblBackgroundAnimationCache.Location = new Point(6, 40);
            lblBackgroundAnimationCache.Size = new Size(136, 16);
            lblBackgroundAnimationCache.Name = "lblBackgroundAnimationCache";

            tbBackgroundAnimationCache = new TrackBarTS();
            tbBackgroundAnimationCache.AutoSize = false;
            tbBackgroundAnimationCache.Location = new Point(142, 36);
            tbBackgroundAnimationCache.Size = new Size(146, 22);
            tbBackgroundAnimationCache.Minimum = 1;      // 32 MB
            tbBackgroundAnimationCache.Maximum = 32;     // 1024 MB
            tbBackgroundAnimationCache.SmallChange = 1;
            tbBackgroundAnimationCache.LargeChange = 4;
            tbBackgroundAnimationCache.Value = 6;        // 192 MB
            tbBackgroundAnimationCache.TickStyle = TickStyle.None;
            tbBackgroundAnimationCache.Name = "tbBackgroundAnimationCache";
            tbBackgroundAnimationCache.ValueChanged += tbBackgroundAnimationCache_ValueChanged;
            toolTip1.SetToolTip(tbBackgroundAnimationCache,
                "RAM cache for the animation frames. When the whole PNG series fits, every frame is decoded " +
                "only once (no CPU load afterwards); otherwise frames are read from disk while playing. " +
                "One frame takes width x height x 4 bytes (1280x800 = 4 MB).");

            string tip = "Animated spectral-area background: put a numbered PNG series in the skin folder " +
                         "Console\\pnlDisplay\\ (or Console\\picDisplay\\), e.g. 000.png, 001.png ... " +
                         "Frames play in name order and loop. Speed in frames per second.";
            toolTip1.SetToolTip(chkBackgroundAnimation, tip);
            toolTip1.SetToolTip(tbBackgroundAnimationFps, tip);

            grpBackgroundAnimation.Controls.Add(chkBackgroundAnimation);
            grpBackgroundAnimation.Controls.Add(lblBackgroundAnimationFps);
            grpBackgroundAnimation.Controls.Add(tbBackgroundAnimationFps);
            grpBackgroundAnimation.Controls.Add(lblBackgroundAnimationCache);
            grpBackgroundAnimation.Controls.Add(tbBackgroundAnimationCache);
            tpAppearanceGeneral.Controls.Add(grpBackgroundAnimation);

            initBackgroundAnimationMeterControls();
            updateBackgroundAnimationLabel();
        }

        private void initBackgroundAnimationMeterControls()
        {
            grpBackgroundAnimationMeter = new GroupBoxTS();
            grpBackgroundAnimationMeter.Text = "Background by S-meter / TX power";
            grpBackgroundAnimationMeter.Location = new Point(313, 341);
            grpBackgroundAnimationMeter.Size = new Size(237, 66);
            grpBackgroundAnimationMeter.Name = "grpBackgroundAnimationMeter";

            chkBackgroundAnimationMeter = new CheckBoxTS();
            chkBackgroundAnimationMeter.Text = "Follow meter";
            chkBackgroundAnimationMeter.Location = new Point(6, 18);
            chkBackgroundAnimationMeter.Size = new Size(92, 17);
            chkBackgroundAnimationMeter.Name = "chkBackgroundAnimationMeter";
            chkBackgroundAnimationMeter.CheckedChanged += chkBackgroundAnimationMeter_CheckedChanged;

            lblBackgroundAnimationTxMax = new LabelTS();
            lblBackgroundAnimationTxMax.Text = "TX max W:";
            lblBackgroundAnimationTxMax.Location = new Point(104, 19);
            lblBackgroundAnimationTxMax.Size = new Size(62, 16);
            lblBackgroundAnimationTxMax.Name = "lblBackgroundAnimationTxMax";

            udBackgroundAnimationTxMax = new NumericUpDownTS();
            udBackgroundAnimationTxMax.Location = new Point(168, 16);
            udBackgroundAnimationTxMax.Size = new Size(60, 20);
            udBackgroundAnimationTxMax.Minimum = 1;
            udBackgroundAnimationTxMax.Maximum = 2000;
            udBackgroundAnimationTxMax.Increment = 5;
            udBackgroundAnimationTxMax.Value = 100;
            udBackgroundAnimationTxMax.Name = "udBackgroundAnimationTxMax";
            udBackgroundAnimationTxMax.ValueChanged += udBackgroundAnimationTxMax_ValueChanged;

            lblBackgroundAnimationSmooth = new LabelTS();
            lblBackgroundAnimationSmooth.Location = new Point(6, 42);
            lblBackgroundAnimationSmooth.Size = new Size(92, 16);
            lblBackgroundAnimationSmooth.Name = "lblBackgroundAnimationSmooth";

            tbBackgroundAnimationSmooth = new TrackBarTS();
            tbBackgroundAnimationSmooth.AutoSize = false;
            tbBackgroundAnimationSmooth.Location = new Point(98, 40);
            tbBackgroundAnimationSmooth.Size = new Size(134, 22);
            tbBackgroundAnimationSmooth.Minimum = 1;     // 0.1 s
            tbBackgroundAnimationSmooth.Maximum = 50;    // 5.0 s
            tbBackgroundAnimationSmooth.SmallChange = 1;
            tbBackgroundAnimationSmooth.LargeChange = 5;
            tbBackgroundAnimationSmooth.Value = 15;      // 1.5 s
            tbBackgroundAnimationSmooth.TickStyle = TickStyle.None;
            tbBackgroundAnimationSmooth.Name = "tbBackgroundAnimationSmooth";
            tbBackgroundAnimationSmooth.ValueChanged += tbBackgroundAnimationSmooth_ValueChanged;

            string tip = "The animated background follows the signal instead of looping: on RX the RX1 S-meter " +
                         "(Meter range Min = first frame, Max = last frame), on TX the forward power (0 W .. TX max W). " +
                         "The level is averaged (Smooth) and the picture moves one frame per tick toward it, " +
                         "so the Speed slider sets how fast the colour follows.";
            toolTip1.SetToolTip(chkBackgroundAnimationMeter, tip);
            toolTip1.SetToolTip(tbBackgroundAnimationSmooth, tip);
            toolTip1.SetToolTip(udBackgroundAnimationTxMax, "Forward power shown as the last frame while transmitting.");

            grpBackgroundAnimationMeter.Controls.Add(chkBackgroundAnimationMeter);
            grpBackgroundAnimationMeter.Controls.Add(lblBackgroundAnimationTxMax);
            grpBackgroundAnimationMeter.Controls.Add(udBackgroundAnimationTxMax);
            grpBackgroundAnimationMeter.Controls.Add(lblBackgroundAnimationSmooth);
            grpBackgroundAnimationMeter.Controls.Add(tbBackgroundAnimationSmooth);
            tpAppearanceGeneral.Controls.Add(grpBackgroundAnimationMeter);

            initBackgroundAnimationRangeControls();
            Display.BackgroundAnimationLevelSource = backgroundAnimationLevel;
        }

        private void initBackgroundAnimationRangeControls()
        {
            grpBackgroundAnimationRange = new GroupBoxTS();
            grpBackgroundAnimationRange.Text = "Meter range (RX)";
            grpBackgroundAnimationRange.Location = new Point(613, 208);
            grpBackgroundAnimationRange.Size = new Size(105, 100);
            grpBackgroundAnimationRange.Name = "grpBackgroundAnimationRange";

            lblBackgroundAnimationMinDbm = new LabelTS { Location = new Point(6, 16), Size = new Size(93, 14), Name = "lblBackgroundAnimationMinDbm" };
            tbBackgroundAnimationMinDbm = rangeTrackBar("tbBackgroundAnimationMinDbm", 30, _bgMeterLowDbm);
            lblBackgroundAnimationMaxDbm = new LabelTS { Location = new Point(6, 56), Size = new Size(93, 14), Name = "lblBackgroundAnimationMaxDbm" };
            tbBackgroundAnimationMaxDbm = rangeTrackBar("tbBackgroundAnimationMaxDbm", 70, _bgMeterHighDbm);

            string tip = "Follow meter: RX1 level (dBm) shown as the first frame (Min) and the last frame (Max).";
            toolTip1.SetToolTip(tbBackgroundAnimationMinDbm, tip);
            toolTip1.SetToolTip(tbBackgroundAnimationMaxDbm, tip);

            grpBackgroundAnimationRange.Controls.Add(lblBackgroundAnimationMinDbm);
            grpBackgroundAnimationRange.Controls.Add(tbBackgroundAnimationMinDbm);
            grpBackgroundAnimationRange.Controls.Add(lblBackgroundAnimationMaxDbm);
            grpBackgroundAnimationRange.Controls.Add(tbBackgroundAnimationMaxDbm);
            tpAppearanceGeneral.Controls.Add(grpBackgroundAnimationRange);
        }

        private TrackBarTS rangeTrackBar(string name, int y, int value)
        {
            TrackBarTS tb = new TrackBarTS();
            tb.AutoSize = false;
            tb.Location = new Point(2, y);
            tb.Size = new Size(99, 22);
            tb.Minimum = -140;
            tb.Maximum = 0;
            tb.SmallChange = 1;
            tb.LargeChange = 6;      // one S unit
            tb.Value = value;
            tb.TickStyle = TickStyle.None;
            tb.Name = name;
            tb.ValueChanged += tbBackgroundAnimationRange_ValueChanged;
            return tb;
        }

        // keep Min below Max (at least 6 dB) whichever slider moves
        private void tbBackgroundAnimationRange_ValueChanged(object sender, EventArgs e)
        {
            if (tbBackgroundAnimationMaxDbm == null) return;   // still building
            if (tbBackgroundAnimationMaxDbm.Value - tbBackgroundAnimationMinDbm.Value < 6)
            {
                if (sender == tbBackgroundAnimationMinDbm)
                    tbBackgroundAnimationMaxDbm.Value = Math.Min(0, tbBackgroundAnimationMinDbm.Value + 6);
                else
                    tbBackgroundAnimationMinDbm.Value = Math.Max(-140, tbBackgroundAnimationMaxDbm.Value - 6);
            }
            _bgMeterLowDbm = tbBackgroundAnimationMinDbm.Value;
            _bgMeterHighDbm = tbBackgroundAnimationMaxDbm.Value;
            lblBackgroundAnimationMinDbm.Text = "Min: " + _bgMeterLowDbm + " dBm";
            lblBackgroundAnimationMaxDbm.Text = "Max: " + _bgMeterHighDbm + " dBm";
        }

        // render thread: 0..1 level for the meter-driven background
        private double backgroundAnimationLevel()
        {
            Console c = console;
            if (c == null || c.IsDisposed) return double.NaN;

            if (Display.MOX)
                return c.alex_fwd / (double)_bgAnimTxMaxW;

            double dbm = c.RX1SignalDbm();
            double low = _bgMeterLowDbm;
            return (dbm - low) / Math.Max(1.0, _bgMeterHighDbm - low);
        }

        private void chkBackgroundAnimationMeter_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            Display.BackgroundAnimationMeterMode = chkBackgroundAnimationMeter.Checked;
        }

        private void tbBackgroundAnimationSmooth_ValueChanged(object sender, EventArgs e)
        {
            updateBackgroundAnimationLabel();
            if (initializing) return;
            Display.BackgroundAnimationSmoothing = tbBackgroundAnimationSmooth.Value / 10.0;
        }

        private void udBackgroundAnimationTxMax_ValueChanged(object sender, EventArgs e)
        {
            _bgAnimTxMaxW = Math.Max(1, (int)udBackgroundAnimationTxMax.Value);
        }

        private void updateBackgroundAnimationLabel()
        {
            lblBackgroundAnimationFps.Text = "Speed: " + tbBackgroundAnimationFps.Value + " fps";
            lblBackgroundAnimationCache.Text = "RAM cache: " + (tbBackgroundAnimationCache.Value * BgCacheStepMB) + " MB";
            if (lblBackgroundAnimationSmooth != null)
                lblBackgroundAnimationSmooth.Text = "Smooth: " + (tbBackgroundAnimationSmooth.Value / 10.0).ToString("0.0") + " s";
        }

        private void chkBackgroundAnimation_CheckedChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            Display.BackgroundAnimationEnabled = chkBackgroundAnimation.Checked;
            // stopped: show the skin's static picture again, not the last frame
            if (!chkBackgroundAnimation.Checked) console.setBackground();
        }

        private void tbBackgroundAnimationFps_ValueChanged(object sender, EventArgs e)
        {
            updateBackgroundAnimationLabel();
            if (initializing) return;
            Display.BackgroundAnimationFps = tbBackgroundAnimationFps.Value;
        }

        private void tbBackgroundAnimationCache_ValueChanged(object sender, EventArgs e)
        {
            updateBackgroundAnimationLabel();
            if (initializing) return;
            Display.BackgroundAnimationCacheMB = tbBackgroundAnimationCache.Value * BgCacheStepMB;
        }
    }
}
