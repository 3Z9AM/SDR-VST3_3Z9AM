//  AoAAutoCal.cs
//
//  Automatic level calibration of the angle of arrival (wire + vertical, level ratio): the CW skimmer's
//  calls are placed by their prefix (cty.dat, AD1C), the distance from the own locator gives the elevation
//  of a one-hop F path, and the RX2-RX1 level measured on each station's frequency against the model for
//  that elevation gives one correction per station; the band's correction is their median.
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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Thetis.FreeDVReporter;

namespace Thetis
{
    internal static class AoAAutoCal
    {
        public static volatile bool On;
        public static volatile string Locator = "";
        public static double LayerKm = 300;
        public static string DataDir = "";
        public static volatile string Problem = "";       // why nothing is collected right now ("" = fine)
        public static event Action Changed;              // a band correction was set (any thread)

        // one hop only, where the elevation is predictable and a prefix position (country centre) is good enough
        public const double MinKm = 100, MaxKm = 3000;
        public const int MinStations = 4;
        private const double MinKeyedSeconds = 1, MaxKeyedSeconds = 8, KeepMs = 20000, SignalOverNoise = 6;

        private sealed class Target
        {
            public string Call;
            public double Off, Lat, Lon;
            public long Ref, Until;
            public double A1, A2;
            public int Frames;
        }

        // a station as measured: its correction is worked out with the current locator / layer / antennas, so
        // changing those (or a restart - kept in <data>AoA_autocal.csv) does not start the collection over
        private sealed class Station
        {
            public double Lat, Lon, Hz, RatioSum;
            public int N;
        }

        private static readonly List<Target> _targets = new List<Target>();
        private static readonly Dictionary<Band, Dictionary<string, Station>> _obs = new Dictionary<Band, Dictionary<string, Station>>();
        private static readonly Dictionary<Band, double[]> _result = new Dictionary<Band, double[]>();   // band -> {stations used, median dB}
        private static string _sig;
        private static volatile bool _loaded;          // nothing is measured (or saved over the file) before it was read
        private static readonly Regex CallRx = new Regex("^([0-9]?[A-Z]{1,2}|[A-Z][0-9])[0-9]{1,2}[A-Z]{1,4}$", RegexOptions.Compiled);
        private static string FilePath => Path.Combine(DataDir, "AoA_autocal.csv");

        // one hop off a layer at hKm, ground distance dKm: elevation in degrees (negative = no one-hop path)
        public static double OneHopElevation(double dKm, double hKm)
        {
            const double R = Maidenhead.EarthRadiusKm;
            double phi = dKm / (2 * R);
            return Math.Atan2(Math.Cos(phi) - R / (R + hKm), Math.Sin(phi)) * 180 / Math.PI;
        }

        // CW skimmer thread
        public static void OnSpot(string call, double offHz)
        {
            if (!On || string.IsNullOrEmpty(call)) return;
            Cty.EnsureLoaded(DataDir);
            if (!Maidenhead.TryToLatLon(Locator, out double myLat, out double myLon)) { Problem = "enter your locator (4 or 6 characters)"; return; }
            if (!Cty.Ready) { Problem = Cty.Failed ? "cty.dat not downloaded (put it in the data folder)" : "loading cty.dat..."; return; }
            Problem = "";
            call = call.Trim().ToUpperInvariant();
            string main = "";
            foreach (string part in call.Split('/')) if (part.Length > main.Length) main = part;   // the call itself, not /P or DL/
            if (!CallRx.IsMatch(main) || !Cty.TryLocate(call, out double lat, out double lon)) return;
            double km = Maidenhead.DistanceKmLatLon(myLat, myLon, lat, lon);
            if (km < MinKm || km > MaxKm) return;
            long now = Environment.TickCount64, refHz = Display.RX1IqOffsetToHz(0);
            lock (_targets)
            {
                Target t = _targets.Find(x => x.Call == call);
                if (t == null)
                {
                    if (_targets.Count >= 40) return;
                    _targets.Add(t = new Target { Call = call, Ref = refHz });
                }
                t.Off = offHz; t.Lat = lat; t.Lon = lon; t.Until = now + (long)KeepMs;
            }
        }

