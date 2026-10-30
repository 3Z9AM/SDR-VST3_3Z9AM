//  CallsignBackground.cs
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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Thetis
{
    // Spectral-area background with the station callsign (Setup > Appearance > General).
    // <AppData>\CallsignBackground is laid out like a skin: Console\picDisplay.png (static, red)
    // and Console\pnlDisplay\0000..0059.png (red = low signal .. green = high). While it exists
    // Skin.cs takes the display background from it instead of the skin. The picture is the
    // 3Z9AM skin fog (text removed, Resources\CallsignFog.png) with the callsign drawn in an
    // Orbitron Black based font (OFL, Resources\CallsignFont.ttf), then hue-shifted per frame.
    internal static class CallsignBackground
    {
        public const int FrameCount = 60;
        private const float RedShiftDeg = -132f;    // green text (hue ~136) -> red (~4), through yellow

        public static string AppDataPath { get; set; } = "";
        public static string Root => Path.Combine(AppDataPath, "CallsignBackground");
        public static bool Active => File.Exists(Path.Combine(Root, "Console", "picDisplay.png"));

        public static string SavedCallsign()
        {
            try
            {
                string p = Path.Combine(Root, "callsign.txt");
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch { return null; }
        }

        // worker thread: builds the pictures in Root.new; Install() swaps them in
        public static void Generate(string call)
        {
            string tmp = Root + ".new";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            string frames = Path.Combine(tmp, "Console", "pnlDisplay");
            Directory.CreateDirectory(frames);

            float[] hsv = toHsv(render(call, 1280, 800));
            for (int k = 0; k < FrameCount; k++)
                savePng(fromHsv(hsv, 1280, 800, RedShiftDeg * (1f - k / (float)(FrameCount - 1))),
                        Path.Combine(frames, k.ToString("0000") + ".png"));
            savePng(fromHsv(toHsv(render(call, 2560, 1600)), 2560, 1600, RedShiftDeg),
                    Path.Combine(tmp, "Console", "picDisplay.png"));
            File.WriteAllText(Path.Combine(tmp, "callsign.txt"), call);
        }

        // call after the display stopped using the old frames
        public static void Install()
        {
            Remove();
            Directory.Move(Root + ".new", Root);
        }

        public static void Remove()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        // ---- picture --------------------------------------------------------------------

        private static readonly float[] BodyColor = { 25 / 255f, 178 / 255f, 66 / 255f };
        private static readonly float[] HighColor = { 90 / 255f, 255 / 255f, 140 / 255f };

        // green base picture, RGB 0..1 interleaved
        private static float[] render(string call, int W, int H)
        {
            int n = W * H;
            int[] fogPx;
            using (Stream s = typeof(CallsignBackground).Assembly.GetManifestResourceStream("Thetis.Resources.CallsignFog.png"))
            using (Bitmap src = new Bitmap(s))
            using (Bitmap fog = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(fog))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, 0, 0, W, H);
                }
                fogPx = readPixels(fog);
            }

            float[] mask = textMask(call, W, H);
            float[] edge = blur(mask, W, H, H * 0.005f);    // soft letter edge
            float[] g1 = blur(mask, W, H, H * 0.015f);      // tight glow
            float[] g2 = blur(mask, W, H, H * 0.06f);       // wide glow
            float[] cloud = clouds(W, H);

            float[] rgb = new float[n * 3];
            Parallel.For(0, H, y =>
            {
                for (int i = y * W; i < (y + 1) * W; i++)
                {
                    int p = fogPx[i];
                    float fr = ((p >> 16) & 255) / 255f, fg = ((p >> 8) & 255) / 255f, fb = (p & 255) / 255f;
                    float lit = (0.9f * g1[i] + g2[i]) * (0.5f + 4f * (fr + fg + fb) / 3f);   // glow lights the fog
                    float e = edge[i], c = cloud[i];
                    float[] fogc = { fr, fg, fb };
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float body = BodyColor[ch] + c * (HighColor[ch] - BodyColor[ch]) * 0.8f;
                        float v = fogc[ch] + lit * BodyColor[ch];
                        v = v * (1f - e) + body * e;
                        rgb[i * 3 + ch] = v + g2[i] * c * 0.5f * HighColor[ch];       // wisps near the text
                    }
                }
            });
            return rgb;
        }

        // callsign as 0..1 coverage: cap height 18% of H at the 3Z9AM place, at most 53% of W wide
        // (squeezed up to 18% first, then made smaller)
        private static float[] textMask(string call, int W, int H)
        {
            using (Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddString(call, fontFamily(), (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();
                float cap = 0.18f * H, maxW = 0.53f * W;
                float s = cap / Math.Max(1f, b.Height), sx = 1f;
                if (b.Width * s > maxW)
                {
                    sx = Math.Max(0.82f, maxW / (b.Width * s));
                    if (b.Width * s * sx > maxW) s = maxW / (b.Width * sx);
                }
                float w = b.Width * s * sx, h = b.Height * s;
                using (Matrix m = new Matrix())
                {
                    m.Translate(0.505f * W - w / 2f, 0.11f * H + (cap - h) / 2f);
                    m.Scale(s * sx, s);
                    m.Translate(-b.X, -b.Y);
                    path.Transform(m);
                }
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(Brushes.White, path);

                int[] px = readPixels(bmp);
                float[] mask = new float[px.Length];
                for (int i = 0; i < px.Length; i++) mask[i] = ((px[i] >> 24) & 255) / 255f;
                return mask;
            }
        }

        private static PrivateFontCollection _fonts;
        private static FontFamily fontFamily()
        {
            if (_fonts == null)
            {
                byte[] data;
                using (Stream s = typeof(CallsignBackground).Assembly.GetManifestResourceStream("Thetis.Resources.CallsignFont.ttf"))
                {
                    data = new byte[s.Length];
                    s.ReadExactly(data);
                }
                IntPtr mem = Marshal.AllocCoTaskMem(data.Length);   // must stay alive while the font is used: never freed
                Marshal.Copy(data, 0, mem, data.Length);
                PrivateFontCollection pfc = new PrivateFontCollection();
                pfc.AddMemoryFont(mem, data.Length);
                _fonts = pfc;
            }
            return _fonts.Families[0];
        }

        // soft value-noise "clouds" inside the letters, 0..1; cells scale with H so every size matches
        private static float[] clouds(int W, int H)
        {
            float[] c = new float[W * H];
            addNoise(c, W, H, Math.Max(4, H / 8), 0.7f, 1);
            addNoise(c, W, H, Math.Max(3, H / 20), 0.3f, 2);
            for (int i = 0; i < c.Length; i++) c[i] = Math.Clamp((c[i] - 0.4f) * 1.8f, 0f, 1f);
            return c;
        }

        private static void addNoise(float[] c, int W, int H, int cell, float amp, int seed)
        {
            Random rnd = new Random(seed);
            int gw = W / cell + 2, gh = H / cell + 2;
            float[] grid = new float[gw * gh];
            for (int i = 0; i < grid.Length; i++) grid[i] = (float)rnd.NextDouble();
            Parallel.For(0, H, y =>
            {
                float fy = y / (float)cell; int iy = (int)fy; float ty = smooth(fy - iy);
                for (int x = 0; x < W; x++)
                {
                    float fx = x / (float)cell; int ix = (int)fx; float tx = smooth(fx - ix);
                    int o = iy * gw + ix;
                    float top = grid[o] + (grid[o + 1] - grid[o]) * tx;
                    float bot = grid[o + gw] + (grid[o + gw + 1] - grid[o + gw]) * tx;
                    c[y * W + x] += amp * (top + (bot - top) * ty);
                }
            });
        }

        private static float smooth(float t) => t * t * (3f - 2f * t);

        // gaussian approximation: three box blurs of radius ~sigma
        private static float[] blur(float[] a, int W, int H, float sigma)
        {
            int r = Math.Max(1, (int)Math.Round(sigma));
            float[] x = (float[])a.Clone(), t = new float[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                boxPass(x, t, H, W, r, 1);      // rows
                boxPass(t, x, W, H, r, W);      // columns
            }
            return x;
        }

        // running-sum box filter: count lines of len samples, step 1 = rows, step W = columns
        private static void boxPass(float[] src, float[] dst, int count, int len, int r, int step)
        {
            float inv = 1f / (2 * r + 1);
            Parallel.For(0, count, line =>
            {
                int o = step == 1 ? line * len : line;
                float sum = 0;
                for (int i = -r; i <= r; i++) sum += src[o + Math.Clamp(i, 0, len - 1) * step];
                for (int i = 0; i < len; i++)
                {
                    dst[o + i * step] = sum * inv;
                    sum += src[o + Math.Min(i + r + 1, len - 1) * step] - src[o + Math.Max(i - r, 0) * step];
                }
            });
        }

        // ---- colour -----------------------------------------------------------------------

        private static float[] toHsv(float[] rgb)
        {
            int n = rgb.Length / 3;
            float[] hsv = new float[rgb.Length];
            Parallel.For(0, n, i =>
            {
                float r = Math.Clamp(rgb[i * 3], 0f, 1f), g = Math.Clamp(rgb[i * 3 + 1], 0f, 1f), b = Math.Clamp(rgb[i * 3 + 2], 0f, 1f);
                float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                float h = 0;
                if (d > 0)
                {
                    if (max == r) h = 60f * (((g - b) / d) % 6f);
                    else if (max == g) h = 60f * ((b - r) / d + 2f);
                    else h = 60f * ((r - g) / d + 4f);
                }
                hsv[i * 3] = h;
                hsv[i * 3 + 1] = max > 0 ? d / max : 0;
                hsv[i * 3 + 2] = max;
            });
            return hsv;
        }

        private static Bitmap fromHsv(float[] hsv, int W, int H, float shiftDeg)
        {
            int[] px = new int[W * H];
            Parallel.For(0, px.Length, i =>
            {
                float h = (hsv[i * 3] + shiftDeg) % 360f;
                if (h < 0) h += 360f;
                float s = hsv[i * 3 + 1], v = hsv[i * 3 + 2];
                float c = v * s, x = c * (1 - Math.Abs(h / 60f % 2f - 1)), m = v - c;
                float r, g, b;
                if (h < 60) { r = c; g = x; b = 0; }
                else if (h < 120) { r = x; g = c; b = 0; }
                else if (h < 180) { r = 0; g = c; b = x; }
                else if (h < 240) { r = 0; g = x; b = c; }
                else if (h < 300) { r = x; g = 0; b = c; }
                else { r = c; g = 0; b = x; }
                px[i] = unchecked((int)0xFF000000) | (to8(r + m) << 16) | (to8(g + m) << 8) | to8(b + m);
            });
            Bitmap bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
            return bmp;
        }

        private static int to8(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);

        private static int[] readPixels(Bitmap bmp)
        {
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] px = new int[bmp.Width * bmp.Height];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            bmp.UnlockBits(bd);
            return px;
        }

        private static void savePng(Bitmap bmp, string file)
        {
            using (bmp) bmp.Save(file, ImageFormat.Png);
        }
    }
}
