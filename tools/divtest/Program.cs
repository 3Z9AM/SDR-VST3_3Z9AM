//  divtest
//
//  Offline diversity test bench (sections, max-SINR, clean, inject)
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

// Offline prototype: diversity combining per frequency section (MRC per band) vs one weight
// for the whole filter, on IQ recorded by DiversityForm "Rec 30 s" (or a synthetic two-path
// ionospheric channel). Writes SSB audio WAVs next to the input for listening and prints S/N.
//
//   dotnet run -c Release --project tools/divtest -- <Diversity_...wav> [sections,..] [tau s]
//   dotnet run -c Release --project tools/divtest -- synth      (self-check, PASS/FAIL)
// Also per-section noise nulling (RX2 = noise antenna): N<Hz> = section width, Nall = whole filter.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Thetis;

static class Program
{
    const int AudioRate = 24000, Na = 2048;        // audio IFFT; analysis N = rate * Na / AudioRate

    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: divtest <Diversity_...wav> [1,5,10,20,40] [0.2] | synth"); return 1; }
        int[] sections = args.Length > 1 ? args[1].Split(',').Select(int.Parse).ToArray() : new[] { 1, 5, 10, 20, 40 };
        double tau = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0.2;

        if (args[0] == "synth")
        {
            string path = Path.Combine(Path.GetTempPath(), "Diversity_synth_0Hz_48000_vfo0_f300_2700.wav");
            Synth(path, 48000, 30, false);
            var r = Run(path, new[] { 1, 20 }, tau);
            // fewer fading holes per section than with one weight, and one weight no worse than one antenna
            // (mean S/N barely tells them apart: a random-phase sum adds powers on average too)
            bool ok = r["S20"] < r["S1"] - 2 && r["S1"] < Math.Min(r["rx1"], r["rx2"]) + 0.5;
            Console.WriteLine(ok ? "PASS (combining)" : "FAIL (combining)");

            // noise antenna: signal 17 dB down on RX2, two local noise sources (300-1500, 1500-2700 Hz) with
            // different RX1/RX2 ratios, 10 dB over the thermal noise: narrow sections null both, one weight cannot
            path = Path.Combine(Path.GetTempPath(), "Diversity_synthnull_0Hz_48000_vfo0_f300_2700.wav");
            Synth(path, 48000, 30, true);
            r = Run(path, new int[0], tau);
            bool okn = r["SNR N12"] > r["SNR rx1"] + 5 && r["SNR N12"] > r["SNR Nall"] + 2;
            Console.WriteLine(okn ? "PASS (nulling)" : "FAIL (nulling)");
            ok &= okn;
            Console.WriteLine(ok ? "PASS" : "FAIL");
            return ok ? 0 : 1;
        }
        if (args[0] == "secdiv") return SecDivCheck();
        Run(args[0], sections, tau);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void create_divEXT(int id, int run, int nr, int size);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVOutput(int id, int output);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVSections(int mode, int sections, double tau, int rate, int lo, int hi);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void xdivEXT(int id, int n, IntPtr[] input, IntPtr output);

