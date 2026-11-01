//  Display.HudPlate.cs
//
//  One HUD glass plate over the RX1 panadapter: ionosphere drift from the steady carriers in the visible
//  span (IONO) and in the whole IQ band (FULL), side by side.
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
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Color = System.Drawing.Color;
using Rect = Vortice.Mathematics.Rect;

namespace Thetis
{
    partial class Display
    {
        private static IDWriteTextFormat _hudFont;
        private static float _hudName, _hudArrow, _hudSpeed, _hudDetail, _hudH;   // fixed columns (widest content)
        private static DriftReading _ionoView, _ionoFull;
        private static long _ionoHudTicks, _ionoHudShown, _hudHoldUntil;
        // FULL speed summed over time since the start / last retune: metres the layer moved (+ = down).
        // shortcut: the speed is against the 60 s mean, so a move lasting many minutes is undercounted
        private static double _ionoMoved;
        private static long _ionoMovedRef = long.MinValue;
        // Setup > Antennas "Ionosphere HUD"
        public static volatile bool IonoHudOn = true;
        // elevation of the RX1 filter signal when IONO was latched (NaN = none): an oblique path changes by
        // 2 sin(el) dh, so the vertical speed is v / sin(el); el kept >= 10 deg (x5.8 at most)
        private static float _ionoElev = float.NaN;
        private const double IonoMinElev = 10;

        // DrawIonoDriftDX2D hands its readings over instead of drawing them; the plate takes a new value
        // twice a second (user: every frame flickered)
        private static void SetIonoHud(DriftReading view, DriftReading full)
        {
            long now = DateTime.UtcNow.Ticks;
            long refHz = RX1IqOffsetToHz(0);
            if (refHz != _ionoMovedRef) { _ionoMovedRef = refHz; _ionoMoved = 0; }
            else if (full.N > 0 && !full.Steady && now - _ionoHudTicks < 10000000)   // no sum across a gap (TX)
                _ionoMoved += full.V * (now - _ionoHudTicks) / 1e7;
            _ionoHudTicks = now;
            if (now - _ionoHudShown < 5000000) return;
            _ionoHudShown = now;
            _ionoView = view;
            _ionoFull = full;
            _ionoElev = DiversityForm.AoAElevationDeg;
        }

        private static void releaseHudFont()
        {
            _hudFont?.Dispose();
            _hudFont = null;
            _hudName = 0;
        }

        private static float hudWidth(string s, out float h)
        {
            using (IDWriteTextLayout lay = fontFactory.CreateTextLayout(s, _hudFont, 4000f, 200f))
            {
                h = lay.Metrics.Height;
                return lay.Metrics.WidthIncludingTrailingWhitespace;
            }
        }

