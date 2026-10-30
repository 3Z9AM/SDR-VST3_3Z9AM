using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.DXGI;
using SDXPixelFormat = Vortice.DCommon.PixelFormat;

namespace Thetis
{
    // Animated spectral-area background: a PNG series in the skin folder
    // (Console\pnlDisplay\*.png or Console\picDisplay\*.png, played in name order).
    // GPU cost is one background bitmap whose pixels are replaced when the next frame
    // is due - a single W*H*4 upload per animation frame. Frames are decoded and scaled
    // (to cover the display, never above the source size) on a worker thread and kept
    // in RAM when the whole series fits the RAM cache limit, otherwise streamed from disk.
    // Meter mode: instead of looping, the frame follows an averaged level (RX1 S-meter /
    // TX forward power, BackgroundAnimationLevelSource), one frame per tick toward it.
    partial class Display
    {

        private static readonly object _bgAnimLock = new object();
        private static readonly AutoResetEvent _bgAnimWake = new AutoResetEvent(false);
        private static Thread _bgAnimThread;

        private static string[] _bgAnimFiles;      // null = no animation
        private static int _bgAnimGen;             // bumped on every new series
        private static int _bgAnimW, _bgAnimH;     // decoded frame size (worker)
        private static int _bgAnimForW, _bgAnimForH; // display size that frame size was chosen for
        private static bool _bgAnimAtSource;       // frames already at full source resolution
        private static byte[][] _bgAnimCache;      // decoded frames when within budget
        private static byte[] _bgAnimReady;        // next frame, waiting for upload
        private static int _bgAnimReadyIdx = -1;
        private static int _bgAnimReadyW, _bgAnimReadyH;
        private static int _bgAnimShownIdx = -1;
        private static double _bgAnimNextDueMs;

        private static volatile bool _bgAnimEnabled = true;
        public static bool BackgroundAnimationEnabled
        {
            get { return _bgAnimEnabled; }
            set { _bgAnimEnabled = value; _bgAnimWake.Set(); }
        }

        private static volatile int _bgAnimFps = 25;
        public static int BackgroundAnimationFps
        {
            get { return _bgAnimFps; }
            set { _bgAnimFps = Math.Max(1, Math.Min(60, value)); }
        }

        // RAM cache limit: a series that fits is decoded once, then only uploaded
        private static volatile int _bgAnimCacheMB = 192;
        public static int BackgroundAnimationCacheMB
        {
            get { return _bgAnimCacheMB; }
            set
            {
                int v = Math.Max(32, Math.Min(2048, value));
                if (v == _bgAnimCacheMB) return;
                _bgAnimCacheMB = v;
                bgAnimRedecide();   // re-decide caching for the current series
            }
        }

        // drop decoded frames so the worker picks frame size and caching again
        private static void bgAnimRedecide()
        {
            lock (_bgAnimLock)
            {
                _bgAnimGen++;
                _bgAnimCache = null;
                _bgAnimReady = null;
                _bgAnimReadyIdx = -1;
                _bgAnimW = _bgAnimH = 0;
            }
            _bgAnimWake.Set();
        }

        // ---- meter mode: frame follows the RX1 S-meter (RX) or forward power (TX) ----
        // Level source set by the Console: 0..1 (first..last frame), NaN = no reading.
        public static Func<double> BackgroundAnimationLevelSource;

        private static volatile bool _bgAnimMeterMode;
        public static bool BackgroundAnimationMeterMode
        {
            get { return _bgAnimMeterMode; }
            set
            {
                if (value == _bgAnimMeterMode) return;
                _bgAnimMeterMode = value;
                lock (_bgAnimLock)
                {
                    _bgAnimReady = null;
                    _bgAnimReadyIdx = -1;
                }
                _bgAnimWake.Set();
            }
        }

        // averaging time constant of the level, seconds
        private static double _bgAnimSmoothSec = 1.5;
        public static double BackgroundAnimationSmoothing
        {
            get { return _bgAnimSmoothSec; }
            set { _bgAnimSmoothSec = Math.Max(0.1, Math.Min(10.0, value)); }
        }

        private static double _bgAnimLevel = double.NaN;    // averaged level 0..1
        private static double _bgAnimLevelLastMs;
        private static volatile int _bgAnimTargetIdx = -1;
        private static int _bgAnimPhase = -1;   // loop mode: step in the forth-and-back sequence

