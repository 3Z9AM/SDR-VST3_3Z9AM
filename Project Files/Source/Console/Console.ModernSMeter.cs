//  Console.ModernSMeter.cs
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
using System.Drawing.Text;
using System.Windows.Forms;

namespace Thetis
{
    // Modern S-meter (Setup > Appearance > General > Look): a movable Console panel in the
    // Modern VFO style - RX1 S units and dBm as digits plus a bar with fast attack, slow
    // decay and a peak mark; while transmitting it shows forward power in red.
    public partial class Console
    {
        private ModernSMeterPanel panelModernSMeter;
        private Timer _modernSMeterTimer;

        // RX1 signal level in dBm as the S-meter shows it (meter + preamp/calibration offset)
        internal float RX1SignalDbm(bool average = false)
        {
            return WDSP.CalculateRXMeter(0, 0, average ? WDSP.MeterType.AVG_SIGNAL_STRENGTH : WDSP.MeterType.SIGNAL_STRENGTH) + RXOffset(1);
        }

        public bool ModernSMeter
        {
            get { return panelModernSMeter != null && panelModernSMeter.Visible; }
            set
            {
                if (panelModernSMeter == null) return;
                panelModernSMeter.Visible = value;
                if (value)
                {
                    panelModernSMeter.BringToFront();
                    _modernSMeterTimer.Start();
                }
                else _modernSMeterTimer.Stop();
            }
        }

        private void createModernSMeter()
        {
            panelModernSMeter = new ModernSMeterPanel(this);
            panelModernSMeter.Name = "panelModernSMeter";
            panelModernSMeter.Size = new Size(grpVFOA.Width, 64);
            panelModernSMeter.Location = new Point(grpVFOB.Right + 6, grpVFOB.Top);
            panelModernSMeter.Visible = false;
            Controls.Add(panelModernSMeter);

            _modernSMeterTimer = new Timer { Interval = 50 };
            _modernSMeterTimer.Tick += (s, e) => panelModernSMeter.Sample();
        }

        private sealed class ModernSMeterPanel : PanelTS
        {
            private const float MinDbm = -127f;     // S0
            private const float MaxDbm = -13f;      // S9+60
            private const float S9Dbm = -73f;
            private const float DecayDbPerSec = 24f;
            private const double PeakHoldMs = 1500;

            private readonly Console _c;
            private float _shown = MinDbm, _peak = MinDbm;
            private double _peakTime;
            private bool _tx, _off;
            private float _watts, _swr;
            public bool Average = true;   // AVG at every start, a click toggles MAX/AVG
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            private double _lastMs;
            // phosphor bar: a segment lights up at once and its glow fades out like an old CRT
            private const float PhosphorTauS = 0.5f, SegPitch = 5f, SegWidth = 3.5f;
            private float[] _glow = new float[0];
            private Font _fBig, _fSmall, _fTick;   // sized from the panel height

            public ModernSMeterPanel(Console c)
            {
                _c = c;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = ModernPanelBack;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button != MouseButtons.Left) return;
                Average = !Average;
                Invalidate();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                _fBig?.Dispose(); _fSmall?.Dispose(); _fTick?.Dispose();
                FontFamily fam = ModernVfoDisplay.Family;
                _fBig = new Font(fam, Math.Max(1f, Height * 0.36f), FontStyle.Regular, GraphicsUnit.Pixel);
                _fSmall = new Font(fam, Math.Max(1f, Height * 0.2f), FontStyle.Regular, GraphicsUnit.Pixel);
                _fTick = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point);
                if (Width < 2 * ModernGlassRadius || Height < 2 * ModernGlassRadius) return;
                using (GraphicsPath p = roundedRect(new RectangleF(0, 0, Width, Height), ModernGlassRadius))
                    setModernVfoRegion(this, new Region(p));
            }

            // UI timer: read the meter, fast attack / slow decay, peak hold
            public void Sample()
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                float dt = (float)Math.Min(0.5, (now - _lastMs) / 1000.0);
                _lastMs = now;

                _off = !_c.PowerOn;
                _tx = !_off && _c._mox;
                float level;
                if (_off)
                {
                    // radio off: the meter returns garbage, show "--" and an empty bar
                    _shown = _peak = MinDbm;
                    Array.Clear(_glow, 0, _glow.Length);
                    Invalidate();
                    return;
                }
                if (_tx)
                {
                    _watts = _c.alex_fwd;
                    _swr = _c.alex_swr;
                    level = MinDbm + Math.Max(0f, Math.Min(1f, _watts / 100f)) * (MaxDbm - MinDbm);
                }
                else
                {
                    level = _c.RX1SignalDbm(Average);
                    if (float.IsNaN(level) || float.IsInfinity(level)) level = MinDbm;
                }

                _shown = level > _shown ? level : Math.Max(level, _shown - DecayDbPerSec * dt);
                if (level >= _peak || now - _peakTime > PeakHoldMs)
                {
                    _peak = level;
                    _peakTime = now;
                }