        // Diversity phase thread, after every DiversityCross.Feed
        public static void Feed(DiversityCross dc, Band band)
        {
            if (!On || !_loaded) return;
            long now = Environment.TickCount64, refHz = Display.RX1IqOffsetToHz(0);
            bool tx = Audio.MOX;
            double frameS = 1 / dc.BinHz;
            var noise1 = new double[36]; var noise2 = new double[36];
            List<Target> done = null;
            lock (_targets)
            {
                for (int i = _targets.Count - 1; i >= 0; i--)
                {
                    Target t = _targets[i];
                    bool finished = t.Frames * frameS >= MaxKeyedSeconds;
                    if (t.Ref != refHz || now > t.Until || finished)
                    {
                        _targets.RemoveAt(i);
                        if (t.Ref == refHz) (done ??= new List<Target>()).Add(t);
                        continue;
                    }
                    if (tx) continue;
                    // the strongest of the 3 bins around the spot (the skimmer bin is narrower), noise = median of the neighbours
                    int b = (int)Math.Round(t.Off / dc.BinHz);
                    double p1 = 0, p2 = 0;
                    for (int j = -1; j <= 1; j++)
                    {
                        dc.BinPower(b + j, out double q1, out double q2);
                        if (q1 + q2 > p1 + p2) { p1 = q1; p2 = q2; }
                    }
                    int n = 0;
                    for (int j = 3; j <= 20; j++)
                        for (int s = -1; s <= 1; s += 2) { dc.BinPower(b + s * j, out noise1[n], out noise2[n]); n++; }
                    Array.Sort(noise1, 0, n); Array.Sort(noise2, 0, n);
                    double n1 = noise1[n / 2], n2 = noise2[n / 2];
                    if (p1 + p2 <= SignalOverNoise * (n1 + n2)) continue;     // key up / fading
                    t.A1 += Math.Max(0, p1 - n1); t.A2 += Math.Max(0, p2 - n2);
                    t.Frames++;
                }
            }
            if (done == null) return;
            bool added = false;
            foreach (Target t in done)
            {
                if (t.Frames * frameS < MinKeyedSeconds || t.A1 <= 0 || t.A2 <= 0) continue;
                double hz = Display.RX1IqOffsetToHz(t.Off);
                if (hz <= 0) continue;
                lock (_obs)
                {
                    if (!_obs.TryGetValue(band, out var calls)) _obs[band] = calls = new Dictionary<string, Station>();
                    if (!calls.TryGetValue(t.Call, out Station st)) calls[t.Call] = st = new Station();
                    st.Lat = t.Lat; st.Lon = t.Lon; st.Hz = hz;
                    st.RatioSum += 10 * Math.Log10(t.A2 / t.A1); st.N++;
                }
                added = true;
            }
            if (!added) return;
            save();
            recompute(band);
        }

        // the band's correction from its stations with the current settings; sets AoABandCal at >= MinStations
        private static void recompute(Band band)
        {
            if (!Maidenhead.TryToLatLon(Locator, out double myLat, out double myLon)) return;
            if (DiversityForm.AoARx1Vertical == DiversityForm.AoARx2Vertical) return;
            var v = new List<double>();
            lock (_obs)
            {
                if (!_obs.TryGetValue(band, out var calls)) return;
                foreach (Station st in calls.Values)
                {
                    double km = Maidenhead.DistanceKmLatLon(myLat, myLon, st.Lat, st.Lon);
                    if (km < MinKm || km > MaxKm) continue;
                    double el = OneHopElevation(km, LayerKm);
                    if (el > 0 && DiversityForm.AoARatioCal(st.RatioSum / st.N, el, st.Hz, out double cal) && Math.Abs(cal) <= 50) v.Add(cal);
                }
            }
            v.Sort();
            int count = v.Count;
            double median = count == 0 ? 0 : Math.Round(count % 2 == 1 ? v[count / 2] : (v[count / 2 - 1] + v[count / 2]) / 2, 1);
            lock (_result) _result[band] = new double[] { count, median };
            if (count < MinStations) return;
            double[] old = DiversityForm.AoABandCal.TryGetValue(band, out double[] o) ? o : new double[2];
            if (Math.Abs(old[0] - median) < 0.05) return;
            DiversityForm.AoABandCal[band] = new[] { median, old[1] };
            Changed?.Invoke();
        }

