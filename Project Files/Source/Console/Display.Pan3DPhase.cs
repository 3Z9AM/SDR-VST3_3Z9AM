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

        private static void Fill3DPhaseRow(Pan3DHistory h, int head, int cols)
        {
            uint[] row = h.Phase[head];
            if (row == null || row.Length < cols) h.Phase[head] = row = new uint[cols];
            FillPhaseColours(row, cols);
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
    }
}
