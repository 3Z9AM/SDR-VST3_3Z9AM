//  Display.Pan3DPhase.cs
//
//  Diversity phase colours for the 3D panadapter, the waterfall sphere and the GPU waterfall
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

namespace Thetis
{
    // Phase view: RX1 3D rows and RX1 GPU waterfall rows coloured by the phase of RX2 against
    // RX1 (hue) and their coherence (grey = none) instead of the level palette; 3D heights and
    // waterfall brightness stay the level. The per-bin cross spectrum (~12 Hz bins) comes from
    // the DiversityForm phase thread (diversity on, window open).
    partial class Display
    {
        private static bool _pan3DPhaseColours = false;
        public static bool Pan3DPhaseColours
        {
            get { return _pan3DPhaseColours; }
            set { _pan3DPhaseColours = value; }
        }

        private static bool _waterfallPhaseView = false;
        public static bool WaterfallPhaseView
        {
            get { return _waterfallPhaseView; }
            set { _waterfallPhaseView = value; }
        }

        internal static volatile bool PhaseViewRx1Lead;   // colours from the phase of RX1 against RX2 (hue mirrored)

        // waterfall sphere, lower half: RX1 frequency change (Doppler) instead of the RX2-RX1 phase
        private static volatile bool _pan3DSphereDoppler = false;
        public static bool Pan3DSphereDoppler
        {
            get { return _pan3DSphereDoppler; }
            set
            {
                if (_pan3DSphereDoppler == value) return;
                _pan3DSphereDoppler = value;
                ensureDoppler();
            }
        }

        // waterfall sphere: arrows on the equator - ionosphere going down / up on the steady carriers
        private static volatile bool _pan3DIonoDrift = false;
        public static bool Pan3DIonoDrift
        {
            get { return _pan3DIonoDrift; }
            set { _pan3DIonoDrift = value; ensureDoppler(); }
        }

        // on by default while diversity runs (console.Diversity2), the checkbox adds it without diversity
        private static volatile bool _diversityOn;
        internal static bool DiversityOn
        {
            set { _diversityOn = value; ensureDoppler(); }
        }
        private static bool IonoDriftActive => _pan3DIonoDrift || _diversityOn;

