//  Pick.cs - pick one of two stations by its RX2-RX1 phase signature (offline test)
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
using System.Globalization;
using System.IO;
using System.Linq;

static partial class Program
{
    // divtest pickscan <wav> : strongest 2.7 kHz windows of the IQ band (offsets for pick)
    static int PickScan(string path)
    {
        var tw = Console.Out; Console.SetOut(TextWriter.Null);
        Rec r; try { r = Load(path, 0.2); } finally { Console.SetOut(tw); }
        int F = Math.Min(r.frames, (int)(10.0 * r.fs / r.hop)), N = r.N, w = (int)(2700 / r.binHz);
        var X = Stft(r.z1, N, r.hop, F, -N / 2, N);
        var p = new double[N];
        for (int f = 0; f < F; f++) for (int k = 0; k < N; k++) p[k] += Pow(X, f * N + k);
        int edge = (int)(0.05 * N), step = (int)(1000 / r.binHz);
        var win = Enumerable.Range(0, (N - 2 * edge - w) / step).Select(i => edge + i * step).Select(s => (s, v: p.Skip(s).Take(w).Sum())).ToArray();
        double med = Median(win.Select(x => x.v).ToArray());
        double fc = (r.k0 + r.nb / 2.0) * r.binHz;
        Console.WriteLine($"{Path.GetFileName(path)}: filter centre {fc:F0} Hz from VFO; windows 2.7 kHz, dB over the median window, offset for pick:");
        foreach (var x in win.OrderByDescending(x => x.v).Take(12))
        {
            double c = (x.s - N / 2 + w / 2.0) * r.binHz;
            Console.WriteLine($"  {c,8:F0} Hz from VFO  {10 * Math.Log10(x.v / med),5:F1} dB   offset {c - fc,8:F0}");
        }
        return 0;
    }

