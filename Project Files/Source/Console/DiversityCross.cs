using System;

namespace Thetis
{
    // Cross-spectrum of the two diversity receivers inside a band of IQ offsets (Hz, the
    // panadapter frame: Q/I swapped like the GPU waterfall). Input comes interleaved per
    // sample (even = RX1, odd = RX2), 2*N values per Feed.
    internal sealed class DiversityCross
    {
        public readonly int N;
        private readonly double _rate, _fast, _slow;
        private readonly Fft _fft;
        private readonly float[] _win, _re1, _im1, _re2, _im2;
        private readonly double[] _pw, _sorted;              // per-bin RX1+RX2 power around the filter
        public const double NoiseSpanHz = 3000, NoiseLimit = 4;   // null band = filter +-3 kHz; signals > 4x median skipped
        private double _fr, _fi, _f1, _f2;      // short average: phase meter
        private double _sr, _si, _s1, _s2;      // long average of the noise floor around the filter: null target
        private long _count;
        private int _lo = int.MinValue, _hi;

        public DiversityCross(int rate, double binHz, double fastSeconds, double slowSeconds)
        {
            _rate = rate; _fast = fastSeconds; _slow = slowSeconds;
            N = 1024;
            while (N * binHz < rate) N <<= 1;
            _fft = new Fft(N);
            _re1 = new float[N]; _im1 = new float[N]; _re2 = new float[N]; _im2 = new float[N];
            _win = new float[N];
            _pw = new double[N]; _sorted = new double[N];
            for (int k = 0; k < N; k++) _win[k] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * k / N));
        }

        public void Feed(float[] bi, float[] bq, int lo, int hi)
        {
            if (lo != _lo || hi != _hi) { _lo = lo; _hi = hi; _count = 0; }   // retuned: start over

            for (int k = 0; k < N; k++)
            {
                int d = _fft.BitRev[k];
                _re1[d] = bq[2 * k] * _win[k]; _im1[d] = bi[2 * k] * _win[k];
                _re2[d] = bq[2 * k + 1] * _win[k]; _im2[d] = bi[2 * k + 1] * _win[k];
            }
            _fft.Transform(_re1, _im1);
            _fft.Transform(_re2, _im2);

            double binHz = _rate / N;
            double sr = 0, si = 0, s1 = 0, s2 = 0;
            for (int b = (int)Math.Ceiling(lo / binHz); b <= (int)Math.Floor(hi / binHz); b++)
            {
                int k = b & (N - 1);
                // RX2 * conj(RX1) in the swapped frame
                sr += _re2[k] * _re1[k] + _im2[k] * _im1[k];
                si += _im2[k] * _re1[k] - _re2[k] * _im1[k];
                s1 += _re1[k] * _re1[k] + _im1[k] * _im1[k];
                s2 += _re2[k] * _re2[k] + _im2[k] * _im2[k];
            }

            // null target: the noise floor around the filter only. Local noise (PSUs, PLC) is broadband,
            // so its null holds inside the filter too, and the station being listened to stays out.
            int b0 = (int)Math.Ceiling((lo - NoiseSpanHz) / binHz), b1 = (int)Math.Floor((hi + NoiseSpanHz) / binHz);
            int nb = Math.Min(N, b1 - b0 + 1);
            for (int j = 0; j < nb; j++)
            {
                int k = (b0 + j) & (N - 1);
                _pw[j] = _re1[k] * _re1[k] + _im1[k] * _im1[k] + _re2[k] * _re2[k] + _im2[k] * _im2[k];
            }
            Array.Copy(_pw, _sorted, nb);
            Array.Sort(_sorted, 0, nb);
            double limit = NoiseLimit * _sorted[nb / 2];
            double nr = 0, ni = 0, n1 = 0, n2 = 0;
            for (int j = 0; j < nb; j++)
            {
                if (_pw[j] > limit) continue;
                int k = (b0 + j) & (N - 1);
                nr += _re2[k] * _re1[k] + _im2[k] * _im1[k];
                ni += _im2[k] * _re1[k] - _re2[k] * _im1[k];
                n1 += _re1[k] * _re1[k] + _im1[k] * _im1[k];
                n2 += _re2[k] * _re2[k] + _im2[k] * _im2[k];
            }

            // plain mean until the average is full, so a fresh start is not stuck on its first frame
            _count++;
            double a = Math.Max(N / _rate / _fast, 1.0 / _count);
            _fr += a * (sr - _fr); _fi += a * (si - _fi); _f1 += a * (s1 - _f1); _f2 += a * (s2 - _f2);
            a = Math.Max(N / _rate / _slow, 1.0 / _count);
            _sr += a * (nr - _sr); _si += a * (ni - _si); _s1 += a * (n1 - _s1); _s2 += a * (n2 - _s2);
        }

        public double BinHz => _rate / N;

        // Phase waterfall: exponential average (factor a) of the last Feed's cross spectrum over bins
        // b0 .. b0+cr.Length-1 (swapped frame, may be negative), same terms as the band sums above.
        public void AverageBins(int b0, double a, double[] cr, double[] ci, double[] p1, double[] p2)
        {
            for (int j = 0; j < cr.Length; j++)
            {
                int k = (b0 + j) & (N - 1);
                double q1 = _re1[k] * _re1[k] + _im1[k] * _im1[k], q2 = _re2[k] * _re2[k] + _im2[k] * _im2[k];
                // fast release: a strong signal that ended would otherwise colour the bin for
                // seconds (decaying tens of dB at the slow rate); noise rarely drops 6 dB under its mean
                double aj = 4 * (q1 + q2) < p1[j] + p2[j] ? Math.Max(a, 0.4) : a;
                cr[j] += aj * (_re2[k] * _re1[k] + _im2[k] * _im1[k] - cr[j]);
                ci[j] += aj * (_im2[k] * _re1[k] - _re2[k] * _im1[k] - ci[j]);
                p1[j] += aj * (q1 - p1[j]);
                p2[j] += aj * (q2 - p2[j]);
            }
        }

        public bool Valid => _count > 0 && _f1 > 0 && _f2 > 0;
        public double Phase => Math.Atan2(-_fi, _fr);   // how far RX2 leads RX1 (the swap conjugates the cross term)
        public double Coherence => Math.Min(1, Math.Sqrt(_fr * _fr + _fi * _fi) / Math.Sqrt(_f1 * _f2));

        // RX2/RX1 ratio h (real IQ frame) of the strongest stable part of the noise floor around the
        // band over the long average: main eigenvector of the 2x2 cross-spectral matrix, which noise
        // or skywave of similar power on both antennas does not pull. coh = coherence of that floor.
        public bool NoiseRatio(out double hr, out double hi, out double coh)
        {
            hr = hi = coh = 0;
            // in the real IQ frame E[RX1*conj(RX2)] = (_sr, _si), see Phase
            double c2 = _sr * _sr + _si * _si;
            if (_count == 0 || c2 <= 0) return false;
            double m = (_s1 + _s2) / 2, d = Math.Sqrt((_s1 - _s2) * (_s1 - _s2) / 4 + c2);
            hr = (m + d - _s1) * _sr / c2; hi = -(m + d - _s1) * _si / c2;
            coh = Math.Sqrt(c2 / (_s1 * _s2));
            return true;
        }
    }
}
