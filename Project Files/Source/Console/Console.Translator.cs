//  Console.Translator.cs
//
//  Live subtitles of the RX1 audio: speech cut at pauses, recognised by Whisper (whisper.cpp through
//  Whisper.net, Vulkan GPU or CPU) and shown on a HUD plate over the panadapter, as heard or translated to English.
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
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Thetis
{
    public partial class Console
    {
        // "Translator" tile in the main menu: Off / Transcribe / Translate to English (session only)
        private void initTranslator()
        {
            RxTranslator.ModelDir = Path.Combine(AppDataPath, "whisper");
            ToolStripMenuItem tile = new ToolStripMenuItem("Translator") { ForeColor = SystemColors.ControlLightLight };
            string[] names = { "Off", "Transcribe", "Translate to English" };
            List<ToolStripMenuItem> modes = new List<ToolStripMenuItem>();
            for (int m = 0; m < names.Length; m++)
            {
                int mode = m;
                ToolStripMenuItem it = new ToolStripMenuItem(names[m]) { Checked = m == 0 };
                it.Click += delegate
                {
                    RxTranslator.Mode = mode;
                    foreach (ToolStripMenuItem o in modes) o.Checked = o == it;
                    setTileColour(tile, mode != 0 ? Color.LimeGreen : SystemColors.ControlLightLight);
                };
                modes.Add(it);
                tile.DropDownItems.Add(it);
            }
            int at = _magnifierItem != null ? menuStrip1.Items.IndexOf(_magnifierItem) + 1 : menuStrip1.Items.IndexOf(vstToolStripMenuItem) + 1;
            menuStrip1.Items.Insert(at, tile);
        }
    }

    internal static class RxTranslator
    {
        // large-v3: turbo transcribes as well but cannot translate (tested: it makes up "subtitles by ..." lines)
        private const string ModelFile = "ggml-large-v3-q5_0.bin";
        private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-q5_0.bin";
        private const int Rate = 16000, Frame = 320;          // Whisper input, 20 ms analysis frames
        private const int RingLen = 1 << 20;                  // ~65 s at 16 kHz
        private const int MaxSeg = 8 * Rate;                  // a long over is cut every 8 s
        private const int Hang = 35, MinSpeech = 25, PreRoll = 15;   // frames: 0.7 s pause ends, 0.5 s speech min, 0.3 s lead-in

        public static string ModelDir = "";
        private static volatile int _mode;
        public static int Mode
        {
            get { return _mode; }
            set { _mode = value; if (value != 0) start(); }
        }

        // the plate: status while loading, otherwise the last lines (newest last)
        public static volatile string Status;
        private static readonly List<string> _lines = new List<string>();
        private static long _linesTicks;
        public static string[] Lines(out long ticks)
        {
            lock (_lines) { ticks = _linesTicks; return _lines.ToArray(); }
        }

        private static readonly float[] _ring = new float[RingLen];
        private static long _written;
        private static double _phase, _acc;
        private static int _accN;

        // RX mixer thread, interleaved stereo after the RX VST chain
        public static unsafe void Feed(double* buf, int frames, int rate)
        {
            if (_mode == 0 || Audio.MOX || rate < Rate) return;
            // shortcut: box average as the anti-alias filter (fine for 48 kHz SSB voice), add a FIR if other rates sound bad
            double step = (double)Rate / rate;
            long w = _written;
            for (int i = 0; i < frames; i++)
            {
                _acc += 0.5 * (buf[2 * i] + buf[2 * i + 1]);
                _accN++;
                _phase += step;
                if (_phase >= 1)
                {
                    _phase -= 1;
                    _ring[w++ & (RingLen - 1)] = (float)(_acc / _accN);
                    _acc = 0;
                    _accN = 0;
                }
            }
            Volatile.Write(ref _written, w);
        }

        private static Thread _thread;
        private static void start()
        {
            if (_thread != null) return;
            _thread = new Thread(work) { IsBackground = true, Name = "Translator", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private static void work()
        {
            WhisperFactory factory = null;
            WhisperProcessor[] proc = new WhisperProcessor[3];
            List<float> seg = new List<float>();
            float[] frame = new float[Frame];
            long read = Volatile.Read(ref _written);
            double floor = 0;
            int speech = 0, quiet = 0;
            while (true)
            {
                try
                {
                    int mode = _mode;
                    if (mode == 0)
                    {
                        // off: give the GPU memory back
                        for (int i = 0; i < proc.Length; i++) { proc[i]?.Dispose(); proc[i] = null; }
                        factory?.Dispose();
                        factory = null;
                        seg.Clear();
                        Thread.Sleep(200);
                        read = Volatile.Read(ref _written);
                        continue;
                    }
                    if (factory == null)
                    {
                        factory = load();
                        read = Volatile.Read(ref _written);
                        Status = null;
                    }
                    if (proc[mode] == null)
                    {
                        WhisperProcessorBuilder b = factory.CreateBuilder().WithLanguage("auto").WithNoContext()
                            .WithNoSpeechThreshold(0.6f);
                        if (mode == 2) b = b.WithTranslate();
                        proc[mode] = b.Build();
                        // the first run compiles the Vulkan shaders (12 s offline, then ~2 s): do it on hiss, not on the first over
                        Status = "Translator: warming up...";
                        float[] hiss = new float[Rate];
                        Random rnd = new Random(1);
                        for (int i = 0; i < hiss.Length; i++) hiss[i] = (float)(rnd.NextDouble() - 0.5) * 1e-3f;
                        recognise(proc[mode], hiss, false);
                        Status = null;
                        read = Volatile.Read(ref _written);
                    }

                    long w = Volatile.Read(ref _written);
                    if (w - read > RingLen / 2) read = w - Rate;      // fell behind (recognition slower than speech)
                    if (w - read < Frame) { Thread.Sleep(40); continue; }
                    double e = 0;
                    for (int i = 0; i < Frame; i++) { frame[i] = _ring[(read + i) & (RingLen - 1)]; e += frame[i] * frame[i]; }
                    read += Frame;
                    e = e / Frame + 1e-12;

                    // noise floor: follows down at once, up 0.5 dB/s; speech = 6 dB over it
                    floor = floor <= 0 || e < floor ? e : floor * 1.0023;
                    bool voice = e > 4 * floor;
                    if (seg.Count == 0 && !voice) continue;
                    if (seg.Count == 0)
                    {
                        long from = Math.Max(0, read - (PreRoll + 1) * Frame);
                        for (long k = from; k < read - Frame; k++) seg.Add(_ring[k & (RingLen - 1)]);
                        speech = 0;
                    }
                    seg.AddRange(frame);
                    if (voice) { speech++; quiet = 0; } else quiet++;
                    if (quiet < Hang && seg.Count < MaxSeg) continue;

                    if (speech >= MinSpeech) recognise(proc[mode], seg.ToArray());
                    seg.Clear();
                    quiet = 0;
                }
                catch (Exception ex)
                {
                    Status = "Translator: " + ex.Message;
                    System.Diagnostics.Debug.WriteLine("Translator: " + ex);
                    for (int i = 0; i < proc.Length; i++) { try { proc[i]?.Dispose(); } catch { } proc[i] = null; }
                    try { factory?.Dispose(); } catch { }
                    factory = null;
                    seg.Clear();
                    Thread.Sleep(5000);
                }
            }
        }

        private static WhisperFactory load()
        {
            string path = Path.Combine(ModelDir, ModelFile);
            if (!File.Exists(path)) download(path).GetAwaiter().GetResult();
            Status = "Translator: loading model...";
            RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu };
            return WhisperFactory.FromPath(path);
        }

        private static async Task download(string path)
        {
            Directory.CreateDirectory(ModelDir);
            string part = path + ".part";
            using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            using (HttpResponseMessage r = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                r.EnsureSuccessStatusCode();
                long total = r.Content.Headers.ContentLength ?? 0, got = 0;
                using (Stream src = await r.Content.ReadAsStreamAsync())
                using (FileStream dst = File.Create(part))
                {
                    byte[] buf = new byte[1 << 20];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        got += n;
                        Status = "Translator: downloading model (1.1 GB) " + (total > 0 ? (100 * got / total) + "%" : (got >> 20) + " MB");
                    }
                }
                if (total > 0 && got != total) throw new IOException("model download incomplete");
            }
            File.Move(part, path, true);
        }

        private static void recognise(WhisperProcessor p, float[] audio, bool show = true)
        {
            List<string> found = new List<string>();
            Task.Run(async () =>
            {
                await foreach (SegmentData s in p.ProcessAsync(audio))
                {
                    string t = s.Text?.Trim();
                    if (string.IsNullOrEmpty(t) || s.NoSpeechProbability > 0.6f || hallucination(t)) continue;
                    found.Add((s.Language ?? "").ToUpperInvariant() + " › " + t);
                }
            }).GetAwaiter().GetResult();
            if (found.Count == 0 || !show) return;
            lock (_lines)
            {
                _lines.AddRange(found);
                while (_lines.Count > 2) _lines.RemoveAt(0);
                _linesTicks = DateTime.UtcNow.Ticks;
            }
        }

        // Whisper's stock lines on noise (from film subtitles in its training data)
        private static readonly string[] _junk = { "subtit", "субтит", "amara.org", "thanks for watching", "thank you for watching", "продолжение следует" };
        private static bool hallucination(string t)
        {
            string l = t.ToLowerInvariant();
            foreach (string j in _junk) if (l.Contains(j)) return true;
            return false;
        }
    }
}