                RectangleF bar = barRect();
                int n = Math.Max(0, (int)Math.Ceiling(bar.Width / SegPitch));
                if (_glow.Length != n) _glow = new float[n];
                float fade = (float)Math.Exp(-dt / PhosphorTauS), xv = xOf(bar, level);
                for (int i = 0; i < n; i++)
                    _glow[i] = bar.X + i * SegPitch < xv ? 1f : _glow[i] * fade;
                Invalidate();
            }

            private RectangleF barRect()
            {
                int W = ClientSize.Width, H = ClientSize.Height;
                return new RectangleF(ModernGlassRadius, H * 0.66f, W - 2 * ModernGlassRadius, Math.Max(4f, H * 0.14f));
            }

            private static float xOf(RectangleF bar, float dbm) =>
                bar.X + (Math.Max(MinDbm, Math.Min(MaxDbm, dbm)) - MinDbm) / (MaxDbm - MinDbm) * bar.Width;

            private static string sUnits(float dbm)
            {
                if (dbm <= S9Dbm)
                {
                    int s = (int)Math.Round((dbm - MinDbm) / 6f);
                    return "S" + Math.Max(0, Math.Min(9, s));
                }
                return "S9+" + (int)Math.Round(dbm - S9Dbm);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int W = ClientSize.Width, H = ClientSize.Height;

                Color accent = _tx ? ModernVfoColorTX : ModernVfoColorA;
                ModernTheme.GlassFrame(g,new RectangleF(0.5f, 0.5f, W - 1f, H - 1f), _tx ? accent : ModernTheme.BorderStrong,
                    ModernGlassRadius * 1.4f, _tx);
                // digits: S units (or watts) big, dBm (or SWR) small on the right
                string big = _off ? "--" : _tx ? _watts.ToString("0.0") + " W" : sUnits(_shown);
                bool swr = _tx && _watts > 0.5f && _swr >= 1f;   // no reading without forward power
                string small = _off ? "-- dBm" : _tx ? (swr ? "SWR " + _swr.ToString("f2") : "TX") : _shown.ToString("f1") + " dBm";
                if (!_tx) small = (Average ? "AVG  " : "MAX  ") + small;
                if (_fBig == null) return;
                Font fBig = _fBig, fSmall = _fSmall;
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(40, accent)))
                using (SolidBrush bb = new SolidBrush(lerpColor(accent, Color.White, 0.25f)))
                // TX accent is already red, so high SWR (> 1.7) is shown in yellow
                using (SolidBrush sb = new SolidBrush(swr && _swr > 1.7f ? Color.Gold : Color.FromArgb(190, accent)))
                {
                    float x = ModernGlassRadius, y = 3f;
                    g.DrawString(big, fBig, glow, x + 1, y + 1);
                    g.DrawString(big, fBig, bb, x, y);
                    SizeF ss = g.MeasureString(small, fSmall);
                    g.DrawString(small, fSmall, sb, W - ModernGlassRadius - ss.Width, y + fBig.Height - ss.Height - 1);
                }

                // bar: S0 .. S9+60, amber up to S9, red above; ticks at S1..S9, +20, +40
                RectangleF bar = barRect();
                float Xof(float dbm) => xOf(bar, dbm);
                using (SolidBrush track = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                    g.FillRectangle(track, bar);

                // segmented bar with phosphor afterglow (_glow, faded in Sample)
                float xs9 = Xof(S9Dbm);
                for (int i = 0; i < _glow.Length; i++)
                {
                    if (_glow[i] < 0.02f) continue;
                    float sx = bar.X + i * SegPitch;
                    Color c = _tx || sx >= xs9 ? (_tx ? accent : ModernVfoColorTX) : ModernVfoColorA;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(210 * _glow[i]), c)))
                        g.FillRectangle(b, sx, bar.Y, Math.Min(SegWidth, bar.Right - sx), bar.Height);
                }

                float xp = Xof(_peak);
                if (!_off)
                    using (SolidBrush pk = new SolidBrush(lerpColor(accent, Color.White, 0.5f)))
                        g.FillRectangle(pk, xp - 1f, bar.Y - 1f, 2f, bar.Height + 2f);

                Font fTick = _fTick;
                using (Pen tick = new Pen(Color.FromArgb(90, 255, 255, 255)))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(150, 200, 200, 200)))
                {
                    float ty = bar.Bottom + 1f;
                    foreach (var (dbm, label) in new[] { (-121f, "1"), (-109f, "3"), (-97f, "5"), (-85f, "7"), (S9Dbm, "9"), (-53f, "+20"), (-33f, "+40") })
                    {
                        float tx = Xof(dbm);
                        g.DrawLine(tick, tx, bar.Y + bar.Height, tx, ty + 2f);
                        SizeF ls = g.MeasureString(label, fTick);
                        if (!_tx) g.DrawString(label, fTick, tb, tx - ls.Width / 2f, ty + 1f);
                    }
                }
            }
        }
    }
}
