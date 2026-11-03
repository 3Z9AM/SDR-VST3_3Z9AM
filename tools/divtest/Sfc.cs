//  Sfc.cs - selective fading on SSB recordings and a correction prototype ("AGC per band")
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

using System;
using System.IO;
using System.Linq;

static partial class Program
{
    // divtest sfc <wav ...> [tau=0.5] [max=6] [bands=10]
    // RX1 band levels on an ERB scale. The station's own long-term spectrum (5 s) is the reference; a slow
    // correction (tau) pulls each band back toward it, at most +-max dB, only where the band is 6 dB over the
    // noise (holes in the noise are left alone). Total station power kept, so the broadband AGC stays in charge.
    // Measures:
    //   fade  = RMS of the RX1-RX2 band-shape difference / sqrt 2: the speech is the same on both antennas,
    //           so this is the selective fading alone (lower bound if both antennas fade alike)
    //   dev   = RMS of the short-term vs long-term band shape on RX1 (speech + fading), before and after
    // Writes <recording>_sfc0.wav (RX1 + broadband AGC) and _sfc.wav (+ correction) for listening.
    static int Sfc(string[] args)
    {
        double tau = 0.5, maxDb = 6; int bands = 10;
        foreach (string a in args.Where(x => x.Contains(':')))
        {
            string[] kv = a.Split(':');
            double v = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
            if (kv[0] == "tau") tau = v; else if (kv[0] == "max") maxDb = v; else if (kv[0] == "bands") bands = (int)v;
        }
        Console.WriteLine($"sfc: tau {tau} s, max {maxDb} dB, {bands} ERB bands");
        Console.WriteLine($"{"recording",-58} {"MHz",7} {"act%",5} {"fade",5} {"dev",5} {"after",5} {"|g|",5} {">3dB%",6}");
        foreach (string p in args.Where(x => !x.Contains(':')))
        {
            try { SfcOne(p, tau, maxDb, bands); }
            catch (Exception e) { Console.WriteLine($"{Path.GetFileName(p),-58} skipped: {e.Message}"); }
        }
        return 0;
    }

    static double Erb(double f) => 21.4 * Math.Log10(1 + 0.00437 * f);