        private static bool DopplerWanted => _pan3DSphereDoppler || IonoDriftActive;
        private static int _dopplerRunning;
        private static void ensureDoppler()
        {
            if (DopplerWanted && System.Threading.Interlocked.CompareExchange(ref _dopplerRunning, 1, 0) == 0)
                new System.Threading.Thread(dopplerWork) { IsBackground = true, Name = "3D Doppler", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        // RX1 only, so no Diversity window: its own copy of the RX1 IQ (waterfall_iq ring 7, cmaster.c).
        // FFT ~12 Hz bins slid by a quarter; frames 4 hops = one FFT apart are compared (DiversityCross.LagAverage).
        private const int DopplerRing = 7;
        private const double DopplerFastSeconds = 0.3, DopplerSlowSeconds = 5;
        // shortcut: drift is against the last minute (the transmitter's own offset is unknown, so a layer that moves
        // steadily for many minutes fades out); a GPS-locked carrier of known frequency would give the absolute value
        private const double DriftFastSeconds = 2, DriftSlowSeconds = 60;
        private static void dopplerWork()
        {
            ExactGpuNative.CM_WaterfallIQ_Init(DopplerRing, 1 << 20);
            ExactGpuNative.CM_WaterfallIQ_SetEnabled(DopplerRing, 1);
            const int Lag = 4;
            int rate = 0, N = 0, hop = 0, fill = 0;
            long lastRef = long.MinValue;
            Fft fft = null;
            float[] bi = null, bq = null, ti = null, tq = null, re = null, im = null, win = null;
            double[] fr = null, fi = null, sr = null, si = null, pm = null;
            double[] gr = null, gi = null, hr = null, hi = null, gm = null;   // drift: slower, against a longer mean
            var lag = new DiversityCross.LagAverage();
            var drift = new DiversityCross.LagAverage();
            try
            {
                while (DopplerWanted)
                {
                    System.Threading.Thread.Sleep(10);
                    try
                    {
                        int r = cmaster.GetInputRate(0, 0);
                        int avail = ExactGpuNative.CM_WaterfallIQ_Available(DopplerRing);
                        long refHz = RX1IqOffsetToHz(0);
                        if (r <= 0 || Audio.MOX || refHz != lastRef || r != rate)
                        {
                            ExactGpuNative.CM_WaterfallIQ_Skip(DopplerRing, avail);
                            lastRef = refHz; fill = 0; lag.Reset(); drift.Reset();
                            if (r > 0 && r != rate)
                            {
                                rate = r;
                                N = 1024; while ((double)rate / N > 12) N <<= 1;
                                hop = N / Lag;
                                fft = new Fft(N);
                                bi = new float[N]; bq = new float[N]; ti = new float[hop]; tq = new float[hop];
                                re = new float[N]; im = new float[N]; win = new float[N];
                                for (int k = 0; k < N; k++) win[k] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * k / N));
                                fr = new double[N]; fi = new double[N]; sr = new double[N]; si = new double[N]; pm = new double[N];
                                gr = new double[N]; gi = new double[N]; hr = new double[N]; hi = new double[N]; gm = new double[N];
                            }
                            continue;
                        }
                        bool fed = false;
                        while (avail >= hop)
                        {
                            int n = ExactGpuNative.CM_WaterfallIQ_Get(DopplerRing, hop, ti, tq);
                            if (n < hop) break;
                            avail -= n;
                            // slide the window by one hop
                            Array.Copy(bi, hop, bi, 0, N - hop); Array.Copy(ti, 0, bi, N - hop, hop);
                            Array.Copy(bq, hop, bq, 0, N - hop); Array.Copy(tq, 0, bq, N - hop, hop);
                            if (fill < Lag) { fill++; continue; }
                            for (int k = 0; k < N; k++)
                            {
                                int d = fft.BitRev[k];
                                re[d] = bq[k] * win[k]; im[d] = bi[k] * win[k];   // same I/Q swap as the GPU waterfall
                            }
                            fft.Transform(re, im);
                            double perSec = (double)rate / hop;
                            lag.Add(Lag, re, im, DopplerFastSeconds * perSec, DopplerSlowSeconds * perSec, fr, fi, sr, si, pm);
                            if (IonoDriftActive) drift.Add(Lag, re, im, DriftFastSeconds * perSec, DriftSlowSeconds * perSec, gr, gi, hr, hi, gm);
                            fed = true;
                        }
                        if (fed)
                        {
                            SetDopplerSpectrum((double)rate / N, fr, fi, sr, si, pm);
                            if (IonoDriftActive) SetDriftSpectrum((double)rate / N, gr, gi, hr, hi, gm);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("3D Doppler: " + ex.Message);
                    }
                }
            }
            finally
            {
                ExactGpuNative.CM_WaterfallIQ_SetEnabled(DopplerRing, 0);
                System.Threading.Interlocked.Exchange(ref _dopplerRunning, 0);
                ensureDoppler();                            // switched back on while this thread was leaving
            }
        }
        private const double DopplerRangeHz = 1.0;          // full colour at +-1 Hz against the 5 s mean

        // GPU waterfall phase view: colour = how fast the RX2-RX1 phase turns instead of the phase itself
        // (steady = local noise / groundwave, turning = skywave)
        private static volatile bool _waterfallPhaseRate = false;
        public static bool WaterfallPhaseRate
        {
            get { return _waterfallPhaseRate; }
            set { _waterfallPhaseRate = value; }
        }
        private const double PhaseRateRangeHz = 0.5;        // full colour at half a turn per second

        internal static bool PhaseViewWanted => _pan3DPhaseColours || _pan3DSphere || _waterfallPhaseView;

        private static readonly object _3dPhaseLock = new object();
        private static double[] _3dPhCr, _3dPhCi, _3dPhP1, _3dPhP2;   // FFT bin order, display (swapped) frame
        private static double _3dPhBinHz;
        private static long _3dPhTicks;

        internal static void SetPhaseSpectrum(double binHz, double[] cr, double[] ci, double[] p1, double[] p2)
        {
            lock (_3dPhaseLock)
            {
                if (_3dPhCr == null || _3dPhCr.Length != cr.Length)
                {
                    _3dPhCr = new double[cr.Length]; _3dPhCi = new double[cr.Length];
                    _3dPhP1 = new double[cr.Length]; _3dPhP2 = new double[cr.Length];
                }
                Array.Copy(cr, _3dPhCr, cr.Length); Array.Copy(ci, _3dPhCi, cr.Length);
                Array.Copy(p1, _3dPhP1, cr.Length); Array.Copy(p2, _3dPhP2, cr.Length);
                _3dPhBinHz = binHz;
                _3dPhTicks = DateTime.UtcNow.Ticks;
            }
        }

        private static double[] _dopR, _dopI, _dopP;       // per fine bin: fast * conj(slow) / |slow|, RX1 power
        private static double _dopBinHz;
        private static long _dopTicks;

        // magnifier waterfall (Console.Magnifier.cs): the RX1 palette at strength 0..1, and the dB range the
        // RX1 waterfall paints with
        internal static int RX1WaterfallColour(float strength)
        {
            strength = Math.Max(0, Math.Min(1, strength));
            GetWaterfallColor(-100 + 100 * strength, -100, 0, _rx1_color_scheme,
                waterfall_low_color, _rx1_waterfall_grad, _rx1_waterfall_grad_ok, out int R, out int G, out int B);
            return unchecked((int)0xFF000000) | R << 16 | G << 8 | B;
        }

        internal static float RX1WaterfallRange() => Get3DWfSyncThresholds(1, out float lo, out float hi) ? hi - lo : 60;

        internal static void SetDopplerSpectrum(double binHz, double[] fr, double[] fi, double[] sr, double[] si, double[] pw)
        {
            lock (_3dPhaseLock)
            {
                againstMean(fr, fi, sr, si, pw, ref _dopR, ref _dopI, ref _dopP);
                _dopBinHz = binHz;
                _dopTicks = DateTime.UtcNow.Ticks;
            }
        }

        private static double[] _drR, _drI, _drP;          // the same for the drift arrows (2 s against 60 s)
        private static double _drBinHz;
        private static long _drTicks;

        internal static void SetDriftSpectrum(double binHz, double[] fr, double[] fi, double[] sr, double[] si, double[] pw)
        {
            lock (_3dPhaseLock)
            {
                againstMean(fr, fi, sr, si, pw, ref _drR, ref _drI, ref _drP);
                _drBinHz = binHz;
                _drTicks = DateTime.UtcNow.Ticks;
            }
        }

        // per bin fast * conj(slow) / |slow| (angle = frequency change against the slow mean), power as is
        private static void againstMean(double[] fr, double[] fi, double[] sr, double[] si, double[] pw, ref double[] R, ref double[] I, ref double[] P)
        {
            int n = fr.Length;
            if (R == null || R.Length != n) { R = new double[n]; I = new double[n]; P = new double[n]; }
            for (int k = 0; k < n; k++)
            {
                double m = Math.Sqrt(sr[k] * sr[k] + si[k] * si[k]);
                if (m > 0)
                {
                    R[k] = (fr[k] * sr[k] + fi[k] * si[k]) / m;
                    I[k] = (fi[k] * sr[k] - fr[k] * si[k]) / m;
                }
                else R[k] = I[k] = 0;
                P[k] = pw[k];
            }
        }

        // Ionosphere drift: small arrows on the equator / horizon at every steady carrier. Frequency up =
        // the path shortens = the reflecting layer comes down (arrow down, red); down = it rises (arrow up, blue).
        // Label: weighted mean as vertical speed, v = c df / (2 f) (vertical incidence; oblique paths read lower).
        private const int DriftCols = 160;
        private static readonly double[] _drHz = new double[DriftCols], _drW = new double[DriftCols], _drCoh = new double[DriftCols];
        private static void DrawIonoDriftDX2D(int top, int W, int H, float ey, float ry, bool inside)
        {
            if (!IonoDriftActive || W < 50 || _d2dRenderTarget == null) return;
            double low = rx_display_low, span = rx_display_high - rx_display_low;
            lock (_3dPhaseLock)
            {
                if (_drR == null || DateTime.UtcNow.Ticks - _drTicks > 10000000) return;
                int n = _drR.Length;
                double binHz = _drBinHz;
                for (int c = 0; c < DriftCols; c++)
                {
                    int b0 = (int)Math.Floor((low + span * c / DriftCols) / binHz + 0.5);
                    int b1 = Math.Max(b0 + 1, (int)Math.Floor((low + span * (c + 1) / DriftCols) / binHz + 0.5));
                    double dr = 0, di = 0, p = 0;
                    for (int b = b0; b < b1; b++) { int k = b & (n - 1); dr += _drR[k]; di += _drI[k]; p += _drP[k]; }
                    _drCoh[c] = p > 0 ? Math.Min(1, Math.Sqrt(dr * dr + di * di) / p) : 0;
                    _drHz[c] = Math.Atan2(di, dr) * binHz / (2 * Math.PI);
                    _drW[c] = p;
                }
            }

            // steady carriers: coherent columns, local power peaks well above the median column
            double[] sorted = (double[])_drW.Clone();
            Array.Sort(sorted);
            double floor = sorted[DriftCols / 2] * 6;
            double sumW = 0, sumHz = 0, sumF = 0;
            int count = 0;
            for (int c = 0; c < DriftCols; c++)
            {
                if (_drCoh[c] < 0.6 || _drW[c] <= floor) continue;
                if ((c > 0 && _drW[c - 1] > _drW[c]) || (c < DriftCols - 1 && _drW[c + 1] >= _drW[c])) continue;
                double hz = _drHz[c];
                float x = (c + 0.5f) * W / DriftCols;
                System.Numerics.Vector2 p;
                if (inside)
                {
                    if (!SphereInsidePoint(x / W, 0f, W, top, H, out p) || p.Y > top + H - 4) continue;
                }
                else
                {
                    const float lat = 0f;                       // on the equator (user)
                    float k = 2f * x / W - 1f;
                    p = new System.Numerics.Vector2(W * 0.5f + (W * 0.5f - 2f) * (float)Math.Cos(lat) * k, ey - ry * (float)Math.Sin(lat));
                }
                drawDriftArrow(p, hz);
                double w = _drW[c] * _drCoh[c];
                sumW += w; sumHz += w * hz; sumF += w * RX1IqOffsetToHz(low + span * (c + 0.5) / DriftCols);
                count++;
            }

            string label;
            if (count == 0) label = "IONO: no steady carrier";
            else
            {
                double hz = sumHz / sumW, f = sumF / sumW;
                double v = 299792458.0 * hz / (2 * f);         // + = layer coming down
                label = "IONO " + (Math.Abs(hz) < DriftDeadHz ? "= steady" : (hz > 0 ? "▼ down " : "▲ up ") + Math.Abs(v).ToString("0.0") + " m/s") +
                        "  (" + hz.ToString("+0.00;-0.00") + " Hz, " + count + ")";
            }
            float ly = inside ? top + H - 18 : Math.Min(top + H - 18, ey + ry * 0.8f);
            drawStringDX2D(label, fontDX2d_font9, Get3DBrush(230, 240, 245, 0.95f), W * 0.5f - label.Length * 3f, ly);
        }

        private const double DriftDeadHz = 0.02;
        private static void drawDriftArrow(System.Numerics.Vector2 p, double hz)
        {
            if (Math.Abs(hz) < DriftDeadHz)
            {
                _d2dRenderTarget.FillEllipse(new Vortice.Direct2D1.Ellipse(p, 2.5f, 2.5f), Get3DBrush(80, 220, 100, 0.95f));
                return;
            }
            float dir = hz > 0 ? 1f : -1f;                      // screen y: + = down
            float len = 6f + 14f * (float)Math.Min(1, Math.Abs(hz) / DopplerRangeHz);
            var b = hz > 0 ? Get3DBrush(255, 90, 70, 0.95f) : Get3DBrush(80, 150, 255, 0.95f);
            var tip = new System.Numerics.Vector2(p.X, p.Y + dir * len / 2);
            _d2dRenderTarget.DrawLine(new System.Numerics.Vector2(p.X, p.Y - dir * len / 2), new System.Numerics.Vector2(p.X, tip.Y - dir * 4f), b, 2f);
            using (var geo = _d2dFactory.CreatePathGeometry())
            {
                using (var sink = geo.Open())
                {
                    sink.BeginFigure(tip, Vortice.Direct2D1.FigureBegin.Filled);
                    sink.AddLine(new System.Numerics.Vector2(p.X - 4f, tip.Y - dir * 6f));
                    sink.AddLine(new System.Numerics.Vector2(p.X + 4f, tip.Y - dir * 6f));
                    sink.EndFigure(Vortice.Direct2D1.FigureEnd.Closed);
                    sink.Close();
                }
                _d2dRenderTarget.FillGeometry(geo, b);
            }
        }

        private static double[] _rateR, _rateI, _rateP;    // per fine bin: x_t conj(x_t-lag) of RX2 conj(RX1), mean |..|
        private static double _rateBinHz;
        private static long _rateTicks;

        internal static void SetPhaseRateSpectrum(double binHz, double[] rr, double[] ri, double[] pm)
        {
            lock (_3dPhaseLock)
            {
                int n = rr.Length;
                if (_rateR == null || _rateR.Length != n) { _rateR = new double[n]; _rateI = new double[n]; _rateP = new double[n]; }
                Array.Copy(rr, _rateR, n); Array.Copy(ri, _rateI, n); Array.Copy(pm, _rateP, n);
                _rateBinHz = binHz;
                _rateTicks = DateTime.UtcNow.Ticks;
            }
        }

        // GPU waterfall phase view row
        private static void FillWaterfallPhaseColours(uint[] row, int cols)
        {
            if (!_waterfallPhaseRate) { FillPhaseColours(row, cols); return; }
            // the swapped frame conjugates the cross term: minus = RX2 phase advancing on RX1
            lock (_3dPhaseLock) FillTurnColours(row, cols, _rateR, _rateI, _rateP, _rateBinHz, _rateTicks, PhaseViewRx1Lead ? 1 : -1, PhaseRateRangeHz);
        }

        private static void Fill3DPhaseRow(Pan3DHistory h, int head, int cols)
        {
            uint[] row = h.Phase[head];
            if (row == null || row.Length < cols) h.Phase[head] = row = new uint[cols];
            if (_pan3DSphereDoppler && (_pan3DSphere || _pan3DPhaseColours)) { lock (_3dPhaseLock) FillTurnColours(row, cols, _dopR, _dopI, _dopP, _dopBinHz, _dopTicks, 1, DopplerRangeHz); }
            else FillPhaseColours(row, cols);
        }

        // colours of one RX1 row across the display span (BGRA, alpha 0 = no data: the
        // shaders keep the normal colours)
        private static void FillPhaseColours(uint[] row, int cols)
        {
            lock (_3dPhaseLock)
            {
                if (_3dPhCr == null || DateTime.UtcNow.Ticks - _3dPhTicks > 10000000)
                {
                    Array.Clear(row, 0, cols);
                    return;
                }
                int n = _3dPhCr.Length;
                double low = rx_display_low, span = rx_display_high - rx_display_low, binHz = _3dPhBinHz;
                for (int c = 0; c < cols; c++)
                {
                    // column = display offsets [low + span*c/cols, next); zoomed in it is under a bin wide
                    int b0 = (int)Math.Floor((low + span * c / cols) / binHz + 0.5);
                    int b1 = Math.Max(b0 + 1, (int)Math.Floor((low + span * (c + 1) / cols) / binHz + 0.5));
                    double cr = 0, ci = 0, p1 = 0, p2 = 0;
                    for (int b = b0; b < b1; b++)
                    {
                        int k = b & (n - 1);
                        cr += _3dPhCr[k]; ci += _3dPhCi[k]; p1 += _3dPhP1[k]; p2 += _3dPhP2[k];
                    }
                    double den = Math.Sqrt(p1 * p2);
                    double coh = den > 0 ? Math.Min(1, Math.Sqrt(cr * cr + ci * ci) / den) : 0;
                    int rgb = DiversityPhaseWaterfall.Hsv((PhaseViewRx1Lead ? -1 : 1) * Math.Atan2(-ci, cr), 1);   // RX2 lead, real IQ frame
                    double k2 = coh * coh;
                    uint r = (uint)(115 + (((rgb >> 16) & 255) - 115) * k2);
                    uint g = (uint)(115 + (((rgb >> 8) & 255) - 115) * k2);
                    uint b2 = (uint)(115 + ((rgb & 255) - 115) * k2);
                    row[c] = 0xFF000000u | r << 16 | g << 8 | b2;
                }
            }
        }

        // hue = turns per second (angle per FFT length x binHz, times sign): red +, green 0, blue - (full at
        // +-rangeHz); grey = not steady (noise decorrelates between the frames compared). Caller holds _3dPhaseLock.
        private static void FillTurnColours(uint[] row, int cols, double[] R, double[] I, double[] P, double binHz, long ticks, double sign, double rangeHz)
        {
            if (R == null || DateTime.UtcNow.Ticks - ticks > 10000000)
            {
                Array.Clear(row, 0, cols);
                return;
            }
            int n = R.Length;
            double low = rx_display_low, span = rx_display_high - rx_display_low;
            for (int c = 0; c < cols; c++)
            {
                int b0 = (int)Math.Floor((low + span * c / cols) / binHz + 0.5);
                int b1 = Math.Max(b0 + 1, (int)Math.Floor((low + span * (c + 1) / cols) / binHz + 0.5));
                double dr = 0, di = 0, p = 0;
                for (int b = b0; b < b1; b++)
                {
                    int k = b & (n - 1);
                    dr += R[k]; di += I[k]; p += P[k];
                }
                double coh = p > 0 ? Math.Min(1, Math.Sqrt(dr * dr + di * di) / p) : 0;
                double x = Math.Max(-1, Math.Min(1, sign * Math.Atan2(di, dr) * binHz / (2 * Math.PI) / rangeHz));
                int rgb = DiversityPhaseWaterfall.Hsv(2 * Math.PI / 3 * (1 - x), 1);
                double k2 = coh * coh;
                uint r = (uint)(115 + (((rgb >> 16) & 255) - 115) * k2);
                uint g = (uint)(115 + (((rgb >> 8) & 255) - 115) * k2);
                uint b2 = (uint)(115 + ((rgb & 255) - 115) * k2);
                row[c] = 0xFF000000u | r << 16 | g << 8 | b2;
            }
        }
    }
}