    // divtest pick <A.wav> <B.wav> [offB Hz] [sir dB,...]
    // A = the station to hear (its recording: station + its noise, both antennas), B = an interferer: the filter
    // of B.wav moved by offB Hz (a station elsewhere in the same IQ band when B = A) laid onto A's filter at
    // A/B = sir dB (station powers over the noise). Both keep their real RX2-RX1 signatures. Methods:
    //   mask  = keep cells whose phase patch agrees with the slider phase (tracked per bin), strong other cells down
    //   lcmv  = linear per bin: A kept (gain 1), B nulled, signatures learned blind from the mix (A = agrees with
    //           the slider, B = strong and disagreeing)
    //   lcmvO = the same with signatures from A and B apart (ceiling)
    // Scored from 3 s against A alone: SDR (A fitted by one complex gain, the rest of A and all of B = error),
    // B down, A level. WAVs <A>_pick_*.wav.
    static int Pick(string[] args)
    {
        double off = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0;
        double[] sirs = args.Length > 3 ? args[3].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray() : new[] { -6.0, 0, 6 };
        Rec A = Load(args[0], 0.2), B = args[1] == args[0] ? A : Load(args[1], 0.2);
        int nb = A.nb, F = Math.Min(A.frames, B.frames), f0 = (int)Math.Round(3.0 * A.fs / A.hop);
        if (Math.Abs(A.binHz - B.binHz) > 1e-6) throw new Exception("bin widths differ");
        double fps = (double)A.fs / A.hop;

        // B's bins at A's audio frequencies (mirrored + conj when the sidebands differ), RX2 scaled as Load does
        int sh = (int)Math.Round(off / B.binHz);
        bool flip = A.lsb != B.lsb;
        double[] Map(double[] z, double g)
        {
            int kf = flip ? -(A.k0 + nb - 1) + sh : A.k0 + sh;
            var Y = Stft(z, B.N, B.hop, F, kf, nb);
            var o = new double[2 * F * nb];
            for (int f = 0; f < F; f++)
                for (int k = 0; k < nb; k++)
                {
                    int j = f * nb + (flip ? nb - 1 - k : k), i = f * nb + k;
                    o[2 * i] = g * Y[2 * j]; o[2 * i + 1] = (flip ? -g : g) * Y[2 * j + 1];
                }
            return o;
        }
        double[] B1 = Map(B.z1, 1), B2 = Map(B.z2, B.g2);
        double[] A1 = A.X1.Take(2 * F * nb).ToArray(), A2 = A.X2.Take(2 * F * nb).ToArray();
        double[] nA = NoiseFloor(A1, F, nb), nB = NoiseFloor(B1, F, nb);
        double Station(double[] X, double[] n) { double s = 0; for (int f = 0; f < F; f++) for (int k = 0; k < nb; k++) s += Math.Max(0, Pow(X, f * nb + k) - n[k]); return s; }
        double pA = Station(A1, nA), pB = Station(B1, nB);

        // signatures: mean z1 conj(z2) over cells with the station, and how steady (|sum x| / sum |x|)
        (double ph, double st, double lv) Sig(double[] X1, double[] X2, double[] n)
        {
            double sr = 0, si = 0, sm = 0, p1 = 0, p2 = 0;
            for (int f = 0; f < F; f++) for (int k = 0; k < nb; k++)
                {
                    int c = f * nb + k;
                    if (Pow(X1, c) < 4 * n[k]) continue;
                    Cross(X1, X2, c, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi);
                    if (m > 0) { sr += xr / m; si += xi / m; sm += 1; }
                    p1 += Pow(X1, c); p2 += Pow(X2, c);
                }
            return (Math.Atan2(si, sr), sm > 0 ? Math.Sqrt(sr * sr + si * si) / sm : 0, 10 * Math.Log10(p2 / p1));
        }
        var sa = Sig(A1, A2, nA); var sb = Sig(B1, B2, nB);
        double dphi = Math.IEEERemainder(sa.ph - sb.ph, 2 * Math.PI) * 180 / Math.PI;
        Console.WriteLine($"A phase {sa.ph * 180 / Math.PI,6:F0} deg (steady {sa.st:F2}, RX2 {sa.lv:+0;-0} dB), B phase {sb.ph * 180 / Math.PI,6:F0} deg (steady {sb.st:F2}, RX2 {sb.lv:+0;-0} dB), difference {dphi:F0} deg; B offset {off:F0} Hz{(flip ? " mirrored" : "")}");

        string bas = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), Path.GetFileNameWithoutExtension(args[0]));
        foreach (double sir in sirs)
        {
            double g = Math.Sqrt(pA / pB / Math.Pow(10, sir / 10));
            var Bg1 = B1.Select(x => x * g).ToArray(); var Bg2 = B2.Select(x => x * g).ToArray();
            var M1 = new double[A1.Length]; var M2 = new double[A1.Length];
            for (int i = 0; i < M1.Length; i++) { M1[i] = A1[i] + Bg1[i]; M2[i] = A2[i] + Bg2[i]; }
            double[] n1 = NoiseFloor(M1, F, nb);
            Console.WriteLine($" A/B {sir:+0;-0;0} dB");
            void Out(string name, double[] Ya, double[] Yb, bool wav = true)
            {
                PickScore(name, A1, Ya, Bg1, Yb, F, nb, f0);
                if (wav) WriteAudio($"{bas}_pick{sir:+0;-0;0}_{name}.wav", Ya.Zip(Yb, (x, y) => x + y).ToArray(), F, nb, A.k0, A.lsb);
            }
            Out("mix", A1, Bg1);
            if (sir == sirs[0]) WriteAudio($"{bas}_pick_A.wav", A1, F, nb, A.k0, A.lsb);
            foreach (var (a0, a1, name) in new[] { (new PickPar().A0, new PickPar().A1, "mask"), (0.6, 0.9, "maskN") })
            {
                var G = MaskKeep(M1, M2, F, nb, n1, sa.ph, fps, new PickPar(A0: a0, A1: a1), out _);
                Out(name, Mul(A1, G), Mul(Bg1, G));
            }
            var Gs = MaskKeep(M1, M2, F, nb, n1, sa.ph + Math.PI / 4, fps, new PickPar(), out _);
            Out("mask+45", Mul(A1, Gs), Mul(Bg1, Gs), false);
            var W = Lcmv(M1, M2, F, nb, n1, sa.ph, 0.3 * fps, null);
            Out("lcmv", Lin(A1, A2, W), Lin(Bg1, Bg2, W));
            var Wo = Lcmv(M1, M2, F, nb, n1, sa.ph, 0.3 * fps, (A1, A2, nA, Bg1, Bg2, nB.Select(x => x * g * g).ToArray()));
            Out("lcmvO", Lin(A1, A2, Wo), Lin(Bg1, Bg2, Wo), false);
        }
        return 0;
    }

    static readonly double PickMaxNoiseGain = double.Parse(Environment.GetEnvironmentVariable("PICK_NG") ?? "4", CultureInfo.InvariantCulture);

    // Agree with widths: reference over +-refHalf bins, causal patch +-patchHalf bins x patchFrames frames
    static double AgreeW(double[] X1, double[] X2, int f, int k, int nb, double[] rr, double[] ri, int refHalf, int patchHalf, int patchFrames)
    {
        double Rr = 0, Ri = 0;
        for (int j = Math.Max(0, k - refHalf); j <= Math.Min(nb - 1, k + refHalf); j++) { Rr += rr[j]; Ri += ri[j]; }
        double Rm = Math.Sqrt(Rr * Rr + Ri * Ri), sr = 0, si = 0, sm = 0;
        for (int ff = Math.Max(0, f - patchFrames + 1); ff <= f; ff++)
            for (int j = Math.Max(0, k - patchHalf); j <= Math.Min(nb - 1, k + patchHalf); j++)
            {
                Cross(X1, X2, ff * nb + j, out double xr, out double xi);
                sr += xr; si += xi; sm += Math.Sqrt(xr * xr + xi * xi);
            }
        return Rm > 0 && sm > 0 ? (sr * Rr + si * Ri) / (Rm * sm) : 1;
    }

    static double[] Mul(double[] X, double[] G) { var Y = new double[X.Length]; for (int i = 0; i < G.Length; i++) { Y[2 * i] = X[2 * i] * G[i]; Y[2 * i + 1] = X[2 * i + 1] * G[i]; } return Y; }

    // y = w1 z1 + w2 z2 per cell, W = (w1r, w1i, w2r, w2i) per cell
    static double[] Lin(double[] X1, double[] X2, double[] W)
    {
        var Y = new double[X1.Length];
        for (int i = 0; i < X1.Length / 2; i++)
        {
            double a = W[4 * i], b = W[4 * i + 1], c = W[4 * i + 2], d = W[4 * i + 3];
            Y[2 * i] = a * X1[2 * i] - b * X1[2 * i + 1] + c * X2[2 * i] - d * X2[2 * i + 1];
            Y[2 * i + 1] = a * X1[2 * i + 1] + b * X1[2 * i] + c * X2[2 * i + 1] + d * X2[2 * i];
        }
        return Y;
    }

    // PICK parameters (wdsprs/src/pick.rs constants; defaults = what the radio runs)
    record PickPar(double A0 = -0.5, double A1 = 0.8, double TrackAg = 0.9, double TrackOn = 4, double PatchOn = 3, double TauS = 2, double Pull = 0.05, double FloorDb = -15, int Model = 0, int RefHalf = 2, int PatchHalf = 1, int PatchFrames = 2, double BinTauS = 0);

    // keep mask, as wdsprs pick.rs: reference phase per bin starts at the slider (phi0) and follows cells that agree
    // with it; the station as a whole is tracked too (mean of those cells) and bins it did not sound in this frame are
    // pulled toward that. A cell with a strong signal (patch over PatchOn x the noise) gets floor..1 by its agreement
    // A0..A1, weak cells stay 1 (no gating of the noise). phase (out): the tracked phase at the end.
    // Model 1: the station's phase across the filter as a line (phase + slope per bin; slope from adjacent tracked
    // bins, u_k conj u_k-1), un-hit bins pulled to the line instead of to one mean phase. Model 2: every bin's
    // reference = the line (no per-bin references).
    static double[] MaskKeep(double[] X1, double[] X2, int F, int nb, double[] n1, double phi0, double fps, PickPar q, out double phase)
    {
        double a = Math.Min(1, 1 / (q.TauS * fps)), fl = Math.Pow(10, q.FloorDb / 20);
        double ab = q.BinTauS > 0 ? Math.Min(1, 1 / (q.BinTauS * fps)) : a;      // per-bin reference step (0 = as the station)
        var rr = Enumerable.Repeat(Math.Cos(phi0), nb).ToArray(); var ri = Enumerable.Repeat(Math.Sin(phi0), nb).ToArray();
        double gr = Math.Cos(phi0), gi = Math.Sin(phi0), sr = 1, si = 0;   // sr/si: slope per bin as a unit-ish vector
        var hit = new bool[nb];
        var ur = new double[nb]; var ui = new double[nb];
        var G = new double[F * nb];
        for (int f = 0; f < F; f++)
        {
            double tr = 0, ti = 0, tn = 0;
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                hit[k] = false;
                double pp = 0; int pn = 0;
                for (int ff = Math.Max(0, f - 1); ff <= f; ff++)
                    for (int j = Math.Max(0, k - 1); j <= Math.Min(nb - 1, k + 1); j++) { pp += Pow(X1, ff * nb + j) / n1[j]; pn++; }
                double ag = AgreeW(X1, X2, f, k, nb, rr, ri, q.RefHalf, q.PatchHalf, q.PatchFrames);
                G[c] = pp / pn < q.PatchOn ? 1 : fl + (1 - fl) * Math.Clamp((ag - q.A0) / (q.A1 - q.A0), 0, 1);
                if (Pow(X1, c) > q.TrackOn * n1[k] && ag > q.TrackAg)
                {
                    Cross(X1, X2, c, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi);
                    if (m <= 0) continue;
                    rr[k] += ab * (xr / m - rr[k]); ri[k] += ab * (xi / m - ri[k]);
                    hit[k] = true; tr += xr / m; ti += xi / m; tn++; ur[k] = xr / m; ui[k] = xi / m;
                }
            }
            if (tn > 0 && q.Model > 0)
            {
                double pr = 0, pi = 0, pc = 0;
                for (int k = 1; k < nb; k++)
                    if (hit[k] && hit[k - 1]) { pr += ur[k] * ur[k - 1] + ui[k] * ui[k - 1]; pi += ui[k] * ur[k - 1] - ur[k] * ui[k - 1]; pc++; }
                if (pc > 0) { sr += a * (pr / pc - sr); si += a * (pi / pc - si); }
                double sl = Math.Atan2(si, sr), dr = 0, di = 0;
                for (int k = 0; k < nb; k++)
                    if (hit[k]) { double c = Math.Cos(sl * k), sn = Math.Sin(sl * k); dr += ur[k] * c + ui[k] * sn; di += ui[k] * c - ur[k] * sn; }
                gr += a * (dr / tn - gr); gi += a * (di / tn - gi);
                double g0 = Math.Atan2(gi, gr), b2 = q.Model == 2 ? 1 : a * q.Pull;
                for (int k = 0; k < nb; k++)
                    if (!hit[k] || q.Model == 2) { rr[k] += b2 * (Math.Cos(g0 + sl * k) - rr[k]); ri[k] += b2 * (Math.Sin(g0 + sl * k) - ri[k]); }
            }
            else if (tn > 0)
            {
                gr += a * (tr / tn - gr); gi += a * (ti / tn - gi);
                double gm = Math.Sqrt(gr * gr + gi * gi), b2 = a * q.Pull;
                if (gm > 0)
                    for (int k = 0; k < nb; k++)
                        if (!hit[k]) { rr[k] += b2 * (gr / gm - rr[k]); ri[k] += b2 * (gi / gm - ri[k]); }
            }
        }
        phase = Math.Atan2(gi, gr);
        return G;
    }

    // two clusters per bin: references for the station (A, from the slider) and the other signal (B, learned from strong
    // cells far from A). A strong cell goes to the nearer one and teaches only it (tau s, refHalf bins); gain from
    // s = agree(A) - agree(B) mapped s0..s1 -> floor..1 (B not learned yet in that bin: agree(A) on A0..A1 as MaskKeep)
    static double[] MaskTwo(double[] X1, double[] X2, int F, int nb, double[] n1, double phi0, double fps, PickPar q, double tau, int rh, double s0, double s1, double margin)
    {
        double a = Math.Min(1, 1 / (tau * fps)), fl = Math.Pow(10, q.FloorDb / 20);
        var ar = Enumerable.Repeat(Math.Cos(phi0), nb).ToArray(); var ai = Enumerable.Repeat(Math.Sin(phi0), nb).ToArray();
        var br = new double[nb]; var bi = new double[nb];
        var G = new double[F * nb];
        for (int f = 0; f < F; f++)
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                double pp = 0; int pn = 0;
                for (int ff = Math.Max(0, f - 1); ff <= f; ff++)
                    for (int j = Math.Max(0, k - 1); j <= Math.Min(nb - 1, k + 1); j++) { pp += Pow(X1, ff * nb + j) / n1[j]; pn++; }
                double agA = AgreeW(X1, X2, f, k, nb, ar, ai, rh, 1, 2);
                double bm = 0;
                for (int j = Math.Max(0, k - rh); j <= Math.Min(nb - 1, k + rh); j++) bm += Math.Sqrt(br[j] * br[j] + bi[j] * bi[j]);
                bool hasB = bm > 0.2 * (2 * rh + 1);
                double agB = hasB ? AgreeW(X1, X2, f, k, nb, br, bi, rh, 1, 2) : -1;
                double g = hasB ? Math.Clamp((agA - agB - s0) / (s1 - s0), 0, 1) : Math.Clamp((agA - q.A0) / (q.A1 - q.A0), 0, 1);
                G[c] = pp / pn < q.PatchOn ? 1 : fl + (1 - fl) * g;
                if (Pow(X1, c) > q.TrackOn * n1[k])
                {
                    Cross(X1, X2, c, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi);
                    if (m <= 0) continue;
                    if (agA > agB + margin && agA > 0.5) { ar[k] += a * (xr / m - ar[k]); ai[k] += a * (xi / m - ai[k]); }
                    else if (agB > agA + margin || (!hasB && agA < 0)) { br[k] += a * (xr / m - br[k]); bi[k] += a * (xi / m - bi[k]); }
                }
            }
        return G;
    }

    // LCMV per bin with two antennas: h_A = (1, a), h_B = (1, b) (a, b = z2/z1 of each station, smoothed +-2 bins),
    // y = (b z1 - z2) / (b - a): A at gain 1, B nulled. Blind: a from strong cells agreeing with the tracked slider
    // phase, b from strong cells disagreeing (agreement < 0.2); truth != null: a, b from the stations apart.
    // Noise gain (1 + |b|^2) / |b - a|^2 over 4 (6 dB) or nothing learned -> RX1 alone.
    static double[] Lcmv(double[] X1, double[] X2, int F, int nb, double[] n1, double phi0, double tauFrames,
                         (double[] A1, double[] A2, double[] nA, double[] B1, double[] B2, double[] nB)? truth)
    {
        double al = Math.Min(1, 1 / tauFrames), at = Math.Min(1, 0.6 / tauFrames);   // reference phase over ~0.5 s when tau = 0.3 s
        var rr = Enumerable.Repeat(Math.Cos(phi0), nb).ToArray(); var ri = Enumerable.Repeat(Math.Sin(phi0), nb).ToArray();
        // per bin: sum z2 conj(z1) (re, im) and |z1|^2 for A and B
        var sA = new double[3 * nb]; var sB = new double[3 * nb];
        var W = new double[4 * F * nb];
        void Acc(double[] s, int k, double[] Z1, double[] Z2, int c)
        {
            double ar = Z1[2 * c], ai = Z1[2 * c + 1], br = Z2[2 * c], bi = Z2[2 * c + 1];
            s[3 * k] += al * (br * ar + bi * ai - s[3 * k]); s[3 * k + 1] += al * (bi * ar - br * ai - s[3 * k + 1]); s[3 * k + 2] += al * (ar * ar + ai * ai - s[3 * k + 2]);
        }
        for (int f = 0; f < F; f++)
        {
            for (int k = 0; k < nb; k++)
            {
                int c = f * nb + k;
                if (truth.HasValue)
                {
                    var t = truth.Value;
                    if (Pow(t.A1, c) > 4 * t.nA[k]) Acc(sA, k, t.A1, t.A2, c);
                    if (Pow(t.B1, c) > 4 * t.nB[k]) Acc(sB, k, t.B1, t.B2, c);
                    continue;
                }
                if (Pow(X1, c) < 4 * n1[k]) continue;
                double ag = Agree(X1, X2, f, k, nb, rr, ri, 1);
                if (ag > 0.6)
                {
                    Acc(sA, k, X1, X2, c);
                    Cross(X1, X2, c, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi);
                    rr[k] += at * (xr / m - rr[k]); ri[k] += at * (xi / m - ri[k]);
                }
                else if (ag < 0.2) Acc(sB, k, X1, X2, c);
            }
            for (int k = 0; k < nb; k++)
            {
                double Ar = 0, Ai = 0, Ap = 0, Br = 0, Bi = 0, Bp = 0;
                for (int j = Math.Max(0, k - 2); j <= Math.Min(nb - 1, k + 2); j++)
                { Ar += sA[3 * j]; Ai += sA[3 * j + 1]; Ap += sA[3 * j + 2]; Br += sB[3 * j]; Bi += sB[3 * j + 1]; Bp += sB[3 * j + 2]; }
                int o = 4 * (f * nb + k);
                W[o] = 1;
                if (Ap <= 0 || Bp <= 0) continue;
                double ar = Ar / Ap, ai = Ai / Ap, br = Br / Bp, bi = Bi / Bp, dr = br - ar, di = bi - ai, D = dr * dr + di * di;
                if ((1 + br * br + bi * bi) / D > PickMaxNoiseGain) continue;
                // w1 = b / (b - a), w2 = -1 / (b - a)
                W[o] = (br * dr + bi * di) / D; W[o + 1] = (bi * dr - br * di) / D;
                W[o + 2] = -dr / D; W[o + 3] = di / D;
            }
        }
        return W;
    }

    static double PickScore(string name, double[] A1, double[] Ya, double[] B1, double[] Yb, int F, int nb, int f0)
    {
        double sa = 0, sb = 0, ya = 0, yb = 0, cr = 0, ci = 0;
        for (int i = f0 * nb; i < F * nb; i++)
        {
            sa += Pow(A1, i); sb += Pow(B1, i); ya += Pow(Ya, i); yb += Pow(Yb, i);
            cr += Ya[2 * i] * A1[2 * i] + Ya[2 * i + 1] * A1[2 * i + 1]; ci += Ya[2 * i + 1] * A1[2 * i] - Ya[2 * i] * A1[2 * i + 1];
        }
        double gr = cr / sa, gi = ci / sa, e = 0;
        for (int i = f0 * nb; i < F * nb; i++)
        {
            double tr = gr * A1[2 * i] - gi * A1[2 * i + 1], ti = gr * A1[2 * i + 1] + gi * A1[2 * i];
            double dr = Ya[2 * i] - tr + Yb[2 * i], di = Ya[2 * i + 1] - ti + Yb[2 * i + 1];
            e += dr * dr + di * di;
        }
        double sdr = 10 * Math.Log10((gr * gr + gi * gi) * sa / e);
        if (name != null) Console.WriteLine($"   {name,-8} SDR {sdr,6:F1} dB   B down {10 * Math.Log10(yb / sb),6:F1} dB   A level {10 * Math.Log10(ya / sa),5:F1} dB");
        return sdr;
    }

    // divtest picktune <wav:offB> ... : the PICK constants over many station pairs (each recording: A = its own filter,
    // B = offB Hz away in the same IQ band), A/B 0 and -6 dB, slider started at A's true phase and +-45 deg (set by ear).
    // One constant at a time around the defaults; score = mean SDR gain over the mix (dB) and the worst case.
    static int PickTune(string[] args)
    {
        var cases = new System.Collections.Generic.List<(string name, double[] A1, double[] A2a, double[] M1, double[] M2, double[] Bg1, double[] n1, int F, int nb, int f0, double fps, double ph, double mix)>();
        foreach (var grp in args.Select(x => (path: x.Substring(0, x.LastIndexOf(':')), off: double.Parse(x.Substring(x.LastIndexOf(':') + 1), CultureInfo.InvariantCulture))).GroupBy(x => x.path))
        {
            var tw = Console.Out; Console.SetOut(TextWriter.Null);
            Rec A; try { A = Load(grp.Key, 0.2); } finally { Console.SetOut(tw); }
            foreach (var (path, off) in grp)
                foreach (double sir in new[] { 6.0, 0.0, -6.0 })
                {
                    var m = PickMixOf(A, A, off, sir);
                    double mix = PickScore(null, m.A1, m.A1, m.Bg1, m.Bg1, m.F, m.nb, m.f0);
                    cases.Add(($"{Path.GetFileName(path).Substring(10, 15)} {off,7:F0} {sir,3:F0}", m.A1, m.A2, m.M1, m.M2, m.Bg1, m.n1, m.F, m.nb, m.f0, m.fps, m.ph, mix));
                }
        }
        Console.WriteLine($"{cases.Count} cases (pairs x A/B 0, -6 dB) x slider start 0, +45, -45 deg");
        // how much a line across the filter explains the station's phase: per frame |mean u| (one phase for all bins)
        // vs |mean u e^-j s k| with the frame's own slope s (1 = all bins on it); slope steadiness over 1 s / whole
        Console.WriteLine("station alone (A): one phase / line per frame, slope steady 1 s / all, slope deg per 100 Hz");
        foreach (var c in cases.Where(x => x.name.EndsWith(" 0")).GroupBy(x => x.name.Substring(0, 15)).Select(g => g.First()))
        {
            int F = c.F, nb = c.nb, blk = (int)Math.Round(c.fps);
            double[] nA = NoiseFloor(c.A1, F, nb);
            double one = 0, lin = 0, n = 0, Sr = 0, Si = 0, bR = 0, bI = 0, b1 = 0, nb1 = 0, sAll = 0;
            var u = new (double r, double i)?[nb];
            for (int f = 0; f < F; f++)
            {
                int cnt = 0; double mr = 0, mi = 0, pr = 0, pi = 0;
                for (int k = 0; k < nb; k++)
                {
                    int i = f * nb + k; u[k] = null;
                    if (Pow(c.A1, i) < 4 * nA[k]) continue;
                    Cross(c.A1, c.A2a, i, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi); if (m <= 0) continue;
                    u[k] = (xr / m, xi / m); mr += xr / m; mi += xi / m; cnt++;
                    if (k > 0 && u[k - 1] is var (qr, qi)) { pr += xr / m * qr + xi / m * qi; pi += xi / m * qr - xr / m * qi; }
                }
                if (cnt < 8 || pr * pr + pi * pi == 0) continue;
                double sl = Math.Atan2(pi, pr), dr = 0, di = 0;
                for (int k = 0; k < nb; k++) if (u[k] is var (vr, vi)) { double cs = Math.Cos(sl * k), sn = Math.Sin(sl * k); dr += vr * cs + vi * sn; di += vi * cs - vr * sn; }
                one += Math.Sqrt(mr * mr + mi * mi) / cnt; lin += Math.Sqrt(dr * dr + di * di) / cnt; n++;
                double ar = Math.Cos(sl), ai = Math.Sin(sl);
                Sr += ar; Si += ai; bR += ar; bI += ai; b1++; sAll += sl;
                if (b1 >= blk) { nb1 += Math.Sqrt(bR * bR + bI * bI) / b1; bR = bI = b1 = 0; one += 0; }
            }
            double binHz = 24000.0 / 2048;
            Console.WriteLine($"  {c.name.Substring(0, 15)}: one {one / n:F2}  line {lin / n:F2}   slope steady {nb1 / Math.Max(1, Math.Floor(n / blk)):F2} / {Math.Sqrt(Sr * Sr + Si * Si) / n:F2}   slope {Math.Atan2(Si, Sr) * 180 / Math.PI * 100 / binHz:+0;-0} deg/100 Hz");
        }
        (double mean, double worst) Run(PickPar q)
        {
            var r = new System.Collections.Concurrent.ConcurrentBag<double>();
            System.Threading.Tasks.Parallel.ForEach(cases, c =>
            {
                foreach (double d in new[] { 0.0, 45, -45 })
                {
                    var G = MaskKeep(c.M1, c.M2, c.F, c.nb, c.n1, c.ph + d * Math.PI / 180, c.fps, q, out _);
                    r.Add(PickScore(null, c.A1, Mul(c.A1, G), c.Bg1, Mul(c.Bg1, G), c.F, c.nb, c.f0) - c.mix);
                }
            });
            return (r.Average(), r.Min());
        }
        if (Environment.GetEnvironmentVariable("PICK_GRID") != null)
        {
            var res = new System.Collections.Generic.List<(PickPar q, double mean, double worst)>();
            foreach (double pull in new[] { 0.0, 0.05 })
                foreach (double tau in new[] { 1.0, 2, 4, 1e9 })
                    foreach (double tag in new[] { 0.7, 0.8, 0.9 })
                        foreach (double a0 in new[] { 0.0, 0.15, 0.3 })
                            foreach (double fl in new[] { -10.0, -15, -20 })
                            {
                                var q = new PickPar(A0: a0, TrackAg: tag, TauS: tau, Pull: pull, FloorDb: fl);
                                var r = Run(q);
                                res.Add((q, r.mean, r.worst));
                            }
            foreach (var x in res.OrderByDescending(x => x.mean).Take(15))
                Console.WriteLine($"  mean {x.mean:+0.00;-0.00} worst {x.worst:+0.0;-0.0}  {x.q}");
            Console.WriteLine("by worst:");
            foreach (var x in res.OrderByDescending(x => x.worst).Take(8))
                Console.WriteLine($"  mean {x.mean:+0.00;-0.00} worst {x.worst:+0.0;-0.0}  {x.q}");
            return 0;
        }
        var d0 = new PickPar();
        var b0 = Run(d0);
        Console.WriteLine($"defaults {d0}: mean {b0.mean:+0.00;-0.00} dB, worst {b0.worst:+0.0;-0.0}");
        // auto slider: the dominant steady phase of the mix over the first secs (as PICK measures it live), then the mask
        // from there; error vs the station's true phase and the SDR gain, per A/B
        foreach (double secs in new[] { -3.0, -10.0, -30.0 })
        {
            var byS = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<(double err, double gain, double gainTrue)>>();
            System.Threading.Tasks.Parallel.ForEach(cases, c =>
            {
                double auto = secs < 0 ? PickAutoPeak(c.M1, c.M2, c.F, c.nb, c.n1, c.fps, -secs) : PickAutoPhase(c.M1, c.M2, c.F, c.nb, c.n1, c.fps, secs);
                double err = Math.Abs(Math.IEEERemainder(auto - c.ph, 2 * Math.PI)) * 180 / Math.PI;
                var G = MaskKeep(c.M1, c.M2, c.F, c.nb, c.n1, auto, c.fps, d0, out _);
                var Gt = MaskKeep(c.M1, c.M2, c.F, c.nb, c.n1, c.ph, c.fps, d0, out _);
                double g = PickScore(null, c.A1, Mul(c.A1, G), c.Bg1, Mul(c.Bg1, G), c.F, c.nb, c.f0) - c.mix;
                double gt = PickScore(null, c.A1, Mul(c.A1, Gt), c.Bg1, Mul(c.Bg1, Gt), c.F, c.nb, c.f0) - c.mix;
                byS.GetOrAdd(c.name.Substring(c.name.Length - 3).Trim(), _ => new()).Add((err, g, gt));
            });
            foreach (var kv in byS.OrderByDescending(x => double.Parse(x.Key)))
            {
                var v = kv.Value.ToArray();
                Console.WriteLine($"  auto {secs} s, A/B {kv.Key,3} dB: error median {Median(v.Select(x => x.err).ToArray()),3:F0} deg, within 45: {v.Count(x => x.err < 45)}/{v.Length}; " +
                                  $"gain auto {v.Average(x => x.gain):+0.00;-0.00} (worst {v.Min(x => x.gain):+0.0;-0.0}) vs slider at A {v.Average(x => x.gainTrue):+0.00;-0.00}");
            }
        }
        // ceiling: per-bin reference from the station alone (perfect tracking per bin), +-refHalf bins, over tau
        foreach (var (tau, rh) in new[] { (0.2, 0), (0.5, 0), (0.5, 2), (2.0, 2) })
        {
            var r = new System.Collections.Concurrent.ConcurrentBag<double>();
            System.Threading.Tasks.Parallel.ForEach(cases, c =>
            {
                int F = c.F, nb = c.nb; double a = Math.Min(1, 1 / (tau * c.fps)), fl = Math.Pow(10, d0.FloorDb / 20);
                double[] nA = NoiseFloor(c.A1, F, nb);
                var rr = new double[nb]; var ri = new double[nb]; var G = new double[F * nb];
                for (int f = 0; f < F; f++)
                    for (int k = 0; k < nb; k++)
                    {
                        int i = f * nb + k;
                        double pp = 0; int pn = 0;
                        for (int ff = Math.Max(0, f - 1); ff <= f; ff++)
                            for (int j = Math.Max(0, k - 1); j <= Math.Min(nb - 1, k + 1); j++) { pp += Pow(c.M1, ff * nb + j) / c.n1[j]; pn++; }
                        double ag = AgreeW(c.M1, c.M2, f, k, nb, rr, ri, rh, 1, 2);
                        G[i] = pp / pn < d0.PatchOn ? 1 : fl + (1 - fl) * Math.Clamp((ag - d0.A0) / (d0.A1 - d0.A0), 0, 1);
                        if (Pow(c.A1, i) > 4 * nA[k])
                        {
                            Cross(c.A1, c.A2a, i, out double xr, out double xi);
                            double m = Math.Sqrt(xr * xr + xi * xi);
                            if (m > 0) { rr[k] += a * (xr / m - rr[k]); ri[k] += a * (xi / m - ri[k]); }
                        }
                    }
                r.Add(PickScore(null, c.A1, Mul(c.A1, G), c.Bg1, Mul(c.Bg1, G), F, nb, c.f0) - c.mix);
            });
            Console.WriteLine($"  oracle per bin, {tau} s, +-{rh} bins: mean {r.Average():+0.00;-0.00} dB, worst {r.Min():+0.0;-0.0}");
        }
        foreach (var (tau, rh, s0, s1, mg) in new[] { (0.5, 2, -0.5, 0.5, 0.2), (2.0, 2, -0.5, 0.5, 0.2), (0.5, 2, 0.0, 1.0, 0.2), (0.5, 2, -1.0, 0.5, 0.2), (0.5, 0, -0.5, 0.5, 0.2), (0.5, 2, -0.5, 0.5, 0.5), (1.0, 2, -0.5, 0.5, 0.3) })
        {
            var r = new System.Collections.Concurrent.ConcurrentBag<double>();
            System.Threading.Tasks.Parallel.ForEach(cases, c =>
            {
                foreach (double d in new[] { 0.0, 45, -45 })
                {
                    var G = MaskTwo(c.M1, c.M2, c.F, c.nb, c.n1, c.ph + d * Math.PI / 180, c.fps, d0, tau, rh, s0, s1, mg);
                    r.Add(PickScore(null, c.A1, Mul(c.A1, G), c.Bg1, Mul(c.Bg1, G), c.F, c.nb, c.f0) - c.mix);
                }
            });
            Console.WriteLine($"  two clusters {tau} s +-{rh} bins, s {s0}..{s1}, margin {mg}: mean {r.Average():+0.00;-0.00} dB, worst {r.Min():+0.0;-0.0}");
        }
        var sweeps = new (string, double[], Func<PickPar, double, PickPar>)[]
        {
            ("RefHalf", new[] { 0.0, 1, 4, 8 }, (p, v) => p with { RefHalf = (int)v }),
            ("PatchHalf", new[] { 0.0, 2 }, (p, v) => p with { PatchHalf = (int)v }),
            ("PatchFr", new[] { 1.0, 3 }, (p, v) => p with { PatchFrames = (int)v }),
            ("BinTauS", new[] { 0.5, 1, 4 }, (p, v) => p with { BinTauS = v }),
            ("Ref0Bin1", new[] { 1.0 }, (p, v) => p with { RefHalf = 0, BinTauS = v }),
            ("Ref1Bin1", new[] { 1.0 }, (p, v) => p with { RefHalf = 1, BinTauS = v }),
            ("M1Pull", new[] { 0.25, 1 }, (p, v) => p with { Model = 1, Pull = v }),
            ("M1Tau", new[] { 1.0, 4 }, (p, v) => p with { Model = 1, TauS = v }),
            ("M1Ag", new[] { 0.8 }, (p, v) => p with { Model = 1, TrackAg = v }),
            ("A0", new[] { -0.8, -0.4 }, (p, v) => p with { A0 = v }),
            ("A1", new[] { 0.6, 0.7, 0.9 }, (p, v) => p with { A1 = v }),
            ("TrackAg", new[] { 0.8, 0.95 }, (p, v) => p with { TrackAg = v }),
            ("TrackOn", new[] { 2.0, 8, 16 }, (p, v) => p with { TrackOn = v }),
            ("PatchOn", new[] { 1.5, 6, 12 }, (p, v) => p with { PatchOn = v }),
            ("TauS", new[] { 1.0, 4, 1e9 }, (p, v) => p with { TauS = v }),
            ("Pull", new[] { 0.0, 0.1, 0.2 }, (p, v) => p with { Pull = v }),
            ("FloorDb", new[] { -10.0, -12, -18 }, (p, v) => p with { FloorDb = v }),
        };
        foreach (var (n, vals, with) in sweeps)
            foreach (double v in vals)
            {
                var r = Run(with(d0, v));
                Console.WriteLine($"  {n,-8} {v,6}: mean {r.mean:+0.00;-0.00} dB, worst {r.worst:+0.0;-0.0}");
            }
        return 0;
    }

    // A's own filter + B (offB Hz away in B's IQ band, mapped onto A's audio bins) at A/B = sir dB
    static (double[] A1, double[] A2, double[] Bg1, double[] Bg2, double[] M1, double[] M2, double[] n1, int F, int nb, int f0, double fps, double ph)
        PickMixOf(Rec A, Rec B, double off, double sir)
    {
        int nb = A.nb, F = Math.Min(A.frames, B.frames), f0 = (int)Math.Round(3.0 * A.fs / A.hop);
        if (Math.Abs(A.binHz - B.binHz) > 1e-6) throw new Exception("bin widths differ");
        int sh = (int)Math.Round(off / B.binHz);
        bool flip = A.lsb != B.lsb;
        double[] Map(double[] z, double g)
        {
            int kf = flip ? -(A.k0 + nb - 1) + sh : A.k0 + sh;
            var Y = Stft(z, B.N, B.hop, F, kf, nb);
            var o = new double[2 * F * nb];
            for (int f = 0; f < F; f++)
                for (int k = 0; k < nb; k++)
                {
                    int j = f * nb + (flip ? nb - 1 - k : k), i = f * nb + k;
                    o[2 * i] = g * Y[2 * j]; o[2 * i + 1] = (flip ? -g : g) * Y[2 * j + 1];
                }
            return o;
        }
        double[] B1 = Map(B.z1, 1), B2 = Map(B.z2, B.g2);
        double[] A1 = A.X1.Take(2 * F * nb).ToArray(), A2 = A.X2.Take(2 * F * nb).ToArray();
        double[] nA = NoiseFloor(A1, F, nb), nB = NoiseFloor(B1, F, nb);
        double Station(double[] X, double[] n) { double s = 0; for (int f = 0; f < F; f++) for (int k = 0; k < nb; k++) s += Math.Max(0, Pow(X, f * nb + k) - n[k]); return s; }
        double gB = Math.Sqrt(Station(A1, nA) / Station(B1, nB) / Math.Pow(10, sir / 10));
        var Bg1 = B1.Select(x => x * gB).ToArray(); var Bg2 = B2.Select(x => x * gB).ToArray();
        var M1 = new double[A1.Length]; var M2 = new double[A1.Length];
        for (int i = 0; i < M1.Length; i++) { M1[i] = A1[i] + Bg1[i]; M2[i] = A2[i] + Bg2[i]; }
        double sr = 0, si = 0;
        for (int c = 0; c < F * nb; c++)
        {
            if (Pow(A1, c) < 4 * nA[c % nb]) continue;
            Cross(A1, A2, c, out double xr, out double xi);
            double m = Math.Sqrt(xr * xr + xi * xi);
            if (m > 0) { sr += xr / m; si += xi / m; }
        }
        return (A1, A2, Bg1, Bg2, M1, M2, NoiseFloor(M1, F, nb), F, nb, f0, (double)A.fs / A.hop, Math.Atan2(si, sr));
    }

    // divtest pickdrift <wav:offB> [sir dB] : long recordings (5 min). The slider set at A's phase over the first 10 s
    // (as by ear at the start), then per 30 s: A's and B's phase (and steadiness) and the SDR gain over the mix with
    // a fixed phase vs tracking 0.5 / 2 / 8 s - does the tracking keep the station as its phase drifts?
    static int PickDrift(string[] args)
    {
        string x = args[0];
        string path = x.Substring(0, x.LastIndexOf(':'));
        double off = double.Parse(x.Substring(x.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
        double sir = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 0;
        var tw = Console.Out; Console.SetOut(TextWriter.Null);
        Rec A; try { A = Load(path, 0.2); } finally { Console.SetOut(tw); }
        var m = PickMixOf(A, A, off, sir);
        int F = m.F, nb = m.nb, seg = (int)Math.Round(30 * m.fps);
        // B alone on RX2 too, for its phase
        double[] nA = NoiseFloor(m.A1, F, nb), nB = NoiseFloor(m.Bg1, F, nb);
        (double ph, double st) Phase(double[] X1, double[] X2, double[] n, int f0, int f1)
        {
            double sr = 0, si = 0, c = 0;
            for (int f = f0; f < f1; f++)
                for (int k = 0; k < nb; k++)
                {
                    int i = f * nb + k;
                    if (Pow(X1, i) < 4 * n[k]) continue;
                    Cross(X1, X2, i, out double xr, out double xi);
                    double mm = Math.Sqrt(xr * xr + xi * xi);
                    if (mm > 0) { sr += xr / mm; si += xi / mm; c++; }
                }
            return (Math.Atan2(si, sr) * 180 / Math.PI, c > 0 ? Math.Sqrt(sr * sr + si * si) / c : 0);
        }
        double start = Phase(m.A1, m.A2, nA, 0, Math.Min(F, (int)(10 * m.fps))).ph * Math.PI / 180;
        var vars = new (string, PickPar)[]
        {
            ("fixed", new PickPar(TauS: 1e9, Pull: 0)),
            ("0.5 s", new PickPar(TauS: 0.5)),
            ("2 s", new PickPar()),
            ("8 s", new PickPar(TauS: 8)),
        };
        var G = vars.Select(v => MaskKeep(m.M1, m.M2, F, nb, m.n1, start, m.fps, v.Item2, out _)).ToArray();
        Console.WriteLine($"{Path.GetFileName(path)}: {F / m.fps:F0} s, B offset {off:F0} Hz, A/B {sir:+0;-0;0} dB, slider at {start * 180 / Math.PI:F0} deg (A, first 10 s)");
        Console.WriteLine($"  {"time",9} {"A deg",6} {"st",4} {"B deg",6} {"st",4}   SDR gain over mix: " + string.Join("  ", vars.Select(v => $"{v.Item1,6}")));
        var sum = new double[vars.Length]; int ns = 0;
        for (int s0 = 0; s0 + seg / 2 < F; s0 += seg)
        {
            int s1 = Math.Min(F, s0 + seg);
            var pa = Phase(m.A1, m.A2, nA, s0, s1); var pb = Phase(m.Bg1, m.Bg2, nB, s0, s1);
            double mix = PickScore(null, m.A1, m.A1, m.Bg1, m.Bg1, s1, nb, s0);
            var gains = G.Select(g => PickScore(null, m.A1, Mul(m.A1, g), m.Bg1, Mul(m.Bg1, g), s1, nb, s0) - mix).ToArray();
            for (int i = 0; i < gains.Length; i++) sum[i] += gains[i];
            ns++;
            Console.WriteLine($"  {s0 / m.fps,4:F0}-{s1 / m.fps,3:F0} s {pa.ph,6:F0} {pa.st,4:F2} {pb.ph,6:F0} {pb.st,4:F2}   " + string.Join("  ", gains.Select(v => $"{v,6:+0.0;-0.0}")));
        }
        Console.WriteLine($"  {"mean",9} {"",6} {"",4} {"",6} {"",4}   " + string.Join("  ", sum.Select(v => $"{v / ns,6:+0.0;-0.0}")));
        return 0;
    }

    // divtest pickreal <wav> [slider deg] : a real recording (a station + splatter / QRM from a neighbour), no truth.
    // 1) RX2-RX1 phase per 200 Hz across the whole IQ band (mean unit z1 conj z2 of strong cells, steadiness, level):
    //    the station's phase in the filter, the neighbour's beside it; 2) per 30 s the phase in the filter thirds;
    // 3) PICK on the filter (slider = the filter's middle third over the first 10 s, or given), fixed vs tracking 2 s:
    //    level change per 200 Hz of the filter (the splatter edge should go down, the station's core not); WAVs.
    static int PickReal(string[] args)
    {
        Rec r = Load(args[0], 0.2);
        int N = r.N, hop = r.hop, F = r.frames, nb = r.nb;
        double fps = (double)r.fs / hop, bin = r.binHz;
        // whole band, in chunks of frames (memory)
        int span = (int)(200 / bin), K = N - 2 * (int)(0.05 * N);
        int k0 = -N / 2 + (int)(0.05 * N);
        var cr = new double[K]; var ci = new double[K]; var cc = new double[K]; var pw = new double[K];
        const int chunk = 256;
        for (int f0 = 0; f0 < F; f0 += chunk)
        {
            int nf = Math.Min(chunk, F - f0);
            var sub1 = new double[2 * ((nf - 1) * hop + N)]; var sub2 = new double[sub1.Length];
            Array.Copy(r.z1, 2 * f0 * hop, sub1, 0, Math.Min(sub1.Length, r.z1.Length - 2 * f0 * hop));
            Array.Copy(r.z2, 2 * f0 * hop, sub2, 0, Math.Min(sub2.Length, r.z2.Length - 2 * f0 * hop));
            var X1 = Stft(sub1, N, hop, nf, k0, K); var X2 = Stft(sub2, N, hop, nf, k0, K);
            for (int i = 0; i < X2.Length; i++) X2[i] *= r.g2;
            var nf1 = NoiseFloor(X1, nf, K);
            for (int f = 0; f < nf; f++)
                for (int k = 0; k < K; k++)
                {
                    int i = f * K + k;
                    double p = Pow(X1, i); pw[k] += p;
                    if (p < 4 * nf1[k]) continue;
                    Cross(X1, X2, i, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi);
                    if (m > 0) { cr[k] += xr / m; ci[k] += xi / m; cc[k]++; }
                }
        }
        double med = Median(pw);
        Console.WriteLine($"RX2-RX1 phase per 200 Hz (from the VFO; filter {r.k0 * bin:F0}..{(r.k0 + nb - 1) * bin:F0} Hz), only where the level is 6 dB over the median:");
        for (int s = 0; s + span <= K; s += span)
        {
            double R = 0, I = 0, C = 0, P = 0;
            for (int k = s; k < s + span; k++) { R += cr[k]; I += ci[k]; C += cc[k]; P += pw[k]; }
            double lv = 10 * Math.Log10(P / span / med), hz = (k0 + s + span / 2.0) * bin;
            bool inF = hz >= r.k0 * bin && hz <= (r.k0 + nb - 1) * bin;
            if (lv < 6 && !inF) continue;
            Console.WriteLine($"  {hz,7:F0} Hz {(inF ? "F" : " ")} level {lv,5:F1} dB  phase {(C > 0 ? Math.Atan2(I, R) * 180 / Math.PI : double.NaN),5:F0}  steady {(C > 0 ? Math.Sqrt(R * R + I * I) / C : 0),4:F2}");
        }

        // per 30 s: the phase in each third of the filter
        int seg = (int)Math.Round(30 * fps), third = nb / 3;
        double[] nA = NoiseFloor(r.X1, F, nb);
        (double ph, double st) Ph(int b0, int b1, int f0, int f1)
        {
            double sr = 0, si = 0, c = 0;
            for (int f = f0; f < f1; f++)
                for (int k = b0; k < b1; k++)
                {
                    int i = f * nb + k;
                    if (Pow(r.X1, i) < 4 * nA[k]) continue;
                    Cross(r.X1, r.X2, i, out double xr, out double xi);
                    double m = Math.Sqrt(xr * xr + xi * xi);
                    if (m > 0) { sr += xr / m; si += xi / m; c++; }
                }
            return (Math.Atan2(si, sr) * 180 / Math.PI, c > 0 ? Math.Sqrt(sr * sr + si * si) / c : 0);
        }
        Console.WriteLine($"filter thirds (from the low RF end: {r.k0 * bin:F0}.. Hz) per 30 s: phase / steadiness");
        for (int s0 = 0; s0 + seg / 2 < F; s0 += seg)
        {
            int s1 = Math.Min(F, s0 + seg);
            var a = Ph(0, third, s0, s1); var b = Ph(third, 2 * third, s0, s1); var c = Ph(2 * third, nb, s0, s1);
            Console.WriteLine($"  {s0 / fps,4:F0}-{s1 / fps,3:F0} s   {a.ph,5:F0} {a.st:F2}   {b.ph,5:F0} {b.st:F2}   {c.ph,5:F0} {c.st:F2}");
        }

        double start = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : Ph(third, 2 * third, 0, Math.Min(F, (int)(10 * fps))).ph;
        string bas = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), Path.GetFileNameWithoutExtension(args[0]));
        WriteAudio(bas + "_real_rx1.wav", r.X1, F, nb, r.k0, r.lsb);
        Console.WriteLine($"PICK, slider {start:F0} deg: level change per 200 Hz of the filter (all frames / frames where that band is 10 dB over its noise)");
        var res = new System.Collections.Generic.List<(string, double[])>();
        foreach (var (name, q) in new[] { ("fixed", new PickPar(TauS: 1e9, Pull: 0)), ("track2s", new PickPar()) })
        {
            var G = MaskKeep(r.X1, r.X2, F, nb, nA, start * Math.PI / 180, fps, q, out double endPh);
            var Y = Mul(r.X1, G);
            WriteAudio($"{bas}_real_pick_{name}.wav", Y, F, nb, r.k0, r.lsb);
            Console.WriteLine($"  {name}: tracked phase at the end {endPh * 180 / Math.PI:F0} deg");
            res.Add((name, G));
        }
        // reject: cells whose patch agrees with the interferer's phase (args[2], fixed) go down; others stay
        if (args.Length > 2)
        {
            double rej = double.Parse(args[2], CultureInfo.InvariantCulture) * Math.PI / 180, fl = Math.Pow(10, -15 / 20.0);
            var rr = Enumerable.Repeat(Math.Cos(rej), nb).ToArray(); var ri = Enumerable.Repeat(Math.Sin(rej), nb).ToArray();
            foreach (var (a0, a1, name) in new[] { (0.3, 0.8, "rej"), (0.6, 0.9, "rejN") })
            {
                var G = new double[F * nb];
                for (int f = 0; f < F; f++)
                    for (int k = 0; k < nb; k++)
                        G[f * nb + k] = 1 - (1 - fl) * Math.Clamp((Agree(r.X1, r.X2, f, k, nb, rr, ri, 0) - a0) / (a1 - a0), 0, 1);
                WriteAudio($"{bas}_real_pick_{name}.wav", Mul(r.X1, G), F, nb, r.k0, r.lsb);
                res.Add((name, G));
            }
        }
        for (int s = 0; s + span <= nb; s += span)
        {
            var line = $"  {(r.k0 + s + span / 2.0) * bin,7:F0} Hz";
            foreach (var (name, G) in res)
            {
                double a = 0, b = 0, aa = 0, bb = 0;
                for (int f = 0; f < F; f++)
                {
                    double p = 0, y = 0, n = 0;
                    for (int k = s; k < s + span; k++) { int i = f * nb + k; p += Pow(r.X1, i); y += Pow(r.X1, i) * G[i] * G[i]; n += nA[k]; }
                    a += p; b += y;
                    if (p > 10 * n) { aa += p; bb += y; }
                }
                line += $"   {name} {10 * Math.Log10(b / a),5:F1} / {(aa > 0 ? 10 * Math.Log10(bb / aa) : 0),5:F1}";
            }
            Console.WriteLine(line);
        }
        return 0;
    }

    // divtest picksteady <wav...> : how steady the RX2-RX1 phase is in the RX filter, as PICK would measure it live:
    // per bin a running mean (tau s) of the unit z1 conj z2 of strong cells (over 4x the noise); steadiness = mean
    // |that| over the bins weighted by how often they are strong. Printed every 5 s (median per recording); random
    // phase gives ~1/sqrt(cells in tau).
    static int PickSteady(string[] args)
    {
        double tau = 3;
        foreach (string path in args)
        {
            var tw = Console.Out; Console.SetOut(TextWriter.Null);
            Rec r; try { r = Load(path, 0.2); } finally { Console.SetOut(tw); }
            int F = r.frames, nb = r.nb; double fps = (double)r.fs / r.hop, a = 1 / (tau * fps);
            double[] n1 = NoiseFloor(r.X1, F, nb);
            var vr = new double[nb]; var vi = new double[nb]; var w = new double[nb];
            var line = new System.Text.StringBuilder(); var all = new System.Collections.Generic.List<double>();
            for (int f = 0; f < F; f++)
            {
                for (int k = 0; k < nb; k++)
                {
                    int i = f * nb + k;
                    bool on = Pow(r.X1, i) > 4 * n1[k];
                    double ur = 0, ui = 0;
                    if (on) { Cross(r.X1, r.X2, i, out double xr, out double xi); double m = Math.Sqrt(xr * xr + xi * xi); if (m > 0) { ur = xr / m; ui = xi / m; } else on = false; }
                    if (on) { vr[k] += a * (ur - vr[k]); vi[k] += a * (ui - vi[k]); }
                    w[k] += a * ((on ? 1 : 0) - w[k]);
                }
                double num = 0, den = 0;
                for (int k = 0; k < nb; k++) { num += w[k] * Math.Sqrt(vr[k] * vr[k] + vi[k] * vi[k]); den += w[k]; }
                // |v| of a bin = mean unit vector of its strong cells; bins weighted by how often they are strong
                double st = den > 0 ? num / den : 0;
                if (f > 3 * fps && f % (int)(5 * fps) == 0) { line.Append($" {st:F2}"); all.Add(st); }
            }
            Console.WriteLine($"{Path.GetFileName(path).Substring(10, 15)} {(r.fs == 24000 ? "24k" : "192k")} median {Median(all.ToArray()):F2}:{line}");
        }
        return 0;
    }

    // the dominant steady phase in the filter over the first secs: per bin the mean unit z1 conj z2 of strong cells
    // (over 4x the noise), weighted by how often the bin is strong (as wdsprs pick.rs steadiness) - steady, busy
    // bins lead; rad
    static double PickAutoPhase(double[] X1, double[] X2, int F, int nb, double[] n1, double fps, double secs)
    {
        int f1 = Math.Min(F, (int)(secs * fps));
        var vr = new double[nb]; var vi = new double[nb]; var vw = new double[nb];
        for (int f = 0; f < f1; f++)
            for (int k = 0; k < nb; k++)
            {
                int i = f * nb + k;
                if (Pow(X1, i) <= 4 * n1[k]) continue;
                Cross(X1, X2, i, out double xr, out double xi);
                double m = Math.Sqrt(xr * xr + xi * xi);
                if (m > 0) { vr[k] += xr / m; vi[k] += xi / m; vw[k]++; }
            }
        double sr = 0, si = 0;
        for (int k = 0; k < nb; k++)
            if (vw[k] > 0) { double w = vw[k] / f1; sr += w * vr[k] / vw[k]; si += w * vi[k] / vw[k]; }
        return Math.Atan2(si, sr);
    }

    // the most common phase of the strong cells over the first secs (power-weighted histogram, 5 deg steps, smoothed
    // +-20 deg circularly) - a peak is not pulled toward a second station the way a mean is; rad
    static double PickAutoPeak(double[] X1, double[] X2, int F, int nb, double[] n1, double fps, double secs)
    {
        int f1 = Math.Min(F, (int)(secs * fps));
        var h = new double[72];
        for (int f = 0; f < f1; f++)
            for (int k = 0; k < nb; k++)
            {
                int i = f * nb + k;
                double p = Pow(X1, i);
                if (p <= 4 * n1[k]) continue;
                Cross(X1, X2, i, out double xr, out double xi);
                int b = ((int)Math.Floor((Math.Atan2(xi, xr) + Math.PI) / (2 * Math.PI) * 72) % 72 + 72) % 72;
                h[b] += Math.Log(p / n1[k]);
            }
        int best = 0; double bv = -1;
        for (int b = 0; b < 72; b++)
        {
            double v = 0;
            for (int d = -4; d <= 4; d++) v += (5 - Math.Abs(d)) * h[(b + d + 72) % 72];
            if (v > bv) { bv = v; best = b; }
        }
        return (best + 0.5) / 72 * 2 * Math.PI - Math.PI;
    }
}
