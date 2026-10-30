//  Display.ModernUI.cs
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

namespace Thetis
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using Vortice.Direct2D1;
    using Vortice.DirectWrite;
    using Color = System.Drawing.Color;
    using Rectangle = System.Drawing.Rectangle;
    using RectangleF = Vortice.Mathematics.Rect;

    // Modern UI on the panadapter (ModernTheme.Enabled): scale labels on a dark rounded backing,
    // a fainter grid, the RX/TX filter as a top-down gradient with crisp edges, spot tags as
    // rounded pills that fade with age, and a short fade when the view jumps (band change,
    // zoom). Everything here is drawing only - positions and mouse mapping are unchanged.
    partial class Display
    {
        private static readonly Dictionary<int, ID2D1LinearGradientBrush> _modernGradientBrushes = new Dictionary<int, ID2D1LinearGradientBrush>();

        private const float ModernGridOpacity = 0.55f;
        private static Color ModernLabelBacking => ModernTheme.Tint(Color.FromArgb(170, 3, 14, 22));

        // scale label: rounded dark backing first, so labels stay readable over the trace
        private static void drawScaleLabelDX2D(string s, IDWriteTextFormat tf, ID2D1Brush b, float x, float y)
        {
            if (ModernTheme.Enabled && !string.IsNullOrEmpty(s))
            {
                System.Drawing.SizeF sz = measureStringDX2D(s, tf);
                RoundedRectangle rr = new RoundedRectangle
                {
                    Rect = new RectangleF(x - 3f, y, sz.Width + 6f, sz.Height),
                    RadiusX = 3f,
                    RadiusY = 3f,
                };
                _d2dRenderTarget.FillRoundedRectangle(rr, getDXBrushForColour(ModernLabelBacking));
            }
            drawStringDX2D(s, tf, b, x, y);
        }

        // grid pens are user colours; the modern look only lowers their opacity while drawing
        private static void setModernGridOpacity(params ID2D1Brush[] pens)
        {
            float op = ModernTheme.Enabled ? ModernGridOpacity : 1f;
            foreach (ID2D1Brush p in pens)
                if (p != null && p.Opacity != op) p.Opacity = op;
        }

        // filter area: the user's filter colour at full strength at the top fading to a third at
        // the bottom, with 1 px edges in the stronger colour
        private static bool fillModernFilterDX2D(ID2D1Brush brush, RectangleF rect)
        {
            if (!ModernTheme.Enabled || !(brush is ID2D1SolidColorBrush solid)) return false;

            Vortice.Mathematics.Color4 c = solid.Color;
            float a = c.A * solid.Opacity;
            int key = ((int)(a * 255f) << 24) | ((int)(c.R * 255f) << 16) | ((int)(c.G * 255f) << 8) | (int)(c.B * 255f);
            if (!_modernGradientBrushes.TryGetValue(key, out ID2D1LinearGradientBrush grad))
            {
                GradientStop[] stops =
                {
                    new GradientStop { Color = new Vortice.Mathematics.Color4(c.R, c.G, c.B, Math.Min(1f, a * 1.25f)), Position = 0f },
                    new GradientStop { Color = new Vortice.Mathematics.Color4(c.R, c.G, c.B, a * 0.35f), Position = 1f },
                };
                using (ID2D1GradientStopCollection coll = _d2dRenderTarget.CreateGradientStopCollection(stops))
                    grad = _d2dRenderTarget.CreateLinearGradientBrush(new LinearGradientBrushProperties(), coll);
                _modernGradientBrushes[key] = grad;
            }
            grad.StartPoint = new Vector2(0, rect.Top);
            grad.EndPoint = new Vector2(0, rect.Bottom);
            _d2dRenderTarget.FillRectangle(rect, grad);

            ID2D1Brush edge = getDXBrushForColour(Color.FromArgb((int)Math.Min(255f, a * 255f * 2.2f),
                (int)(c.R * 255f), (int)(c.G * 255f), (int)(c.B * 255f)));
            _d2dRenderTarget.DrawLine(new Vector2(rect.Left + 0.5f, rect.Top), new Vector2(rect.Left + 0.5f, rect.Bottom), edge, 1f);
            _d2dRenderTarget.DrawLine(new Vector2(rect.Right - 0.5f, rect.Top), new Vector2(rect.Right - 0.5f, rect.Bottom), edge, 1f);
            return true;
        }

        // ---- spot tags ----

        // alpha multiplier: full for 5 minutes, then down to 45% at 30 minutes
        private static float modernSpotAgeFade(SpotManager2.smSpot spot)
        {
            if (!ModernTheme.Enabled) return 1f;
            double min = (DateTime.UtcNow - spot.utc_spot_time).TotalMinutes;
            if (min <= 5) return 1f;
            if (min >= 30) return 0.45f;
            return (float)(1.0 - 0.55 * (min - 5) / 25.0);
        }

        private static void fillSpotTagDX2D(Color colour, int alpha, Rectangle r, float fade)
        {
            ID2D1Brush b = getDXBrushForColour(colour, (int)(alpha * fade));
            if (!ModernTheme.Enabled)
            {
                drawFillRectangleDX2D(b, r);
                return;
            }
            float rad = Math.Min(r.Height / 2f, 7f);
            _d2dRenderTarget.FillRoundedRectangle(new RoundedRectangle { Rect = new RectangleF(r.X, r.Y, r.Width, r.Height), RadiusX = rad, RadiusY = rad }, b);
        }

        private static void drawSpotTagBorderDX2D(ID2D1Brush b, Rectangle r, float width)
        {
            if (!ModernTheme.Enabled)
            {
                drawRectangleDX2D(b, r, width);
                return;
            }
            float rad = Math.Min(r.Height / 2f, 8f);
            _d2dRenderTarget.DrawRoundedRectangle(new RoundedRectangle { Rect = new RectangleF(r.X, r.Y, r.Width, r.Height), RadiusX = rad, RadiusY = rad }, b, width);
        }

        // ---- view transition: a short fade-in after the displayed span jumps ----

        private static readonly long[] _modernLastCentre = new long[3];
        private static readonly int[] _modernLastSpan = new int[3];
        private static readonly double[] _modernFadeStart = new double[3] { -1e9, -1e9, -1e9 };
        private const double ModernFadeMs = 160;

        // called once per panadapter frame with what is on screen; a jump of more than half the
        // span (band change, memory recall) or a zoom step restarts the fade
        private static void modernViewTransitionDX2D(int rx, long centreHz, int spanHz, int W, int H, int nVerticalShift)
        {
            if (rx < 1 || rx > 2) return;
            double now = m_dElapsedFrameStart;
            long lastCentre = _modernLastCentre[rx];
            int lastSpan = _modernLastSpan[rx];
            _modernLastCentre[rx] = centreHz;
            _modernLastSpan[rx] = spanHz;
            if (!ModernTheme.Enabled || lastSpan == 0) return;

            bool jump = Math.Abs(centreHz - lastCentre) > Math.Max(1, lastSpan) / 2 ||
                        (spanHz != lastSpan && Math.Abs(spanHz - lastSpan) > lastSpan / 20);
            if (jump) _modernFadeStart[rx] = now;

            double t = (now - _modernFadeStart[rx]) / ModernFadeMs;
            if (t < 0 || t >= 1) return;
            int alpha = (int)(200 * (1 - t) * (1 - t));
            if (alpha <= 0) return;
            _d2dRenderTarget.FillRectangle(new RectangleF(0, nVerticalShift, W, H), getDXBrushForColour(Color.FromArgb(alpha, 0, 0, 0)));
        }

        // ---- soft shadow around the console panels that float over the display ----

        // panel rectangles in display coordinates; set by the console while Modern UI is on
        internal static Rectangle[] ModernPanelShadowRects;
        // the display window (cursor mapping for the hover lift), the open drawer and how far it has risen (0..1)
        internal static IntPtr ModernDisplayHwnd;
        internal static Rectangle ModernDrawerRect;
        internal static float ModernDim;

        private static float[] _modernLift = new float[0];
        private static double _modernLastShadowMs;

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref System.Drawing.Point p);

        // stroked rings outside each panel only, so a see-through panel is not darkened inside;
        // the rings sink so the shadow is heavier below the panel. A panel under the mouse rises:
        // its shadow grows, falls further down and softens. The open drawer floats highest.
        private static void drawModernPanelShadowsDX2D()
        {
            if (!ModernTheme.Enabled) return;
            double now = m_dElapsedFrameStart;
            float dt = (float)Math.Max(0.0, Math.Min(0.1, (now - _modernLastShadowMs) / 1000.0));
            _modernLastShadowMs = now;

            Rectangle[] rects = ModernPanelShadowRects;
            if (rects != null)
            {
                System.Drawing.Point mouse = System.Windows.Forms.Cursor.Position;
                bool mouseOk = ModernDisplayHwnd != IntPtr.Zero && ScreenToClient(ModernDisplayHwnd, ref mouse);
                if (_modernLift.Length != rects.Length) _modernLift = new float[rects.Length];
                for (int n = 0; n < rects.Length; n++)
                {
                    float target = mouseOk && rects[n].Contains(mouse) ? 1f : 0f;
                    _modernLift[n] += (target - _modernLift[n]) * Math.Min(1f, dt * 12f);
                    drawModernShadowDX2D(rects[n], _modernLift[n], 0f);
                }
            }
            Rectangle d = ModernDrawerRect;
            if (!d.IsEmpty) drawModernShadowDX2D(d, 1f, ModernDim);
        }

        // lift 0..1 = hover, height 0..1 = extra for the drawer
        private static void drawModernShadowDX2D(Rectangle r, float lift, float height)
        {
            int rings = 7 + (int)Math.Round(5 * lift + 6 * height);
            float sink = 0.4f + 0.4f * lift + 0.4f * height;
            for (int i = 1; i <= rings; i++)
            {
                double k = 1.0 - (i - 1) / (double)rings;
                int alpha = (int)(110 * (1f - 0.2f * lift) * k * k);
                if (alpha <= 0) continue;
                RoundedRectangle rr = new RoundedRectangle
                {
                    Rect = new RectangleF(r.X - i + 0.5f, r.Y - i + 0.5f + i * sink, r.Width + 2 * i - 1, r.Height + 2 * i - 1),
                    RadiusX = 5 + i,
                    RadiusY = 5 + i,
                };
                _d2dRenderTarget.DrawRoundedRectangle(rr, getDXBrushForColour(Color.FromArgb(alpha, 0, 0, 0)), 1f);
            }
        }

        // ---- the panorama under glass: a faint reflection at the top ----

        private static ID2D1LinearGradientBrush _modernSheen;

        private static void drawModernGlassDX2D(float W, float H)
        {
            if (!ModernTheme.Enabled || W < 2 || H < 2) return;
            if (_modernSheen == null)
            {
                GradientStop[] stops =
                {
                    new GradientStop { Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 0.07f), Position = 0f },
                    new GradientStop { Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 0f), Position = 1f },
                };
                using (ID2D1GradientStopCollection coll = _d2dRenderTarget.CreateGradientStopCollection(stops))
                    _modernSheen = _d2dRenderTarget.CreateLinearGradientBrush(new LinearGradientBrushProperties(), coll);
            }
            _modernSheen.StartPoint = new Vector2(0, 0);
            _modernSheen.EndPoint = new Vector2(0, H * 0.22f);
            _d2dRenderTarget.FillRectangle(new RectangleF(0, 0, W, H * 0.22f), _modernSheen);
        }

        private static void releaseModernBrushes()
        {
            foreach (ID2D1LinearGradientBrush b in _modernGradientBrushes.Values) b?.Dispose();
            _modernGradientBrushes.Clear();
            _modernSheen?.Dispose();
            _modernSheen = null;
        }
    }
}