        // next frame to show: loop = forth and back (a red -> green series has no jump), meter =
        // one step toward the target
        private static int bgAnimNextWanted(int shown, int count)
        {
            if (!_bgAnimMeterMode)
            {
                int p = (_bgAnimPhase + 1) % (2 * (count - 1));
                return p < count ? p : 2 * (count - 1) - p;
            }
            int target = _bgAnimTargetIdx;
            if (target < 0) return -1;
            target = Math.Min(target, count - 1);
            if (shown < 0) return target;
            if (shown == target) return -1;
            return shown + Math.Sign(target - shown);
        }

        private static void bgAnimUpdateLevel(double now, int count)
        {
            Func<double> src = BackgroundAnimationLevelSource;
            double v = double.NaN;
            try { if (src != null) v = src(); } catch { }
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            v = Math.Max(0.0, Math.Min(1.0, v));

            double dt = (now - _bgAnimLevelLastMs) / 1000.0;
            _bgAnimLevelLastMs = now;
            if (double.IsNaN(_bgAnimLevel) || dt <= 0 || dt > 5)
                _bgAnimLevel = v;
            else
                _bgAnimLevel += (v - _bgAnimLevel) * (1.0 - Math.Exp(-dt / _bgAnimSmoothSec));

            int target = (int)Math.Round(_bgAnimLevel * (count - 1));
            if (target != _bgAnimTargetIdx)
            {
                _bgAnimTargetIdx = target;
                _bgAnimWake.Set();
            }
        }

        /// <summary>New PNG series (null or fewer than 2 files = static background).</summary>
        public static void SetBackgroundAnimationFrames(string[] files)
        {
            lock (_bgAnimLock)
            {
                _bgAnimFiles = files != null && files.Length >= 2 ? files : null;
                _bgAnimGen++;
                _bgAnimCache = null;
                _bgAnimReady = null;
                _bgAnimReadyIdx = -1;
                _bgAnimShownIdx = -1;
                _bgAnimPhase = -1;
                _bgAnimW = _bgAnimH = 0;

                if (_bgAnimFiles != null && _bgAnimThread == null)
                {
                    _bgAnimThread = new Thread(bgAnimWorker)
                    {
                        Name = "Background animation decoder",
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    _bgAnimThread.Start();
                }
            }
            _bgAnimWake.Set();
        }

        private static void bgAnimWorker()
        {
            while (true)
            {
                _bgAnimWake.WaitOne(250);
                try
                {
                    string[] files;
                    int gen, want;
                    lock (_bgAnimLock)
                    {
                        files = _bgAnimFiles;
                        gen = _bgAnimGen;
                        if (files == null || !_bgAnimEnabled) continue;
                        if (_bgAnimReady != null) continue; // previous frame not uploaded yet
                        want = bgAnimNextWanted(_bgAnimShownIdx, files.Length);
                        if (want < 0) continue;             // meter mode: already at the target
                    }

                    if (_bgAnimW == 0) bgAnimChooseSize(files, gen);
                    int w = _bgAnimW, h = _bgAnimH;
                    if (w <= 0 || h <= 0) continue;

                    byte[][] cache = _bgAnimCache;
                    byte[] data = cache != null ? cache[want] : null;
                    if (data == null)
                    {
                        data = bgAnimDecode(files[want], w, h);
                        if (data == null) continue;
                        if (cache != null) cache[want] = data;
                    }

                    lock (_bgAnimLock)
                    {
                        if (gen != _bgAnimGen) continue; // series changed meanwhile
                        _bgAnimReady = data;
                        _bgAnimReadyIdx = want;
                        _bgAnimReadyW = w;
                        _bgAnimReadyH = h;
                    }
                }
                catch (Exception ex)
                {
                    Common.LogString("Background animation: " + ex.Message);
                    Thread.Sleep(1000);
                }
            }
        }

        private static void bgAnimChooseSize(string[] files, int gen)
        {
            int srcW, srcH;
            using (FileStream fs = File.OpenRead(files[0]))
            using (Image img = Image.FromStream(fs, false, false))
            {
                srcW = img.Width;
                srcH = img.Height;
            }

            // cover the display (it is drawn stretched or aspect-fitted to it), but
            // never upscale - keeps both the upload and the memory as small as possible
            double dw = Math.Max(1.0, displayTargetWidth);
            double dh = Math.Max(1.0, displayTargetHeight);
            double s = Math.Min(1.0, Math.Max(dw / srcW, dh / srcH));
            int w = Math.Max(1, (int)Math.Round(srcW * s));
            int h = Math.Max(1, (int)Math.Round(srcH * s));

            long total = (long)w * h * 4 * files.Length;
            lock (_bgAnimLock)
            {
                if (gen != _bgAnimGen) return;
                _bgAnimW = w;
                _bgAnimH = h;
                _bgAnimForW = (int)dw;
                _bgAnimForH = (int)dh;
                _bgAnimAtSource = s >= 1.0;
                _bgAnimCache = total <= (long)_bgAnimCacheMB * 1024 * 1024 ? new byte[files.Length][] : null;
            }
        }

        private static byte[] bgAnimDecode(string file, int w, int h)
        {
            using (FileStream fs = File.OpenRead(file))
            using (Image img = Image.FromStream(fs, false, false))
            using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, 0, 0, w, h);
                }

