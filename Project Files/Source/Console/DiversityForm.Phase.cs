//  DiversityForm.Phase.cs
//
//  Diversity: phase meter, per-section diversity controls (max-SINR, clean, AUTO per bin), recording
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
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace Thetis
{
    // Diversity phase meter: phase of RX2 against RX1 and their coherence (0..1) inside the
    // RX1 filter, drawn on the radar as an arrow (angle = phase, length = coherence).
    // Red dot: where the radar handle should go to null the local noise (RX1 main antenna,
    // RX2 noise antenna), from the noise floor around the RX1 filter over a few seconds.
    // ChannelMaster/sync.c interleaves the two IQ streams sample by sample into ring 5.
    public partial class DiversityForm
    {
        private const int PhaseRing = 5;
        private const double PhaseBinHz = 47, PhaseAvgSeconds = 0.3, NullAvgSeconds = 3, NullMinCoherence = 0.7;
        private volatile bool _phaseRun;
        private double _phaseRad, _phaseCoh, _noiseHr, _noiseHi, _noiseCoh;
        private long _phaseStamp;                           // Environment.TickCount64 of the last result
        private volatile DiversityPhaseWaterfall _phaseWf;  // open phase waterfall or null
        // phase views (3D, waterfall): FFT 8x finer than the meter (~6 Hz bins) slid by one meter
        // frame (87.5% overlap), per-bin average short enough to follow the waterfall
        private const int PhaseViewFine = 4;
        private const double PhaseViewAvgSeconds = 0.3;

        private void startPhaseMeter()
        {
            var btn = new ButtonTS { Text = "Phase WF", Location = new Point(6, 1), Size = new Size(70, 20) };
            toolTip1.SetToolTip(btn, "Phase waterfall +-25 kHz around VFO A: colour = phase RX2-RX1, brightness = coherence");
            btn.Click += delegate
            {
                if (_phaseWf != null) { _phaseWf.Activate(); return; }
                var wf = new DiversityPhaseWaterfall();
                wf.FormClosed += delegate { _phaseWf = null; };
                _phaseWf = wf;
                wf.Show(this);
            };
            Controls.Add(btn);

            // named so Common.RestoreForm (after this in the constructor) keeps it
            var wfp = new ComboBoxTS { Name = "comboWaterfallPhase", DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(82, 1), Size = new Size(76, 21), DropDownWidth = 90 };
            wfp.Items.AddRange(new object[] { "WF off", "WF phase", "WF Δphase" });
            wfp.SelectedIndex = 0;
            toolTip1.SetToolTip(wfp, "GPU waterfall RX1 (brightness = level, grey = no coherence):\n" +
                "phase - colour = phase RX2-RX1\n" +
                "Δphase - colour = how fast that phase turns: green = steady (local noise, groundwave - what null / max-SINR can take out),\n" +
                "red / blue = turning one way or the other (skywave), full colour at half a turn per second");
            wfp.SelectedIndexChanged += delegate
            {
                Display.WaterfallPhaseRate = wfp.SelectedIndex == 2;
                Display.WaterfallPhaseView = wfp.SelectedIndex > 0;
            };
            Controls.Add(wfp);
            FormClosed += delegate { Display.WaterfallPhaseView = false; };

            var inv = new CheckBoxTS { Name = "chkPhaseRx1Rx2", Text = "RX1-RX2", AutoSize = true, Location = new Point(162, 3), UseVisualStyleBackColor = true };
            toolTip1.SetToolTip(inv, "Phase views (WF phase, Phase WF, 3D): colour = phase RX1-RX2 instead of RX2-RX1");
            inv.CheckedChanged += delegate { Display.PhaseViewRx1Lead = inv.Checked; };
            Controls.Add(inv);

            // per-section diversity (wdsp/secdiv.c): every control in the radar's corners, outside the circle (user: room
            // there; the strip above RX Source was full - mode cut short, Rec under On Top); named so Common.RestoreForm keeps them
            int rw = picRadar.Width, rh = picRadar.Height;
            var sec = new ComboBoxTS { Name = "comboSectionDiv", DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(2, 2), Size = new Size(84, 21), DropDownWidth = 130 };
            sec.Items.AddRange(SectionModeItems);
            _secCombo = sec;
            sec.SelectedIndex = 0;
            toolTip1.SetToolTip(sec, "Diversity per frequency section instead of the radar weight (Enable + RX1+RX2 mix):\n" +
                "MRC - RX2 phased onto RX1 and weighted by its signal share\nco-phase - RX2 phased onto RX1 and raised to its level in every section (antennas of similar strength)\nnull - RX2 as a noise antenna: its noise subtracted from RX1 per section\nmax-SINR - per section the best signal to noise+interference (acts as MRC, null or co-phase as the noise needs), background stays at the RX1 noise floor\nadds one FFT of delay (~21 ms)");
            sec.SelectedIndexChanged += delegate { _secMode = sec.SelectedIndex; };
            picRadar.Controls.Add(sec);
            // extra for chosen testers (activation code); off = the radar weight as before
            VisibleChanged += delegate { sec.Enabled = ActivationCode.Has(ActivationFeature.SectionDiversity); };
            var secN = new NumericUpDownTS { Name = "udSectionDiv", Minimum = 1, Maximum = 60, Value = 20, Location = new Point(2, 26), Size = new Size(42, 20) };
            toolTip1.SetToolTip(secN, "Sections across the RX1 filter (1 = one weight; bins are ~47 Hz, fine ~12 Hz)");
            secN.ValueChanged += delegate { _secSections = (int)secN.Value; };
            picRadar.Controls.Add(secN);

            var dots = new CheckBoxTS { Name = "chkSectionDots", Text = "dots", AutoSize = true, Checked = true, BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right };
            toolTip1.SetToolTip(dots, "Per-section weights on the radar (also the console mini radar): one dot per filter bin");
            dots.CheckedChanged += delegate { _secDots = dots.Checked; picRadar.Invalidate(); };
            picRadar.Controls.Add(dots);
            dots.Location = new Point(rw - dots.PreferredSize.Width - 2, 2);
            var secF = new CheckBoxTS { Name = "chkSectionDivFine", Text = "fine", AutoSize = true, BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right };
            toolTip1.SetToolTip(secF, "FFT 4x longer: ~12 Hz bins and 4x the sections (60 = every bin);\n" +
                "offline +0.3..1.1 dB S/N with max-SINR, but ~85 ms of delay instead of ~21 ms");
            secF.CheckedChanged += delegate { _secFine = secF.Checked ? 1 : 0; };
            picRadar.Controls.Add(secF);
            secF.Location = new Point(rw - secF.PreferredSize.Width - 2, 26);

            var rec = new ButtonTS { Text = "Rec 30 s", Size = new Size(68, 20), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            toolTip1.SetToolTip(rec, "Record 30 s of RX1+RX2 IQ (4-channel float WAV in the data folder) for tools/divtest\n" +
                "Shift + click: 5 min around the RX1 filter, 24 kHz (~115 MB)");
            rec.Click += delegate { _recLong = (ModifierKeys & Keys.Shift) == Keys.Shift; _recRequest = true; };
            picRadar.Controls.Add(rec);
            rec.Location = new Point(rw - rec.Width - 2, rh - rec.Height - 2);
            FormClosed += delegate { _secMode = 0; };

            Display.ExactGpuNative.CM_WaterfallIQ_Init(PhaseRing, 1 << 20);
            Display.ExactGpuNative.CM_WaterfallIQ_SetEnabled(PhaseRing, 1);
            _phaseRun = true;
            new Thread(phaseWork) { IsBackground = true, Name = "Diversity phase", Priority = ThreadPriority.BelowNormal }.Start();
            FormClosed += delegate
            {
                _phaseRun = false;
                Display.ExactGpuNative.CM_WaterfallIQ_SetEnabled(PhaseRing, 0);
            };
        }

        private void phaseWork()
        {
            int rate = 0;
            DiversityCross dc = null;
            float[] bi = null, bq = null;
            DiversityCross fine = null;
            float[] fbi = null, fbq = null;                              // sliding window, 2*fine.N
            double[] cr3 = null, ci3 = null, p13 = null, p23 = null;   // phase views, all fine bins
            long count3 = 0;
            double[] rrr = null, rri = null, rpm = null;       // WF phase rate view
            long dCentre = long.MinValue;
            long lastPaint = 0;

            while (_phaseRun)
            {
                Thread.Sleep(20);
                try
                {
                    int r = cmaster.GetInputRate(0, 0);
                    if (r <= 0) continue;
                    if (r != rate)
                    {
                        rate = r;
                        dc = new DiversityCross(rate, PhaseBinHz, PhaseAvgSeconds, NullAvgSeconds);
                        bi = new float[2 * dc.N]; bq = new float[2 * dc.N];
                        fine = new DiversityCross(rate, dc.BinHz / PhaseViewFine, PhaseAvgSeconds, NullAvgSeconds);
                        fbi = new float[2 * fine.N]; fbq = new float[2 * fine.N];
                        cr3 = new double[fine.N]; ci3 = new double[fine.N]; p13 = new double[fine.N]; p23 = new double[fine.N];
                        count3 = 0;
                    }

                    // a ring overflow may have cut a pair in half: drop everything and realign
                    if (Display.ExactGpuNative.CM_WaterfallIQ_DroppedSamples(PhaseRing) != 0)
                    {
                        stopRecording("stopped: samples dropped");
                        Display.ExactGpuNative.CM_WaterfallIQ_ResetDropped(PhaseRing);
                        Display.ExactGpuNative.CM_WaterfallIQ_Skip(PhaseRing, Display.ExactGpuNative.CM_WaterfallIQ_Available(PhaseRing));
                        continue;
                    }

                    pushSections(rate);

                    bool fed = false;
                    while (_phaseRun && Display.ExactGpuNative.CM_WaterfallIQ_Available(PhaseRing) >= 2 * dc.N)
                    {
                        Display.ExactGpuNative.CM_WaterfallIQ_Get(PhaseRing, 2 * dc.N, bi, bq);
                        Display.RX1FilterIqOffsets(out int lo, out int hi);
                        if (_recRequest) { _recRequest = false; startRecording(rate, lo, hi); }
                        if (_rec != null) writeRecording(bi, bq, 2 * dc.N, lo, hi);
                        dc.Feed(bi, bq, lo, hi);
                        _phaseWf?.Feed(dc);
                        if (Display.PhaseViewWanted)
                        {
                            // slide the fine window by this frame; full after PhaseViewFine frames
                            int keep = 2 * (fine.N - dc.N);
                            Array.Copy(fbi, 2 * dc.N, fbi, 0, keep); Array.Copy(bi, 0, fbi, keep, 2 * dc.N);
                            Array.Copy(fbq, 2 * dc.N, fbq, 0, keep); Array.Copy(bq, 0, fbq, keep, 2 * dc.N);
                            if (++count3 >= PhaseViewFine)
                            {
                                fine.Feed(fbi, fbq, lo, hi);
                                // one hop = one meter frame
                                double a3 = Math.Max(1 / dc.BinHz / PhaseViewAvgSeconds, 1.0 / (count3 - PhaseViewFine + 1));
                                fine.AverageBins(0, a3, cr3, ci3, p13, p23);
                                Display.SetPhaseSpectrum(fine.BinHz, cr3, ci3, p13, p23);
                                if (Display.WaterfallPhaseView && Display.WaterfallPhaseRate)
                                {
                                    // retuned: the old frames and means belong to other frequencies
                                    long cen = Display.RX1IqOffsetToHz(0);
                                    if (rrr == null || rrr.Length != fine.N || cen != dCentre)
                                    {
                                        rrr = new double[fine.N]; rri = new double[fine.N]; rpm = new double[fine.N];
                                        dCentre = cen;
                                        fine.ResetPhaseRate();
                                    }
                                    // one hop = one meter frame = 1 / dc.BinHz s
                                    fine.AveragePhaseRate(PhaseViewFine, dc.BinHz * PhaseViewAvgSeconds, rrr, rri, rpm);
                                    Display.SetPhaseRateSpectrum(fine.BinHz, rrr, rri, rpm);
                                }
                                else rrr = null;
                            }
                        }
                        else count3 = 0;
                        fed = true;
                    }
                    if (fed && dc.Valid)
                    {
                        _phaseRad = dc.Phase;
                        _phaseCoh = dc.Coherence;
                        _phaseStamp = Environment.TickCount64;
                        if (!dc.NoiseRatio(out _noiseHr, out _noiseHi, out _noiseCoh)) _noiseCoh = 0;
                    }

                    if (Environment.TickCount64 - lastPaint >= 100 && IsHandleCreated && !IsDisposed)
                    {
                        lastPaint = Environment.TickCount64;
                        if (_secMode > 0)
                        {
                            var w = new double[3 * SectionWeightsMax];
                            _secWeights = Tuple.Create(w, WDSP.GetEXTDIVSectionsWeights(w, SectionWeightsMax));
                        }
                        else _secWeights = null;
                        BeginInvoke(new Action(() => picRadar.Invalidate()));   // also repaints the console mini radar
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Diversity phase: " + ex.Message);
                    stopRecording("error: " + ex.Message);
                }
            }
            stopRecording("stopped");
            pushSections(rate);                                 // _secMode is 0 once the form closed
        }

        private volatile int _secMode, _secSections = 20, _secFine, _secFineSent = -1, _secClean, _secCleanSent = -1;
        private ComboBox _secCombo;

        // also offered in the console's Audio drawer (Console.NewGUI.cs)
        public static readonly object[] SectionModeItems = { "Sections off", "Sections MRC", "Sections co-phase", "Sections null", "Sections max-SINR" };
        public int SectionMode
        {
            get => _secCombo.SelectedIndex;
            set { if (value >= 0 && value < _secCombo.Items.Count) _secCombo.SelectedIndex = value; }
        }
        // max-SINR clean share 0..100 % (the DSP drawer slider on the console)
        public int SectionClean
        {
            get => _secClean;
            set { _secClean = Math.Max(-1, Math.Min(100, value)); if (_secClean >= 0) _secCleanPct = _secClean; }     // -1 = auto per bin
        }
        public int SectionCleanPct => _secCleanPct;         // the manual share, kept while auto is on
        // auto: how readily a bin counts as station (50 = default thresholds, more = clean sooner)
        public int SectionCleanSens
        {
            get => _secSens;
            set => _secSens = Math.Max(0, Math.Min(100, value));
        }
        // mean clean share auto gave the filter bins in the last frame, 0..1 (-1 = off)
        public double SectionCleanShare => WDSP.GetEXTDIVSectionsCleanShare();
        private volatile int _secCleanPct, _secSens = 50, _secSensSent = -1;
        private int _secSent = -1, _secSentN, _secSentRate, _secSentLo, _secSentHi;
        private const double SectionTau = 0.2;              // weight averaging, s (offline: 0.1-0.4 alike)

        private const int SectionFreezeAfterTxMs = 500;     // relays, RX return ramp, AGC recovery
        private long _secTxEnd;
        private int _secFrozen = -1;
        private long _secCentre;

        private void pushSections(int rate)
        {
            // learning stops during TX and for a moment after it; the weights from before TX stay
            long now = Environment.TickCount64;
            if (Audio.MOX) _secTxEnd = now;
            int frz = now - _secTxEnd < SectionFreezeAfterTxMs ? 1 : 0;
            if (frz != _secFrozen) { _secFrozen = frz; WDSP.SetEXTDIVSectionsFreeze(frz); }

            // the IQ centre moved (tuning without CTUN, band change; smooth tuning too): the weights learn
            // again once it stops. A VFO move inside the span (CTUN) keeps them - the bins hold the same RF.
            long centre = Display.RX1IqOffsetToHz(0);
            if (centre != _secCentre) { _secCentre = centre; WDSP.SetEXTDIVSectionsReset(); }

            if (_secFine != _secFineSent) { _secFineSent = _secFine; WDSP.SetEXTDIVSectionsFine(_secFine); }
            if (_secClean != _secCleanSent) { _secCleanSent = _secClean; WDSP.SetEXTDIVSectionsClean(_secClean); }
            if (_secSens != _secSensSent) { _secSensSent = _secSens; WDSP.SetEXTDIVSectionsCleanSens(_secSens); }

            Display.RX1FilterIqOffsets(out int lo, out int hi);
            int m = _phaseRun && ActivationCode.Has(ActivationFeature.SectionDiversity) ? _secMode : 0, n = _secSections;   // locked: radar weight
            if (m == _secSent && n == _secSentN && rate == _secSentRate && lo == _secSentLo && hi == _secSentHi) return;
            _secSent = m; _secSentN = n; _secSentRate = rate; _secSentLo = lo; _secSentHi = hi;
            WDSP.SetEXTDIVSections(m, n, SectionTau, rate, -hi, -lo);    // panadapter frame Q + jI -> native I + jQ mirrors
        }

        // Raw ring 5 as a 4-channel float WAV: I1, Q1, I2, Q2 as read (the panadapter frame is Q + jI).
        // Name: centre RF Hz, rate, VFO A and RX1 filter edges as IQ offsets - all tools/divtest needs.
        private const int RecordSeconds = 30, LongRecordSeconds = 300, LongRecordRate = 24000;
        private volatile bool _recRequest, _recLong;
        // long recording: decimated to LongRecordRate around the filter (DiversityDecimator); null = raw ring as read
        private DiversityDecimator _recDecim;
        private System.IO.FileStream _rec;
        private long _recLeft;
        private int _recRate, _recLo, _recHi, _recVfo;
        private byte[] _recBuf = new byte[0];

        private void startRecording(int rate, int lo, int hi)
        {
            stopRecording(null);
            _recVfo = Display.RX1VfoIqOffset();
            bool lng = _recLong && rate % LongRecordRate == 0;
            int dec = lng ? rate / LongRecordRate : 1;
            // name in the shifted frame, divtest reads it as is; shortcut: its IQ DC guess (-vfo) is then off by sh,
            // the occupied-bin check of divtest O drops the spur anyway - put sh in the name if that ever matters
            int sh = lng ? (lo + hi) / 2 : 0;
            string path = System.IO.Path.Combine(console.AppDataPath, "Diversity_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                + "_" + Display.RX1IqOffsetToHz(sh) + "Hz_" + rate / dec + "_vfo" + (_recVfo - sh) + "_f" + (lo - sh) + "_" + (hi - sh) + ".wav");
            _rec = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            _rec.Write(new byte[44], 0, 44);                    // header written on close
            _recLeft = (long)(rate / dec) * (lng ? LongRecordSeconds : RecordSeconds);
            _recRate = rate / dec; _recLo = lo; _recHi = hi;
            _recDecim = lng ? new DiversityDecimator(rate, dec, sh) : null;
            recStatus("recording...");
        }

        // n interleaved values = n/2 sample pairs
        private void writeRecording(float[] bi, float[] bq, int n, int lo, int hi)
        {
            if (Audio.MOX) { stopRecording("stopped by TX"); return; }
            if (lo != _recLo || hi != _recHi || Display.RX1VfoIqOffset() != _recVfo) { stopRecording("stopped by retune"); return; }
            if (_recDecim != null) { writeDecimated(bi, bq, n); return; }
            int m = (int)Math.Min(n / 2, _recLeft);
            if (_recBuf.Length < m * 16) _recBuf = new byte[m * 16];
            for (int k = 0; k < m; k++)
            {
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, k * 16, 4), bi[2 * k]);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, k * 16 + 4, 4), bq[2 * k]);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, k * 16 + 8, 4), bi[2 * k + 1]);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, k * 16 + 12, 4), bq[2 * k + 1]);
            }
            _rec.Write(_recBuf, 0, m * 16);
            _recLeft -= m;
            if (_recLeft <= 0) stopRecording("saved");
        }

        private void writeDecimated(float[] bi, float[] bq, int n)
        {
            int outs = 0;
            if (_recBuf.Length < (n / 2 + 1) * 16) _recBuf = new byte[(n / 2 + 1) * 16];
            for (int k = 0; k < n / 2 && _recLeft > 0; k++)
            {
                if (!_recDecim.Push(bi[2 * k], bq[2 * k], bi[2 * k + 1], bq[2 * k + 1], out float i1, out float q1, out float i2, out float q2)) continue;
                int o = 16 * outs++;
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, o, 4), i1);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, o + 4, 4), q1);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, o + 8, 4), i2);
                BitConverter.TryWriteBytes(new Span<byte>(_recBuf, o + 12, 4), q2);
                _recLeft--;
            }
            _rec.Write(_recBuf, 0, outs * 16);
            if (_recLeft <= 0) stopRecording("saved");
        }

        private void stopRecording(string why)
        {
            System.IO.FileStream f = _rec;
            if (f == null) return;
            _rec = null;
            try
            {
                uint data = (uint)(f.Length - 44);
                System.IO.BinaryWriter w = new System.IO.BinaryWriter(f);
                f.Seek(0, System.IO.SeekOrigin.Begin);
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)3); w.Write((short)4); w.Write(_recRate); w.Write(_recRate * 16); w.Write((short)16); w.Write((short)32);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data);
                w.Flush();
                if (why != null) recStatus(why + " (" + data / 16 / Math.Max(1, _recRate) + " s): " + System.IO.Path.GetFileName(f.Name));
            }
            finally { f.Dispose(); }
        }

        private void recStatus(string s)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Text = "Phasing Control - " + s));
        }

        // console mini radar: small caption of the per-section mode, low inside the circle (the panel is an ellipse)
        private static readonly string[] SectionModeNames = { null, "MRC", "CO-PHASE", "NULL", "MAX-SINR" };

        // "MAX-SINR 60 F" while a per-section mode runs (also the console status bar), else null
        public string SectionCaption
        {
            get
            {
                int m = _phaseRun ? _secMode : 0;
                if (m <= 0 || m >= SectionModeNames.Length) return null;
                return SectionModeNames[m] + " " + _secSections + (_secFine != 0 ? " F" : "") + (_secClean != 0 && m == 4 ? " C" + (_secClean < 0 ? "A" : _secClean < 100 ? _secClean.ToString() : "") : "");
            }
        }
        private void drawSectionModeMini(Graphics g, int size)
        {
            string s = SectionCaption;
            if (s == null) return;
            using (Font f = new Font("Segoe UI", Math.Max(7f, size / 14f), FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF sz = g.MeasureString(s, f);
                g.DrawString(s, f, Brushes.Yellow, (size - sz.Width) / 2, size * 0.80f - sz.Height / 2);
            }
        }

        // per-section weights on the radar, one dot per RX1 filter bin, refreshed with the radar (100 ms):
        // angle = arg w (radar maths; w is native I + jQ, the arrow the conjugate panadapter frame, so MRC dots sit
        // on the arrow), distance = RX2 share |w| / (v1 + |w|) -
        // centre RX1 alone, middle ring both alike, edge RX2 alone; colour = place in the filter, blue = left
        // (low) edge of the panadapter, red = right. MRC/co-phase gather at the station phase, a null sits
        // opposite the noise phase, a bin with no learning yet stays in the centre.
        private const int SectionWeightsMax = 2048;
        private volatile Tuple<double[], int> _secWeights;
        private volatile bool _secDots = true;
        private void drawSectionWeights(Graphics g, int size)
        {
            var sw = _secWeights;
            if (!_secDots || sw == null || sw.Item2 <= 0 || SectionCaption == null) return;
            double[] w = sw.Item1;
            int n = sw.Item2;
            float d = Math.Max(2.5f, Math.Min(5f, size / 50f)), h = size / 2f;
            for (int j = 0; j < n; j++)
            {
                double v1 = w[3 * j], wr = w[3 * j + 1], wi = w[3 * j + 2], m = Math.Sqrt(wr * wr + wi * wi);
                double r = v1 + m > 0 ? m / (v1 + m) : 0, a = -Math.Atan2(wi, wr);
                float x = (float)(h + r * Math.Cos(a) * h), y = (float)(h + r * Math.Sin(a) * h);
                // native bins run against the panadapter (it mirrors I + jQ): j = 0 is its right edge
                double t = n > 1 ? 1.0 - (double)j / (n - 1) : 0.5;
                using (SolidBrush b = new SolidBrush(hueColour(240 * (1 - t))))
                    g.FillEllipse(b, x - d / 2, y - d / 2, d, d);
            }
        }

        // hue 0..360 at full saturation, value 1
        private static Color hueColour(double hue)
        {
            double hh = (hue % 360) / 60, x = 1 - Math.Abs(hh % 2 - 1);
            double r = 0, gg = 0, b = 0;
            switch ((int)hh) { case 0: r = 1; gg = x; break; case 1: r = x; gg = 1; break; case 2: gg = 1; b = x; break;
                               case 3: gg = x; b = 1; break; case 4: r = x; b = 1; break; default: r = 1; b = x; break; }
            return Color.FromArgb(220, (int)(r * 255), (int)(gg * 255), (int)(b * 255));
        }

        private void drawPhaseArrow(Graphics g, int size, bool showLabels)
        {
            if (Environment.TickCount64 - _phaseStamp > 1000) return;      // no diversity data
            double phase = _phaseRad, coh = _phaseCoh;
            Point tip = PolarToXY(coh, -phase, size);                     // radar maths: 0 deg right, counter-clockwise
            using (Pen pen = new Pen(Color.Cyan, 2f))
            {
                pen.CustomEndCap = new AdjustableArrowCap(4, 4);
                g.DrawLine(pen, new Point(size / 2, size / 2), tip);
            }
            if (showLabels)
                g.DrawString("Δφ " + (phase * 180 / Math.PI).ToString("F0") + "°  coh " + coh.ToString("F2")
                    + "  noise coh " + _noiseCoh.ToString("F2"),
                    _font, Brushes.Cyan, new PointF(2, size - _font.Height - 2));

            if (_noiseCoh >= NullMinCoherence && nullTarget(out double r, out double a))
            {
                Point p = PolarToXY(r, -a, size);
                g.FillEllipse(Brushes.Red, p.X - 5, p.Y - 5, 10, 10);
            }
        }

        // Handle position (udR, udAngle as the mouse sets them) whose UpdateDiversity weight w cancels
        // the noise ratio h = RX2/RX1: RX1 + w*RX2 = 0 (Sync1) or w*RX1 + RX2 = 0 (Sync2).
        // Beyond the radar edge (|w| > gain multiplier) the dot sits on the edge.
        private bool nullTarget(out double r, out double angle)
        {
            r = angle = 0;
            double hr = _noiseHr, hi = _noiseHi, h2 = hr * hr + hi * hi;
            if (h2 <= 0 || m_dGainMulti <= 0) return false;
            bool sync1 = radioButtonMerc1.Checked;
            double wr = sync1 ? -hr / h2 : -hr, wi = sync1 ? hi / h2 : -hi;
            double phi = Math.Atan2(wi, wr);
            // UpdateDiversity: phase = base + angle + 2*pi*d_lambda*cos(angle + cal), not invertible
            // in closed form, so scan the handle angle in 0.1 deg steps
            double baseA = sync1 ? cross_fire : Math.PI, cal = (double)udCalib.Value, best = double.MaxValue;
            for (int i = -1800; i < 1800; i++)
            {
                double t = i * Math.PI / 1800;
                double e = Math.Abs(Math.IEEERemainder(baseA + t + 2 * Math.PI * d_lambda * Math.Cos(t + cal) - phi, 2 * Math.PI));
                if (e < best) { best = e; angle = t; }
            }
            r = Math.Sqrt(wr * wr + wi * wi) / m_dGainMulti;
            return true;
        }
    }
}
