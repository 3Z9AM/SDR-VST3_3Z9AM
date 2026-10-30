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
//   ... -- replay <Diversity_...wav> [mode sections fine pass2]  (through the real-time secdiv in wdsp.dll, from bin\x64\Release)
// Also per-section noise nulling (RX2 = noise antenna): N<Hz> = section width, Nall = whole filter.
// M = phase mask: cells whose RX2-RX1 phase matches the station's are kept, others pulled down (W = power-only
// mask on RX1 for comparison, MW = both); "mask" = synthetic check with true S/N from separate signal/noise.
// Q = noise phase mask: cells with the steady phase of the local noise (learned in pauses) pulled down.
// O = null with one weight learned outside the RX filter (every quiet frame there, not only pauses);
// "outside" = synthetic check with one wideband local source.
// L<s> = long null: weight from ALL cells over <s> seconds (no pauses needed) - a steady-phase interferer
// dominates, a sky-wave station whose phase wanders averages out; "long" = synthetic check.
// B<kHz> / Ball = null learned over the whole IQ band (every free cell, stations/carriers/DC/filter left out),
// weight smoothed over +-<kHz> (Ball = one); prints the noise coherence map across the band.
// dirs <wav> = are there several noise directions? null depth in pauses vs the time x frequency window the weight
// is learned from (leave-one-out), and a histogram of the RX2-RX1 phase in windows with high local coherence.
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
        // k=v anywhere: secdiv test knob k (SetEXTDIVSectionsKnob), e.g. 6=0.3 = clean
        foreach (string kv in args.Where(x => x.Contains('=')))
            SetEXTDIVSectionsKnob(int.Parse(kv.Split('=')[0]), double.Parse(kv.Split('=')[1], CultureInfo.InvariantCulture));
        args = args.Where(x => !x.Contains('=')).ToArray();
        if (args.Length == 0) { Console.WriteLine("usage: divtest <Diversity_...wav> [1,5,10,20,40] [0.2] | synth"); return 1; }
        if (args[0] == "decimate") return DecimateCheck(args[1]);
        if (args[0] == "dirs") return Dirs(args[1]);
        if (args[0] == "replay") return Replay(args[1], args.Skip(2).Select(int.Parse).ToArray());
        if (args[0] == "inject")
        {
            // k=v: secdiv test knob k (SetEXTDIVSectionsKnob)
            foreach (string kv in args.Skip(4).Where(x => x.Contains('=')))
                SetEXTDIVSectionsKnob(int.Parse(kv.Split('=')[0]), double.Parse(kv.Split('=')[1], CultureInfo.InvariantCulture));
            foreach (string s in args[3].Split(','))
                Inject(args[1], args[2], double.Parse(s, CultureInfo.InvariantCulture), args.Skip(4).Select(int.Parse).ToArray());
            return 0;
        }
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
            // independent noise: max-SINR with the band noise must combine as the pause-learned one does
            bool oka = r["SNR AB47"] > r["SNR A47"] - 1;
            Console.WriteLine(oka ? "PASS (max-SINR band, combining)" : "FAIL (max-SINR band, combining)");
            ok &= oka;

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
        if (args[0] == "mask") return MaskCheck();
        if (args[0] == "outside") return OutsideCheck();
        if (args[0] == "long") return LongCheck();
        Run(args[0], sections, tau);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void create_divEXT(int id, int run, int nr, int size);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVOutput(int id, int output);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVSections(int mode, int sections, double tau, int rate, int lo, int hi);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVSectionsFine(int fine);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVSectionsPass2(int on);
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
        // flat: local noise without the 1 ms delay (one weight can null it); cont: the station never pauses
        double Snr(int mode, int sections, bool local = false, bool weak = false, bool ctun = false, double tone = 1, bool qrm2 = false, bool flat = false, bool cont = false)
        {
            var rnd = new Random(1);
            double G() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            var f = new double[nt]; var ph = new double[nt];
            for (int t = 0; t < nt; t++) { f[t] = 400 + 100 * t; ph[t] = rnd.NextDouble() * 2 * Math.PI; }
            var a = new double[2 * n]; var b = new double[2 * n]; var y = new double[2 * n];
            var u = new double[2 * n];
            if (local) for (int i = 0; i < 2 * n; i++) u[i] = 30 * G();
            int d = flat ? 0 : fs / 1000;
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
                if (cont) env = 1;
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
        // station never pauses, flat wideband local noise: only the weight learned beside the filter can null it
        double fr1 = Snr(-1, 0, true, false, false, 1, false, true, true), fn50 = Snr(3, 50, true, false, false, 1, false, true, true), fstart = _startDb;
        Console.WriteLine($"no pauses, flat local noise: rx1 {fr1:F1} dB, null 50 {fn50:F1} (first 0.5 s {fstart:+0.0;-0.0} dB vs RX1)");
        // max-SINR has no pauses here: it must take the band noise (and not lift RX1 at the start)
        double fa = Snr(4, 50, true, false, false, 1, false, true, true), astart = _startDb;
        Console.WriteLine($"  max-SINR 50 {fa:F1} dB (first 0.5 s {astart:+0.0;-0.0})");
        ok &= fn50 > fr1 + 5 && fa > fr1 + 5 && astart < 1;
        // strong station that never pauses (CW, digital), equal antennas: the 1.5 s floor follows the station, so
        // "pauses" taught max-SINR the station as noise; with the band noise it must combine as with pauses
        double sr1 = Snr(-1, 0, false, false, false, 3, false, false, true), sa20 = Snr(4, 20, false, false, false, 3, false, false, true);
        double sm20 = Snr(1, 20, false, false, false, 3, false, false, true);
        Console.WriteLine($"strong station, no pauses: rx1 {sr1:F1} dB, max-SINR 20 {sa20:F1}, MRC 20 {sm20:F1}");
        ok &= sa20 > sm20 - 1;
        // fine (FFT 4x, ~12 Hz bins, 4x the sections): at the same section width in Hz (fine 5 = normal 20) it must
        // do what the normal FFT does; fine 20 (4x narrower) loses here - this channel is flat, wider always wins
        SetEXTDIVSectionsFine(1);
        double fa5 = Snr(4, 5), fa20 = Snr(4, 20), fm5 = Snr(1, 5), fla50 = Snr(4, 50, true);
        SetEXTDIVSectionsFine(0); SetEXTDIVSectionsPass2(0);
        Console.WriteLine($"fine: auto equal antennas 5 {fa5:F1} dB (normal 20 {a20:F1}), 20 {fa20:F1}, MRC 5 {fm5:F1} (normal 20 {m20:F1}), noise antenna (50) {fla50:F1} (normal {la50:F1})");
        // noise antenna: max-SINR Rx over tau (0.2 s) is only ~5 fine frames - this channel never changes, so a longer
        // mean always wins here (Rx 1 s: -0.2 dB); recordings with a wandering station phase gain from 0.2 s (replay)
        ok &= fa5 > a20 - 0.5 && fm5 > m20 - 0.5 && fla50 > la50 - 2;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    // A recording through the real-time secdiv (wdsp.dll, from bin\x64\Release) as the radio runs it, from a
    // reset: per second the output against RX1 alone - floor = minimum in-filter frame power (the noise in
    // pauses), mean = all frames (the station kept); then S/N for 0.5-3 s and from 3 s, audio <wav>_sd<mode>.wav.
    //   replay <wav> [mode 4] [sections 60] [fine 1]
    static int Replay(string path, int[] opt)
    {
        int mode = opt.Length > 0 ? opt[0] : 4, sections = opt.Length > 1 ? opt[1] : 60, fine = opt.Length > 2 ? opt[2] : 1, pass2 = opt.Length > 3 ? opt[3] : 0;
        Rec r = Load(path, 0.2);
        byte[] b = File.ReadAllBytes(path);
        var m = Regex.Match(Path.GetFileName(path), @"_vfo(-?\d+)_f(-?\d+)_(-?\d+)");
        int lo = int.Parse(m.Groups[2].Value), hi = int.Parse(m.Groups[3].Value), fs = r.fs, n = (b.Length - 44) / 16, blk = 1024;
        var a = new double[2 * n]; var c = new double[2 * n]; var y = new double[2 * n];
        for (int i = 0; i < n; i++)
        {
            int o = 44 + 16 * i;
            a[2 * i] = BitConverter.ToSingle(b, o); a[2 * i + 1] = BitConverter.ToSingle(b, o + 4);
            c[2 * i] = BitConverter.ToSingle(b, o + 8); c[2 * i + 1] = BitConverter.ToSingle(b, o + 12);
        }
        create_divEXT(0, 1, 2, blk); SetEXTDIVOutput(0, 2);
        SetEXTDIVSectionsFine(fine); SetEXTDIVSectionsPass2(pass2);
        SetEXTDIVSections(mode, sections, 0.2, fs, -hi, -lo);
        var ha = System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned);
        var hc = System.Runtime.InteropServices.GCHandle.Alloc(c, System.Runtime.InteropServices.GCHandleType.Pinned);
        var hy = System.Runtime.InteropServices.GCHandle.Alloc(y, System.Runtime.InteropServices.GCHandleType.Pinned);
        for (int s = 0; s + blk <= n; s += blk)
            xdivEXT(0, blk, new[] { ha.AddrOfPinnedObject() + 16 * s, hc.AddrOfPinnedObject() + 16 * s }, hy.AddrOfPinnedObject() + 16 * s);
        ha.Free(); hc.Free(); hy.Free();
        SetEXTDIVSections(0, sections, 0.2, fs, -hi, -lo);
        SetEXTDIVSectionsFine(0);

        // output (native I + jQ, one secdiv FFT late) into the panadapter frame of Load, same STFT as rx1
        int d = 1024;                                    // sd_alloc
        while (d * 46.875 < fs) d <<= 1;
        d <<= 2 * fine;
        var zy = new double[2 * n];
        for (int i = 0; i + d < n; i++)
        {
            double ph = -2 * Math.PI * r.vfo * (double)i / fs, cs = Math.Cos(ph), sn = Math.Sin(ph);
            double yi = y[2 * (i + d)], yq = y[2 * (i + d) + 1];
            zy[2 * i] = yq * cs - yi * sn; zy[2 * i + 1] = yq * sn + yi * cs;
        }
        var Y = Stft(zy, r.N, r.hop, r.frames, r.k0, r.nb);
        Mains(r, "RX1", r.z1); Mains(r, "RX2", r.z2); Mains(r, "secdiv", zy);
        Mains(r, "RX2 @42", r.z2, 42); Mains(r, "sd @42", zy, 42);   // control: no mains there = the bias of the search
        Mains(r, "RX1 @71", r.z1, 71.3); Mains(r, "sd @71", zy, 71.3); Mains(r, "RX2 @71", r.z2, 71.3);   // 29.10 recordings: ~71.3 Hz on RX1 only
        double Fp(double[] X, int f, double g) { double p = 0; for (int k = 0; k < r.nb; k++) p += Pow(X, f * r.nb + k); return p / (g * g); }
        int per = (int)Math.Round((double)fs / r.hop), last = r.frames - (d + r.N) / r.hop;
        Console.WriteLine($"secdiv mode {mode}, {sections} sections, fine {fine}, 2 pass {pass2}: per second, output vs RX1 (floor = min frame, mean = all)");
        Console.WriteLine("  t s   RX1 floor   floor dB   mean dB");
        for (int f0 = 0; f0 + per <= last; f0 += per)
        {
            double m1 = double.MaxValue, my = double.MaxValue, s1 = 0, sy = 0;
            for (int f = f0; f < f0 + per; f++)
            {
                double p1 = Fp(r.X1, f, 1), py = Fp(Y, f, 1);
                m1 = Math.Min(m1, p1); my = Math.Min(my, py); s1 += p1; sy += py;
            }
            Console.WriteLine($"{f0 / (double)per,5:F0} {10 * Math.Log10(m1),10:F1} {10 * Math.Log10(my / m1),+10:+0.0;-0.0} {10 * Math.Log10(sy / s1),+9:+0.0;-0.0}");
        }
        // settled part (from 3 s), S/N as the offline variants
        void Part(string title, int fa, int fb)
        {
            int nf = fb - fa, len = 2 * nf * r.nb;
            var sx1 = new double[len]; var sy2 = new double[len];
            Array.Copy(r.X1, 2 * fa * r.nb, sx1, 0, len); Array.Copy(Y, 2 * fa * r.nb, sy2, 0, len);
            Console.WriteLine(title);
            Report("rx1", sx1, nf, r.nb, out _);
            Report("sd", sy2, nf, r.nb, out _);
        }
        Part("0.5-3 s:", per / 2, 3 * per);
        Part("from 3 s:", 3 * per, last);
        // stripes (user: cut-out bands on the zoomed panadapter, sounds like NR): per 1 s the output/RX1 power per bin,
        // its spread across neighbouring bins in dB (the RX1-alone value by itself is ~0)
        double rip = 0; int nr = 0;
        for (int f0 = 3 * per; f0 + per <= last; f0 += per, nr++)
        {
            var db = new double[r.nb];
            for (int k = 0; k < r.nb; k++)
            {
                double p1 = 0, py = 0;
                for (int f = f0; f < f0 + per; f++) { p1 += Pow(r.X1, f * r.nb + k); py += Pow(Y, f * r.nb + k); }
                db[k] = 10 * Math.Log10(py / p1);
            }
            double s = 0;
            for (int k = 1; k < r.nb; k++) s += (db[k] - db[k - 1]) * (db[k] - db[k - 1]);
            rip += Math.Sqrt(s / (r.nb - 1) / 2);
        }
        Console.WriteLine($"stripes: bin-to-bin spread of output/RX1 over 1 s {rip / Math.Max(1, nr):F2} dB");
        WriteAudio(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path)) + $"_sd{mode}{(pass2 != 0 ? "p2" : "")}.wav", Y, last, r.nb, r.k0, r.lsb);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern int GetEXTDIVSectionsWeights(double[] w, int size);
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern double GetEXTDIVSectionsGain();
    [System.Runtime.InteropServices.DllImport("wdsp.dll")] static extern void SetEXTDIVSectionsKnob(int id, double v);

    // Semi-synthetic truth (replay S/N rewards gating): real noise of both antennas from the filter of noiseWav
    // plus a real station (both antennas, its own fading) cut from the filter of stationWav (+-150 Hz), moved
    // onto the noise filter and scaled to snr dB on RX1 (mean powers in the filter). The mix goes through
    // secdiv; its weights, read after every frame, are applied to the station and the noise apart in the same
    // STFT, so station level and noise of the output are known. Prints one line, from 3 s.
    static int Inject(string noiseWav, string stationWav, double snr, int[] opt)
    {
        int mode = opt.Length > 0 ? opt[0] : 4, sections = opt.Length > 1 ? opt[1] : 60, fine = opt.Length > 2 ? opt[2] : 1;
        (double[] z1, double[] z2, int fs, int lo, int hi) Read(string p)
        {
            byte[] b = File.ReadAllBytes(p);
            var m = Regex.Match(Path.GetFileName(p), @"_vfo(-?\d+)_f(-?\d+)_(-?\d+)");
            int n = (b.Length - 44) / 16;
            var a = new double[2 * n]; var c = new double[2 * n];      // native I + jQ
            for (int i = 0; i < n; i++)
            {
                int o = 44 + 16 * i;
                a[2 * i] = BitConverter.ToSingle(b, o); a[2 * i + 1] = BitConverter.ToSingle(b, o + 4);
                c[2 * i] = BitConverter.ToSingle(b, o + 8); c[2 * i + 1] = BitConverter.ToSingle(b, o + 12);
            }
            // name offsets are in the panadapter frame (Q + jI): native = mirror
            return (a, c, BitConverter.ToInt32(b, 24), -int.Parse(m.Groups[3].Value), -int.Parse(m.Groups[2].Value));
        }
        var nz = Read(noiseWav); var st = Read(stationWav);
        if (nz.fs != st.fs) throw new Exception("rates differ");
        int fs = nz.fs, len = Math.Min(nz.z1.Length, st.z1.Length) / 2;
        double shift = (nz.lo + nz.hi) / 2.0 - (st.lo + st.hi) / 2.0;
        var s1 = BandShift(st.z1, len, fs, st.lo - 150, st.hi + 150, shift);
        var s2 = BandShift(st.z2, len, fs, st.lo - 150, st.hi + 150, shift);

        int N = 1024; while (N * 46.875 < fs) N <<= 1; N <<= 2 * fine;
        int hop = N / 2, frames = len / hop, k0 = (int)Math.Ceiling(nz.lo * (double)N / fs), nb = (int)Math.Floor(nz.hi * (double)N / fs) - k0 + 1;
        // secdiv framing: frame f = samples [(f+1) hop - N, (f+1) hop), zeros before the start
        double[] Sd(double[] z) { var p = new double[2 * (len + N)]; Array.Copy(z, 0, p, 2 * (N - hop), 2 * len); return Stft(p, N, hop, frames, k0, nb); }
        double[] S1 = Sd(s1), S2 = Sd(s2), N1 = Sd(nz.z1), N2 = Sd(nz.z2);
        double ps = 0, pn = 0;
        for (int i = 0; i < S1.Length / 2; i++) { ps += Pow(S1, i); pn += Pow(N1, i); }
        double gs = Math.Sqrt(Math.Pow(10, snr / 10) * pn / ps);
        for (int i = 0; i < S1.Length; i++) { S1[i] *= gs; S2[i] *= gs; }
        var a = new double[2 * len]; var c = new double[2 * len];
        for (int i = 0; i < 2 * len; i++) { a[i] = nz.z1[i] + gs * s1[i]; c[i] = nz.z2[i] + gs * s2[i]; }

        create_divEXT(0, 1, 2, hop); SetEXTDIVOutput(0, 2);
        SetEXTDIVSectionsFine(fine); SetEXTDIVSectionsPass2(0);
        double tauX = double.Parse(Environment.GetEnvironmentVariable("DIVTEST_TAU") ?? "0.2", CultureInfo.InvariantCulture);
        SetEXTDIVSections(mode, sections, tauX, fs, nz.lo, nz.hi);
        var ha = System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned);
        var hc = System.Runtime.InteropServices.GCHandle.Alloc(c, System.Runtime.InteropServices.GCHandleType.Pinned);
        var y = new double[2 * hop];
        var hy = System.Runtime.InteropServices.GCHandle.Alloc(y, System.Runtime.InteropServices.GCHandleType.Pinned);
        var w = new double[3 * nb];
        int f0 = (int)Math.Round(3.0 * fs / hop);
        var Ys = new double[S1.Length]; var Yn = new double[S1.Length];   // output station / noise apart
        // noise gain of the weights per cell vs RX1, from the noise covariance of the whole record per bin:
        // its spread in time per bin = flicker (gating, NR-like warble)
        var R = new double[4 * nb];
        for (int f = 0; f < frames; f++)
            for (int k = 0; k < nb; k++)
            {
                int i = f * nb + k;
                R[4 * k] += Pow(N1, i); R[4 * k + 1] += Pow(N2, i);
                R[4 * k + 2] += N1[2 * i] * N2[2 * i] + N1[2 * i + 1] * N2[2 * i + 1]; R[4 * k + 3] += N1[2 * i + 1] * N2[2 * i] - N1[2 * i] * N2[2 * i + 1];
            }
        var G = new double[frames * nb];
        for (int f = 0; f < frames; f++)
        {
            xdivEXT(0, hop, new[] { ha.AddrOfPinnedObject() + 16 * f * hop, hc.AddrOfPinnedObject() + 16 * f * hop }, hy.AddrOfPinnedObject());
            int n = GetEXTDIVSectionsWeights(w, nb);
            double g = GetEXTDIVSectionsGain();
            for (int k = 0; k < n; k++)
            {
                int i = f * nb + k;
                double v = w[3 * k], wr = g * w[3 * k + 1], wi = g * w[3 * k + 2];
                Ys[2 * i] = v * S1[2 * i] + wr * S2[2 * i] - wi * S2[2 * i + 1]; Ys[2 * i + 1] = v * S1[2 * i + 1] + wr * S2[2 * i + 1] + wi * S2[2 * i];
                Yn[2 * i] = v * N1[2 * i] + wr * N2[2 * i] - wi * N2[2 * i + 1]; Yn[2 * i + 1] = v * N1[2 * i + 1] + wr * N2[2 * i + 1] + wi * N2[2 * i];
                // E|v z1 + c z2|^2 = v^2 R11 + |c|^2 R22 + 2 v Re(conj(c) R12), R12 = <z1 z2*>
                G[i] = (v * v * R[4 * k] + (wr * wr + wi * wi) * R[4 * k + 1] + 2 * v * (wr * R[4 * k + 2] + wi * R[4 * k + 3])) / R[4 * k];
            }
        }
        double fl = 0;
        for (int k = 0; k < nb; k++)
        {
            double m = 0, m2 = 0; int cn = 0;
            for (int f = f0; f < frames; f++) { double d = 10 * Math.Log10(Math.Max(1e-6, G[f * nb + k])); m += d; m2 += d * d; cn++; }
            fl += Math.Sqrt(Math.Max(0, m2 / cn - (m / cn) * (m / cn)));
        }
        ha.Free(); hc.Free(); hy.Free();
        SetEXTDIVSections(0, sections, 0.2, fs, nz.lo, nz.hi);
        SetEXTDIVSectionsFine(0);
        Console.Write($"inject {snr,4:F0} dB  mode {mode} {sections} f{fine}:  flicker {fl / nb:F2} dB");
        Score(S1, N1, Ys, Yn, frames, nb, f0, pn / frames);
        if (Environment.GetEnvironmentVariable("DIVTEST_ORACLE") != null) Oracle(S1, S2, N1, N2, frames, nb, f0, pn / frames, (double)fs / hop);
        return 0;
    }

    // From frame f0, vs RX1 alone (S1 + N1). S/N rewards gating (pauses and empty cells pushed down, the station
    // with them); SDR does not: the output station is fitted to a*S1 (one complex a, as a level control would)
    // and whatever is left of it - a cut or bent station - counts as error with the noise.
    static void Score(double[] S1, double[] N1, double[] Ys, double[] Yn, int frames, int nb, int f0, double act)
    {
        double si = 0, ni = 0, so = 0, no = 0, xr = 0, xi = 0;
        for (int i = f0 * nb; i < frames * nb; i++)
        {
            si += Pow(S1, i); ni += Pow(N1, i); so += Pow(Ys, i); no += Pow(Yn, i);
            xr += S1[2 * i] * Ys[2 * i] + S1[2 * i + 1] * Ys[2 * i + 1]; xi += S1[2 * i] * Ys[2 * i + 1] - S1[2 * i + 1] * Ys[2 * i];
        }
        double ar = xr / si, ai = xi / si, e = 0, seg1 = 0, sego = 0; int nseg = 0;
        for (int f = f0; f < frames; f++)
        {
            double fe = 0, fs1 = 0, fn1 = 0;
            for (int k = 0; k < nb; k++)
            {
                int i = f * nb + k;
                double tr = ar * S1[2 * i] - ai * S1[2 * i + 1], ti = ar * S1[2 * i + 1] + ai * S1[2 * i];
                double dr = Ys[2 * i] - tr + Yn[2 * i], di = Ys[2 * i + 1] - ti + Yn[2 * i + 1];
                fe += dr * dr + di * di; fs1 += Pow(S1, i); fn1 += Pow(N1, i);
            }
            e += fe;
            // speech frames (station over the mean noise on RX1): mean frame SDR in dB
            if (fs1 > act) { seg1 += 10 * Math.Log10(fs1 / fn1); sego += 10 * Math.Log10((ar * ar + ai * ai) * fs1 / fe); nseg++; }
        }
        double snr1 = 10 * Math.Log10(si / ni);
        Console.WriteLine($"  S/N {10 * Math.Log10(so / no) - snr1,+5:+0.0;-0.0}  SDR {10 * Math.Log10((ar * ar + ai * ai) * si / e) - snr1,+5:+0.0;-0.0}" +
            $"  speech SDR {(sego - seg1) / Math.Max(1, nseg),+5:+0.0;-0.0}  station {10 * Math.Log10(so / si),+5:+0.0;-0.0}  noise {10 * Math.Log10(no / ni),+5:+0.0;-0.0}  (RX1 S/N {snr1:F1})");
    }

    // the ceiling: max-SINR as sd_auto (h = (1, conj(S12)/S11), w = Rn^-1 h / h^H Rn^-1 h) from the true station and
    // noise covariances apart - station over 0.2 s, noise over tauN s, +-100 Hz; printed for a few tauN
    static void Oracle(double[] S1, double[] S2, double[] N1, double[] N2, int frames, int nb, int f0, double act, double fps)
    {
        foreach (double tauN in new[] { 0.2, 2.0, 1e9 })
        {
            double aS = Math.Min(1, 1 / (0.2 * fps)), aN = Math.Min(1, 1 / (tauN * fps));
            var s = new double[4 * nb]; var q = new double[4 * nb];   // 11, 22, re12, im12 per bin
            var Ys = (double[])S1.Clone(); var Yn = (double[])N1.Clone();
            int cnt = 0;
            for (int f = 0; f < frames; f++)
            {
                cnt++;
                double aq = Math.Max(aN, 1.0 / cnt);
                for (int k = 0; k < nb; k++)
                {
                    int i = f * nb + k;
                    double ar = S1[2 * i], ai = S1[2 * i + 1], br = S2[2 * i], bi = S2[2 * i + 1];
                    s[4 * k] += aS * (ar * ar + ai * ai - s[4 * k]); s[4 * k + 1] += aS * (br * br + bi * bi - s[4 * k + 1]);
                    s[4 * k + 2] += aS * (ar * br + ai * bi - s[4 * k + 2]); s[4 * k + 3] += aS * (ai * br - ar * bi - s[4 * k + 3]);
                    ar = N1[2 * i]; ai = N1[2 * i + 1]; br = N2[2 * i]; bi = N2[2 * i + 1];
                    q[4 * k] += aq * (ar * ar + ai * ai - q[4 * k]); q[4 * k + 1] += aq * (br * br + bi * bi - q[4 * k + 1]);
                    q[4 * k + 2] += aq * (ar * br + ai * bi - q[4 * k + 2]); q[4 * k + 3] += aq * (ai * br - ar * bi - q[4 * k + 3]);
                }
                if (f < f0) continue;
                int h = Math.Max(1, nb / 25);    // ~ +-100 Hz at fine 12 Hz bins (nb ~ 230 for 2.7 kHz)
                for (int k = 0; k < nb; k++)
                {
                    double A = 0, Cr = 0, Ci = 0, a = 0, b = 0, cr = 0, ci = 0;
                    for (int j = Math.Max(0, k - h); j <= Math.Min(nb - 1, k + h); j++)
                    { A += s[4 * j]; Cr += s[4 * j + 2]; Ci += s[4 * j + 3]; a += q[4 * j]; b += q[4 * j + 1]; cr += q[4 * j + 2]; ci += q[4 * j + 3]; }
                    if (A <= 0) continue;
                    double hr = Cr / A, hi = -Ci / A, p = 1.0001 * a, r = 1.0001 * b;
                    double w1r = r - (cr * hr - ci * hi), w1i = -(cr * hi + ci * hr), w2r = p * hr - cr, w2i = p * hi + ci;
                    double D = w1r + w2r * hr + w2i * hi;
                    if (D <= 0) continue;
                    // y = (conj(w1) z1 + conj(w2) z2) / D
                    int i = f * nb + k;
                    void Y(double[] X1, double[] X2, double[] O)
                    {
                        O[2 * i] = (w1r * X1[2 * i] + w1i * X1[2 * i + 1] + w2r * X2[2 * i] + w2i * X2[2 * i + 1]) / D;
                        O[2 * i + 1] = (w1r * X1[2 * i + 1] - w1i * X1[2 * i] + w2r * X2[2 * i + 1] - w2i * X2[2 * i]) / D;
                    }
                    Y(S1, S2, Ys); Y(N1, N2, Yn);
                }
            }
            Console.Write($"   oracle noise {(tauN > 1e8 ? "all" : tauN + " s"),5}:");
            Score(S1, N1, Ys, Yn, frames, nb, f0, act);
        }
    }

    // z (native, re/im pairs, len samples) band-passed to lo..hi Hz and moved up by shift Hz; block FFT, sqrt-Hann 50%
    static double[] BandShift(double[] z, int len, int fs, double lo, double hi, double shift)
    {
        const int M = 8192;
        var fft = new Fft(M);
        var win = new float[M];
        for (int i = 0; i < M; i++) win[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / M));
        var o = new double[2 * len];
        var re = new float[M]; var im = new float[M];
        for (int s = -M / 2; s < len; s += M / 2)
        {
            for (int i = 0; i < M; i++)
            {
                int t = s + i, d = fft.BitRev[i];
                re[d] = t >= 0 && t < len ? (float)z[2 * t] * win[i] : 0; im[d] = t >= 0 && t < len ? (float)z[2 * t + 1] * win[i] : 0;
            }
            fft.Transform(re, im);
            for (int k = 0; k < M; k++)
            {
                double hz = (k < M / 2 ? k : k - M) * (double)fs / M;
                if (hz < lo || hz > hi) { re[k] = 0; im[k] = 0; }
            }
            // inverse = conj(fft(conj X)) / M
            var r2 = new float[M]; var i2 = new float[M];
            for (int k = 0; k < M; k++) { r2[fft.BitRev[k]] = re[k]; i2[fft.BitRev[k]] = -im[k]; }
            fft.Transform(r2, i2);
            for (int i = 0; i < M; i++)
            {
                int t = s + i;
                if (t < 0 || t >= len) continue;
                double ph = 2 * Math.PI * shift * t / fs, cs = Math.Cos(ph), sn = Math.Sin(ph);
                double xr = r2[i] * win[i] / M, xi = -i2[i] * win[i] / M;
                o[2 * t] += xr * cs - xi * sn; o[2 * t + 1] += xr * sn + xi * cs;
            }
        }
        return o;
    }

    class Rec
    {
        public double[] X1, X2, n1, noise; public int frames, nb, k0, fs, hop; public bool lsb; public double binHz, g2;
        public double[] Xe1, Xe2; public int eOff, eW, eDc;   // band +- eOff bins (outer eW each side), IQ DC bin in it
        public double[] z1, z2; public int N, vfo;             // whole IQ band, VFO at 0 Hz (z2 not scaled)
    }
    const double OutGapHz = 300, OutWidthHz = 2000;

    // recording -> band STFTs, RX2 scaled to the RX1 noise floor
    static Rec Load(string path, double tau)
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
        int eW = (int)(OutWidthHz / binHz), eOff = eW + (int)(OutGapHz / binHz);
        var Xe1 = Stft(z1, N, hop, frames, k0 - eOff, nb + 2 * eOff); var Xe2 = Stft(z2, N, hop, frames, k0 - eOff, nb + 2 * eOff);
        Console.WriteLine($"{Path.GetFileName(path)}: {n / (double)fs:F1} s at {fs}, {(lsb ? "LSB" : "USB")} {Math.Abs(lo)}-{Math.Abs(hi)} Hz, {nb} bins of {binHz:F1} Hz, tau {tau} s");

        // equal noise on both antennas (MRC with a unit eigenvector assumes it): RX2 scaled once
        double[] n1 = NoiseFloor(X1, frames, nb), n2 = NoiseFloor(X2, frames, nb);
        double g2 = Math.Sqrt(Median(n1) / Median(n2));
        for (int i = 0; i < X2.Length; i++) X2[i] *= g2;
        for (int i = 0; i < Xe2.Length; i++) Xe2[i] *= g2;
        var noise = new double[nb];
        for (int k = 0; k < nb; k++) noise[k] = (n1[k] + n2[k] * g2 * g2) / 2;
        return new Rec { X1 = X1, X2 = X2, n1 = n1, noise = noise, frames = frames, nb = nb, k0 = k0, fs = fs, hop = hop, lsb = lsb, binHz = binHz, g2 = g2,
                         Xe1 = Xe1, Xe2 = Xe2, eOff = eOff, eW = eW, eDc = (int)Math.Round(-vfo / binHz) - (k0 - eOff),
                         z1 = z1, z2 = z2, N = N, vfo = vfo };
    }

    static Dictionary<string, double> Run(string path, int[] sections, double tau)
    {
        Rec r = Load(path, tau);
        double[] X1 = r.X1, X2 = r.X2, n1 = r.n1, noise = r.noise;
        int frames = r.frames, nb = r.nb, k0 = r.k0, fs = r.fs, hop = r.hop;
        bool lsb = r.lsb; double binHz = r.binHz, g2 = r.g2;
        Console.WriteLine($"RX2 noise {20 * Math.Log10(1 / g2):+0.0;-0.0} dB vs RX1 (RX2 scaled to match)");
        Coherence(X1, X2, frames, nb, noise, Math.Max(1, (int)Math.Round(0.5 * fs / hop)));
        NoiseCoherence(X1, X2, frames, nb, n1);
        PhaseSteadiness(X1, X2, frames, nb, n1, Pauses(X1, X2, frames, nb, NullTau * fs / hop), (int)Math.Round((double)fs / hop));

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
        // AL = station phase modelled across the filter (gain, phase, slope), AS = RX1 per bin where the measured
        // SINR of the max-SINR output is not better, ALS = both
        foreach (int hz in new[] { 12, 47 })
        {
            int hb = (int)(hz / binHz / 2);
            Out("AL" + hz, Auto(X1, X2, frames, nb, hb, (double)fs / hop, true));
            Out("AS" + hz, Auto(X1, X2, frames, nb, hb, (double)fs / hop, false, SafeDb));
            Out("ALS" + hz, Auto(X1, X2, frames, nb, hb, (double)fs / hop, true, SafeDb));
        }
        // masks lower the noise floor the S/N is measured against: compare M with W, not with the linear ones
        double[] gm = PhaseMask(X1, X2, frames, nb, noise, MaskTau * fs / hop), gw = PowerMask(X1, frames, nb, n1);
        Out("M", Apply(X1, gm));
        Out("W", Apply(X1, gw));
        Out("MW", Apply(X1, gm.Zip(gw, (x, y) => x * y).ToArray()));
        Out("O", Outside(r, NullTau * fs / hop));
        BroadStats bs = Broad(r);
        foreach (int khz in new[] { 1, 5, 20, 0 })
            Out(khz > 0 ? "B" + khz + "k" : "Ball", BroadApply(r, bs, khz > 0 ? khz * 1000 : 1e9));
        Out("B5kn", BroadApply(r, bs, 5000, true));
        // max-SINR with the band noise (+-5 kHz), per 47 Hz section and one weight for the filter
        foreach (int hz in new[] { 47, 0 })
            Out(hz > 0 ? "AB" + hz : "ABall", AutoBand(r, bs, 5000, hz > 0 ? (int)(hz / binHz / 2) : nb, (double)fs / hop));
        foreach (int sec in new[] { 30, 120 })
            Out("L" + sec, Null(X1, X2, frames, nb, n1, (int)(47 / binHz / 2), sec * (double)fs / hop, NullMaxGainDb, null, true));
        double[] gq = NoisePhaseMask(X1, X2, frames, nb, NullTau * fs / hop);
        Out("Q", Apply(X1, gq));
        Out("QW", Apply(X1, gq.Zip(gw, (x, y) => x * y).ToArray()));
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
    // model: FitLinearPhase on the window sums; safeDb: per bin the candidate output's own SINR (its power over
    // all cells and in the pauses, same rates as Rx/Rn) must beat RX1's by safeDb, else RX1 is passed through.
    static double[] Auto(double[] X1, double[] X2, int frames, int nb, int half, double fps, bool model = false, double safeDb = double.NaN)
    {
        double an = Math.Min(1, 1 / fps), a2 = Math.Min(1, 1 / (2 * fps)), a4 = Math.Min(1, 1 / fps);
        bool safe = !double.IsNaN(safeDb);
        var q11 = new double[nb]; var q22 = new double[nb]; var qr = new double[nb]; var qi = new double[nb]; var cnt = new long[nb];
        var r11 = new double[nb]; var r22 = new double[nb]; var cr = new double[nb]; var ci = new double[nb];
        var ry = new double[nb]; var qy = new double[nb]; var akq = new double[nb];
        var P = new double[10][]; for (int i = 0; i < 10; i++) P[i] = new double[nb + 1];
        var Wn = new double[10][]; for (int i = 0; i < 10; i++) Wn[i] = new double[nb];
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
                akq[k] = 0;
                if (pause)
                {
                    double ak = akq[k] = Math.Max(a2, 1.0 / ++cnt[k]);
                    q11[k] += ak * (p1 - q11[k]); q22[k] += ak * (p2 - q22[k]); qr[k] += ak * (xr - qr[k]); qi[k] += ak * (xi - qi[k]);
                }
                r11[k] += a4 * (p1 - r11[k]); r22[k] += a4 * (p2 - r22[k]); cr[k] += a4 * (xr - cr[k]); ci[k] += a4 * (xi - ci[k]);
            }
            double[][] src = { r11, r22, cr, ci, q11, q22, qr, qi, ry, qy };
            for (int i = 0; i < 10; i++) for (int k = 0; k < nb; k++) P[i][k + 1] = P[i][k] + src[i][k];
            for (int k = 0; k < nb; k++)
            {
                int lo = Math.Max(0, k - half), hi = Math.Min(nb, k + half + 1);
                for (int i = 0; i < 10; i++) Wn[i][k] = P[i][hi] - P[i][lo];
            }
            if (model) FitLinearPhase(Wn, nb);
            for (int k = 0; k < nb; k++)
            {
                SdAuto(Wn[0][k], Wn[1][k], Wn[2][k], Wn[3][k], Wn[4][k], Wn[5][k], Wn[6][k], Wn[7][k], out double v1, out double wr, out double wi);
                int c = f * nb + k;
                double br = X2[2 * c], bi = X2[2 * c + 1];
                double yr = v1 * X1[2 * c] + wr * br - wi * bi, yi = v1 * X1[2 * c + 1] + wr * bi + wi * br;
                if (safe)
                {
                    double p = yr * yr + yi * yi;
                    if (akq[k] > 0) qy[k] += akq[k] * (p - qy[k]);
                    ry[k] += a4 * (p - ry[k]);
                    if (!Better(Wn, k, safeDb)) { yr = X1[2 * c]; yi = X1[2 * c + 1]; }
                }
                Y[2 * c] = yr; Y[2 * c + 1] = yi;
            }
        }
        return Y;
    }

    const double SafeDb = 0.5;

    // Measured SINR of the max-SINR output (W[8] all cells, W[9] pauses - frames before, weights as they were)
    // vs RX1 (W[0], W[4]); +0.2 x noise on both signal parts so a bin without a station compares the noise
    // alone. The weights' own Ri cannot decide: MVDR beats RX1 under the Ri it was built from by construction -
    // only the output measured on new cells shows an estimate that was wrong.
    static bool Better(double[][] W, int k, double marginDb)
    {
        double A = W[0][k], a = W[4][k], Ry = W[8][k], Ny = W[9][k];
        if (a <= 0 || Ny <= 0) return false;
        double e = 0.2 * a;
        return (Math.Max(Ry - Ny, 0) + e) * a > Math.Pow(10, marginDb / 10) * (Math.Max(A - a, 0) + e) * Ny;
    }

    // Station part of the cross term, S = Rx12 - Rn12, replaced per bin by den x G e^j(psi + d k): one gain,
    // phase and phase slope per bin (path delay difference) for the whole filter instead of a noisy estimate
    // per section. d from the lag-1 product of S across bins, G e^j psi by least squares; den as in SdAuto.
    // Assumes a channel between the antennas that is flat apart from a delay.
    static void FitLinearPhase(double[][] W, int nb)
    {
        var den = new double[nb];
        double lr = 0, li = 0, pr = 0, pi = 0;
        for (int k = 0; k < nb; k++)
        {
            double a = W[4][k], sr = W[2][k] - W[6][k], si = W[3][k] - W[7][k];
            den[k] = a > 0 ? Math.Max(W[0][k] - a, 0) + 0.2 * a : 0;
            if (k > 0) { lr += sr * pr + si * pi; li += si * pr - sr * pi; }
            pr = sr; pi = si;
        }
        double d = Math.Atan2(li, lr), nr = 0, ni = 0, dd = 0;
        for (int k = 0; k < nb; k++)
        {
            double c = Math.Cos(d * k), s = Math.Sin(d * k), sr = W[2][k] - W[6][k], si = W[3][k] - W[7][k];
            nr += den[k] * (sr * c + si * s); ni += den[k] * (si * c - sr * s); dd += den[k] * den[k];
        }
        if (dd <= 0) return;
        double gr = nr / dd, gi = ni / dd;
        for (int k = 0; k < nb; k++)
        {
            double c = Math.Cos(d * k), s = Math.Sin(d * k);
            W[2][k] = W[6][k] + den[k] * (gr * c - gi * s); W[3][k] = W[7][k] + den[k] * (gr * s + gi * c);
        }
    }

    // max-SINR (Auto) with the noise covariance Rn from the free cells of the IQ band within +-halfHz of each
    // filter bin (Broad) instead of the speech pauses - learns without pauses, from far more cells. Rn per cell:
    // powers = plain means of the free bins, cross term = the gated cells' coherence scaled to those powers.
    // Rx as in Auto (all cells, 1 s, +-half filter bins). Rn is the whole-recording average (offline).
    static double[] AutoBand(Rec r, BroadStats st, double halfHz, int half, double fps)
    {
        int N = r.N, nb = r.nb, frames = r.frames, h = (int)Math.Min(N, halfHz / r.binHz);
        double[] X1 = r.X1, X2 = r.X2;
        var S = new double[7][]; for (int i = 0; i < 7; i++) S[i] = new double[N + 1];
        for (int i = 0; i < N; i++)
        {
            double[] v = { st.ok[i] ? st.u11[i] : 0, st.ok[i] ? st.u22[i] : 0, st.p11[i], st.p22[i], st.cr[i], st.ci[i], st.ok[i] ? 1 : 0 };
            for (int q = 0; q < 7; q++) S[q][i + 1] = S[q][i] + v[q];
        }
        // per filter bin: noise covariance of one cell
        var n11 = new double[nb]; var n22 = new double[nb]; var nr = new double[nb]; var ni = new double[nb];
        for (int j = 0; j < nb; j++)
        {
            int i = r.k0 + j + N / 2, lo = Math.Max(0, i - h), hi = Math.Min(N, i + h + 1);
            double W(int q) => S[q][hi] - S[q][lo];
            if (W(6) < 1 || W(2) <= 0 || W(3) <= 0) continue;
            n11[j] = W(0) / W(6); n22[j] = W(1) / W(6);
            double sc = Math.Sqrt(n11[j] * n22[j] / (W(2) * W(3)));            // coherence of the gated cells kept
            nr[j] = W(4) * sc; ni[j] = W(5) * sc;
        }
        var Pn = new double[4][]; for (int i = 0; i < 4; i++) Pn[i] = new double[nb + 1];
        double[][] nsrc = { n11, n22, nr, ni };
        for (int i = 0; i < 4; i++) for (int k = 0; k < nb; k++) Pn[i][k + 1] = Pn[i][k] + nsrc[i][k];
        double a4 = Math.Min(1, 1 / fps);
        var r11 = new double[nb]; var r22 = new double[nb]; var cr = new double[nb]; var ci = new double[nb];
        var P = new double[4][]; for (int i = 0; i < 4; i++) P[i] = new double[nb + 1];
        var Y = new double[X1.Length];
        for (int f = 0; f < frames; f++)
        {
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
                r11[k] += a4 * (ar * ar + ai * ai - r11[k]); r22[k] += a4 * (br * br + bi * bi - r22[k]);
                cr[k] += a4 * (ar * br + ai * bi - cr[k]); ci[k] += a4 * (ai * br - ar * bi - ci[k]);
            }
            double[][] src = { r11, r22, cr, ci };
            for (int i = 0; i < 4; i++) for (int k = 0; k < nb; k++) P[i][k + 1] = P[i][k] + src[i][k];
            for (int k = 0; k < nb; k++)
            {
                int lo = Math.Max(0, k - half), hi = Math.Min(nb, k + half + 1);
                double Sx(int i) => P[i][hi] - P[i][lo];
                double Sn(int i) => Pn[i][hi] - Pn[i][lo];
                SdAuto(Sx(0), Sx(1), Sx(2), Sx(3), Sn(0), Sn(1), Sn(2), Sn(3), out double v1, out double wr, out double wi);
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
    // Null weight learned outside the RX filter (user idea): bins OutGapHz..+OutWidthHz beyond both edges,
    // without the bins a station or carrier occupies (30 s mean over 2x the floor - offline; a slow tracker
    // in real time) and the IQ DC spur, in every frame where those bins sit at their floor - not only in
    // speech pauses. One weight for the whole filter: assumes the RX1/RX2 transfer of the noise is flat over
    // a few kHz (one wideband source, as in the 80 m noise recording).
    static double[] Outside(Rec r, double tauFrames)
    {
        int frames = r.frames, nb = r.nb, nbe = nb + 2 * r.eOff;
        double[] nfe = NoiseFloor(r.Xe1, frames, nbe);
        var bins = new List<int>();
        for (int j = 0; j < nbe; j++)
        {
            if (j >= r.eW && j < nbe - r.eW) continue;                    // gap + filter
            if (Math.Abs(j - r.eDc) <= Math.Max(2, (int)(200 / r.binHz))) continue;
            double m = 0; for (int f = 0; f < frames; f++) m += Pow(r.Xe1, f * nbe + j);
            if (m / frames <= 2 * nfe[j]) bins.Add(j);
        }
        int no = bins.Count;
        var O1 = new double[2 * frames * Math.Max(1, no)]; var O2 = new double[O1.Length];
        for (int f = 0; f < frames; f++)
            for (int q = 0; q < no; q++)
            {
                int s = 2 * (f * nbe + bins[q]), d = 2 * (f * no + q);
                O1[d] = r.Xe1[s]; O1[d + 1] = r.Xe1[s + 1]; O2[d] = r.Xe2[s]; O2[d + 1] = r.Xe2[s + 1];
            }
        bool[] learn = no > 0 ? Pauses(O1, O2, frames, no, tauFrames) : new bool[frames];
        bool[] inPause = Pauses(r.X1, r.X2, frames, nb, tauFrames);
        double a = Math.Min(1, 1 / tauFrames), cap = Math.Pow(10, NullMaxGainDb / 20);
        double r22 = 0, cr = 0, ci = 0, T22 = 0, Tcr = 0, Tci = 0, T11 = 0; long cnt = 0;
        var Y = new double[r.X1.Length];
        for (int f = 0; f < frames; f++)
        {
            if (learn[f])
            {
                double s22 = 0, scr = 0, sci = 0, s11 = 0;
                for (int q = 0; q < no; q++)
                {
                    int c = f * no + q;
                    Cross(O1, O2, c, out double xr, out double xi);
                    scr += xr; sci += xi; s22 += Pow(O2, c); s11 += Pow(O1, c);
                }
                double ak = Math.Max(a, 1.0 / ++cnt);
                r22 += ak * (s22 - r22); cr += ak * (scr - cr); ci += ak * (sci - ci);
                T22 += s22; Tcr += scr; Tci += sci; T11 += s11;
            }
            double wr = 0, wi = 0;
            if (r22 > 0)
            {
                wr = cr / r22; wi = ci / r22;
                double mag = Math.Sqrt(wr * wr + wi * wi);
                if (mag > cap) { wr *= cap / mag; wi *= cap / mag; }
            }
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double br = r.X2[2 * c], bi = r.X2[2 * c + 1];
                Y[2 * c] = r.X1[2 * c] - (wr * br - wi * bi);
                Y[2 * c + 1] = r.X1[2 * c + 1] - (wr * bi + wi * br);
            }
        }
        double coh = T11 * T22 > 0 ? Math.Sqrt(Tcr * Tcr + Tci * Tci) / Math.Sqrt(T11 * T22) : 0;
        Console.WriteLine($"outside the filter: {no} free bins, learning in {100.0 * learn.Count(x => x) / frames:F0} % of frames (pauses in the filter {100.0 * inPause.Count(x => x) / frames:F0} %), noise coherence there {coh:F2} (null {-10 * Math.Log10(Math.Max(1e-6, 1 - coh * coh)):F1} dB)");
        return Y;
    }

    // The long recording's decimator (Console/DiversityDecimator.cs, as DiversityForm uses it) on a full-rate
    // recording: the 24 kHz copy must give divtest the same numbers as the original
    static int DecimateCheck(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int fs = BitConverter.ToInt32(b, 24), dec = fs / 24000, n = (b.Length - 44) / 16;
        var m = Regex.Match(Path.GetFileName(path), @"^(.*_)(-?\d+)Hz_(\d+)_vfo(-?\d+)_f(-?\d+)_(-?\d+)\.wav$");
        int vfo = int.Parse(m.Groups[4].Value), lo = int.Parse(m.Groups[5].Value), hi = int.Parse(m.Groups[6].Value), sh = (lo + hi) / 2;
        string outPath = Path.Combine(Path.GetTempPath(), $"Diversity_dec_0Hz_24000_vfo{vfo - sh}_f{lo - sh}_{hi - sh}.wav");
        var d = new Thetis.DiversityDecimator(fs, dec, sh);
        var o = new byte[44 + 16 * (n / dec + 1)];
        int outs = 0;
        for (int i = 0; i < n; i++)
        {
            int p = 44 + 16 * i;
            if (!d.Push(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8), BitConverter.ToSingle(b, p + 12),
                out float i1, out float q1, out float i2, out float q2)) continue;
            int q = 44 + 16 * outs++;
            BitConverter.TryWriteBytes(new Span<byte>(o, q, 4), i1); BitConverter.TryWriteBytes(new Span<byte>(o, q + 4, 4), q1);
            BitConverter.TryWriteBytes(new Span<byte>(o, q + 8, 4), i2); BitConverter.TryWriteBytes(new Span<byte>(o, q + 12, 4), q2);
        }
        using (var bw = new BinaryWriter(File.Create(outPath)))
        {
            bw.Write("RIFF"u8); bw.Write(36 + 16 * outs); bw.Write("WAVEfmt "u8); bw.Write(16);
            bw.Write((short)3); bw.Write((short)4); bw.Write(24000); bw.Write(24000 * 16); bw.Write((short)16); bw.Write((short)32);
            bw.Write("data"u8); bw.Write(16 * outs);
            bw.Write(o, 44, 16 * outs);
        }
        var a = Run(path, new[] { 1 }, 0.2);
        var r = Run(outPath, new[] { 1 }, 0.2);
        bool ok = true;
        foreach (string k in new[] { "SNR rx1", "SNR rx2", "SNR S1", "SNR N47", "SNR A47" })
        {
            Console.WriteLine($"{k,-8} full rate {a[k],6:F1} dB, decimated {r[k],6:F1} dB");
            ok &= Math.Abs(a[k] - r[k]) < 0.3;
        }
        Console.WriteLine(ok ? "PASS (decimated recording)" : "FAIL (decimated recording)");
        return ok ? 0 : 1;
    }

    // Long null on a station of equal strength on both antennas plus a steady wideband local source: with the
    // sky paths' Doppler (+-0.3 Hz per path and antenna) the station's RX2-RX1 phase wanders and the 30 s
    // weight nulls the local noise only; with a steady station (no Doppler) it nulls the station too - the risk
    static int LongCheck()
    {
        var res = new Dictionary<string, double>();
        foreach (double dop in new[] { 0.3, 0.0 })
        {
            string path = Path.Combine(Path.GetTempPath(), $"Diversity_synthlong{dop:F1}_0Hz_48000_vfo0_f300_2700.wav".Replace(',', '.'));
            Synth(path, 48000, 30, false, 10, 0, true, dop);
            var r = Run(path, new int[0], 0.2);
            Console.WriteLine($"Doppler {dop} Hz: rx1 {r["SNR rx1"]:F1} dB, null in pauses {r["SNR Nall"]:F1}, long null 30 s {r["SNR L30"]:F1}");
            res[dop > 0 ? "wander" : "steady"] = r["SNR L30"] - r["SNR rx1"];
        }
        bool ok = res["wander"] > 5;
        Console.WriteLine(ok ? "PASS (long null, wandering station)" : "FAIL (long null, wandering station)");
        return ok ? 0 : 1;
    }

    // one wideband local source with a flat RX1/RX2 transfer, RX2 = noise antenna: the weight from outside
    // the filter should null it about as well as the one from the pauses
    static int OutsideCheck()
    {
        string path = Path.Combine(Path.GetTempPath(), "Diversity_synthwide_0Hz_48000_vfo0_f300_2700.wav");
        Synth(path, 48000, 30, true, 10, 0, true);
        var r = Run(path, new int[0], 0.2);
        bool ok = r["SNR O"] > r["SNR rx1"] + 5 && r["SNR O"] > r["SNR Nall"] - 2;
        Console.WriteLine(ok ? "PASS (null learned outside the filter)" : "FAIL (null learned outside the filter)");
        bool okb = r["SNR Ball"] > r["SNR rx1"] + 5 && r["SNR B5k"] > r["SNR Nall"] - 2;
        Console.WriteLine(okb ? "PASS (null learned over the whole band)" : "FAIL (null learned over the whole band)");
        ok &= okb;
        bool oka = r["SNR AB47"] > r["SNR rx1"] + 5 && r["SNR AB47"] > r["SNR Nall"] - 2;
        Console.WriteLine(oka ? "PASS (max-SINR with the band noise)" : "FAIL (max-SINR with the band noise)");
        ok &= oka;
        return ok ? 0 : 1;
    }

    // Null learned over the whole IQ band (user idea: the local noise and QRM keep one RX1/RX2 signature over
    // the 192 kHz, every station has another). Two passes over the full-band STFT: per-bin means, then the
    // covariance from free cells - bins a station or carrier occupies (mean over 2x the median of +-1 kHz) and
    // cells over 4x it (transient stations; drops ~2% of pure noise) left out, also the IQ DC (+-200 Hz), the band
    // edges (> 0.45 rate) and the RX1 filter +-300 Hz (no station leaks in). Whole-recording average (offline).
    // n = cells summed per bin; u11/u22 = plain mean power of a free bin (no cell gate - the 4x median gate
    // keeps cells under ~2.8x the mean, their mean power is ~0.8 of it)
    class BroadStats { public double[] p11, p22, cr, ci, n, u11, u22; public bool[] ok; }

    static BroadStats Broad(Rec r)
    {
        int N = r.N, hop = N / 2, frames = r.frames;
        var fft = new Fft(N);
        var win = new float[N];
        for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
        var a = new float[2][]; var b = new float[2][];
        for (int q = 0; q < 2; q++) { a[q] = new float[N]; b[q] = new float[N]; }
        // index i = signed bin + N/2 (VFO at 0 Hz)
        void Frame(int f)
        {
            double[][] z = { r.z1, r.z2 };
            for (int q = 0; q < 2; q++)
            {
                for (int i = 0; i < N; i++)
                {
                    int d = fft.BitRev[i], s = 2 * (f * hop + i);
                    a[q][d] = (float)z[q][s] * win[i]; b[q][d] = (float)z[q][s + 1] * win[i];
                }
                fft.Transform(a[q], b[q]);
            }
        }
        double g = r.g2;
        var m1 = new double[N]; var m2 = new double[N];
        for (int f = 0; f < frames; f++)
        {
            Frame(f);
            for (int k = 0; k < N; k++)
            {
                int i = (k + N / 2) & (N - 1);
                m1[i] += a[0][k] * a[0][k] + b[0][k] * b[0][k];
                m2[i] += g * g * (a[1][k] * a[1][k] + b[1][k] * b[1][k]);
            }
        }
        double binHz = r.binHz;
        int w1k = Math.Max(2, (int)(1000 / binHz)), dc = (int)Math.Round(-r.vfo / binHz) + N / 2, dcEx = Math.Max(2, (int)(200 / binHz));
        int fl = r.k0 - (int)(300 / binHz) + N / 2, fh = r.k0 + r.nb + (int)(300 / binHz) + N / 2;
        var med1 = new double[N]; var med2 = new double[N]; var tmp = new List<double>();
        for (int i = 0; i < N; i += w1k / 4)                    // local medians on a coarse grid, held between
        {
            tmp.Clear(); for (int j = Math.Max(0, i - w1k); j < Math.Min(N, i + w1k); j++) tmp.Add(m1[j]);
            double x1 = Median(tmp.ToArray());
            tmp.Clear(); for (int j = Math.Max(0, i - w1k); j < Math.Min(N, i + w1k); j++) tmp.Add(m2[j]);
            double x2 = Median(tmp.ToArray());
            for (int j = i; j < Math.Min(N, i + w1k / 4); j++) { med1[j] = x1 / frames; med2[j] = x2 / frames; }
        }
        var st = new BroadStats { p11 = new double[N], p22 = new double[N], cr = new double[N], ci = new double[N], ok = new bool[N],
            n = new double[N], u11 = new double[N], u22 = new double[N] };
        for (int i = 0; i < N; i++)
        {
            double fq = (i - N / 2) * binHz + r.vfo;            // offset from the IQ centre
            st.u11[i] = m1[i] / frames; st.u22[i] = m2[i] / frames;
            st.ok[i] = Math.Abs(i - dc) > dcEx && Math.Abs(fq) < 0.45 * r.fs && (i < fl || i > fh)
                && m1[i] / frames <= 2 * med1[i] && m2[i] / frames <= 2 * med2[i];
        }
        for (int f = 0; f < frames; f++)
        {
            Frame(f);
            for (int k = 0; k < N; k++)
            {
                int i = (k + N / 2) & (N - 1);
                if (!st.ok[i]) continue;
                double ar = a[0][k], ai = b[0][k], br = g * a[1][k], bi = g * b[1][k];
                double p1 = ar * ar + ai * ai, p2 = br * br + bi * bi;
                if (p1 > 4 * med1[i] || p2 > 4 * med2[i]) continue;
                st.p11[i] += p1; st.p22[i] += p2; st.cr[i] += ar * br + ai * bi; st.ci[i] += ai * br - ar * bi; st.n[i]++;
            }
        }
        // noise coherence map: one weight per 12 kHz of the IQ band
        var sb = new System.Text.StringBuilder("noise coherence across the band (kHz from IQ centre: coh):");
        int chunk = (int)(12000 / binHz), free = st.ok.Count(x => x);
        for (int c0 = 0; c0 + chunk <= N; c0 += chunk)
        {
            double P1 = 0, P2 = 0, R = 0, I = 0; int n = 0;
            for (int i = c0; i < c0 + chunk; i++) if (st.ok[i]) { P1 += st.p11[i]; P2 += st.p22[i]; R += st.cr[i]; I += st.ci[i]; n++; }
            if (n < chunk / 4) continue;
            double fq = ((c0 + chunk / 2) - N / 2) * binHz + r.vfo;
            sb.Append($" {fq / 1000:F0}:{Math.Sqrt(R * R + I * I) / Math.Sqrt(P1 * P2):F2}");
        }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"  free bins {100.0 * free / N:F0} % of the band");
        return st;
    }

    // y = z1 - w(f) z2 in the filter, w(f) = sum cr / sum p22 of the free bins within +-halfHz
    // norm: every free bin counts alike (its sums divided by its cells x its mean RX2 power) - otherwise a strong
    // local QRM a few kHz away (not in the filter) outweighs the noise and sets the weight (7.1 MHz 25.10 12:26)
    static double[] BroadApply(Rec r, BroadStats st, double halfHz, bool norm = false)
    {
        int N = r.N, frames = r.frames, nb = r.nb, h = (int)Math.Min(N, halfHz / r.binHz);
        var S22 = new double[N + 1]; var Sr = new double[N + 1]; var Si = new double[N + 1];
        for (int i = 0; i < N; i++)
        {
            double q = !norm ? 1 : st.n[i] > 0 && st.u22[i] > 0 ? 1 / (st.n[i] * st.u22[i]) : 0;
            S22[i + 1] = S22[i] + q * st.p22[i]; Sr[i + 1] = Sr[i] + q * st.cr[i]; Si[i + 1] = Si[i] + q * st.ci[i];
        }
        double cap = Math.Pow(10, NullMaxGainDb / 20);
        var wr = new double[nb]; var wi = new double[nb];
        for (int j = 0; j < nb; j++)
        {
            int i = r.k0 + j + N / 2, lo = Math.Max(0, i - h), hi = Math.Min(N, i + h + 1);
            double B = S22[hi] - S22[lo];
            if (B <= 0) continue;
            wr[j] = (Sr[hi] - Sr[lo]) / B; wi[j] = (Si[hi] - Si[lo]) / B;
            double mag = Math.Sqrt(wr[j] * wr[j] + wi[j] * wi[j]);
            if (mag > cap) { wr[j] *= cap / mag; wi[j] *= cap / mag; }
        }
        var Y = new double[r.X1.Length];
        for (int f = 0; f < frames; f++)
            for (int j = 0; j < nb; j++)
            {
                int c = f * nb + j;
                double br = r.X2[2 * c], bi = r.X2[2 * c + 1];
                Y[2 * c] = r.X1[2 * c] - (wr[j] * br - wi[j] * bi);
                Y[2 * c + 1] = r.X1[2 * c + 1] - (wr[j] * bi + wi[j] * br);
            }
        return Y;
    }

    // pause detector as in wdsp/secdiv.c: whole filter at the tracked floor on both antennas
    static bool[] Pauses(double[] X1, double[] X2, int frames, int nb, double tauFrames)
    {
        var r = new bool[frames];
        double T1 = 0, T2 = 0, th = 1 + 3 / Math.Sqrt(nb), an = Math.Min(1, 2 / tauFrames);   // floor tracker ~1 s (tauFrames = 2 s)
        for (int f = 0; f < frames; f++)
        {
            double P1 = 0, P2 = 0;
            for (int k = 0; k < nb; k++) { P1 += Pow(X1, f * nb + k); P2 += Pow(X2, f * nb + k); }
            r[f] = T1 > 0 && P1 < th * T1 && P2 < th * T2;
            if (T1 <= 0 || P1 < th * T1) T1 += (T1 <= 0 ? 1 : an) * (P1 - T1); else T1 *= 1 + 0.05 * an;
            if (T2 <= 0 || P2 < th * T2) T2 += (T2 <= 0 ? 1 : an) * (P2 - T2); else T2 *= 1 + 0.05 * an;
        }
        return r;
    }

    // wOut (optional): the complex weight per cell, to apply the same null to separate signal/noise parts
    // allCells: learn from every cell, not only pauses (long null L<s>)
    static double[] Null(double[] X1, double[] X2, int frames, int nb, double[] n1, int half, double tauFrames, double maxGainDb, double[] wOut = null, bool allCells = false)
    {
        double a = Math.Min(1, 1 / tauFrames), cap = Math.Pow(10, maxGainDb / 20);
        var r22 = new double[nb]; var cr = new double[nb]; var ci = new double[nb]; var cnt = new long[nb];
        var s22 = new double[nb + 1]; var scr = new double[nb + 1]; var sci = new double[nb + 1];
        var Y = new double[X1.Length];
        bool[] pauses = Pauses(X1, X2, frames, nb, tauFrames);
        for (int f = 0; f < frames; f++)
        {
            bool pause = allCells || pauses[f];
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
                if (wOut != null) { wOut[2 * c] = wr; wOut[2 * c + 1] = wi; }
                double br = X2[2 * c], bi = X2[2 * c + 1];
                Y[2 * c] = X1[2 * c] - (wr * br - wi * bi);
                Y[2 * c + 1] = X1[2 * c + 1] - (wr * bi + wi * br);
            }
        }
        return Y;
    }

    const double MaskTau = 0.2, MaskFloorDb = -15, MaskA0 = 0.2, MaskA1 = 0.7;

    // Phase mask: a far station is a plane wave, so z1 conj(z2) keeps a steady phase per bin (learned from
    // cells with signal over MaskTau, +-2 bins), while uncorrelated noise has a random one. Per cell the
    // agreement with that phase over a causal 3 bins x 2 frames patch, sum x projected / sum |x| (noise
    // alone ~0 +- 0.33), maps MaskA0..MaskA1 to a gain floor..1. Causal, so no extra delay in real time.
    static double[] PhaseMask(double[] X1, double[] X2, int frames, int nb, double[] noise, double tauFrames)
    {
        double a = Math.Min(1, 1 / tauFrames), fl = Math.Pow(10, MaskFloorDb / 20);
        var rr = new double[nb]; var ri = new double[nb];
        var G = new double[frames * nb];
        for (int f = 0; f < frames; f++)
        {
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                if (Pow(X1, c) + Pow(X2, c) < 6 * noise[k]) continue;
                Cross(X1, X2, c, out double xr, out double xi);
                rr[k] += a * (xr - rr[k]); ri[k] += a * (xi - ri[k]);
            }
            for (int k = 0; k < nb; k++)
                G[f * nb + k] = fl + (1 - fl) * Math.Clamp((Agree(X1, X2, f, k, nb, rr, ri, 1) - MaskA0) / (MaskA1 - MaskA0), 0, 1);
        }
        return G;
    }

    static void Cross(double[] X1, double[] X2, int c, out double xr, out double xi)
    {
        double ar = X1[2 * c], ai = X1[2 * c + 1], br = X2[2 * c], bi = X2[2 * c + 1];
        xr = ar * br + ai * bi; xi = ai * br - ar * bi;              // z1 * conj(z2)
    }

    // agreement of the causal 3 bins x 2 frames patch of z1 conj(z2) with the reference phase (+-2 bins of rr/ri):
    // sum x projected on it / sum |x|, -1..1, random phase ~0 +- 0.33; noRef when nothing is learned yet
    static double Agree(double[] X1, double[] X2, int f, int k, int nb, double[] rr, double[] ri, double noRef)
    {
        double Rr = 0, Ri = 0;
        for (int j = Math.Max(0, k - 2); j <= Math.Min(nb - 1, k + 2); j++) { Rr += rr[j]; Ri += ri[j]; }
        double Rm = Math.Sqrt(Rr * Rr + Ri * Ri), sr = 0, si = 0, sm = 0;
        for (int ff = Math.Max(0, f - 1); ff <= f; ff++)
            for (int j = Math.Max(0, k - 1); j <= Math.Min(nb - 1, k + 1); j++)
            {
                Cross(X1, X2, ff * nb + j, out double xr, out double xi);
                sr += xr; si += xi; sm += Math.Sqrt(xr * xr + xi * xi);
            }
        return Rm > 0 && sm > 0 ? (sr * Rr + si * Ri) / (Rm * sm) : noRef;
    }

    // Noise phase mask (the inverse): local noise keeps a steady RX2-RX1 phase, the sky wave wanders. The
    // noise phase per bin is learned in pauses (as Null, tauFrames); a cell whose patch agrees with it goes
    // down to the floor, anything else stays. Only as good as the noise phase is steady (noise coherence):
    // random-phase noise cannot be told from the station.
    static double[] NoisePhaseMask(double[] X1, double[] X2, int frames, int nb, double tauFrames)
    {
        double a = Math.Min(1, 1 / tauFrames), fl = Math.Pow(10, MaskFloorDb / 20);
        var rr = new double[nb]; var ri = new double[nb]; var cnt = new long[nb];
        var G = new double[frames * nb];
        bool[] pauses = Pauses(X1, X2, frames, nb, tauFrames);
        for (int f = 0; f < frames; f++)
        {
            if (pauses[f])
                for (int k = 0; k < nb; k++)
                {
                    double ak = Math.Max(a, 1.0 / ++cnt[k]);
                    Cross(X1, X2, f * nb + k, out double xr, out double xi);
                    rr[k] += ak * (xr - rr[k]); ri[k] += ak * (xi - ri[k]);
                }
            for (int k = 0; k < nb; k++)
                G[f * nb + k] = 1 - (1 - fl) * Math.Clamp((Agree(X1, X2, f, k, nb, rr, ri, 0) - MaskA0) / (MaskA1 - MaskA0), 0, 1);
        }
        return G;
    }

    // How steady the RX2-RX1 phase is: |mean of x/|x||, median over bins (1 = constant, random ~1/sqrt(cells)),
    // for noise (pause frames) and the station (cells 4x over the RX1 floor outside pauses), over the whole
    // recording and in 1 s blocks
    static void PhaseSteadiness(double[] X1, double[] X2, int frames, int nb, double[] n1, bool[] pauses, int block)
    {
        double Med(bool noise, int len)
        {
            var vals = new List<double>();
            for (int k = 0; k < nb; k++)
                for (int f0 = 0; f0 + len <= frames; f0 += len)
                {
                    double ur = 0, ui = 0; int n = 0;
                    for (int f = f0; f < f0 + len; f++)
                    {
                        int c = f * nb + k;
                        if (noise ? !pauses[f] : pauses[f] || Pow(X1, c) < 4 * n1[k]) continue;
                        Cross(X1, X2, c, out double xr, out double xi);
                        double m = Math.Sqrt(xr * xr + xi * xi); if (m <= 0) continue;
                        ur += xr / m; ui += xi / m; n++;
                    }
                    if (n >= 8) vals.Add(Math.Sqrt(ur * ur + ui * ui) / n);
                }
            return vals.Count > 0 ? Median(vals.ToArray()) : double.NaN;
        }
        string Scales(bool noise) => string.Join(" / ", new[] { 1, 10, 60 }.Where(t => t * block <= frames).Select(t => $"{Med(noise, t * block):F2} per {t} s")) + $" / {Med(noise, frames):F2} whole";
        Console.WriteLine($"phase steadiness RX2-RX1 (1 = constant): noise {Scales(true)}");
        Console.WriteLine($"                                         station {Scales(false)}");
    }

    // RX1 only, for comparison: Wiener gain 1 - beta/snr (beta > 1 = over-subtraction, W2) from the same patch power over the noise floor
    static double[] PowerMask(double[] X1, int frames, int nb, double[] n1, double beta = 1)
    {
        double fl = Math.Pow(10, MaskFloorDb / 20);
        var G = new double[frames * nb];
        for (int f = 0; f < frames; f++)
            for (int k = 0; k < nb; k++)
            {
                double p = 0; int cnt = 0;
                for (int ff = Math.Max(0, f - 1); ff <= f; ff++)
                    for (int j = Math.Max(0, k - 1); j <= Math.Min(nb - 1, k + 1); j++) { p += Pow(X1, ff * nb + j) / n1[j]; cnt++; }
                G[f * nb + k] = Math.Max(fl, 1 - beta / Math.Max(beta, p / cnt));
            }
        return G;
    }

    static double[] Apply(double[] X, double[] G)
    {
        var Y = new double[X.Length];
        for (int c = 0; c < G.Length; c++) { Y[2 * c] = G[c] * X[2 * c]; Y[2 * c + 1] = G[c] * X[2 * c + 1]; }
        return Y;
    }

    // Synthetic check of the masks with the true S/N: the same recording synthesised three times (mix,
    // signal only, noise only - same random draws), masks computed on the mix, applied to the parts.
    static int MaskCheck()
    {
        string P(int part) => Path.Combine(Path.GetTempPath(), $"Diversity_synthmask{part}_0Hz_48000_vfo0_f300_2700.wav");
        bool ok = true;
        foreach (double snrDb in new[] { 10.0, 3.0, 0.0 })
        {
            for (int part = 0; part < 3; part++) Synth(P(part), 48000, 30, false, snrDb, part);
            Rec mix = Load(P(0), MaskTau), sig = Load(P(1), MaskTau), noi = Load(P(2), MaskTau);
            double fps = (double)mix.fs / mix.hop;
            double[] gm = PhaseMask(mix.X1, mix.X2, mix.frames, mix.nb, mix.noise, MaskTau * fps);
            double[] gw = PowerMask(mix.X1, mix.frames, mix.nb, mix.n1), gw2 = PowerMask(mix.X1, mix.frames, mix.nb, mix.n1, 2);
            double[] gmw = gm.Zip(gw, (x, y) => x * y).ToArray(), one = Enumerable.Repeat(1.0, gm.Length).ToArray();
            double S0 = 0; for (int c = 0; c < gm.Length; c++) S0 += Pow(sig.X1, c);
            Console.WriteLine($"synth {snrDb} dB in 2.4 kHz, independent noise on both antennas:");
            var snr = new Dictionary<string, double>();
            foreach (var (name, g) in new[] { ("rx1", one), ("M", gm), ("W", gw), ("W2", gw2), ("MW", gmw) })
            {
                double sp = 0, np = 0;
                for (int c = 0; c < g.Length; c++) { sp += g[c] * g[c] * Pow(sig.X1, c); np += g[c] * g[c] * Pow(noi.X1, c); }
                snr[name] = 10 * Math.Log10(sp / np);
                Console.WriteLine($"  {name,-4} true S/N {snr[name],6:F1} dB   signal {10 * Math.Log10(sp / S0),6:F1} dB");
            }
            // measured: M +2.3..2.8 dB, W more; the phase only adds on top of W (MW ~ W2 at equal signal loss)
            ok &= snr["M"] > snr["rx1"] + 2 && snr["MW"] > snr["W"];
        }
        Console.WriteLine(ok ? "PASS (phase mask)" : "FAIL (phase mask)");

        // RX2 = noise antenna, two local sources with a fixed RX1/RX2 transfer (steady phase) 10 dB over the
        // thermal noise: noise phase mask Q against the linear null N47, truth from the separate parts
        for (int part = 0; part < 3; part++) Synth(P(part), 48000, 30, true, 10, part);
        {
            Rec mix = Load(P(0), MaskTau), sig = Load(P(1), MaskTau), noi = Load(P(2), MaskTau);
            double fps = (double)mix.fs / mix.hop;
            foreach (Rec q in new[] { sig, noi })                       // the mix's RX2 scaling on the parts too
                for (int i = 0; i < q.X2.Length; i++) q.X2[i] *= mix.g2 / q.g2;
            var w = new double[mix.X1.Length];
            Null(mix.X1, mix.X2, mix.frames, mix.nb, mix.n1, (int)(47 / mix.binHz / 2), NullTau * fps, NullMaxGainDb, w);
            double[] gq = NoisePhaseMask(mix.X1, mix.X2, mix.frames, mix.nb, NullTau * fps);
            double[] gw = PowerMask(mix.X1, mix.frames, mix.nb, mix.n1);
            int cells = gq.Length;
            double[] Lin(Rec q)                                         // y = z1 - w z2 per cell
            {
                var y = new double[2 * cells];
                for (int c = 0; c < cells; c++)
                {
                    double br = q.X2[2 * c], bi = q.X2[2 * c + 1];
                    y[2 * c] = q.X1[2 * c] - (w[2 * c] * br - w[2 * c + 1] * bi);
                    y[2 * c + 1] = q.X1[2 * c + 1] - (w[2 * c] * bi + w[2 * c + 1] * br);
                }
                return y;
            }
            double[] one = Enumerable.Repeat(1.0, cells).ToArray(), gqw = gq.Zip(gw, (x, y) => x * y).ToArray();
            double S0 = 0; for (int c = 0; c < cells; c++) S0 += Pow(sig.X1, c);
            Console.WriteLine("synth noise antenna, steady local noise 10 dB over thermal:");
            var snr = new Dictionary<string, double>();
            foreach (var (name, ys, yn, g) in new[] { ("rx1", sig.X1, noi.X1, one), ("N47", Lin(sig), Lin(noi), one), ("Q", sig.X1, noi.X1, gq),
                                                       ("W", sig.X1, noi.X1, gw), ("QW", sig.X1, noi.X1, gqw), ("N+QW", Lin(sig), Lin(noi), gqw) })
            {
                double sp = 0, np = 0;
                for (int c = 0; c < cells; c++) { sp += g[c] * g[c] * Pow(ys, c); np += g[c] * g[c] * Pow(yn, c); }
                snr[name] = 10 * Math.Log10(sp / np);
                Console.WriteLine($"  {name,-5} true S/N {snr[name],6:F1} dB   signal {10 * Math.Log10(sp / S0),6:F1} dB");
            }
            bool okq = snr["Q"] > snr["rx1"] + 3;
            Console.WriteLine(okq ? "PASS (noise phase mask +3 dB)" : "FAIL (noise phase mask +3 dB)");
            ok &= okq;
        }
        return ok ? 0 : 1;
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
    // snrDb: S/N in 2.4 kHz for one antenna; part 0 = all, 1 = signal only, 2 = noise only (same random draws)
    // wideLocal: one local source over the whole IQ band (flat transfer) instead of the two in-filter ones
    // dopplerHz: spread of the per-path, per-antenna Doppler (0 = the RX2-RX1 phase of the station never moves)
    static void Synth(string path, int fs, int seconds, bool noiseAnt, double snrDb = 10, int part = 0, bool wideLocal = false, double dopplerHz = 0.3)
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
        double nsd = Math.Sqrt(sig / Math.Pow(10, snrDb / 10) * fs / 2400 / 2);
        // local sources: RX2 gets amp*u, RX1 h*amp*u; 10 dB over the thermal noise in their band on RX1
        double[][] u = wideLocal ? new[] { Band(0, fs) } : noiseAnt ? new[] { Band(300, 1500), Band(1500, 2700) } : new double[0][];
        double ubw = wideLocal ? fs : 1200;
        double ks = part == 2 ? 0 : 1, kn = part == 1 ? 0 : 1;
        double[] hr = { 0.5 * Math.Cos(1), 0.7 * Math.Cos(-2) }, hi = { 0.5 * Math.Sin(1), 0.7 * Math.Sin(-2) };
        var amp = new double[u.Length];
        for (int q = 0; q < u.Length; q++) amp[q] = Math.Sqrt(10 * 2 * nsd * nsd * ubw / fs / MeanPow(u[q]) / (hr[q] * hr[q] + hi[q] * hi[q]));
        var buf = new byte[44 + 16 * n];
        for (int ant = 0; ant < 2; ant++)
        {
            double sg = noiseAnt && ant == 1 ? Math.Pow(10, -17 / 20.0) : 1;
            var fd = Enumerable.Range(0, 3).Select(_ => (rnd.NextDouble() - 0.5) * 2 * dopplerHz).ToArray();
            var p0 = Enumerable.Range(0, 3).Select(_ => rnd.NextDouble() * 2 * Math.PI).ToArray();
            for (int i = 0; i < n; i++)
            {
                double zr = kn * nsd * Gauss(), zi = kn * nsd * Gauss();
                for (int p = 0; p < 3; p++)
                {
                    int j = i - delay[p]; if (j < 0) continue;
                    double ph = p0[p] + 2 * Math.PI * fd[p] * i / fs, c = Math.Cos(ph), sn = Math.Sin(ph);
                    zr += ks * sg * gain[p] * (s[2 * j] * c - s[2 * j + 1] * sn);
                    zi += ks * sg * gain[p] * (s[2 * j] * sn + s[2 * j + 1] * c);
                }
                for (int q = 0; q < u.Length; q++)
                {
                    double ur = kn * amp[q] * u[q][2 * i], ui = kn * amp[q] * u[q][2 * i + 1];
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

    // mains-synchronous noise (user: "pyrkanie" at 50 Hz left after max-SINR): filter-band power in 0.33 ms steps,
    // folded modulo the mains period (best of 49.8-50.2 Hz); peak = folded max vs median, periodic = share of the power
    // above the folded minimum (0 = steady noise), gate20/gate40 = power left if the loudest 20/40 % of the cycle were blanked
    static void Mains(Rec r, string title, double[] z, double fc = 50)
    {
        const int N = 256, hop = 64, B = 40;   // 60 steps per 20 ms: every 0.5 ms phase bin gets frames at exactly 50 Hz too
        double bw = (double)r.fs / N, lo = r.k0 * r.binHz, hi = (r.k0 + r.nb) * r.binHz;
        int k0 = (int)Math.Ceiling(lo / bw), nb = Math.Max(1, (int)Math.Floor(hi / bw) - k0);
        int frames = (z.Length / 2 - N) / hop + 1;
        var X = Stft(z, N, hop, frames, k0, nb);
        var e = new double[frames];
        for (int f = 0; f < frames; f++) for (int k = 0; k < nb; k++) e[f] += Pow(X, f * nb + k);
        double dt = (double)hop / r.fs, best = 0, bf = 50; double[] bfold = null;
        for (double fm = fc - 0.2; fm <= fc + 0.2001; fm += 0.002)
        {
            var fold = new double[B]; var cnt = new int[B];
            for (int f = 0; f < frames; f++) { double ph = f * dt * fm; int j = (int)((ph - Math.Floor(ph)) * B) % B; fold[j] += e[f]; cnt[j]++; }
            for (int j = 0; j < B; j++) fold[j] /= Math.Max(1, cnt[j]);
            double m = fold.Average(), v = fold.Sum(x => (x - m) * (x - m));
            if (v > best) { best = v; bf = fm; bfold = fold; }
        }
        if (fc == 50)
        {
            // any other rhythm: spectrum of the power envelope, lines 5-400 Hz over the median of +-5 Hz around them
            int L = 1; while (2 * L <= frames) L *= 2;
            var fft = new Fft(L); var re = new float[L]; var im = new float[L];
            double em = e.Take(L).Average();
            for (int i = 0; i < L; i++) re[fft.BitRev[i]] = (float)((e[i] - em) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / L)));
            fft.Transform(re, im);
            double df = 1 / (dt * L); var sp = new double[L / 2];
            for (int i = 0; i < L / 2; i++) sp[i] = re[i] * re[i] + im[i] * im[i];
            int w = (int)(5 / df), i0 = (int)(5 / df), i1 = Math.Min(L / 2 - w - 1, (int)(400 / df));
            var lines = new List<(double f, double db)>();
            for (int i = i0; i <= i1; i++)
            {
                if (sp[i] < sp[i - 1] || sp[i] < sp[i + 1]) continue;
                var nb5 = new double[2 * w]; Array.Copy(sp, i - w, nb5, 0, 2 * w); Array.Sort(nb5);
                double db = 10 * Math.Log10((sp[i - 1] + sp[i] + sp[i + 1]) / 3 / nb5[w]);
                if (db >= 8) lines.Add((i * df, db));
            }
            Console.WriteLine($"rhythm {title,-7}: " + (lines.Count == 0 ? "no envelope line >= 8 dB in 5-400 Hz" :
                string.Join("  ", lines.OrderByDescending(l => l.db).Take(8).Select(l => $"{l.f:F2} Hz {l.db:F0} dB"))));
        }
        if (fc != 50 && fc != 42)
        {
            // coherent? a waveform repeating every period = RF comb with lines bf apart: 0.37 Hz Welch spectrum of the
            // filter band folded modulo bf (peak over median of the fold; noise alone ~1 dB)
            int Nw = 1 << 19, segs = z.Length / 2 / Nw;
            if (segs >= 2)
            {
                double bwf = (double)r.fs / Nw; int c0 = (int)Math.Ceiling(lo / bwf), cn = (int)((hi - lo) / bwf);
                var W = Stft(z, Nw, Nw, segs, c0, cn); var ps = new double[cn];
                for (int f = 0; f < segs; f++) for (int k = 0; k < cn; k++) ps[k] += Pow(W, f * cn + k);
                const int CB = 48; var cf = new double[CB]; var cc = new int[CB];
                for (int k = 0; k < cn; k++) { double ph = (c0 + k) * bwf / bf; int j = (int)((ph - Math.Floor(ph)) * CB) % CB; cf[j] += ps[k]; cc[j]++; }
                for (int j = 0; j < CB; j++) cf[j] /= Math.Max(1, cc[j]);
                var cs = cf.OrderBy(x => x).ToArray();
                double lineFrac = cf.Sum(x => x - cs[CB / 2]) / cf.Sum();   // power above the comb floor
                Console.WriteLine($"comb   {title,-7}: lines every {bf:F3} Hz, peak {10 * Math.Log10(cs[CB - 1] / cs[CB / 2]):F1} dB over the floor between them, " +
                                  $"{100 * Math.Max(0, lineFrac):F0} % of the band power in the lines ({segs} x {Nw / (double)r.fs:F1} s, {bwf:F2} Hz bins)");
            }
        }
        var srt = bfold.OrderBy(x => x).ToArray();
        double tot = bfold.Sum(), min = srt[0], med = srt[B / 2];
        double Gate(double frac) => srt.Take(B - (int)(frac * B)).Sum() / tot * (1 / (1 - frac));   // noise per unit time left
        int pk = Array.IndexOf(bfold, srt[B - 1]);
        int wide = bfold.Count(x => x > med + 0.5 * (srt[B - 1] - med));
        Console.WriteLine($"mains {title,-7}: {bf:F3} Hz, peak {10 * Math.Log10(srt[B - 1] / med):F1} dB at {pk * 1000 / bf / B:F1} ms, half-height width {wide * 1000 / bf / B:F1} ms, " +
                          $"periodic {100 * (tot - B * min) / tot:F0} %, gate20 {10 * Math.Log10(Gate(0.2)):F1} dB, gate40 {10 * Math.Log10(Gate(0.4)):F1} dB");
    }

    static int Dirs(string path)
    {
        var r = Load(path, 0.2);
        int nb = r.nb, fr = r.frames;
        bool[] pause = Pauses(r.X1, r.X2, fr, nb, NullTau * r.fs / r.hop);
        int[] pf = Enumerable.Range(0, fr).Where(f => pause[f]).ToArray();
        int P = pf.Length;
        double fh = (double)r.hop / r.fs;
        Console.WriteLine($"pauses {P} of {fr} frames ({fh * 1000:F0} ms each), bins {r.binHz:F1} Hz");
        foreach (int shift in new[] { 0, P / 3 })
        {
            // shift > 0: RX2 from other pause frames = no common noise, shows the estimation bias alone
            Func<int, int, int> c2 = (i, k) => pf[(i + shift) % P] * nb + k;
            var S = new double[5][];                // 2D prefix sums over (pause frame, bin): r11 r22 r12re r12im
            for (int q = 0; q < 4; q++) S[q] = new double[(P + 1) * (nb + 1)];
            for (int i = 0; i < P; i++)
                for (int k = 0; k < nb; k++)
                {
                    int a = pf[i] * nb + k, b = c2(i, k);
                    double x1r = r.X1[2 * a], x1i = r.X1[2 * a + 1], x2r = r.X2[2 * b], x2i = r.X2[2 * b + 1];
                    double[] v = { x1r * x1r + x1i * x1i, x2r * x2r + x2i * x2i, x1r * x2r + x1i * x2i, x1i * x2r - x1r * x2i };
                    for (int q = 0; q < 4; q++)
                        S[q][(i + 1) * (nb + 1) + k + 1] = v[q] + S[q][i * (nb + 1) + k + 1] + S[q][(i + 1) * (nb + 1) + k] - S[q][i * (nb + 1) + k];
                }
            Func<int, int, int, int, int, double> box = (q, i0, i1, k0, k1) =>
                S[q][i1 * (nb + 1) + k1] - S[q][i0 * (nb + 1) + k1] - S[q][i1 * (nb + 1) + k0] + S[q][i0 * (nb + 1) + k0];
            Console.WriteLine(shift == 0 ? "null depth in pauses (dB vs RX1), weight from +-T frames x +-F bins without the cell itself:"
                                         : "same with RX2 from other frames (no common noise = estimation bias only):");
            int[] Ts = { 0, 1, 2, 4, 8, 16, 64, P }, Fs = { 0, 1, 4, 16, nb };
            Console.WriteLine("  T x F" + string.Join("", Fs.Select(F => (F == nb ? "all" : "+-" + F).PadLeft(8))));
            foreach (int T in Ts)
            {
                var line = (T == P ? "all" : "+-" + T).PadLeft(6) + $" ({(2 * T + 1) * fh:F2} s)".PadRight(10);
                foreach (int F in Fs)
                {
                    double e = 0, x = 0;
                    for (int i = 0; i < P; i++)
                        for (int k = 0; k < nb; k++)
                        {
                            int i0 = Math.Max(0, i - T), i1 = Math.Min(P, i + T + 1), k0 = Math.Max(0, k - F), k1 = Math.Min(nb, k + F + 1);
                            int a = pf[i] * nb + k, b = c2(i, k);
                            double x1r = r.X1[2 * a], x1i = r.X1[2 * a + 1], x2r = r.X2[2 * b], x2i = r.X2[2 * b + 1];
                            double p2 = x2r * x2r + x2i * x2i;
                            double r22 = box(1, i0, i1, k0, k1) - p2;
                            double cr = box(2, i0, i1, k0, k1) - (x1r * x2r + x1i * x2i), ci = box(3, i0, i1, k0, k1) - (x1i * x2r - x1r * x2i);
                            double wr = r22 > 0 ? cr / r22 : 0, wi = r22 > 0 ? ci / r22 : 0;   // x1 ~ w x2
                            double er = x1r - (wr * x2r - wi * x2i), ei = x1i - (wr * x2i + wi * x2r);
                            e += er * er + ei * ei; x += x1r * x1r + x1i * x1i;
                        }
                    line += (10 * Math.Log10(e / x)).ToString("F1").PadLeft(8);
                }
                Console.WriteLine(line);
            }
            // direction: RX2-RX1 phase (and level) of windows with high local coherence
            const int WT = 8, WF = 8;
            var hist = new int[12]; var lev = new List<(double ph, double db, double c)>();
            int nw = 0;
            for (int i = 0; i + WT <= P; i += WT)
                for (int k = 0; k + WF <= nb; k += WF)
                {
                    nw++;
                    double r11 = box(0, i, i + WT, k, k + WF), r22 = box(1, i, i + WT, k, k + WF), cr = box(2, i, i + WT, k, k + WF), ci = box(3, i, i + WT, k, k + WF);
                    double c = Math.Sqrt((cr * cr + ci * ci) / (r11 * r22));
                    if (c < 0.6) continue;
                    double ph = Math.Atan2(-ci, cr) * 180 / Math.PI;   // arg(x2 conj x1)
                    hist[((int)Math.Floor((ph + 180) / 30)) % 12]++;
                    lev.Add((ph, 10 * Math.Log10(r22 / r11), c));
                }
            Console.WriteLine($"windows {WT} frames x {WF} bins ({WT * fh:F2} s x {WF * r.binHz:F0} Hz): {lev.Count} of {nw} with coherence >= 0.6 (bias ~{1 / Math.Sqrt(WT * WF):F2})");
            Console.WriteLine("  phase RX2-RX1: " + string.Join(" ", Enumerable.Range(0, 12).Select(j => $"{-180 + 30 * j:+0;-0}:{hist[j]}")));
            if (lev.Count > 0)
                foreach (var g in lev.GroupBy(l => ((int)Math.Floor((l.ph + 180) / 30)) % 12).OrderByDescending(g => g.Count()).Take(3))
                    Console.WriteLine($"  phase {-180 + 30 * g.Key:+0;-0}..{-150 + 30 * g.Key:+0;-0}: RX2 vs RX1 {g.Average(l => l.db):F1} dB, coherence {g.Average(l => l.c):F2}, {g.Count()} windows");
        }
        return 0;
    }
}
