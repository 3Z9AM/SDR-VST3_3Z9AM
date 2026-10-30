//  DiversityDecimator.cs
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
    // Long diversity recording (DiversityForm "Rec", Shift + click): both antennas mixed so the RX1 filter
    // sits at 0, low-passed and decimated by an integer factor (192 kHz -> 24 kHz keeps the filter and a
    // few kHz beside it for tools/divtest). Works in the panadapter frame z = Q + jI, as the file name and
    // divtest do. Also linked into tools/divtest (decimate check).
    public sealed class DiversityDecimator
    {
        private readonly int _dec, _rate;
        private readonly double _w;
        private readonly double[] _taps, _hist;     // history per tap: re1, im1, re2, im2
        private int _n, _head;

        public DiversityDecimator(int rate, int dec, int shiftHz)
        {
            _rate = rate; _dec = dec;
            _w = -2 * Math.PI * shiftHz / rate;
            // Blackman-windowed sinc at the output Nyquist, 16 taps per output sample: flat to ~0.33 of the
            // output rate, and what aliases lands beyond it (divtest uses +-6 kHz of the 24 kHz)
            int L = 16 * dec + 1;
            _taps = new double[L]; double sum = 0;
            for (int i = 0; i < L; i++)
            {
                double x = i - (L - 1) / 2.0, sinc = x == 0 ? 1 : Math.Sin(Math.PI * x / dec) / (Math.PI * x / dec);
                _taps[i] = sinc * (0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (L - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (L - 1)));
                sum += _taps[i];
            }
            for (int i = 0; i < L; i++) _taps[i] /= sum;
            _hist = new double[4 * L];
        }

        // one input pair (I, Q per antenna as read); true every dec-th sample with the filtered output pair
        public bool Push(float i1, float q1, float i2, float q2, out float oi1, out float oq1, out float oi2, out float oq2)
        {
            double ph = _w * _n, c = Math.Cos(ph), s = Math.Sin(ph);
            if (++_n == _rate) _n = 0;                  // whole Hz shift: period 1 s
            int L = _taps.Length, h = 4 * _head;
            _hist[h] = q1 * c - i1 * s; _hist[h + 1] = q1 * s + i1 * c;
            _hist[h + 2] = q2 * c - i2 * s; _hist[h + 3] = q2 * s + i2 * c;
            if (++_head == L) _head = 0;
            oi1 = oq1 = oi2 = oq2 = 0;
            if (_n % _dec != 0) return false;
            double a = 0, b = 0, e = 0, f = 0;
            for (int t = 0, j = _head; t < L; t++)      // oldest first; symmetric taps
            {
                double g = _taps[t]; int q = 4 * j;
                a += g * _hist[q]; b += g * _hist[q + 1]; e += g * _hist[q + 2]; f += g * _hist[q + 3];
                if (++j == L) j = 0;
            }
            oi1 = (float)b; oq1 = (float)a; oi2 = (float)f; oq2 = (float)e;
            return true;
        }
    }
}