                byte[] data = new byte[w * h * 4];
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(bd.Scan0 + y * bd.Stride, data, y * w * 4, w * 4);
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
                return data;
            }
        }

        // Render thread, inside _objDX2Lock, before anything draws the background.
        private static void TickBackgroundAnimation()
        {
            if (!_bgAnimEnabled || _bitmapBackground == null || _d2dRenderTarget == null) return;

            byte[] data;
            int w, h;
            double now = m_dElapsedFrameStart;
            // display grew well past the size the frames were scaled for: scale them again
            if (_bgAnimW > 0 && !_bgAnimAtSource &&
                (displayTargetWidth > _bgAnimForW * 1.2 || displayTargetHeight > _bgAnimForH * 1.2))
                bgAnimRedecide();

            if (_bgAnimMeterMode)
            {
                string[] f = _bgAnimFiles;
                if (f != null) bgAnimUpdateLevel(now, f.Length);
            }

            lock (_bgAnimLock)
            {
                if (_bgAnimFiles == null || _bgAnimReady == null) return;
                if (_bgAnimShownIdx >= 0 && now < _bgAnimNextDueMs) return;
                if (_bgAnimMeterMode && _bgAnimReadyIdx != bgAnimNextWanted(_bgAnimShownIdx, _bgAnimFiles.Length))
                {
                    // target moved the other way (or reached) since this frame was prepared
                    _bgAnimReady = null;
                    _bgAnimReadyIdx = -1;
                    _bgAnimWake.Set();
                    return;
                }

                data = _bgAnimReady;
                w = _bgAnimReadyW;
                h = _bgAnimReadyH;
                _bgAnimShownIdx = _bgAnimReadyIdx;
                if (!_bgAnimMeterMode) _bgAnimPhase = (_bgAnimPhase + 1) % (2 * (_bgAnimFiles.Length - 1));
                _bgAnimReady = null;
                _bgAnimReadyIdx = -1;
            }

            double period = 1000.0 / _bgAnimFps;
            _bgAnimNextDueMs = now - _bgAnimNextDueMs > period ? now + period : _bgAnimNextDueMs + period;

            try
            {
                if (_bitmapBackground.PixelSize.Width != w || _bitmapBackground.PixelSize.Height != h)
                {
                    // first frame (or after the static skin image was set again): own bitmap at frame size
                    GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        ID2D1Bitmap bmp = _d2dRenderTarget.CreateBitmap(new Vortice.Mathematics.SizeI(w, h),
                            pin.AddrOfPinnedObject(), (uint)(w * 4),
                            new BitmapProperties(new SDXPixelFormat(Format.B8G8R8A8_UNorm, ALPHA_MODE)));
                        _bitmapBackground.Dispose();
                        _bitmapBackground = bmp;
                    }
                    finally
                    {
                        pin.Free();
                    }
                }
                else
                {
                    _bitmapBackground.CopyFromMemory(new Rectangle(0, 0, w, h), data, (uint)(w * 4));
                }
            }
            catch (Exception ex)
            {
                Common.LogString("Background animation upload: " + ex.Message);
            }

            _bgAnimWake.Set();
        }
    }
}