    static void SfcOne(string path, double tau, double maxDb, int nBands)
    {
        var tw = Console.Out; Console.SetOut(TextWriter.Null);
        Rec r;
        try { r = Load(path, 0.2); } finally { Console.SetOut(tw); }
        int F = r.frames, nb = r.nb;
        double fps = (double)r.fs / r.hop;

        // bin -> ERB band, band centres (ERB) for interpolating the gain
        var fa = new double[nb];
        for (int k = 0; k < nb; k++) fa[k] = Math.Abs((r.k0 + k) * r.binHz);
        double e0 = Erb(fa.Min()), e1 = Erb(fa.Max());
        var band = new int[nb]; var eb = new double[nb];
        for (int k = 0; k < nb; k++) { eb[k] = (Erb(fa[k]) - e0) / (e1 - e0) * nBands - 0.5; band[k] = Math.Clamp((int)Math.Round(eb[k]), 0, nBands - 1); }

        double[] n2b = NoiseFloor(r.X2, F, nb);
        var N1 = new double[nBands]; var N2 = new double[nBands];
        for (int k = 0; k < nb; k++) { N1[band[k]] += r.n1[k]; N2[band[k]] += n2b[k]; }
        double nTot = N1.Sum();

        double aS = 1 - Math.Exp(-1 / (tau * fps)), aL = 1 - Math.Exp(-1 / (5.0 * fps));
        var S1 = new double[nBands]; var S2 = new double[nBands]; var L1 = new double[nBands];
        var g = new double[nBands];                                    // dB
        var gl = new double[nb];
        var Y = (double[])r.X1.Clone();
        double fadeSum = 0, devSum = 0, gSum = 0; long fadeN = 0, devN = 0, gN = 0, big = 0; int act = 0;
        var P1 = new double[nBands]; var P2 = new double[nBands];
        // RX1/RX2 shape difference per band: its spread in time (a fixed antenna response difference is not fading)
        var dS = new double[nBands]; var dQ = new double[nBands]; var dN = new long[nBands];

        for (int f = 0; f < F; f++)
        {
            Array.Clear(P1); Array.Clear(P2);
            for (int k = 0; k < nb; k++) { P1[band[k]] += Pow(r.X1, f * nb + k); P2[band[k]] += Pow(r.X2, f * nb + k); }
            bool active = P1.Sum() > 4 * nTot;
            if (active)
            {
                act++;
                // running mean at the start, then exponential
                double s = Math.Max(aS, 1.0 / act), l = Math.Max(aL, 1.0 / act);
                for (int b = 0; b < nBands; b++)
                {
                    S1[b] += (Math.Max(P1[b] - N1[b], 0) - S1[b]) * s;
                    S2[b] += (Math.Max(P2[b] - N2[b], 0) - S2[b]) * s;
                    L1[b] += (Math.Max(P1[b] - N1[b], 0) - L1[b]) * l;
                }
                if (act > 2 * fps)                                     // reference settled
                {
                    // valid band: 6 dB over the noise (and for the fading measure on RX2 too)
                    var e = new double[nBands]; var d = new double[nBands];
                    bool[] v1 = new bool[nBands], v12 = new bool[nBands];
                    double em = 0, dm = 0; int ne = 0, nd = 0;
                    for (int b = 0; b < nBands; b++)
                    {
                        v1[b] = S1[b] > 4 * N1[b] && L1[b] > 0;
                        v12[b] = v1[b] && S2[b] > 4 * N2[b];
                        if (v1[b]) { e[b] = 10 * Math.Log10(S1[b] / L1[b]); em += e[b]; ne++; }
                        if (v12[b]) { d[b] = 10 * Math.Log10(S1[b] / S2[b]); dm += d[b]; nd++; }
                    }
                    if (ne >= 3)
                    {
                        em /= ne;
                        for (int b = 0; b < nBands; b++)
                            if (v1[b]) { double x = e[b] - em; devSum += x * x; devN++; g[b] = Math.Clamp(-x, -maxDb, maxDb); }
                            else g[b] = Math.Min(g[b], 0);             // a hole in the noise: never boosted
                        // keep the station power: sum S g^2 = sum S
                        double num = 0, den = 0;
                        for (int b = 0; b < nBands; b++) { num += S1[b]; den += S1[b] * Math.Pow(10, g[b] / 10); }
                        double norm = den > 0 ? 10 * Math.Log10(num / den) : 0;
                        for (int b = 0; b < nBands; b++) g[b] = Math.Clamp(g[b] + norm, -maxDb, maxDb);
                    }
                    if (nd >= 3)
                    {
                        dm /= nd;
                        for (int b = 0; b < nBands; b++) if (v12[b]) { double x = d[b] - dm; dS[b] += x; dQ[b] += x * x; dN[b]++; }
                    }
                }
            }
            // gain per bin: linear in dB between band centres
            for (int k = 0; k < nb; k++)
            {
                double x = Math.Clamp(eb[k], 0, nBands - 1);
                int b0 = (int)Math.Floor(x), b1 = Math.Min(b0 + 1, nBands - 1);
                gl[k] = g[b0] + (g[b1] - g[b0]) * (x - b0);
                double m = Math.Pow(10, gl[k] / 20);
                Y[2 * (f * nb + k)] *= m; Y[2 * (f * nb + k) + 1] *= m;
            }
            if (active) { for (int b = 0; b < nBands; b++) { gSum += Math.Abs(g[b]); gN++; } if (g.Any(x => Math.Abs(x) > 3)) big++; }
        }

        for (int b = 0; b < nBands; b++)
            if (dN[b] > 2 * fps) { fadeSum += dQ[b] - dS[b] * dS[b] / dN[b]; fadeN += dN[b]; }
        double devAfter = ShapeDev(Y, F, nb, band, nBands, N1, nTot, tau, fps);
        string name = Path.GetFileNameWithoutExtension(path);
        var m2 = System.Text.RegularExpressions.Regex.Match(name, @"_(\d+)Hz_");
        Console.WriteLine($"{name,-58} {double.Parse(m2.Groups[1].Value) / 1e6,7:F3} {100.0 * act / F,5:F0} {Math.Sqrt(fadeSum / Math.Max(1, fadeN)) / Math.Sqrt(2),5:F1} " +
                          $"{Math.Sqrt(devSum / Math.Max(1, devN)),5:F1} {devAfter,5:F1} {gSum / Math.Max(1, gN),5:F1} {100.0 * big / Math.Max(1, act),6:F0}");

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        var X0 = (double[])r.X1.Clone();
        BroadAgc(X0, F, nb, nTot, fps); BroadAgc(Y, F, nb, nTot, fps);
        WriteAudio(Path.Combine(dir, name + "_sfc0.wav"), X0, F, nb, r.k0, r.lsb);
        WriteAudio(Path.Combine(dir, name + "_sfc.wav"), Y, F, nb, r.k0, r.lsb);
    }

