using System;
using System.Collections.Generic;
using System.Linq;
using Thetis;

static class Program
{
    static readonly Dictionary<char, string> Morse = new Dictionary<char, string>
    {
        {'A',".-"},{'B',"-..."},{'C',"-.-."},{'D',"-.."},{'E',"."},{'F',"..-."},{'G',"--."},{'H',"...."},
        {'I',".."},{'J',".---"},{'K',"-.-"},{'L',".-.."},{'M',"--"},{'N',"-."},{'O',"---"},{'P',".--."},
        {'Q',"--.-"},{'R',".-."},{'S',"..."},{'T',"-"},{'U',"..-"},{'V',"...-"},{'W',".--"},{'X',"-..-"},
        {'Y',"-.--"},{'Z',"--.."},{'0',"-----"},{'1',".----"},{'2',"..---"},{'3',"...--"},{'4',"....-"},
        {'5',"....."},{'6',"-...."},{'7',"--..."},{'8',"---.."},{'9',"----."},{'/',"-..-."},
    };

    // on/off keying envelope (1 = key down) in samples, raised-cosine 5 ms edges
    static float[] Keying(string text, double wpm, int fs, int total, double startS, double jitter, Random rnd)
    {
        var env = new float[total];
        double dit = 1.2 / wpm * fs;
        double t = startS * fs;
        var marks = new List<(double a, double b)>();
        while (t < total)
        {
            foreach (char ch in text)
            {
                if (ch == ' ') { t += 4 * dit; continue; }          // + 3 after the char = 7
                string code = Morse[ch];
                for (int i = 0; i < code.Length; i++)
                {
                    double len = (code[i] == '.' ? 1 : 3) * dit * (1 + jitter * (rnd.NextDouble() - 0.5));
                    marks.Add((t, t + len));
                    t += len + dit * (1 + jitter * (rnd.NextDouble() - 0.5));
                }
                t += 2 * dit;
            }
            t += 7 * dit;
        }
        double edge = 0.005 * fs;
        foreach (var m in marks)
        {
            int a = (int)Math.Max(0, m.a - edge), b = (int)Math.Min(total, m.b + edge);
            for (int i = a; i < b; i++)
            {
                double x = i < m.a ? 0.5 - 0.5 * Math.Cos(Math.PI * (i - (m.a - edge)) / edge)
                         : i > m.b ? 0.5 + 0.5 * Math.Cos(Math.PI * (i - m.b) / edge) : 1;
                env[i] = Math.Max(env[i], (float)x);
            }
        }
        return env;
    }

