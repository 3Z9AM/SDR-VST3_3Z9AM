using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Thetis
{
    // CW skimmer engine: decodes every CW signal in a block of complex IQ at once.
    // Pure DSP, no Thetis dependencies (CwSkimmerForm.cs feeds it and places the spots).
    //
    // An STFT splits the band into ~23 Hz bins every ~11 ms. Each bin's level is the sum
    // of it and its two neighbours (steadier than one bin), with its own noise floor (30th
    // percentile tracker); a bin whose peaks stand MinSnrDb above it becomes a channel. A channel
    // keys mark/space with hysteresis ~6 dB under its peak (the unbiased edge point of the
    // window), learns dit length from 2-means on its marks and letter/word gaps from 2-means on
    // its spaces, decodes Morse and reports a callsign once it has been copied twice.
    public sealed class CwSkimmerEngine
    {
        public struct Spot
        {
            public string Call;
            public double OffsetHz;     // from the IQ centre
            public int Wpm;
            public int SnrDb;
        }

        public struct ChannelInfo
        {
            public double OffsetHz;
            public int Wpm;
            public int SnrDb;
            public string Text;
        }

        public double MinSnrDb = 10;
        public int MaxChannels = 100;
        public event Action<Spot> SpotFound;

        public int SampleRate { get { return _fs; } }

        private const double TargetBinHz = 23.4375;           // tested: 47 Hz lost 4 dB on slow CW, 12 Hz smeared 35+ WPM
        private const double NfQuantile = 0.3, NfStepDb = 0.15; // noise floor = 30th percentile of the level
        private const double PeakDecayDbPerS = 3.0;           // bin peak tracker (channel detection)
        private const int DetectEveryFrames = 94;             // ~0.5 s
        // tuned on synthetic CW in noise (18-40 WPM) and 40 m band recordings (QSB, QRM, hand keys)
        private const double SmoothDits = 0.4;                // level averaged over 0.4 dit
        private const double HystFrac = 0.12, MaxHystDb = 2.5; // keying hysteresis, fraction of the channel SNR
        private const double EdgeDb = 6.0;                    // strong signals key 6 dB under the (fast) peak
        private const double LowFrac = 0.4;                   // ...but never below 40% of the SNR (noise in pauses)
        private const double FastPeakDecayDbPerS = 20.0;      // follows QSB
        private const double SlowPeakDecayDbPerS = 2.0;       // holds the level across pauses
        private const double WordK = 0.7;                     // word gap > letter gap + 0.7 (letter - element gap); lower splits calls (R2S CO)
        private const double HitDb = 1.0, HitFrac = 0.05;     // new channel: >= MinSnr above the floor 5% of the time
        private const double GlitchMark = 0.4;                // marks shorter than 0.4 dit are noise
        private const double BridgeSpace = 0.25;              // gaps shorter than 0.25 dit are fades inside a mark

        private readonly int _fs, _n, _hop;
        private readonly double _binHz, _hopMs;
        private readonly float[] _win, _re, _im, _ringRe, _ringIm;
        private readonly double[] _pow, _lvl, _nf, _pk, _hit;
        private readonly Fft _fft;
        private int _ringPos, _ringFill, _sinceHop;
        private long _frame;
        private bool _nfInit;
        private readonly List<Channel> _channels = new List<Channel>();

        public CwSkimmerEngine(int sampleRate) : this(sampleRate, TargetBinHz) { }

        public CwSkimmerEngine(int sampleRate, double binHz)
        {
            _fs = sampleRate;
            _n = 1024;
            while (_n * binHz < sampleRate) _n <<= 1;
            _hop = _n / 4;
            _binHz = (double)_fs / _n;
            _hopMs = 1000.0 * _hop / _fs;

            _win = new float[_n];
            for (int i = 0; i < _n; i++) _win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _n));
            _re = new float[_n]; _im = new float[_n];
            _ringRe = new float[_n]; _ringIm = new float[_n];
            _pow = new double[_n]; _lvl = new double[_n]; _nf = new double[_n]; _pk = new double[_n]; _hit = new double[_n];

            _fft = new Fft(_n);
        }

        public void Reset()
        {
            lock (_channels) _channels.Clear();
            _ringFill = 0; _sinceHop = 0; _nfInit = false;
        }

        public void Process(float[] re, float[] im, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _ringRe[_ringPos] = re[i];
                _ringIm[_ringPos] = im[i];
                _ringPos = (_ringPos + 1) & (_n - 1);
                if (_ringFill < _n) _ringFill++;
                if (++_sinceHop >= _hop && _ringFill == _n)
                {
                    _sinceHop = 0;
                    frame();
                }
            }
        }

        // readableOnly: drop channels whose text looks like noise (the list); the tuned signal shows anyway
        public ChannelInfo[] Channels(bool readableOnly = true)
        {
            lock (_channels)
            {
                var list = new List<ChannelInfo>();
                foreach (Channel c in _channels)
                    if (c.Decoding && (!readableOnly || Readable(c.Text)))
                        list.Add(new ChannelInfo { OffsetHz = c.OffsetHz(this), Wpm = c.Wpm, SnrDb = (int)Math.Round(c.Snr), Text = c.Text.ToString() });
                return list.ToArray();
            }
        }

        // Noise, voice and digital modes key random short marks: the text is mostly E, T, I and
        // undecodable characters. Real CW (calls, reports, words) has far fewer of them.
        private static bool Readable(StringBuilder text)
        {
            int letters = 0, junk = 0;
            for (int i = Math.Max(0, text.Length - 60); i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ') continue;
                letters++;
                if (c == 'E' || c == 'T' || c == 'I' || c == '*') junk++;
            }
            return letters >= 6 && junk * 2 < letters;
        }

        private double signedBinHz(double k) { return (k >= _n / 2 ? k - _n : k) * _binHz; }

        private void frame()
        {
            _frame++;
            for (int i = 0; i < _n; i++)
            {
                int src = (_ringPos + i) & (_n - 1);           // oldest sample first
                int dst = _fft.BitRev[i];
                _re[dst] = _ringRe[src] * _win[i];
                _im[dst] = _ringIm[src] * _win[i];
            }
            _fft.Transform(_re, _im);

            double decay = PeakDecayDbPerS * _hopMs / 1000.0;
            double hitA = _hopMs / 2000.0;
            for (int k = 0; k < _n; k++)
                _pow[k] = (double)_re[k] * _re[k] + (double)_im[k] * _im[k] + 1e-30;
            for (int k = 0; k < _n; k++)
            {
                double l = 10 * Math.Log10(_pow[(k - 1) & (_n - 1)] + _pow[k] + _pow[(k + 1) & (_n - 1)]);
                _lvl[k] = l;
                if (!_nfInit) { _nf[k] = l; _pk[k] = l; continue; }
                _nf[k] += l > _nf[k] ? NfQuantile * NfStepDb : -(1 - NfQuantile) * NfStepDb;
                _pk[k] = Math.Max(l, _pk[k] - decay);
                _hit[k] += hitA * ((l > _nf[k] + HitDb * MinSnrDb ? 1.0 : 0.0) - _hit[k]);
            }
            _nfInit = true;

            lock (_channels)
            {
                for (int i = _channels.Count - 1; i >= 0; i--)
                    if (!_channels[i].Step(this)) _channels.RemoveAt(i);
                if (_frame % DetectEveryFrames == 0) detect();
            }
        }

        private void detect()
        {
            int edge = _n / 20;                                // skip the 10% band edges
            var found = new List<KeyValuePair<double, int>>();
            for (int s = -_n / 2 + edge; s < _n / 2 - edge; s++)
            {
                int k = s & (_n - 1);
                // keyed carrier: clearly above the floor for a fair share of the last ~2 s
                // (noise alone almost never is; a single noise spike is not enough)
                if (_hit[k] < HitFrac) continue;
                double snr = _pk[k] - _nf[k];
                bool peak = true;
                for (int d = -2; d <= 2 && peak; d++)
                    if (d != 0 && _pk[(k + d) & (_n - 1)] > _pk[k]) peak = false;
                if (!peak) continue;
                bool taken = false;
                foreach (Channel c in _channels)
                    if (Math.Abs(((c.Bin - k + _n / 2) & (_n - 1)) - _n / 2) <= 2) { taken = true; break; }
                if (!taken) found.Add(new KeyValuePair<double, int>(snr, k));
            }
            found.Sort((a, b) => b.Key.CompareTo(a.Key));
            foreach (var f in found)
            {
                if (_channels.Count >= MaxChannels) break;
                bool near = false;                             // equal peaks next to each other: keep one
                foreach (Channel c in _channels)
                    if (Math.Abs(((c.Bin - f.Value + _n / 2) & (_n - 1)) - _n / 2) <= 2) { near = true; break; }
                if (!near) _channels.Add(new Channel(f.Value, this));
            }
        }

        // ---------------------------------------------------------------- per signal

        private static readonly Dictionary<string, char> Morse = new Dictionary<string, char>
        {
            {".-",'A'},{"-...",'B'},{"-.-.",'C'},{"-..",'D'},{".",'E'},{"..-.",'F'},{"--.",'G'},{"....",'H'},
            {"..",'I'},{".---",'J'},{"-.-",'K'},{".-..",'L'},{"--",'M'},{"-.",'N'},{"---",'O'},{".--.",'P'},
            {"--.-",'Q'},{".-.",'R'},{"...",'S'},{"-",'T'},{"..-",'U'},{"...-",'V'},{".--",'W'},{"-..-",'X'},
            {"-.--",'Y'},{"--..",'Z'},{"-----",'0'},{".----",'1'},{"..---",'2'},{"...--",'3'},{"....-",'4'},
            {".....",'5'},{"-....",'6'},{"--...",'7'},{"---..",'8'},{"----.",'9'},{"-..-.",'/'},{"..--..",'?'},
            {"-...-",'='},{".-.-.",'+'},{".-.-.-",'.'},{"--..--",','},
        };

        // optional prefix/, 1-3 char prefix, digit, 1-4 letter suffix, optional /suffix
        private static readonly Regex CallRx = new Regex(@"^(?:[A-Z0-9]{1,4}/)?[A-Z0-9]{0,2}[A-Z][0-9]{1,2}[A-Z]{1,4}(?:/[A-Z0-9]{1,4})?$|^(?:[A-Z0-9]{1,4}/)?[0-9][A-Z][0-9][A-Z]{1,4}(?:/[A-Z0-9]{1,4})?$", RegexOptions.Compiled);
        private static readonly HashSet<string> NotCalls = new HashSet<string> { "R5NN", "TU5NN" };

        private sealed class Channel
        {
            public int Bin;
            public bool Decoding;
            public int Wpm { get { return (int)Math.Round(1200.0 / _dit); } }
            public double Snr { get { return _peak - _floorDb; } }
            public readonly StringBuilder Text = new StringBuilder();

            private double _peak, _fastPeak;
            private double _floorDb;
            private readonly double[] _hist = new double[16];   // recent linear levels for smoothing
            private int _histPos;
            private long _lastStrongFrame;
            private bool _mark;
            private long _start, _prevStart, _lastMarkFrame;
            private double _pendingMarkMs = -1;
            private string _elems = "";
            private bool _charOut, _wordOut = true;
            private double _dit = 60, _bias;                    // ms
            private readonly List<double> _marks = new List<double>();
            private readonly List<double> _spaces = new List<double>();
            private double _charGap = -1, _wordGap = -1;        // learned from the spaces, ms; -1 = from the dit
            private readonly StringBuilder _word = new StringBuilder();
            private bool _wordHasDah;                           // noise keys mostly dits: E5E, SHE...
            private readonly Dictionary<string, KeyValuePair<int, long>> _calls = new Dictionary<string, KeyValuePair<int, long>>();
            private readonly Dictionary<string, long> _spotted = new Dictionary<string, long>();
            private double _accL, _accC, _accR;                 // power around the bin during marks
            private long _frameNo;

            public Channel(int bin, CwSkimmerEngine e)
            {
                Bin = bin;
                _floorDb = e._nf[bin];
                _peak = _fastPeak = e._pk[bin];
                for (int i = 0; i < _hist.Length; i++) _hist[i] = Math.Pow(10, _floorDb / 10);
                _bias = e._n * 1000.0 / e._fs * 0.4;             // window smear lengthens marks
                _lastMarkFrame = 0;
            }

            public double OffsetHz(CwSkimmerEngine e)
            {
                double d = 0, den = _accL - 2 * _accC + _accR;
                if (_accC > 0 && den < 0) d = Math.Max(-0.5, Math.Min(0.5, 0.5 * (_accL - _accR) / den));
                return e.signedBinHz(Bin) + d * e._binHz;
            }

            // false = channel died
            public bool Step(CwSkimmerEngine e)
            {
                _frameNo++;
                // level: the bins that actually hold this signal, averaged over ~0.3 dit
                int n0 = e._n - 1;
                double pc = e._pow[Bin], pl = e._pow[(Bin - 1) & n0], pr = e._pow[(Bin + 1) & n0];
                double lin = pc;
                bool known = _accC > 0 && _marks.Count >= 4;
                if (!known || _accL > 0.3 * _accC) lin += pl;
                if (!known || _accR > 0.3 * _accC) lin += pr;
                _hist[_histPos] = lin;
                _histPos = (_histPos + 1) & 15;
                int avg = Math.Max(1, Math.Min(12, (int)Math.Round(SmoothDits * _dit / e._hopMs)));
                double sum = 0;
                for (int i = 1; i <= avg; i++) sum += _hist[(_histPos - i) & 15];
                double l = 10 * Math.Log10(sum / avg);
                _floorDb += l > _floorDb ? NfQuantile * NfStepDb : -(1 - NfQuantile) * NfStepDb;
                // slow peak: the signal's level across pauses (sets SNR and the threshold's lower bound);
                // fast peak: follows QSB so strong signals key near the -6 dB edge point
                _peak = l > _peak ? _peak + 0.5 * (l - _peak) : _peak - SlowPeakDecayDbPerS * e._hopMs / 1000.0;
                _fastPeak = l > _fastPeak ? _fastPeak + 0.5 * (l - _fastPeak) : _fastPeak - FastPeakDecayDbPerS * e._hopMs / 1000.0;
                if (l > _floorDb + e.MinSnrDb) _lastStrongFrame = _frameNo;
                if (_peak < _floorDb) _peak = _floorDb;
                if (_fastPeak < _floorDb) _fastPeak = _floorDb;

                double snr = _peak - _floorDb;
                Decoding = snr >= e.MinSnrDb * 0.8 && _marks.Count > 0;
                double thr = _floorDb + Math.Max(LowFrac * snr, _fastPeak - _floorDb - EdgeDb);
                double hyst = Math.Max(1.0, Math.Min(MaxHystDb, HystFrac * snr));
                bool on = snr >= e.MinSnrDb * 0.7 && (_mark ? l > thr - hyst : l > thr + hyst);

                if (on)
                {
                    int n = e._n - 1;
                    _accL += e._pow[(Bin - 1) & n]; _accC += e._pow[Bin]; _accR += e._pow[(Bin + 1) & n];
                    _lastMarkFrame = _frameNo;
                }

                double t = (_frameNo - _start) * e._hopMs;
                double ditTrue = _dit;
                if (on && !_mark)
                {
                    // space ended
                    if (_pendingMarkMs >= 0 && t < Math.Max(4.0, BridgeSpace * ditTrue))
                    {
                        _start = _prevStart;                    // short gap: bridge into the previous mark
                        _pendingMarkMs = -1;
                    }
                    else
                    {
                        if (t < 5 * _dit + _bias) learnSpace(t);
                        commitPending(e);
                        endGap(e, t);                           // the whole gap, also when it ended between two frames
                        _prevStart = _start;
                        _start = _frameNo;
                    }
                    _mark = true;
                }
                else if (!on && _mark)
                {
                    double markMs = t;
                    _mark = false;
                    if (markMs < Math.Max(8.0, GlitchMark * (ditTrue + _bias)))
                        _start = _prevStart;                    // glitch: the space carries on
                    else
                    {
                        _pendingMarkMs = markMs;
                        _prevStart = _start;
                        _start = _frameNo;
                    }
                }
                else if (!_mark)
                {
                    if (_pendingMarkMs >= 0 && t >= Math.Max(4.0, BridgeSpace * ditTrue)) commitPending(e);
                    endGap(e, t);
                }

                // nothing clearly above the floor for 15 s -> drop
                return (_frameNo - _lastStrongFrame) * e._hopMs < 15000;
            }

            private void commitPending(CwSkimmerEngine e)
            {
                if (_pendingMarkMs < 0) return;
                double m = _pendingMarkMs;
                _pendingMarkMs = -1;
                _marks.Add(m);
                if (_marks.Count > 24) _marks.RemoveAt(0);
                learnTiming();
                _elems += m < (_dit * 2 + _bias) ? "." : "-";
                if (_elems.Length > 7) _elems = _elems.Substring(_elems.Length - 7);
                _charOut = false;
            }

            // 2-means on the recent marks: dits = d + b, dahs = 3d + b
            private void learnTiming()
            {
                if (_marks.Count < 4) return;
                double lo = double.MaxValue, hi = 0;
                foreach (double m in _marks) { lo = Math.Min(lo, m); hi = Math.Max(hi, m); }
                if (hi < 2.0 * lo) return;                      // one cluster only: keep the estimate
                double c1 = lo, c2 = hi;
                for (int it = 0; it < 6; it++)
                {
                    double s1 = 0, s2 = 0; int n1 = 0, n2 = 0;
                    double split = Math.Sqrt(c1 * c2);
                    foreach (double m in _marks)
                        if (m < split) { s1 += m; n1++; } else { s2 += m; n2++; }
                    if (n1 == 0 || n2 == 0) return;
                    c1 = s1 / n1; c2 = s2 / n2;
                }
                double d = (c2 - c1) / 2, b = c1 - d;
                if (d < 20 || d > 250 || c2 / c1 < 1.8) return;
                _dit = d;
                _bias = Math.Max(0, Math.Min(b, 40));
            }

            // 2-means on the recent spaces: element gaps vs letter gaps (word gaps are left out). Keyers
            // and hands space letters very differently from the textbook 3 dits, so this beats the dit.
            private void learnSpace(double t)
            {
                _spaces.Add(t);
                if (_spaces.Count > 32) _spaces.RemoveAt(0);
                if (_spaces.Count < 8) return;
                double lo = double.MaxValue, hi = 0;
                foreach (double m in _spaces) { lo = Math.Min(lo, m); hi = Math.Max(hi, m); }
                double c1 = lo, c2 = hi;
                int n1 = 0, n2 = 0;
                for (int it = 0; it < 6; it++)
                {
                    double s1 = 0, s2 = 0; n1 = 0; n2 = 0;
                    double split = 0.5 * (c1 + c2);
                    foreach (double m in _spaces)
                        if (m < split) { s1 += m; n1++; } else { s2 += m; n2++; }
                    if (n1 == 0 || n2 == 0) return;
                    c1 = s1 / n1; c2 = s2 / n2;
                }
                if (n1 < 3 || n2 < 2 || c2 < c1 + _dit) { _charGap = _wordGap = -1; return; }
                // fast ops squeeze letter gaps to ~2 element gaps; the median keeps those out of the base
                List<double> low = _spaces.FindAll(m => m < 0.5 * (c1 + c2));
                low.Sort();
                _charGap = Math.Min(0.5 * (c1 + c2), 1.8 * low[low.Count / 2]);
                _wordGap = c2 + WordK * (c2 - c1);
            }

            private void endGap(CwSkimmerEngine e, double t)
            {
                double sp = t + _bias;                           // measured spaces are shortened by the smear
                if (!_charOut && _elems.Length > 0 && (_charGap > 0 ? t > _charGap : sp > 2 * _dit)) emitChar(e);
                if (!_wordOut && (_wordGap > 0 ? t > _wordGap : sp > 5 * _dit)) endWord(e);
            }

            private void emitChar(CwSkimmerEngine e)
            {
                char c;
                if (_elems.IndexOf('-') >= 0) _wordHasDah = true;
                if (Morse.TryGetValue(_elems, out c))
                {
                    Text.Append(c);
                    _word.Append(c);
                }
                else
                {
                    Text.Append('*');
                    _word.Append('*');
                }
                if (Text.Length > 120) Text.Remove(0, Text.Length - 120);
                _elems = "";
                _charOut = true;
                _wordOut = false;
            }

            private void endWord(CwSkimmerEngine e)
            {
                _wordOut = true;
                Text.Append(' ');
                string w = _word.ToString();
                bool hasDah = _wordHasDah;
                _word.Clear();
                _wordHasDah = false;
                if (w.Length < 3 || w.Length > 12 || NotCalls.Contains(w) || !CallRx.IsMatch(w)) return;

                long now = _frameNo;
                long window = (long)(90000 / e._hopMs);         // copies must be within 90 s
                KeyValuePair<int, long> seen;
                int count = _calls.TryGetValue(w, out seen) && now - seen.Value < window ? seen.Key + 1 : 1;
                _calls[w] = new KeyValuePair<int, long>(count, now);
                if (count < (hasDah ? 2 : 3) || Wpm < 6 || Wpm > 60) return;  // all-dit words (S55S) are often noise

                long last;
                if (_spotted.TryGetValue(w, out last) && now - last < (long)(30000 / e._hopMs)) return;
                _spotted[w] = now;
                e.SpotFound?.Invoke(new Spot { Call = w, OffsetHz = OffsetHz(e), Wpm = Wpm, SnrDb = (int)Math.Round(Snr) });
            }
        }
    }
    // In-place radix-2 complex FFT; the caller writes its input in BitRev order.
    internal sealed class Fft
    {
        public readonly int N;
        public readonly int[] BitRev;
        private readonly float[] _cos, _sin;

        public Fft(int n)
        {
            N = n;
            int bits = 0; while ((1 << bits) < n) bits++;
            BitRev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                BitRev[i] = r;
            }
            _cos = new float[n / 2]; _sin = new float[n / 2];
            for (int i = 0; i < n / 2; i++)
            {
                _cos[i] = (float)Math.Cos(-2 * Math.PI * i / n);
                _sin[i] = (float)Math.Sin(-2 * Math.PI * i / n);
            }
        }

        public void Transform(float[] re, float[] im)
        {
            for (int size = 2; size <= N; size <<= 1)
            {
                int half = size >> 1, step = N / size;
                for (int start = 0; start < N; start += size)
                {
                    for (int j = 0; j < half; j++)
                    {
                        float wr = _cos[j * step], wi = _sin[j * step];
                        int a = start + j, b = a + half;
                        float tr = re[b] * wr - im[b] * wi;
                        float ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
                }
            }
        }
    }
}