    // RMS dB of the short-term vs long-term band shape (as in SfcOne), bands 6 dB over the noise
    static double ShapeDev(double[] X, int F, int nb, int[] band, int nBands, double[] N, double nTot, double tau, double fps)
    {
        double aS = 1 - Math.Exp(-1 / (tau * fps)), aL = 1 - Math.Exp(-1 / (5.0 * fps));
        var S = new double[nBands]; var L = new double[nBands]; var P = new double[nBands];
        double sum = 0; long n = 0; int act = 0;
        for (int f = 0; f < F; f++)
        {
            Array.Clear(P);
            for (int k = 0; k < nb; k++) P[band[k]] += Pow(X, f * nb + k);
            if (P.Sum() <= 4 * nTot) continue;
            act++;
            double s = Math.Max(aS, 1.0 / act), l = Math.Max(aL, 1.0 / act);
            for (int b = 0; b < nBands; b++)
            {
                S[b] += (Math.Max(P[b] - N[b], 0) - S[b]) * s;
                L[b] += (Math.Max(P[b] - N[b], 0) - L[b]) * l;
            }
            if (act <= 2 * fps) continue;
            var e = new double[nBands]; double em = 0; int ne = 0;
            for (int b = 0; b < nBands; b++) if (S[b] > 4 * N[b] && L[b] > 0) { e[b] = 10 * Math.Log10(S[b] / L[b]); em += e[b]; ne++; } else e[b] = double.NaN;
            if (ne < 3) continue;
            em /= ne;
            for (int b = 0; b < nBands; b++) if (!double.IsNaN(e[b])) { sum += (e[b] - em) * (e[b] - em); n++; }
        }
        return Math.Sqrt(sum / Math.Max(1, n));
    }

    // the same simple broadband AGC on both A/B files: gain from the frame power, instant attack,
    // 0.5 s decay, at most 10 dB over the noise (pauses not pulled up to full level)
    static void BroadAgc(double[] X, int F, int nb, double nTot, double fps)
    {
        double env = 0, a = 1 - Math.Exp(-1 / (0.5 * fps));
        for (int f = 0; f < F; f++)
        {
            double p = 0;
            for (int k = 0; k < nb; k++) p += Pow(X, f * nb + k);
            env = p > env ? p : env + (p - env) * a;
            double gain = 1 / Math.Sqrt(Math.Max(env, 10 * nTot));
            for (int i = 2 * f * nb; i < 2 * (f + 1) * nb; i++) X[i] *= gain;
        }
    }
}