    // The real-time twin (wdsp/secdiv.c) on the built wdsp.dll - run from bin\x64\Release:
    //   cd "Project Files\bin\x64\Release" && dotnet run -c Release --project ..\..\..\..\tools\divtest -- secdiv
    // 23 tones 400-2600 Hz, RX2 phase 2 pi f 1 ms (one weight cannot follow), equal independent noise,
    // ~27 dB S/N in 0.5 Hz: MRC per section should give ~+3 dB, one weight ~0.
    static double _startDb;
    static int SecDivCheck()
    {
        int fs = 48000, n = fs * 6, blk = 1024, nt = 23;
        // local: RX2 = noise antenna - station 17 dB down, local noise ~10 dB over the thermal on both,
        // delayed 1 ms on RX1 (RX1/RX2 ratio turns with frequency: one weight cannot null it)
        double Snr(int mode, int sections, bool local = false, bool weak = false, bool ctun = false, double tone = 1, bool qrm2 = false)
        {
            var rnd = new Random(1);
            double G() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            var f = new double[nt]; var ph = new double[nt];
            for (int t = 0; t < nt; t++) { f[t] = 400 + 100 * t; ph[t] = rnd.NextDouble() * 2 * Math.PI; }
            var a = new double[2 * n]; var b = new double[2 * n]; var y = new double[2 * n];
            var u = new double[2 * n];
            if (local) for (int i = 0; i < 2 * n; i++) u[i] = 30 * G();
            int d = fs / 1000;
            for (int i = 0; i < n; i++)
            {
                if (local && i >= d)
                {
                    a[2 * i] += u[2 * (i - d)]; a[2 * i + 1] += u[2 * (i - d) + 1];
                    b[2 * i] += u[2 * i]; b[2 * i + 1] += u[2 * i + 1];
                }
                double ar = 0, ai = 0, br = 0, bi = 0;
                for (int t = 0; t < nt; t++)
                {
                    double p = 2 * Math.PI * f[t] * i / fs + ph[t], q = p + 2 * Math.PI * f[t] * 0.001;
                    ar += Math.Cos(p); ai += Math.Sin(p); br += Math.Cos(q); bi += Math.Sin(q);
                }
                // keyed like syllables: 0.25 s on / 0.25 s off, 50 ms raised-cosine edges (auto needs pauses
                // for its noise covariance; the soft edges keep the keying sidebands off the noise points)
                double tk = (i % (fs / 2)) / (double)fs, env = tk >= 0.25 ? 0 : tk < 0.05 ? 0.5 - 0.5 * Math.Cos(Math.PI * tk / 0.05)
                    : tk > 0.2 ? 0.5 + 0.5 * Math.Cos(Math.PI * (tk - 0.2) / 0.05) : 1;
                ar *= env * tone; ai *= env * tone; br *= env * tone; bi *= env * tone;
                double sb = local || weak ? 0.14 : 1;               // weak: RX2 station 17 dB down, own noise
                // qrm2: QRM only RX2 hears, +10 dB over the noise, on with the station (never in a pause)
                if (qrm2) { b[2 * i] += env * 30 * G(); b[2 * i + 1] += env * 30 * G(); }
                a[2 * i] += ar + 10 * G(); a[2 * i + 1] += ai + 10 * G();
                b[2 * i] += sb * br + 10 * G(); b[2 * i + 1] += sb * bi + 10 * G();
            }
            if (mode < 0) y = a;
            else
            {
                create_divEXT(0, 1, 2, blk); SetEXTDIVOutput(0, 2);
                // ctun: the first half on pure noise (filter 4000-6400 Hz, the tones outside it), then the filter
                // moves onto the tones without a reset - as a VFO move with CTUN
                SetEXTDIVSections(mode, sections, 0.2, fs, ctun ? 4000 : 300, ctun ? 6400 : 2700);
                var ha = System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned);
                var hb = System.Runtime.InteropServices.GCHandle.Alloc(b, System.Runtime.InteropServices.GCHandleType.Pinned);
                var hy = System.Runtime.InteropServices.GCHandle.Alloc(y, System.Runtime.InteropServices.GCHandleType.Pinned);
                for (int s = 0; s + blk <= n; s += blk)
                {
                    if (ctun && s == n / 2 / blk * blk) SetEXTDIVSections(mode, sections, 0.2, fs, 300, 2700);
                    xdivEXT(0, blk, new[] { ha.AddrOfPinnedObject() + 16 * s, hb.AddrOfPinnedObject() + 16 * s }, hy.AddrOfPinnedObject() + 16 * s);
                }
                ha.Free(); hb.Free(); hy.Free();
                SetEXTDIVSections(0, sections, 0.2, fs, 300, 2700);
                // start-up (after the one-FFT delay, first 0.5 s): power against RX1 alone - weights from a
                // few frames must not pour RX2 noise in (they ramp in from RX1)
                int d0 = 2048, d1 = fs / 2; double py = 0, pa = 0;
                for (int i = d0; i < d1; i++) { py += y[2 * i] * y[2 * i] + y[2 * i + 1] * y[2 * i + 1]; pa += a[2 * (i - 1024)] * a[2 * (i - 1024)] + a[2 * (i - 1024) + 1] * a[2 * (i - 1024) + 1]; }
                _startDb = 10 * Math.Log10(py / pa);
            }
            // last 2 s: tones vs 11 points between each pair (a few points only would scatter +-1 dB)
            int m0 = n - 2 * fs, len = 2 * fs;
            double Dft(double fr)
            {
                double r = 0, im = 0;
                for (int i = 0; i < len; i++)
                {
                    double p = -2 * Math.PI * fr * i / fs, c = Math.Cos(p), sn = Math.Sin(p), xr = y[2 * (m0 + i)], xi = y[2 * (m0 + i) + 1];
                    r += xr * c - xi * sn; im += xr * sn + xi * c;
                }
                return r * r + im * im;
            }
            double sp = 0, np = 0;
            for (int t = 0; t < nt; t++) { sp += Dft(f[t]); for (int o = 25; o <= 75; o += 5) np += Dft(f[t] + o) / 11; }
            if (Environment.GetEnvironmentVariable("SD_DEBUG") != null) Console.WriteLine($"    mode {mode} sec {sections} ctun {ctun}: signal {10 * Math.Log10(sp):F1} noise {10 * Math.Log10(np):F1}");
            return 10 * Math.Log10(sp / np);
        }
        double r1 = Snr(-1, 0), m1 = Snr(1, 1), m20 = Snr(1, 20), c20 = Snr(2, 20);
        Console.WriteLine($"rx1 {r1:F1} dB, MRC 1 section {m1:F1}, MRC 20 {m20:F1}, co-phase 20 {c20:F1}");
        bool ok = m20 > r1 + 2 && m20 > m1 + 2 && c20 > r1 + 2;
        double lr1 = Snr(-1, 0, true), n1 = Snr(3, 1, true), n50 = Snr(3, 50, true), start50 = _startDb, lm20 = Snr(1, 20, true);
        Console.WriteLine($"noise antenna: rx1 {lr1:F1} dB, null 1 section {n1:F1}, null 50 {n50:F1} (first 0.5 s {start50:+0.0;-0.0} dB vs RX1), MRC 20 {lm20:F1}");
        ok &= n50 > lr1 + 5 && n50 > n1 + 3 && start50 < 1;
        // auto (max-SINR) must match the best mode in each case and never lift the background
        double a20 = Snr(4, 20), la50 = Snr(4, 50, true);
        double wr1 = Snr(-1, 0, false, true), wa20 = Snr(4, 20, false, true), wm20 = Snr(1, 20, false, true), wc20 = Snr(2, 20, false, true), wn20 = Snr(3, 20, false, true);
        Console.WriteLine($"auto: equal antennas {a20:F1} dB, noise antenna (50) {la50:F1}");
        Console.WriteLine($"RX1 dominant: rx1 {wr1:F1} dB, auto 20 {wa20:F1}, MRC 20 {wm20:F1}, co-phase 20 {wc20:F1}, null 20 {wn20:F1}");
        ok &= a20 > m20 - 1 && la50 > n50 - 1.5 && wa20 > wr1 - 0.3;      // general, so within ~1 dB of the best
        // learned on pure noise, then tuned (CTUN) onto the station: the bins under it must not carry a null
        // learned while the station sat outside the filter
        // (a strong station, +20 dB: per bin far over the noise, so a wrong null weight runs into its cap)
        double cr1 = Snr(-1, 0, false, true, true, 10), cn20 = Snr(3, 20, false, true, true, 10), ca20 = Snr(4, 20, false, true, true, 10);
        Console.WriteLine($"noise -> strong station (CTUN): rx1 {cr1:F1} dB, null 20 {cn20:F1}, auto 20 {ca20:F1}");
        ok &= cn20 > cr1 - 1 && ca20 > cr1 - 1;
        // RX1 = main antenna: QRM heard only on the (weak) RX2 must not be pulled into the output
        double qr1 = Snr(-1, 0, false, true, false, 1, true), qa20 = Snr(4, 20, false, true, false, 1, true), qm20 = Snr(1, 20, false, true, false, 1, true);
        Console.WriteLine($"QRM on RX2 only: rx1 {qr1:F1} dB, auto 20 {qa20:F1}, MRC 20 {qm20:F1}");
        ok &= qa20 > qr1 - 0.5;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    static Dictionary<string, double> Run(string path, int[] sections, double tau)
    {
        byte[] b = File.ReadAllBytes(path);
        if (BitConverter.ToInt16(b, 22) != 4) throw new Exception("not a 4-channel diversity recording");
        int fs = BitConverter.ToInt32(b, 24);
        var m = Regex.Match(Path.GetFileName(path), @"_vfo(-?\d+)_f(-?\d+)_(-?\d+)");
        int vfo = int.Parse(m.Groups[1].Value), lo = int.Parse(m.Groups[2].Value) - vfo, hi = int.Parse(m.Groups[3].Value) - vfo;
        if (lo < 0 && hi > 0) throw new Exception("filter on both sides of the carrier: SSB only");
        bool lsb = hi <= 0;
        int N = (int)((long)fs * Na / AudioRate);
        if ((N & (N - 1)) != 0 || N < Na) throw new Exception("rate " + fs + " not 48/96/192/384k");
        int n = (b.Length - 44) / 16, hop = N / 2;
        double binHz = (double)fs / N;
        int k0 = (int)Math.Ceiling(lo / binHz), k1 = (int)Math.Floor(hi / binHz), nb = k1 - k0 + 1;

        // panadapter frame z = Q + jI, VFO mixed down to 0 Hz
        var z1 = new double[2 * n]; var z2 = new double[2 * n];
        for (int i = 0; i < n; i++)
        {
            double ph = -2 * Math.PI * vfo * (double)i / fs, c = Math.Cos(ph), s = Math.Sin(ph);
            int o = 44 + 16 * i;
            double i1 = BitConverter.ToSingle(b, o), q1 = BitConverter.ToSingle(b, o + 4);
            double i2 = BitConverter.ToSingle(b, o + 8), q2 = BitConverter.ToSingle(b, o + 12);
            z1[2 * i] = q1 * c - i1 * s; z1[2 * i + 1] = q1 * s + i1 * c;
            z2[2 * i] = q2 * c - i2 * s; z2[2 * i + 1] = q2 * s + i2 * c;
        }

        // STFT, sqrt-Hann analysis + synthesis at 50% overlap (sums to 1), band bins only
        int frames = (n - N) / hop + 1;
        var X1 = Stft(z1, N, hop, frames, k0, nb); var X2 = Stft(z2, N, hop, frames, k0, nb);
        Console.WriteLine($"{Path.GetFileName(path)}: {n / (double)fs:F1} s at {fs}, {(lsb ? "LSB" : "USB")} {Math.Abs(lo)}-{Math.Abs(hi)} Hz, {nb} bins of {binHz:F1} Hz, tau {tau} s");

        // equal noise on both antennas (MRC with a unit eigenvector assumes it): RX2 scaled once
        double[] n1 = NoiseFloor(X1, frames, nb), n2 = NoiseFloor(X2, frames, nb);
        double g2 = Math.Sqrt(Median(n1) / Median(n2));
        for (int i = 0; i < X2.Length; i++) X2[i] *= g2;
        var noise = new double[nb];
        for (int k = 0; k < nb; k++) noise[k] = (n1[k] + n2[k] * g2 * g2) / 2;
        Console.WriteLine($"RX2 noise {20 * Math.Log10(1 / g2):+0.0;-0.0} dB vs RX1 (RX2 scaled to match)");
        Coherence(X1, X2, frames, nb, noise, Math.Max(1, (int)Math.Round(0.5 * fs / hop)));
        NoiseCoherence(X1, X2, frames, nb, n1);

        var res = new Dictionary<string, double>();
        string bas = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path));
        void Out(string name, double[] Y)
        {
            res[name] = Report(name, Y, frames, nb, out double snr);
            res["SNR " + name] = snr;
            WriteAudio(bas + "_" + name + ".wav", Y, frames, nb, k0, lsb);
        }
        Out("rx1", X1);
        Out("rx2", X2);
        foreach (int sct in sections) Out("S" + sct, Combine(X1, X2, frames, nb, noise, sct, tau * fs / hop, false));
        foreach (int sct in sections) Out("E" + sct, Combine(X1, X2, frames, nb, noise, sct, tau * fs / hop, true));
        foreach (int hz in new[] { 12, 47, 200, 0 })
            Out(hz > 0 ? "N" + hz : "Nall", Null(X1, X2, frames, nb, n1, hz > 0 ? (int)(hz / binHz / 2) : nb, NullTau * fs / hop, NullMaxGainDb));
        foreach (int hz in new[] { 12, 47, 200, 0 })
            Out(hz > 0 ? "A" + hz : "Aall", Auto(X1, X2, frames, nb, hz > 0 ? (int)(hz / binHz / 2) : nb, (double)fs / hop));
        return res;
    }

    // complex [frame][bin] as re,im pairs
    static double[] Stft(double[] z, int N, int hop, int frames, int k0, int nb)
    {
        var fft = new Fft(N);
        var w = new float[N];
        for (int i = 0; i < N; i++) w[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
        var re = new float[N]; var im = new float[N];
        var X = new double[2 * frames * nb];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < N; i++)
            {
                int d = fft.BitRev[i], s = 2 * (f * hop + i);
                re[d] = (float)z[s] * w[i]; im[d] = (float)z[s + 1] * w[i];
            }
            fft.Transform(re, im);
            for (int k = 0; k < nb; k++)
            {
                int kk = (k0 + k) & (N - 1);
                X[2 * (f * nb + k)] = re[kk]; X[2 * (f * nb + k) + 1] = im[kk];
            }
        }
        return X;
    }

    // mean noise power per bin: 10th percentile over time (speech pauses) / 0.1054 (exponential)
    static double[] NoiseFloor(double[] X, int frames, int nb)
    {
        var r = new double[nb]; var p = new double[frames];
        for (int k = 0; k < nb; k++)
        {
            for (int f = 0; f < frames; f++) p[f] = Pow(X, f * nb + k);
            Array.Sort(p);
            r[k] = Math.Max(1e-30, p[frames / 10] / 0.1054);
        }
        return r;
    }

    static double Pow(double[] X, int c) => X[2 * c] * X[2 * c] + X[2 * c + 1] * X[2 * c + 1];
    static double Median(double[] a) { var s = (double[])a.Clone(); Array.Sort(s); return s[s.Length / 2]; }

    // how frequency-selective the channel is: coherence of one weight over the band vs per 1/20 band,
    // in 0.5 s blocks (the sky moves), averaged over blocks
    static void Coherence(double[] X1, double[] X2, int frames, int nb, double[] noise, int block)
    {
        var cr = new double[nb]; var ci = new double[nb]; var p1 = new double[nb]; var p2 = new double[nb];
        double whole = 0, part = 0; int blocks = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
                if (ar * ar + ai * ai + br * br + bi * bi < 6 * noise[k]) continue;
                cr[k] += ar * br + ai * bi; ci[k] += ai * br - ar * bi;
                p1[k] += ar * ar + ai * ai; p2[k] += br * br + bi * bi;
            }
            if ((f + 1) % block != 0) continue;
            if (p1.Sum() > 0)
            {
                double sum = 0; int w = Math.Max(1, nb / 20), cnt = 0;
                for (int a = 0; a + w <= nb; a += w) { sum += Coh(a, a + w); cnt++; }
                whole += Coh(0, nb); part += sum / cnt; blocks++;
            }
            Array.Clear(cr); Array.Clear(ci); Array.Clear(p1); Array.Clear(p2);
        }
        Console.WriteLine($"coherence RX1-RX2 in 0.5 s blocks: whole band {whole / Math.Max(1, blocks):F2}, per 1/20 band {part / Math.Max(1, blocks):F2}");

        double Coh(int a, int e)
        {
            double r = 0, i = 0, q1 = 0, q2 = 0;
            for (int k = a; k < e; k++) { r += cr[k]; i += ci[k]; q1 += p1[k]; q2 += p2[k]; }
            return q1 * q2 > 0 ? Math.Sqrt(r * r + i * i) / Math.Sqrt(q1 * q2) : 0;
        }
    }

    // Can the noise be nulled: coherence of the noise-only cells (as in Null) over the whole recording,
    // per bin (median) and with one weight for the band; best null = -10 log(1 - coh^2).
    static void NoiseCoherence(double[] X1, double[] X2, int frames, int nb, double[] n1)
    {
        var cr = new double[nb]; var ci = new double[nb]; var p1 = new double[nb]; var p2 = new double[nb];
        for (int f = 0; f < frames; f++)
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
                if (ar * ar + ai * ai >= 2 * n1[k]) continue;
                cr[k] += ar * br + ai * bi; ci[k] += ai * br - ar * bi;
                p1[k] += ar * ar + ai * ai; p2[k] += br * br + bi * bi;
            }
        var per = new double[nb];
        for (int k = 0; k < nb; k++) per[k] = p1[k] * p2[k] > 0 ? Math.Sqrt(cr[k] * cr[k] + ci[k] * ci[k]) / Math.Sqrt(p1[k] * p2[k]) : 0;
        double whole = Math.Sqrt(cr.Sum() * cr.Sum() + ci.Sum() * ci.Sum()) / Math.Sqrt(p1.Sum() * p2.Sum()), med = Median(per);
        double Depth(double c) => -10 * Math.Log10(Math.Max(1e-6, 1 - c * c));
        Console.WriteLine($"noise coherence RX1-RX2: per bin median {med:F2} (null {Depth(med):F1} dB), one weight {whole:F2} (null {Depth(whole):F1} dB)");
    }

    // Per bin: covariance R = <[z1 z2][z1 z2]^H>, averaged over time (tau frames, only cells with signal,
    // so speech pauses hold the weights) and over a sliding window of nb/sections bins (1 = whole filter,
    // the single weight of today's diversity). y = v^H z with v the main eigenvector, phase referenced to RX1:
    // RX2 rotated onto RX1 and weighted by its share of the signal; noise stays at the per-antenna level.
    // equalGain: RX2 co-phased and raised to RX1's signal level instead (equal-gain combining), for comparison.
    static double[] Combine(double[] X1, double[] X2, int frames, int nb, double[] noise, int sections, double tauFrames, bool equalGain)
    {
        double a = Math.Min(1, 1 / tauFrames);
        var r11 = new double[nb]; var r22 = new double[nb]; var cr = new double[nb]; var ci = new double[nb];
        var s11 = new double[nb + 1]; var s22 = new double[nb + 1]; var scr = new double[nb + 1]; var sci = new double[nb + 1];
        int half = sections <= 1 ? nb : Math.Max(0, nb / sections / 2);
        var Y = new double[X1.Length];
        for (int f = 0; f < frames; f++)
        {
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
                double p1 = ar * ar + ai * ai, p2 = br * br + bi * bi;
                if (p1 + p2 < 6 * noise[k]) continue;                 // < ~+5 dB over the noise of both
                r11[k] += a * (p1 - r11[k]); r22[k] += a * (p2 - r22[k]);
                cr[k] += a * (ar * br + ai * bi - cr[k]);             // z1 * conj(z2)
                ci[k] += a * (ai * br - ar * bi - ci[k]);
            }
            for (int k = 0; k < nb; k++)
            {
                s11[k + 1] = s11[k] + r11[k]; s22[k + 1] = s22[k] + r22[k];
                scr[k + 1] = scr[k] + cr[k]; sci[k + 1] = sci[k] + ci[k];
            }
            for (int k = 0; k < nb; k++)
            {
                int lo = Math.Max(0, k - half), hi = Math.Min(nb, k + half + 1);
                double A = s11[hi] - s11[lo], B = s22[hi] - s22[lo], Cr = scr[hi] - scr[lo], Ci = sci[hi] - sci[lo];
                double cm = Math.Sqrt(Cr * Cr + Ci * Ci);
                double v1, wr, wi;                                     // y = v1 z1 + (wr + j wi) z2
                if (equalGain) { double g = cm > 0 && B > 0 ? Math.Sqrt(A / B) / cm : 0; v1 = 1; wr = g * Cr; wi = g * Ci; }
                else if (cm <= 0) { v1 = A >= B ? 1 : 0; wr = 1 - v1; wi = 0; }
                else
                {
                    double l = (A + B) / 2 + Math.Sqrt((A - B) * (A - B) / 4 + cm * cm);
                    double nrm = Math.Sqrt(cm * cm + (l - A) * (l - A));
                    v1 = cm / nrm; wr = (l - A) * Cr / cm / nrm; wi = (l - A) * Ci / cm / nrm;
                }
                int c = f * nb + k;
                double br = X2[2 * c], bi = X2[2 * c + 1];
                Y[2 * c] = v1 * X1[2 * c] + wr * br - wi * bi;
                Y[2 * c + 1] = v1 * X1[2 * c + 1] + wr * bi + wi * br;
            }
        }
        return Y;
    }

    const double NullTau = 2, NullMaxGainDb = 20;

    // Twin of mode 4 (auto) in wdsp/secdiv.c: noise covariance Rn from pauses (2 s), Rx from all cells
    // (1 s), per bin the max-SINR weight of SdAuto over +-half bins. fps = frames per second.
    static double[] Auto(double[] X1, double[] X2, int frames, int nb, int half, double fps)
    {
        double an = Math.Min(1, 1 / fps), a2 = Math.Min(1, 1 / (2 * fps)), a4 = Math.Min(1, 1 / fps);
        var q11 = new double[nb]; var q22 = new double[nb]; var qr = new double[nb]; var qi = new double[nb]; var cnt = new long[nb];
        var r11 = new double[nb]; var r22 = new double[nb]; var cr = new double[nb]; var ci = new double[nb];
        var P = new double[8][]; for (int i = 0; i < 8; i++) P[i] = new double[nb + 1];
        var Y = new double[X1.Length];
        double T1 = 0, T2 = 0, th = 1 + 3 / Math.Sqrt(nb);
        for (int f = 0; f < frames; f++)
        {
            double P1 = 0, P2 = 0;
            for (int k = 0; k < nb; k++) { P1 += Pow(X1, f * nb + k); P2 += Pow(X2, f * nb + k); }
            bool pause = T1 > 0 && P1 < th * T1 && P2 < th * T2;
            if (T1 <= 0 || P1 < th * T1) T1 += (T1 <= 0 ? 1 : an) * (P1 - T1); else T1 *= 1 + 0.05 * an;
            if (T2 <= 0 || P2 < th * T2) T2 += (T2 <= 0 ? 1 : an) * (P2 - T2); else T2 *= 1 + 0.05 * an;
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
                double p1 = ar * ar + ai * ai, p2 = br * br + bi * bi, xr = ar * br + ai * bi, xi = ai * br - ar * bi;
                if (pause)
                {
                    double ak = Math.Max(a2, 1.0 / ++cnt[k]);
                    q11[k] += ak * (p1 - q11[k]); q22[k] += ak * (p2 - q22[k]); qr[k] += ak * (xr - qr[k]); qi[k] += ak * (xi - qi[k]);
                }
                r11[k] += a4 * (p1 - r11[k]); r22[k] += a4 * (p2 - r22[k]); cr[k] += a4 * (xr - cr[k]); ci[k] += a4 * (xi - ci[k]);
            }
            double[][] src = { r11, r22, cr, ci, q11, q22, qr, qi };
            for (int i = 0; i < 8; i++) for (int k = 0; k < nb; k++) P[i][k + 1] = P[i][k] + src[i][k];
            for (int k = 0; k < nb; k++)
            {
                int lo = Math.Max(0, k - half), hi = Math.Min(nb, k + half + 1);
                double S(int i) => P[i][hi] - P[i][lo];
                SdAuto(S(0), S(1), S(2), S(3), S(4), S(5), S(6), S(7), out double v1, out double wr, out double wi);
                int c = f * nb + k;
                double br = X2[2 * c], bi = X2[2 * c + 1];
                Y[2 * c] = v1 * X1[2 * c] + wr * br - wi * bi;
                Y[2 * c + 1] = v1 * X1[2 * c + 1] + wr * bi + wi * br;
            }
        }
        return Y;
    }

    // literal port of sd_auto (wdsp/secdiv.c)
    static void SdAuto(double A, double B, double Cr, double Ci, double a, double b, double cr, double ci, out double v1, out double wr, out double wi)
    {
        v1 = 1; wr = wi = 0;
        if (a <= 0 || b <= 0) return;
        double den = Math.Max(A - a, 0) + 0.2 * a, Sr = Cr - cr, Si = Ci - ci;
        double hr = Sr / den, hi = -Si / den;
        double E = Math.Max(0, B - b - (Sr * Sr + Si * Si) / den);
        double p = 1.01 * a, r = 1.01 * (b + E);
        if (p * r - cr * cr - ci * ci <= 0) return;
        double w1r = r - (cr * hr - ci * hi), w1i = -(cr * hi + ci * hr);
        double w2r = p * hr - cr, w2i = p * hi + ci;
        double D = w1r + w2r * hr + w2i * hi;
        if (D <= 0) return;
        double m1 = Math.Sqrt(w1r * w1r + w1i * w1i);
        if (m1 <= 0) { v1 = 0; wr = w2r / D; wi = -w2i / D; return; }
        v1 = m1 / D;
        wr = (w2r * w1r + w2i * w1i) / (m1 * D); wi = (w2r * w1i - w2i * w1r) / (m1 * D);
    }

    // Noise nulling, RX1 = main antenna, RX2 = noise antenna: y = z1 - w z2 with the Wiener weight
    // w = <z1 z2*> / <|z2|^2> per bin, from noise-only cells (RX1 under 2x its floor, so the station is
    // not nulled with it), averaged over tauFrames and over +-half bins. |w| capped at maxGainDb over the
    // noise-floor-matched RX2: a weight from a weak coherence would only pour RX2 noise in.
    static double[] Null(double[] X1, double[] X2, int frames, int nb, double[] n1, int half, double tauFrames, double maxGainDb)
    {
        double a = Math.Min(1, 1 / tauFrames), cap = Math.Pow(10, maxGainDb / 20);
        var r22 = new double[nb]; var cr = new double[nb]; var ci = new double[nb]; var cnt = new long[nb];
        var s22 = new double[nb + 1]; var scr = new double[nb + 1]; var sci = new double[nb + 1];
        var Y = new double[X1.Length];
        // pause detector as in wdsp/secdiv.c: whole filter at the tracked floor on both antennas
        double T1 = 0, T2 = 0, th = 1 + 3 / Math.Sqrt(nb), an = Math.Min(1, 2 / tauFrames);   // floor tracker ~1 s (tauFrames = 2 s)
        for (int f = 0; f < frames; f++)
        {
            double P1 = 0, P2 = 0;
            for (int k = 0; k < nb; k++) { P1 += Pow(X1, f * nb + k); P2 += Pow(X2, f * nb + k); }
            bool pause = T1 > 0 && P1 < th * T1 && P2 < th * T2;
            if (T1 <= 0 || P1 < th * T1) T1 += (T1 <= 0 ? 1 : an) * (P1 - T1); else T1 *= 1 + 0.05 * an;
            if (T2 <= 0 || P2 < th * T2) T2 += (T2 <= 0 ? 1 : an) * (P2 - T2); else T2 *= 1 + 0.05 * an;
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
                if (!pause) continue;
                double ak = Math.Max(a, 1.0 / ++cnt[k]);                // plain mean until the average is full
                r22[k] += ak * (br * br + bi * bi - r22[k]);
                cr[k] += ak * (ar * br + ai * bi - cr[k]);              // z1 * conj(z2)
                ci[k] += ak * (ai * br - ar * bi - ci[k]);
            }
            for (int k = 0; k < nb; k++) { s22[k + 1] = s22[k] + r22[k]; scr[k + 1] = scr[k] + cr[k]; sci[k + 1] = sci[k] + ci[k]; }
            for (int k = 0; k < nb; k++)
            {
                int lo = Math.Max(0, k - half), hi = Math.Min(nb, k + half + 1);
                double B = s22[hi] - s22[lo], wr = 0, wi = 0;
                if (B > 0)
                {
                    wr = (scr[hi] - scr[lo]) / B; wi = (sci[hi] - sci[lo]) / B;
                    double mag = Math.Sqrt(wr * wr + wi * wi);
                    if (mag > cap) { wr *= cap / mag; wi *= cap / mag; }
                }
                int c = f * nb + k;
                double br = X2[2 * c], bi = X2[2 * c + 1];
                Y[2 * c] = X1[2 * c] - (wr * br - wi * bi);
                Y[2 * c + 1] = X1[2 * c + 1] - (wr * bi + wi * br);
            }
        }
        return Y;
    }

    // Prints S/N of the whole recording (signal power over the noise floor); returns the share (%) of signal frames'
    // cells under the noise (selective-fading holes; speech has empty bins too, so compare, not absolute)
    static double _refPow;                                              // rx1 output power, for the "level" column
    static double Report(string name, double[] Y, int frames, int nb, out double snr)
    {
        double[] nf = NoiseFloor(Y, frames, nb);
        double nsum = nf.Sum(), ptot = 0;
        long cells = 0, holes = 0;
        for (int f = 0; f < frames; f++)
        {
            double pf = 0;
            for (int k = 0; k < nb; k++) pf += Pow(Y, f * nb + k);
            ptot += pf;
            if (pf < 4 * nsum) continue;                               // speech pause
            for (int k = 0; k < nb; k++) { cells++; if (Pow(Y, f * nb + k) < nf[k]) holes++; }
        }
        snr = 10 * Math.Log10(Math.Max(1e-6, ptot / frames / nsum - 1));
        if (name == "rx1") _refPow = ptot;
        Console.WriteLine($"  {name,-4} S/N {snr,6:F1} dB   holes {100.0 * holes / Math.Max(1, cells),5:F1} %   level {10 * Math.Log10(ptot / _refPow),6:F1} dB vs rx1");
        return 100.0 * holes / Math.Max(1, cells);
    }

    // band bins -> real SSB audio (USB: +f, LSB: mirrored), overlap-add, 16-bit mono at -20 dBFS RMS
    static void WriteAudio(string path, double[] Y, int frames, int nb, int k0, bool lsb)
    {
        var fft = new Fft(Na);
        var w = new double[Na];
        for (int i = 0; i < Na; i++) w[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Na));
        var outp = new double[(frames + 1) * Na / 2];
        var re = new float[Na]; var im = new float[Na];
        var Ar = new double[Na]; var Ai = new double[Na];
        for (int f = 0; f < frames; f++)
        {
            Array.Clear(Ar); Array.Clear(Ai);
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k, m = Math.Abs(k0 + k);
                if (m == 0 || m >= Na / 2) continue;
                double yr = Y[2 * c], yi = lsb ? -Y[2 * c + 1] : Y[2 * c + 1];
                Ar[m] = yr; Ai[m] = yi; Ar[Na - m] = yr; Ai[Na - m] = -yi;
            }
            for (int i = 0; i < Na; i++) { int d = fft.BitRev[i]; re[d] = (float)Ar[i]; im[d] = (float)-Ai[i]; }
            fft.Transform(re, im);                                     // conj trick: real part = inverse FFT
            for (int i = 0; i < Na; i++) outp[f * Na / 2 + i] += re[i] * w[i];
        }
        double rms = Math.Sqrt(outp.Sum(x => x * x) / outp.Length), g = rms > 0 ? 0.1 * 32767 / rms : 0;
        using var bw = new BinaryWriter(File.Create(path));
        int data = outp.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + data); bw.Write("WAVEfmt "u8); bw.Write(16);
        bw.Write((short)1); bw.Write((short)1); bw.Write(AudioRate); bw.Write(AudioRate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(data);
        foreach (double x in outp) bw.Write((short)Math.Clamp(Math.Round(x * g), -32768, 32767));
    }

    // Synthetic test: speech-like source (300-2700 Hz noise in 150-300 ms bursts, 0.1-0.6 s pauses) over 3 sky paths
    // (0 / 0.8 / 1.7 ms, coherence bandwidth well under the filter), each with its own Doppler on each
    // antenna, plus independent noise of equal power: ~10 dB S/N in the filter for one antenna.
    // noiseAnt: RX2 as a noise antenna (see Main).
    static void Synth(string path, int fs, int seconds, bool noiseAnt)
    {
        var rnd = new Random(7);
        int n = fs * seconds, L = 1; while (L < n) L <<= 1;
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        var fft = new Fft(L);
        // complex Gaussian noise in flo..fhi Hz, re,im pairs
        double[] Band(double flo, double fhi)
        {
            var re = new float[L]; var im = new float[L];
            for (int i = 0; i < n; i++) { int d = fft.BitRev[i]; re[d] = (float)Gauss(); im[d] = (float)Gauss(); }
            fft.Transform(re, im);
            var Br = new float[L]; var Bi = new float[L];
            for (int k = 0; k < L; k++)
            {
                double fhz = (double)k * fs / L;
                bool pass = fhz >= flo && fhz <= fhi;
                int d = fft.BitRev[k];
                Br[d] = pass ? re[k] : 0; Bi[d] = pass ? -im[k] : 0;   // conj trick inverse
            }
            fft.Transform(Br, Bi);
            var z = new double[2 * n];
            for (int i = 0; i < n; i++) { z[2 * i] = Br[i]; z[2 * i + 1] = -Bi[i]; }
            return z;
        }
        double MeanPow(double[] z) { double p = 0; for (int i = 0; i < z.Length; i++) p += z[i] * z[i]; return p / (z.Length / 2); }
        var bsrc = Band(300, 2700);
        var s = new double[2 * n];
        bool on = false; int next = 0;
        for (int i = 0; i < n; i++)
        {
            if (i >= next) { on = !on; next = i + (int)(fs * (on ? 0.15 + 0.15 * rnd.NextDouble() : 0.1 + 0.5 * rnd.NextDouble())); }
            s[2 * i] = on ? bsrc[2 * i] : 0; s[2 * i + 1] = on ? bsrc[2 * i + 1] : 0;
        }
        double ps = MeanPow(s);
        int[] delay = { 0, (int)(0.0008 * fs), (int)(0.0017 * fs) };
        double[] gain = { 1, 0.9, 0.7 };
        double sig = gain.Sum(g => g * g) * ps;                     // ps already counts the pauses
        double nsd = Math.Sqrt(sig / Math.Pow(10, 1.0) * fs / 2400 / 2);   // 10 dB in 2.4 kHz
        // local sources: RX2 gets amp*u, RX1 h*amp*u; 10 dB over the thermal noise in their band on RX1
        double[][] u = noiseAnt ? new[] { Band(300, 1500), Band(1500, 2700) } : new double[0][];
        double[] hr = { 0.5 * Math.Cos(1), 0.7 * Math.Cos(-2) }, hi = { 0.5 * Math.Sin(1), 0.7 * Math.Sin(-2) };
        var amp = new double[u.Length];
        for (int q = 0; q < u.Length; q++) amp[q] = Math.Sqrt(10 * 2 * nsd * nsd * 1200 / fs / MeanPow(u[q]) / (hr[q] * hr[q] + hi[q] * hi[q]));
        var buf = new byte[44 + 16 * n];
        for (int ant = 0; ant < 2; ant++)
        {
            double sg = noiseAnt && ant == 1 ? Math.Pow(10, -17 / 20.0) : 1;
            var fd = Enumerable.Range(0, 3).Select(_ => (rnd.NextDouble() - 0.5) * 0.6).ToArray();   // +-0.3 Hz
            var p0 = Enumerable.Range(0, 3).Select(_ => rnd.NextDouble() * 2 * Math.PI).ToArray();
            for (int i = 0; i < n; i++)
            {
                double zr = nsd * Gauss(), zi = nsd * Gauss();
                for (int p = 0; p < 3; p++)
                {
                    int j = i - delay[p]; if (j < 0) continue;
                    double ph = p0[p] + 2 * Math.PI * fd[p] * i / fs, c = Math.Cos(ph), sn = Math.Sin(ph);
                    zr += sg * gain[p] * (s[2 * j] * c - s[2 * j + 1] * sn);
                    zi += sg * gain[p] * (s[2 * j] * sn + s[2 * j + 1] * c);
                }
                for (int q = 0; q < u.Length; q++)
                {
                    double ur = amp[q] * u[q][2 * i], ui = amp[q] * u[q][2 * i + 1];
                    if (ant == 0) { zr += hr[q] * ur - hi[q] * ui; zi += hr[q] * ui + hi[q] * ur; }
                    else { zr += ur; zi += ui; }
                }
                // file holds I, Q with z = Q + jI
                BitConverter.TryWriteBytes(new Span<byte>(buf, 44 + 16 * i + 8 * ant, 4), (float)zi);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 44 + 16 * i + 8 * ant + 4, 4), (float)zr);
            }
        }
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write("RIFF"u8); bw.Write(36 + 16 * n); bw.Write("WAVEfmt "u8); bw.Write(16);
        bw.Write((short)3); bw.Write((short)4); bw.Write(fs); bw.Write(fs * 16); bw.Write((short)16); bw.Write((short)32);
        bw.Write("data"u8); bw.Write(16 * n);
        bw.Write(buf, 44, buf.Length - 44);
    }
}