        // a fixed plate at the bottom of RX1 (above the CW text bar); every piece of text in its own column so
        // nothing moves when the values change: name | arrow | speed (right aligned) | detail
        private static void DrawHudPlateDX2D(int W, int rx1Height, bool cwBar)
        {
            // always on while the drift runs (user); the last values held through TX and a few seconds after it,
            // otherwise "--" 1 s after the last reading (Δh keeps its sum)
            if (!IonoDriftActive || !IonoHudOn) return;
            long now = DateTime.UtcNow.Ticks;
            if (Audio.MOX) _hudHoldUntil = now + 30000000;
            bool stale = now - _ionoHudTicks > 10000000 && now > _hudHoldUntil;
            if (fontFactory == null || _d2dRenderTarget == null || rx1Height < 60) return;
            if (_hudFont == null)
                _hudFont = fontFactory.CreateTextFormat("Bahnschrift", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal,
                    15f * _d2dRenderTarget.Dpi.Width / 96f);
            if (_hudName <= 0)
            {
                _hudName = hudWidth("IONO  ", out _hudH);
                _hudArrow = hudWidth("▼ ", out _);
                _hudSpeed = hudWidth("888.8 m/s", out _);
                _hudDetail = Math.Max(hudWidth("   (+0.00 Hz, 88, ∠88°)", out _), hudWidth("   (888)   Δh ▼ 8888 m", out _));
            }
            float side = _hudName + _hudArrow + _hudSpeed + _hudDetail, gap = 28f;
            float pw = (float)Math.Round(2 * side + gap + 32f), ph = (float)Math.Round(_hudH + 12f), c = 7f;
            float x = (float)Math.Round((W - pw) / 2f);
            float y = (float)Math.Round(rx1Height - ph - (cwBar ? 40f : 12f));

            Color accent = ModernTheme.Accent;
            using (ID2D1PathGeometry geo = _d2dFactory.CreatePathGeometry())
            {
                using (ID2D1GeometrySink sink = geo.Open())
                {
                    sink.BeginFigure(new Vector2(x + c, y), FigureBegin.Filled);
                    sink.AddLine(new Vector2(x + pw - c, y));
                    sink.AddLine(new Vector2(x + pw, y + c));
                    sink.AddLine(new Vector2(x + pw, y + ph - c));
                    sink.AddLine(new Vector2(x + pw - c, y + ph));
                    sink.AddLine(new Vector2(x + c, y + ph));
                    sink.AddLine(new Vector2(x, y + ph - c));
                    sink.AddLine(new Vector2(x, y + c));
                    sink.EndFigure(FigureEnd.Closed);
                    sink.Close();
                }
                // dark smoked glass, a sheen on its upper half, glowing edge
                _d2dRenderTarget.FillGeometry(geo, getDXBrushForColour(Color.FromArgb(6, 14, 24), 40));
                GradientStop[] stops =
                {
                    new GradientStop { Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 0.06f), Position = 0f },
                    new GradientStop { Color = new Vortice.Mathematics.Color4(accent.R / 255f, accent.G / 255f, accent.B / 255f, 0.05f), Position = 0.5f },
                    new GradientStop { Color = new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f), Position = 1f },
                };
                using (ID2D1GradientStopCollection sc = _d2dRenderTarget.CreateGradientStopCollection(stops))
                using (ID2D1LinearGradientBrush sheen = _d2dRenderTarget.CreateLinearGradientBrush(
                    new LinearGradientBrushProperties { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + ph) }, sc))
                    _d2dRenderTarget.FillGeometry(geo, sheen);
                _d2dRenderTarget.DrawGeometry(geo, getDXBrushForColour(accent, 25), 5f);
                _d2dRenderTarget.DrawGeometry(geo, getDXBrushForColour(accent, 140), 1f);
            }

            float ty = y + 6f;
            hudSide("IONO", stale ? default : _ionoView, true, x + 16f, ty, accent, stale ? float.NaN : _ionoElev);
            hudSide("FULL", stale ? default : _ionoFull, false, x + 16f + side + gap, ty, accent);
        }

        private static void hudSide(string name, DriftReading r, bool hzDetail, float x0, float ty, Color accent, float elev = float.NaN)
        {
            hudText(name, x0, ty, accent);
            float xa = x0 + _hudName, xs = xa + _hudArrow;
            // FULL: Δh always, also with no reading (the sum so far), in its own column
            if (!hzDetail)
                hudText("Δh " + (_ionoMoved >= 0 ? "▼ " : "▲ ") + Math.Abs(_ionoMoved).ToString("0") + " m",
                    xs + _hudSpeed + hudWidth("   (888)   ", out _), ty, accent);
            if (r.N == 0)
            {
                hudText("--", xs + _hudSpeed - hudWidth("--", out _), ty, accent);
                return;
            }
            hudText(r.Steady ? "=" : r.Hz > 0 ? "▼" : "▲", xa, ty, accent);
            bool corr = !float.IsNaN(elev);
            double v = Math.Abs(r.V) / (corr ? Math.Sin(Math.Max(IonoMinElev, elev) * Math.PI / 180) : 1);
            string sp = (r.Steady ? 0 : v).ToString("0.0") + " m/s";
            hudText(sp, xs + _hudSpeed - hudWidth(sp, out _), ty, accent);
            if (hzDetail)
                hudText("   (" + Math.Round(r.Hz, 2).ToString("+0.00;-0.00;0.00") + " Hz, " + r.N + (corr ? ", ∠" + elev.ToString("0") + "°" : "") + ")",
                    xs + _hudSpeed, ty, accent);
            else
            {
                hudText("   (" + r.N + ")", xs + _hudSpeed, ty, accent);
            }
        }

        // lit text: a soft accent halo, then the face
        private static void hudText(string text, float tx, float ty, Color accent)
        {
            ID2D1Brush halo = getDXBrushForColour(accent, 70);
            drawStringDX2D(text, _hudFont, halo, tx - 1f, ty);
            drawStringDX2D(text, _hudFont, halo, tx + 1f, ty);
            drawStringDX2D(text, _hudFont, halo, tx, ty - 1f);
            drawStringDX2D(text, _hudFont, halo, tx, ty + 1f);
            Color face = Color.FromArgb((accent.R + 2 * 255) / 3, (accent.G + 2 * 255) / 3, (accent.B + 2 * 255) / 3);
            drawStringDX2D(text, _hudFont, getDXBrushForColour(face, 255), tx, ty);
        }
    }
}