        // UI timer (1 s): loads the stations once, works the corrections out again after a settings change
        public static void Tick()
        {
            if (!On || string.IsNullOrEmpty(DataDir)) return;
            if (!_loaded) { load(); _loaded = true; }
            if (DiversityForm.AoARx1Vertical == DiversityForm.AoARx2Vertical) { Problem = "needs a wire + a vertical (level ratio)"; return; }
            if (Problem.StartsWith("needs")) Problem = "";
            string sig = string.Join("|", Locator, LayerKm, DiversityForm.AoARx1Height, DiversityForm.AoARx1Length,
                DiversityForm.AoARx2Vertical, DiversityForm.AoARx2Height, DiversityForm.AoARx2Length);
            if (sig == _sig) return;
            _sig = sig;
            var bands = new List<Band>();
            lock (_obs) bands.AddRange(_obs.Keys);
            foreach (Band b in bands) recompute(b);
        }

        // "40M: 3/5 stations" / "40M: 7 stations, level -17.9 dB" (+ what is missing)
        public static string StatusFor(Band band)
        {
            string b = band.ToString().TrimStart('B');
            double[] r;
            lock (_result) _result.TryGetValue(band, out r);
            int n = r == null ? 0 : (int)r[0];
            string s = n >= MinStations ? b + ": " + n + " stations, level " + r[1].ToString("+0.0;-0.0;0.0") + " dB"
                                        : b + ": " + n + "/" + MinStations + " stations";
            string p = Problem;
            return p.Length > 0 ? s + " - " + p : s;
        }

        // band;call;lat;lon;Hz;ratio sum dB;count
        private static void save()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            lock (_obs)
                foreach (var kv in _obs)
                    foreach (var c in kv.Value)
                        sb.Append(kv.Key).Append(';').Append(c.Key).Append(';').Append(c.Value.Lat.ToString("0.###", ci)).Append(';')
                          .Append(c.Value.Lon.ToString("0.###", ci)).Append(';').Append(c.Value.Hz.ToString("0", ci)).Append(';')
                          .Append(c.Value.RatioSum.ToString("0.###", ci)).Append(';').Append(c.Value.N).Append('\n');
            try { File.WriteAllText(FilePath, sb.ToString()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AoA autocal save: " + ex.Message); }
        }

        private static void load()
        {
            var ci = CultureInfo.InvariantCulture;
            try
            {
                if (!File.Exists(FilePath)) return;
                lock (_obs)
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string[] f = line.Split(';');
                        if (f.Length != 7 || !Enum.TryParse(f[0], out Band band) ||
                            !double.TryParse(f[2], NumberStyles.Float, ci, out double lat) || !double.TryParse(f[3], NumberStyles.Float, ci, out double lon) ||
                            !double.TryParse(f[4], NumberStyles.Float, ci, out double hz) || !double.TryParse(f[5], NumberStyles.Float, ci, out double sum) ||
                            !int.TryParse(f[6], out int n) || n <= 0) continue;
                        if (!_obs.TryGetValue(band, out var calls)) _obs[band] = calls = new Dictionary<string, Station>();
                        calls[f[1]] = new Station { Lat = lat, Lon = lon, Hz = hz, RatioSum = sum, N = n };
                    }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AoA autocal load: " + ex.Message); }
        }
    }

    // AD1C country file (www.country-files.com): prefix -> position of the entity (or the alias override)
    internal static class Cty
    {
        public const string Url = "https://www.country-files.com/cty/cty.dat";
        private static Dictionary<string, double[]> _exact, _prefix;
        private static int _maxLen, _state;           // 0 none, 1 loading, 2 ready, 3 failed
        private static long _failedAt;
        public static bool Ready => _state == 2;
        public static bool Failed => _state == 3;