    // recorded IQ from Thetis: stereo float WAV, L = I, R = Q; decoder gets (Q, I) like in Thetis
    static int RunWav(string path)
    {
        byte[] b = System.IO.File.ReadAllBytes(path);
        int fs = BitConverter.ToInt32(b, 24);
        int n = (b.Length - 44) / 8;
        string bh = Environment.GetEnvironmentVariable("SK_BINHZ");
        var eng = bh == null ? new CwSkimmerEngine(fs) : new CwSkimmerEngine(fs, double.Parse(bh, System.Globalization.CultureInfo.InvariantCulture));
        if (Environment.GetEnvironmentVariable("SK_MINSNR") is string ms) eng.MinSnrDb = double.Parse(ms);
        long centre = long.Parse(System.Text.RegularExpressions.Regex.Match(path, @"_(\d+)Hz_").Groups[1].Value);
        var spots = new List<CwSkimmerEngine.Spot>();
        eng.SpotFound += sp => spots.Add(sp);
        int chunk = 2048;
        var bi = new float[chunk]; var bq = new float[chunk];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int s = 0; s < n; s += chunk)
        {
            int m = Math.Min(chunk, n - s);
            for (int k = 0; k < m; k++) { bi[k] = BitConverter.ToSingle(b, 44 + (s + k) * 8); bq[k] = BitConverter.ToSingle(b, 48 + (s + k) * 8); }
            eng.Process(bq, bi, m);
        }
        Console.WriteLine($"{path}: {n / (double)fs:F1}s at {fs}, {sw.ElapsedMilliseconds} ms");
        Console.WriteLine("--- channels (kHz = centre + offset, no CW pitch shift)");
        foreach (var c in eng.Channels().OrderBy(c => c.OffsetHz))
            Console.WriteLine($"{(centre + c.OffsetHz) / 1000.0,10:F2} {c.Wpm,3} wpm {c.SnrDb,3} dB  {c.Text}");
        Console.WriteLine("--- spots");
        foreach (var sp in spots)
            Console.WriteLine($"{sp.Call,-10} {(centre + sp.OffsetHz) / 1000.0,10:F2} {sp.Wpm,3} wpm {sp.SnrDb,3} dB");
        return 0;
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].EndsWith(".wav")) return RunWav(args[0]);
        int fs = args.Length > 0 ? int.Parse(args[0]) : 192000;
        double seconds = Environment.GetEnvironmentVariable("SK_SECONDS") is string ss ? double.Parse(ss) : 45;
        int total = (int)(fs * seconds);
        var rnd = new Random(Environment.GetEnvironmentVariable("SK_SEED") is string sd ? int.Parse(sd) : 12345);

        var all = new (string call, string text, double wpm, double off, double snr50)[]
        {
            ("SP9XYZ", "CQ CQ DE SP9XYZ SP9XYZ K",       22, -30012.3, 15),
            ("DL1ABC", "TEST DL1ABC DL1ABC",             30,  12520.0, 18),
            ("3Z9AM",  "CQ DE 3Z9AM 3Z9AM K",            18,  40210.0, 12),
            ("S50A",   "CQ TEST S50A S50A",              35,  -5000.7, 20),
            ("OH2BH",  "CQ DE OH2BH OH2BH K",            25,  70000.0,  8),
            ("K1ABC",  "CQ K1ABC K1ABC TEST",            40, -61234.0, 16),
        };
        var stations = all;
        if (args.Length > 1 && args[1] == "noise") stations = new (string, string, double, double, double)[0];
        if (args.Length > 2)   // solo <call> <snr>
            stations = all.Where(x => x.call == args[1]).Select(x => (x.call, x.text, x.wpm, x.off, double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var re = new float[total];
        var im = new float[total];
        // complex noise, total power 1
        for (int i = 0; i < total; i++)
        {
            double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1)) * Math.Sqrt(0.5);
            re[i] = (float)(r * Math.Cos(2 * Math.PI * u2));
            im[i] = (float)(r * Math.Sin(2 * Math.PI * u2));
        }
        foreach (var s in stations)
        {
            double amp = Math.Sqrt(Math.Pow(10, s.snr50 / 10) * 50.0 / fs);
            var env = Keying(s.text, s.wpm, fs, total, rnd.NextDouble() * 2, 0.15, rnd);
            double w = 2 * Math.PI * s.off / fs, ph = rnd.NextDouble() * 6;
            for (int i = 0; i < total; i++)
            {
                if (env[i] == 0) continue;
                re[i] += (float)(amp * env[i] * Math.Cos(w * i + ph));
                im[i] += (float)(amp * env[i] * Math.Sin(w * i + ph));
            }
        }

        string bh = Environment.GetEnvironmentVariable("SK_BINHZ");
        var eng = bh == null ? new CwSkimmerEngine(fs) : new CwSkimmerEngine(fs, double.Parse(bh, System.Globalization.CultureInfo.InvariantCulture));
        var spots = new List<CwSkimmerEngine.Spot>();
        if (Environment.GetEnvironmentVariable("SK_MINSNR") is string ms) eng.MinSnrDb = double.Parse(ms);
        eng.SpotFound += sp => spots.Add(sp);
        int maxCh = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int chunk = 2048;
        var bi = new float[chunk]; var bq = new float[chunk];
        for (int i = 0; i < total; i += chunk)
        {
            int n = Math.Min(chunk, total - i);
            Array.Copy(re, i, bi, 0, n); Array.Copy(im, i, bq, 0, n);
            eng.Process(bi, bq, n);
            if ((i / chunk) % 200 == 0) maxCh = Math.Max(maxCh, eng.Channels().Length);
        }
        Console.WriteLine($"max decoding channels seen: {maxCh}");
        sw.Stop();
        Console.WriteLine($"fs={fs}  {seconds}s of IQ processed in {sw.ElapsedMilliseconds} ms ({100.0 * sw.ElapsedMilliseconds / 1000 / seconds:F1}% of one core)");

        Console.WriteLine("--- channels");
        foreach (var c in eng.Channels().OrderBy(c => c.OffsetHz))
            Console.WriteLine($"{c.OffsetHz,10:F1} Hz {c.Wpm,3} wpm {c.SnrDb,3} dB  {c.Text}");
        Console.WriteLine("--- spots");
        foreach (var sp in spots)
            Console.WriteLine($"{sp.Call,-8} {sp.OffsetHz,10:F1} Hz {sp.Wpm,3} wpm {sp.SnrDb,3} dB");

        int fails = 0;
        foreach (var s in stations)
        {
            var hit = spots.FirstOrDefault(p => p.Call == s.call);
            bool ok = hit.Call != null && Math.Abs(hit.OffsetHz - s.off) < 30 && Math.Abs(hit.Wpm - s.wpm) <= 4;
            bool required = s.snr50 >= 10;
            Console.WriteLine($"{(ok ? "PASS" : required ? "FAIL" : "miss")} {s.call} ({s.wpm} wpm, {s.snr50} dB)" + (hit.Call != null ? $" -> {hit.OffsetHz:F1} Hz {hit.Wpm} wpm" : ""));
            if (!ok && required) fails++;
        }
        var bogus = spots.Where(p => !stations.Any(s => s.call == p.Call)).ToList();
        foreach (var b in bogus) Console.WriteLine($"FALSE SPOT {b.Call} {b.OffsetHz:F1}");
        fails += bogus.Count;
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAIL");
        return fails == 0 ? 0 : 1;
    }
}
