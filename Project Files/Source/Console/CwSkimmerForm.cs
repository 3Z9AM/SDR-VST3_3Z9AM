//  CwSkimmerForm.cs
//
//  Copyright (C) 2026 Daniel Poznanski, 3Z9AM
//
//  This program is free software; you can redistribute it and/or
//  modify it under the terms of the GNU General Public License
//  as published by the Free Software Foundation; either version 2
//  of the License, or (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Console
    {
        private CwSkimmerForm _cwSkimmerForm;

        // "CW Skimmer" menu item next to VST Chains; the form is created now so a skimmer
        // left enabled starts with Thetis
        private void initCwSkimmer()
        {
            _cwSkimmerForm = new CwSkimmerForm(this);
            ToolStripMenuItem item = new ToolStripMenuItem("CW Skimmer");
            item.ForeColor = SystemColors.ControlLightLight;
            item.Click += delegate
            {
                if (_cwSkimmerForm.Visible) _cwSkimmerForm.BringToFront();
                else _cwSkimmerForm.Show(this);
            };
            menuStrip1.Items.Insert(menuStrip1.Items.IndexOf(vstToolStripMenuItem) + 1, item);

            // a green caption lights the HUD tile (NgMenuRenderer): skimmer on, audio through a VST chain, PureSignal on
            Color off = SystemColors.ControlLightLight;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += delegate
            {
                bool vst = _vstEnabled && chkPower.Checked &&
                           (chkRXVST.Checked || (chkTXVST.Checked && (MOX || chkVSTTest.Checked)));
                setTileColour(item, _cwSkimmerForm.Running ? Color.LimeGreen : off);
                setTileColour(vstToolStripMenuItem, vst ? Color.LimeGreen : off);
                setTileColour(linearityToolStripMenuItem, psform != null && psform.PSEnabled ? Color.LimeGreen : off);
            };
            t.Start();
        }

        private static void setTileColour(ToolStripItem i, Color c)
        {
            if (i.ForeColor != c) i.ForeColor = c;
        }
    }

    // Built-in CW skimmer: decodes every CW signal in the RX1 IQ (CwSkimmerEngine), lists
    // them here and puts callsigns on the panadapter as spots.
    internal sealed class CwSkimmerForm : Form
    {
        private const int IqChannel = 4;                  // waterfall_iq ring fed with RX1 IQ by cmaster.c
        private static readonly Color SpotColour = Color.FromArgb(0, 110, 160);

        private readonly Console _console;
        private readonly CheckBoxTS chkCWSkimmerEnable;
        private readonly NumericUpDownTS udCWSkimmerMinSnr;
        private readonly LabelTS lblCWSkimmerStatus;
        private readonly ListView lvCWSkimmer;
        private readonly System.Windows.Forms.Timer _uiTimer;
        private readonly bool _restoring;

        private readonly Button btnCWSkimmerRecord;
        private const int RecordSeconds = 30;

        private Thread _worker;
        private volatile bool _run;
        private volatile bool _recRequest;
        private volatile string _recStatus;
        private System.IO.FileStream _rec;                 // worker thread only
        private long _recLeft;
        private int _recRate;
        private byte[] _recBuf = new byte[0];
        private volatile CwSkimmerEngine _engine;
        private int _rate, _spots;

        public CwSkimmerForm(Console console)
        {
            _console = console;
            Text = "CW Skimmer";
            ClientSize = new Size(640, 360);
            MinimumSize = new Size(420, 200);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            chkCWSkimmerEnable = new CheckBoxTS { Name = "chkCWSkimmerEnable", Text = "Enable", Location = new Point(12, 10), AutoSize = true };
            LabelTS lblSnr = new LabelTS { Text = "Min SNR dB:", Location = new Point(90, 12), AutoSize = true };
            udCWSkimmerMinSnr = new NumericUpDownTS { Name = "udCWSkimmerMinSnr", Location = new Point(165, 9), Size = new Size(48, 20), Minimum = 6, Maximum = 30, Value = 10 };
            lblCWSkimmerStatus = new LabelTS { Location = new Point(230, 12), AutoSize = true, Text = "Off" };
            lvCWSkimmer = new ListView
            {
                Location = new Point(12, 38),
                Size = new Size(616, 310),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
            };
            lvCWSkimmer.Columns.Add("kHz", 80);
            lvCWSkimmer.Columns.Add("WPM", 45, HorizontalAlignment.Right);
            lvCWSkimmer.Columns.Add("SNR", 45, HorizontalAlignment.Right);
            lvCWSkimmer.Columns.Add("Text", 420);
            typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(lvCWSkimmer, true);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(chkCWSkimmerEnable, "Decode all CW signals in the RX1 band and spot their callsigns on the panadapter.");
            tip.SetToolTip(udCWSkimmerMinSnr, "Signals weaker than this (dB above the noise) are ignored. Lower = more weak signals, more garbage.");
            tip.SetToolTip(lvCWSkimmer, "Double-click a line to tune VFO A to that signal.");

            btnCWSkimmerRecord = new Button { Text = "Record " + RecordSeconds + " s", Location = new Point(538, 7), Size = new Size(90, 24), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnCWSkimmerRecord.Click += delegate { if (_worker != null) _recRequest = true; };
            tip.SetToolTip(btnCWSkimmerRecord, "Save " + RecordSeconds + " s of RX1 IQ to a WAV file (for tuning the decoder).");

            Controls.AddRange(new Control[] { chkCWSkimmerEnable, lblSnr, udCWSkimmerMinSnr, lblCWSkimmerStatus, btnCWSkimmerRecord, lvCWSkimmer });

            chkCWSkimmerEnable.CheckedChanged += delegate { if (!_restoring) { apply(); Common.SaveForm(this, "CWSkimmer"); } };
            udCWSkimmerMinSnr.ValueChanged += delegate
            {
                CwSkimmerEngine e = _engine;
                if (e != null) e.MinSnrDb = (double)udCWSkimmerMinSnr.Value;
                if (!_restoring) Common.SaveForm(this, "CWSkimmer");
            };
            lvCWSkimmer.DoubleClick += delegate
            {
                if (lvCWSkimmer.SelectedItems.Count == 1)
                    _console.VFOAFreq = (long)lvCWSkimmer.SelectedItems[0].Tag / 1e6;
            };
            FormClosing += (s, e) =>
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Common.SaveForm(this, "CWSkimmer");
                Hide();
            };

            _uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _uiTimer.Tick += delegate { refreshList(); };
            _uiTimer.Start();

            _restoring = true;
            Common.RestoreForm(this, "CWSkimmer", false);
            _restoring = false;
            apply();
        }

        internal bool Running => _worker != null;

        private void apply()
        {
            if (chkCWSkimmerEnable.Checked && _worker == null)
            {
                // spots are drawn only with Setup "Show TCI Spots" on (off by default)
                if (!Display.ShowTCISpots && !_console.IsSetupFormNull) _console.SetupForm.ShowTCISpots = true;
                Display.ExactGpuNative.CM_WaterfallIQ_Init(IqChannel, 1 << 20);
                Display.ExactGpuNative.CM_WaterfallIQ_SetEnabled(IqChannel, 1);
                _run = true;
                _worker = new Thread(work) { IsBackground = true, Name = "CW Skimmer", Priority = ThreadPriority.BelowNormal };
                _worker.Start();
            }
            else if (!chkCWSkimmerEnable.Checked && _worker != null)
            {
                _run = false;
                _worker.Join(1000);
                _worker = null;
                _engine = null;
                Display.ExactGpuNative.CM_WaterfallIQ_SetEnabled(IqChannel, 0);
                lvCWSkimmer.Items.Clear();
            }
        }

        private void work()
        {
            const int Block = 65536;
            float[] i = new float[Block], q = new float[Block];
            long lastRef = long.MinValue;
            int rate = 0;
            bool resync = true;

            while (_run)
            {
                Thread.Sleep(20);
                try
                {
                    int r = cmaster.GetInputRate(0, 0);
                    long refHz = Display.RX1IqOffsetToHz(0);
                    int avail = Display.ExactGpuNative.CM_WaterfallIQ_Available(IqChannel);

                    // TX, a new rate or a retune (centre, CW offset, RIT): the queued samples and
                    // the decoder's per-bin state belong to another frequency - start over
                    if (Audio.MOX || r <= 0) { Display.ExactGpuNative.CM_WaterfallIQ_Skip(IqChannel, avail); resync = true; stopRecording("stopped by TX"); continue; }
                    if (resync || r != rate || refHz != lastRef)
                    {
                        stopRecording("stopped by retune");
                        Display.ExactGpuNative.CM_WaterfallIQ_Skip(IqChannel, avail);
                        if (r != rate || _engine == null)
                        {
                            CwSkimmerEngine e = new CwSkimmerEngine(r);
                            e.MinSnrDb = (double)udCWSkimmerMinSnr.Value;
                            e.SpotFound += onSpot;
                            _engine = e;
                            rate = r;
                            _rate = r;
                        }
                        else _engine.Reset();
                        lastRef = refHz;
                        resync = false;
                        continue;
                    }

                    if (_recRequest) { _recRequest = false; startRecording(rate, lastRef); }

                    CwSkimmerEngine eng = _engine;
                    while (avail > 0 && _run)
                    {
                        int n = Display.ExactGpuNative.CM_WaterfallIQ_Get(IqChannel, Math.Min(avail, Block), i, q);
                        if (n <= 0) break;
                        eng.Process(q, i, n);                   // same I/Q swap as the GPU waterfall
                        if (_rec != null) writeRecording(i, q, n);
                        avail -= n;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("CW skimmer: " + ex.Message);
                    stopRecording("error: " + ex.Message);
                    resync = true;
                }
            }
            stopRecording("stopped");
        }

        // Raw RX1 IQ as a stereo float WAV (left = I, right = Q, as read from the ring; the
        // decoder gets them swapped). Name carries the RF frequency of the IQ centre.
        private void startRecording(int rate, long centreHz)
        {
            stopRecording(null);
            string path = System.IO.Path.Combine(_console.AppDataPath,
                "CWSkimmer_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + centreHz + "Hz_" + rate + ".wav");
            _rec = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            _rec.Write(new byte[44], 0, 44);                    // header written on close
            _recLeft = (long)rate * RecordSeconds;
            _recRate = rate;
            _recStatus = "Recording... " + path;
        }

        private void writeRecording(float[] i, float[] q, int n)
        {
            int m = (int)Math.Min(n, _recLeft);
            if (_recBuf.Length < m * 8) _recBuf = new byte[m * 8];
            for (int k = 0; k < m; k++)
            {
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, k * 8, 4), i[k]);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, k * 8 + 4, 4), q[k]);
            }
            _rec.Write(_recBuf, 0, m * 8);
            _recLeft -= m;
            if (_recLeft <= 0) stopRecording("saved");
        }

        private void stopRecording(string why)
        {
            System.IO.FileStream f = _rec;
            if (f == null) return;
            _rec = null;
            try
            {
                int rate = _recRate;
                uint data = (uint)(f.Length - 44);
                System.IO.BinaryWriter w = new System.IO.BinaryWriter(f);
                f.Seek(0, System.IO.SeekOrigin.Begin);
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)3); w.Write((short)2); w.Write(rate); w.Write(rate * 8); w.Write((short)8); w.Write((short)32);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data);
                w.Flush();
                if (why != null) _recStatus = "Recording " + why + " (" + data / 8 / Math.Max(1, rate) + " s): " + f.Name;
            }
            finally { f.Dispose(); }
        }

        private void onSpot(CwSkimmerEngine.Spot s)
        {
            long hz = Display.RX1IqOffsetToHz(s.OffsetHz);
            SpotManager2.AddSpot(s.Call, DSPMode.CWU, hz, SpotColour, "",
                new SpotManager2.JsonSpotData { Spotter = "SKIMMER", Comment = "CW " + s.Wpm + " WPM " + s.SnrDb + " dB" });
            Interlocked.Increment(ref _spots);
            AoAAutoCal.OnSpot(s.Call, s.OffsetHz);
        }

        private void refreshList()
        {
            CwSkimmerEngine e = _engine;
            if (!chkCWSkimmerEnable.Checked || e == null)
            {
                lblCWSkimmerStatus.Text = chkCWSkimmerEnable.Checked ? "Starting..." : "Off";
                Display.CwDecodeText = null;
                return;
            }
            CwSkimmerEngine.ChannelInfo[] ch = e.Channels();
            lblCWSkimmerStatus.Text = (_rate / 1000) + " kHz IQ, " + ch.Length + " signals, " + _spots + " spots" + (Audio.MOX ? " (paused - TX)" : "");
            Text = _recStatus != null ? "CW Skimmer - " + _recStatus : "CW Skimmer";
            btnCWSkimmerRecord.Enabled = _rec == null && !_recRequest;

            // the signal under VFO A (within 80 Hz) goes to the panadapter text line
            long vfo = (long)Math.Round(_console.VFOAFreq * 1e6);
            string under = null;
            long best = 80;
            foreach (CwSkimmerEngine.ChannelInfo c in e.Channels(false))
            {
                long d = Math.Abs(Display.RX1IqOffsetToHz(c.OffsetHz) - vfo);
                if (d <= best) { best = d; under = c.Wpm + " WPM  " + (c.Text.Length > 60 ? c.Text.Substring(c.Text.Length - 60) : c.Text); }
            }
            Display.CwDecodeText = under;
            if (!Visible) return;

            object selected = lvCWSkimmer.SelectedItems.Count == 1 ? lvCWSkimmer.SelectedItems[0].Tag : null;
            lvCWSkimmer.BeginUpdate();
            lvCWSkimmer.Items.Clear();
            foreach (CwSkimmerEngine.ChannelInfo c in ch.OrderBy(x => x.OffsetHz))
            {
                long hz = Display.RX1IqOffsetToHz(c.OffsetHz);
                string text = c.Text.Length > 70 ? c.Text.Substring(c.Text.Length - 70) : c.Text;
                ListViewItem it = new ListViewItem(new[] { (hz / 1000.0).ToString("F2"), c.Wpm.ToString(), c.SnrDb.ToString(), text }) { Tag = hz };
                lvCWSkimmer.Items.Add(it);
                if (selected != null && Math.Abs((long)selected - hz) < 50) it.Selected = true;
            }
            lvCWSkimmer.EndUpdate();
        }
    }
}