        // <data>cty.dat, downloaded when missing or older than 30 days; a failed attempt is retried after 5 min
        public static void EnsureLoaded(string dir)
        {
            if (_state == 3 && Environment.TickCount64 - _failedAt > 300000) Interlocked.CompareExchange(ref _state, 0, 3);
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    string path = Path.Combine(dir, "cty.dat");
                    if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-30))
                    {
                        try
                        {
                            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                            {
                                byte[] data = await http.GetByteArrayAsync(Url);
                                if (data.Length > 10000) File.WriteAllBytes(path, data);
                            }
                        }
                        catch { if (!File.Exists(path)) throw; }      // an old file still works
                    }
                    Parse(File.ReadAllText(path));
                    _state = 2;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("cty.dat: " + ex.Message);
                    _failedAt = Environment.TickCount64;
                    _state = 3;
                }
            });
        }

        // records "Name: CQ: ITU: Cont: Lat: Lon(+ = west): TZ: Prefix:" then aliases "A,B(cq)[itu]<lat/lon>,=CALL;"
        public static void Parse(string text)
        {
            var exact = new Dictionary<string, double[]>();
            var prefix = new Dictionary<string, double[]>();
            int maxLen = 0;
            var ci = CultureInfo.InvariantCulture;
            var ll = new Regex(@"<([-\d.]+)/([-\d.]+)>");
            foreach (string rec in text.Split(';'))
            {
                string[] h = rec.Split(':');
                if (h.Length < 9) continue;
                if (!double.TryParse(h[4].Trim(), NumberStyles.Float, ci, out double lat) ||
                    !double.TryParse(h[5].Trim(), NumberStyles.Float, ci, out double lon)) continue;
                foreach (string raw in h[8].Split(','))
                {
                    string a = raw.Trim();
                    if (a.Length == 0) continue;
                    double[] pos = { lat, -lon };
                    Match m = ll.Match(a);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, ci, out double la) &&
                        double.TryParse(m.Groups[2].Value, NumberStyles.Float, ci, out double lo)) pos = new[] { la, -lo };
                    bool isExact = a[0] == '=';
                    int end = isExact ? 1 : 0;
                    while (end < a.Length && (char.IsLetterOrDigit(a[end]) || a[end] == '/')) end++;
                    string key = a.Substring(isExact ? 1 : 0, end - (isExact ? 1 : 0)).ToUpperInvariant();
                    if (key.Length == 0) continue;
                    if (isExact) exact[key] = pos;
                    else { prefix[key] = pos; maxLen = Math.Max(maxLen, key.Length); }
                }
            }
            _exact = exact; _prefix = prefix; _maxLen = maxLen;
        }

        private static readonly HashSet<string> Suffixes = new HashSet<string> { "P", "M", "MM", "AM", "QRP", "A", "B", "LH" };

        // the part of a portable call that decides the entity: "DL/G4ABC" and "G4ABC/DL" -> DL, "G4ABC/P" -> G4ABC
        public static string BaseCall(string call)
        {
            string[] parts = call.Split('/');
            string best = null;
            foreach (string p in parts)
            {
                if (p.Length == 0 || Suffixes.Contains(p) || (p.Length == 1 && char.IsDigit(p[0]))) continue;
                if (best == null) best = p;
                else return p.Length < best.Length ? p : best;       // two parts: the shorter is the prefix
            }
            return best ?? call;
        }

        public static bool TryLocate(string call, out double lat, out double lon)
        {
            lat = lon = 0;
            Dictionary<string, double[]> exact = _exact, prefix = _prefix;
            if (exact == null) return false;
            if (exact.TryGetValue(call, out double[] p) || exact.TryGetValue(BaseCall(call), out p)) { lat = p[0]; lon = p[1]; return true; }
            string s = BaseCall(call);
            for (int n = Math.Min(_maxLen, s.Length); n > 0; n--)
                if (prefix.TryGetValue(s.Substring(0, n), out p)) { lat = p[0]; lon = p[1]; return true; }
            return false;
        }
    }
}
