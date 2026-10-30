// Portions Copyright (C) 2026 Daniel Poznanski, 3Z9AM: Waterfall sphere and its inside (planetarium) view on the 3D panadapter
// (sphere/inside projection in the shaders and mesh below).
//
// Tier 3 - GPU mesh based 3D panadapter surface (experimental)
//
// Replaces the per-column DrawLine curtain painting of DrawPanadapter3DHistoryDX2D with a
// real Direct3D11 triangle sheet rendered straight into the shared swapchain backbuffer
// BEFORE the D2D frame begins. The D2D line renderer stays untouched and remains the
// permanent fallback (GPU fallback rule 1); this path is only taken when:
//   - GpuMeshEnabled is set by the user
//   - render path is Hardware (WARP would just be slower CPU rasterisation)
//   - the mesh pipeline is healthy (any failure silently falls back for the frame)
//
// Geometry mirrors the Aether DssRenderer math already used by the D2D path exactly:
// per depth row tS: width frac 1-tS*(1-backW), inset, baseline bottomY-tS*depthSpan,
// foreshortened ridge frontRidge*rowWidthFrac, floor lift pow(s,zCurve).
// Heights are streamed as an R32Float texture (one texel per column x depth row) and the
// static vertex buffer is just a curtain grid - per column per row a crest vertex and a
// floor vertex (Aether's "full-height coloured curtains") - so the per-frame CPU cost is
// one small texture update. Each cell quad hangs vertically from its crest edge to the
// absolute bottom: narrow carriers render as thin vertical needles exactly like the CPU
// path's per-column fills, with no interpolated cliff walls. Colour = palette texture
// lookup by raw strength (built per frame from the same SelectSurfaceColour sources as
// the D2D path), plus horizontal slope shading and linear depth haze to match the look.
//
// Tier-1 parity features rendered by this pass (D2D line renderer equivalents):
//   - crest hairlines: LineList over the crest vertices, ps_line replicating the PASS 2
//     outline formula; drawn back-to-front interleaved with curtain rows so occlusion
//     matches the D2D fill/outline interleave
//   - perspective grid floor + side rails: flat vertex-colour lines, D2D alphas 0.10->0.03 / 0.12
//   - side walls / end caps: edge-trace triangle strips down to absolute bottom,
//     flat colour = front-row edge surface colour x0.32 blended toward bg by mid fog
// All premultiplied-alpha outputs so the shared AlphaBlend state composites as SourceOver.
using System;
// Third-party: GPU interop via Vortice.Windows (MIT License, Copyright (c) Amer Koleci and Contributors).
// Full license text ships with the app (Licenses folder) and lives in the repo under Project Files\lib\licenses\.
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Direct2D1;
using MapMode = Vortice.Direct3D11.MapMode;

namespace Thetis
{
    partial class Display
    {
        #region GPU mesh fields

        private const int MeshPaletteSize = 256;

        private static bool _meshShadersBuilt;
        private static bool _meshFailedLogged;
        private static ID3D11RenderTargetView _meshRTV;
        private static ID3D11VertexShader _meshVS;
        private static ID3D11PixelShader _meshPS;
        private static ID3D11InputLayout _meshIL;
        private static ID3D11Buffer _meshVB;          // static UV grid
        private static ID3D11Buffer _meshIB;          // static index buffer
        private static ID3D11Buffer _meshCB;          // constants
        private static ID3D11Texture2D _meshHeightTex;
        private static ID3D11ShaderResourceView _meshHeightSRV;
        private static ID3D11Texture2D _meshPaletteTex;
        private static ID3D11ShaderResourceView _meshPaletteSRV;
        private static ID3D11Texture2D _meshPhaseTex;        // RX1 phase colours per cell (Display.Pan3DPhase.cs)
        private static ID3D11ShaderResourceView _meshPhaseSRV;
        private static uint[] _meshPhaseScratch;
        private static ID3D11Texture2D _meshMarkTex;         // waterfall sphere: per-column tint (filter, grid, VFO, TX/CW lines)
        private static ID3D11ShaderResourceView _meshMarkSRV;
        private static uint[] _meshMarkScratch;
        private static ID3D11BlendState _meshBlend;
        private static ID3D11RasterizerState _meshRS;
        private static ID3D11SamplerState _sampPoint;
        private static ID3D11SamplerState _sampLinear;

        // depth buffer: lets the whole surface go out in two draw calls (all curtains,
        // then all crest hairlines) instead of two calls per depth row; row order is
        // resolved by z = row depth instead of painter's back-to-front submission
        private static ID3D11Texture2D _meshDepthTex;
        private static ID3D11DepthStencilView _meshDSV;
        private static ID3D11DepthStencilState _dssWrite;     // curtains: test + write
        private static ID3D11DepthStencilState _dssTestOnly;  // hairlines: test, no write
        private static int _meshDepthW, _meshDepthH;
        private const float MeshLineZBias = 0.0005f;          // < half the row z-spacing at 500 rows

        // crest hairlines (PASS 2 parity): line-list index buffer over the same UV grid
        private static ID3D11PixelShader _meshPSLine;
        private static ID3D11Buffer _meshLineIB;

        // grid floor + side walls: tiny dynamic vertex-colour pipeline
        private static ID3D11VertexShader _meshVSFlat;
        private static ID3D11PixelShader _meshPSFlat;
        private static ID3D11InputLayout _meshFlatIL;
        private static ID3D11Buffer _meshAuxVB;       // dynamic, premultiplied colour verts

        // scissored-clear helpers (repaint the plot strip over a pre-drawn skin image)
        private static ID3D11VertexShader _meshClearVS;
        private static ID3D11PixelShader _meshClearPS;
        private static ID3D11Buffer _meshClearVB;
        private static ID3D11Buffer _meshClearIB;
        private static ID3D11BlendState _meshBlendOpaque;   // no blending = overwrite
        private static ID3D11RasterizerState _meshRSScissor;
        private static float[] _meshAuxScratch;       // managed staging for _meshAuxVB
        private static int _meshRows = -1;            // built grid dimensions
        private static int _meshCols = -1;
        private static float[] _meshHeightScratch;    // rows*cols strengths

        // captured by DrawPanadapterDX2D each frame, consumed pre-BeginDraw next frame
        private struct MeshFrameParams
        {
            public bool Valid;
            public float W, PlotH, TargetH, Shift;
            public int Cols, Decimation, GridMin, GridMax;
            public int RX;      // which receiver's pane these params describe
            public int Frame;   // _meshFrameNo when captured (stale panes are skipped)
        }
        private static MeshFrameParams _meshParams;                       // pane being rendered
        private static readonly MeshFrameParams[] _meshParamsRX = new MeshFrameParams[3];
        private static int _meshFrameNo;

        #endregion

        #region GPU mesh public control

        /// <summary>Experimental Tier 3 GPU mesh 3D surface toggle (session only).</summary>
        public static bool GpuMeshEnabled { get; set; } = true;

        private static void CaptureMeshFrameParams(int nVerticalShift, int W, int H, int rx, int nDecimatedWidth, int local_Decimation, int grid_min, int grid_max)
        {
            // each rx pane keeps its own params; the GPU pass renders every pane that
            // was captured in the previous frame (RX1 and RX2 each from their own history)
            int r = rx == 2 ? 2 : 1;
            _meshParamsRX[r].W = W;
            _meshParamsRX[r].PlotH = H;
            _meshParamsRX[r].TargetH = displayTargetHeight;
            _meshParamsRX[r].Shift = nVerticalShift;
            _meshParamsRX[r].Cols = nDecimatedWidth;
            _meshParamsRX[r].Decimation = local_Decimation;
            _meshParamsRX[r].GridMin = grid_min;
            _meshParamsRX[r].GridMax = grid_max;
            _meshParamsRX[r].RX = r;
            _meshParamsRX[r].Frame = _meshFrameNo;
            _meshParamsRX[r].Valid = true;
        }

        #endregion

        #region GPU mesh HLSL

        private const string MESH_HLSL = @"
            cbuffer MeshCB : register(b0)
            {
                float CB_W;         // plot width px
                float CB_TargetH;   // full backbuffer height px
                float CB_BottomY;   // absolute bottom of plot
                float CB_DepthSpan; // total baseline rise
                float CB_FrontRidge;// max ridge height at front
                float CB_BackW;     // back width fraction
                float CB_ZCurve;    // floor lift exponent
                float CB_Haze;      // haze strength
                float3 CB_Background;
                float CB_TexelX;    // 1/cols for neighbour taps
                float CB_TexelY;    // 1/rows
                float CB_ZBias;     // hairlines sit just behind their own curtain
                float CB_FloorT;    // floor transparency 0-1 (level 0 see-through)
                float CB_CapPx;     // >0: tops only - just this many px under each crest are drawn
                float4 CB_Overlay;  // display background colour incl. its alpha (2D overlay)
                float CB_Phase;     // 1: colours from PhaseTex (RX2-RX1 phase) instead of the palette
                float CB_Sphere;    // 1: waterfall sphere (equator = newest row, poles = oldest), 2: seen from inside
                float CB_Hemi;      // sphere: +1 northern (levels), -1 southern (phase) hemisphere
                float CB_PlotH;     // plot height px
                float InD;          // sphere inside: observer distance behind the centre (radius units)
                float InH;          // sphere inside: observer height over the equator plane (horizon arc bend)
                float InK;          // sphere inside: history flow, + recedes (crowds at the top), - approaches (crowds at the horizon)
                float CB_LineA;     // sphere: opacity of the line columns
                float CB_GridA;     // sphere: opacity of the frequency grid lines
                float CB_Stand;     // sphere inside: 1 = crests as vertical walls, 0 = lying along the horizon
                float CB_Tilt;      // sphere inside: crest angle (rad) from the horizon toward up (lying: tilt, standing: 90 deg - tilt)
                float CB_Pad;
            };

            // waterfall sphere: an ellipsoid filling the plot (strongly flattened), seen from
            // the front. Longitude = asin(2u-1) so frequency stays linear in x on the equator;
            // latitude = time from the equator (newest row) to the pole (oldest)
            float SphereLon(float u) { return asin(clamp(u * 2.0 - 1.0, -1.0, 1.0)); }

            // sphere seen from inside (CB_Sphere 2, planetarium): unit sphere, camera suspended in
            // the centre (InD behind it, pitched up by InP), equator = horizon; the band spans
            // +-InL of longitude, the view reaches latitude InTop (no zenith). Twin: Display.SphereInsidePoint
            // InH: observer raised above the equator plane - the horizon bends into an arc
            static const float InP = 0.0, InL = 1.1, InHorizon = 0.5, InTop = 1.05;
            float3 InsideView(float lon, float lat, float rr)
            {
                float3 d = float3(rr * cos(lat) * sin(lon), rr * sin(lat) - InH, rr * cos(lat) * cos(lon) + InD);
                return float3(d.x, d.y * cos(InP) - d.z * sin(InP), d.y * sin(InP) + d.z * cos(InP));
            }
            // longitude whose horizon point lands at linear x (frequency stays linear on the horizon)
            float InsideLon(float u)
            {
                float k = (u * 2.0 - 1.0) * sin(InL) / (cos(InL) + InD);
                return atan(k) + asin(k * InD / sqrt(1.0 + k * k));
            }
            // latitude of history row v: straight ahead the rows land at screen fraction
            // s(v) between the horizon (0) and InTop (1): InK > 0 decelerates (older history
            // recedes), InK < 0 accelerates (rows born far at the horizon approach overhead). Solves InsideView y/z = Y, needs InP = 0
            float InsideLat(float v)
            {
                float s = abs(InK) > 0.01 ? (1.0 - exp(-InK * v)) / (1.0 - exp(-InK)) : v;
                float3 h0 = InsideView(0.0, 0.0, 1.0);
                float3 tp = InsideView(0.0, InTop, 1.0);
                float Y = lerp(h0.y / h0.z, tp.y / tp.z, s);
                return atan(Y) + asin(clamp((InH + Y * InD) / sqrt(1.0 + Y * Y), -1.0, 1.0));
            }
            // anamorphic fit: horizon arc spans the plot width (its middle at InHorizon),
            // latitude InTop straight ahead 3% under the top.
            // Clip space keeps w = depth so rows passing behind the camera clip cleanly; the
            // caller sets z (depth by row age)
            float4 InsideClip(float3 q)
            {
                float top = CB_BottomY - CB_PlotH;
                float hy = top + CB_PlotH * InHorizon;
                float3 h0 = InsideView(0.0, 0.0, 1.0);             // horizon straight ahead -> hy
                float yh = h0.y / h0.z;
                float3 pole = InsideView(0.0, InTop, 1.0);
                float fx = (CB_W * 0.5 - 2.0) * (cos(InL) + InD) * cos(InP) / sin(InL);
                float fy = (hy - top - CB_PlotH * 0.03) / (pole.y / pole.z - yh);
                float sxw = CB_W * 0.5 * q.z + fx * q.x;            // screen x * w
                float syw = hy * q.z - fy * (q.y - yh * q.z);       // screen y * w
                return float4(sxw / CB_W * 2.0 - q.z, q.z - syw / CB_TargetH * 2.0, 0.0, q.z);
            }

            // opacity of the surface at normalised level s: the lowest levels are
            // CB_FloorT transparent so the spectral-area image shows through the
            // floor, signals fade back to fully opaque by 40% of the range
            float FloorAlpha(float s)
            {
                return lerp(1.0 - CB_FloorT, 1.0, smoothstep(0.0, 0.4, saturate(s)));
            }

            Texture2D HeightTex : register(t0);
            Texture2D PaletteTex : register(t1);
            Texture2D PhaseTex : register(t2);      // alpha 0 = no phase data, palette stays
            Texture2D MarkTex : register(t3);       // sphere: per-column mark colour, alpha = strength
            SamplerState PointSamp : register(s0);
            SamplerState LinearSamp : register(s1);

            // sphere: filter band / grid / VFO / TX and CW lines tint the curtains and crests
            float3 SphereMark(float3 c, float tx)
            {
                if (CB_Sphere < 0.5) return c;
                float4 mk = MarkTex.SampleLevel(PointSamp, float2(tx, CB_Hemi > 0.0 ? 0.25 : 0.75), 0);   // row 1 (south): no filter band
                return lerp(c, mk.rgb, mk.a);
            }

            // sphere: a line column (alpha 1 in MarkTex, grid lines 254/255, the filter band
            // stays below) is drawn flat in its own colour only - no crests over it, so the
            // line reads clearly. c = premultiplied colour at the line's opacity
            bool SphereLine(float tx, out float4 c)
            {
                float4 mk = MarkTex.SampleLevel(PointSamp, float2(tx, CB_Hemi > 0.0 ? 0.25 : 0.75), 0);
                float a = mk.a > 0.998 ? CB_LineA : CB_GridA;
                c = float4(mk.rgb * a, a);
                return CB_Sphere > 0.5 && mk.a > 0.99;
            }

            struct PSIN
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 lift : TEXCOORD1;  // x: height above the row baseline in lift units, y: px per lift unit
            };

            // grid uv -> texture uv. Grid coords are vertex indices normalised to
            // [0,1] (u = c/(cols-1)) while texel centres live at (i+0.5)/size -
            // remap so taps hit column centres exactly instead of straddling.
            float2 TexelAt(float u, float v)
            {
                float cols = 1.0 / CB_TexelX;
                float rows = 1.0 / CB_TexelY;
                return float2((u * (cols - 1.0) + 0.5) / cols,
                              (v * (rows - 1.0) + 0.5) / rows);
            }

            // Aether-style curtain vertex: (u, v, corner, 0). corner 0 hangs at the
            // row's crest above column u; corner 1 drops straight down to the
            // absolute plot bottom. One quad per cell per row means a narrow carrier
            // renders as its own thin vertical stripe - exactly like the CPU path's
            // per-column fills - instead of the diagonal cliff wall a continuous
            // heightfield sheet would interpolate across the neighbouring cells.
            PSIN vs_main(float4 vin : POSITION)
            {
                PSIN o;
                float u = vin.x;
                float v = vin.y;                                 // 0 = front row, 1 = back row
                if (CB_Sphere > 1.5)
                {
                    // inside: newest row on the horizon, history up the dome. Crests lie on
                    // screen parallel to the horizon arc at their column (sideways toward the
                    // middle of the sphere), length as a world-vertical wall; the curtain foot stays on
                    // the previous row
                    bool foot = vin.z > 0.5;
                    float lat = InsideLat(v);
                    float lon = InsideLon(u);
                    float hs = HeightTex.SampleLevel(PointSamp, TexelAt(u, v), 0).r;
                    float ls = pow(max(hs, 0.0), CB_ZCurve);
                    float4 lc;
                    if (SphereLine(TexelAt(u, v).x, lc)) ls = 0.0;   // line columns stay flat
                    float hk = min(CB_FrontRidge / max(CB_PlotH, 1.0) * 0.5, 0.35) * (0.1 + 0.9 * cos(lat));   // world units
                    // Standing bars: full Ridge height; lying crests read long, 30% of it
                    float rise = foot ? 0.0 : ls * hk * (CB_Stand > 0.5 ? 1.0 : 0.3);
                    float3 q = InsideView(lon, CB_Hemi * (foot ? InsideLat(max(v - CB_TexelY * 1.5, 0.0)) : lat), 1.0);
                    o.pos = InsideClip(q);
                    // horizon arc tangent at this column (pixels, y up), toward the middle
                    float4 ha = InsideClip(InsideView(InsideLon(max(u - 0.002, 0.0)), 0.0, 1.0));
                    float4 hb = InsideClip(InsideView(InsideLon(min(u + 0.002, 1.0)), 0.0, 1.0));
                    float2 tm = normalize((hb.xy / hb.w - ha.xy / ha.w) * float2(CB_W, CB_TargetH)) * sign(0.5 - u);
                    // CB_Tilt from the horizon toward up (down in the south)
                    float2 nrm = tm * cos(CB_Tilt) + float2(0.0, CB_Hemi) * sin(CB_Tilt);
                    float px = rise * (InsideClip(q + float3(0.0, 1.0, 0.0)).y - o.pos.y) / o.pos.w * CB_TargetH * 0.5;
                    float side = abs(cos(CB_Tilt));
                    if (side > 0.01) px = min(px, abs(o.pos.x / o.pos.w) * CB_W * 0.5 * 0.95 / side);   // never past the middle
                    o.pos.xy += nrm * px * float2(2.0 / CB_W, 2.0 / CB_TargetH) * o.pos.w;
                    // depth by row age: the crests stick out past the dome over older rows
                    o.pos.z = saturate(v * 0.98 + 0.01 + CB_ZBias) * o.pos.w;
                    o.uv = float2(u, v);
                    // shortcut: px per lift unit is approximate (tops-only cap width), exact projection not needed
                    o.lift = float2(foot ? 0.0 : ls, max(hk * CB_PlotH / max(q.z, 0.05), 1e-3));
                    return o;
                }
                if (CB_Sphere > 0.5)
                {
                    // curtains rise away from the equator (up in the north, down in the
                    // south) like the flat 3D, so rows nearer the equator occlude older
                    // ones; the curtain foot sits on the previous row so rows tile
                    float hpx = max(min(CB_FrontRidge * 0.35, CB_PlotH * 0.2), 1e-3);
                    float rx = CB_W * 0.5 - 2.0;
                    float ry = CB_PlotH * 0.5 * 0.97 - hpx;
                    float cy = CB_BottomY - CB_PlotH * 0.5;
                    float lon = SphereLon(u);
                    float lat = v * 1.5707963;
                    float vf = (vin.z > 0.5) ? max(v - CB_TexelY * 1.5, 0.0) : v;   // ~one row step toward the equator
                    float latF = vf * 1.5707963;
                    float ySurf = cy - CB_Hemi * ry * sin(lat);
                    float hs = HeightTex.SampleLevel(PointSamp, TexelAt(u, v), 0).r;
                    float ls = pow(max(hs, 0.0), CB_ZCurve);
                    float4 lc;
                    if (SphereLine(TexelAt(u, v).x, lc)) ls = 0.0;   // line columns stay flat
                    float hk = max(hpx * (0.1 + 0.9 * cos(lat)), 1e-3);   // amplitude fades to 10% at the poles
                    float sy = (vin.z > 0.5) ? cy - CB_Hemi * ry * sin(latF) : ySurf - CB_Hemi * ls * hk;
                    float sx = CB_W * 0.5 + rx * cos(latF) * sin(lon);
                    float sz = saturate(0.5 - 0.45 * cos(lat) * (0.5 + 0.5 * cos(lon)) + CB_ZBias);   // nearer = smaller
                    o.pos = float4(sx / CB_W * 2.0 - 1.0, 1.0 - sy / CB_TargetH * 2.0, sz, 1.0);
                    o.uv = float2(u, v);
                    o.lift = float2(CB_Hemi * (ySurf - sy) / hk, hk);   // colours keep the full level
                    return o;
                }
                float rwf = 1.0 - v * (1.0 - CB_BackW);          // row width fraction
                float inset = CB_W * (1.0 - rwf) * 0.5;
                float h = HeightTex.SampleLevel(PointSamp, TexelAt(u, v), 0).r;
                float lift = pow(max(h, 0.0), CB_ZCurve);
                float x = inset + u * (CB_W - 2.0 * inset);
                float baseline = CB_BottomY - v * CB_DepthSpan;
                float yTop = baseline - lift * CB_FrontRidge * rwf; // uniform perspective scaling
                float y = (vin.z > 0.5) ? CB_BottomY : yTop;        // curtains reach the floor
                // depth = row depth: nearer rows occlude farther ones (z-buffer replaces
                // the per-row painter's submission order)
                float z = saturate(v * 0.98 + 0.01 + CB_ZBias);
                o.pos = float4(x / CB_W * 2.0 - 1.0, 1.0 - y / CB_TargetH * 2.0, z, 1.0);
                o.uv = float2(u, v);
                // lift is linear in screen y within a row, so interpolating it gives each
                // pixel of the curtain its own level (negative below the row baseline)
                float pxPerLift = max(CB_FrontRidge * rwf, 1e-3);
                o.lift = float2((baseline - y) / pxPerLift, pxPerLift);
                return o;
            }

            float4 ps_main(PSIN i) : SV_Target
            {
                float2 t = TexelAt(i.uv.x, i.uv.y);
                float4 lc;
                if (SphereLine(t.x, lc)) return lc;   // the line only, nothing else
                float s = HeightTex.SampleLevel(PointSamp, t, 0).r;
                // horizontal slope shading (Aether kSlopeGain=0.55, shade 0.68-1.32),
                // matching the D2D path's per-column shade so the look carries over
                float hl = HeightTex.SampleLevel(PointSamp, t - float2(CB_TexelX, 0.0), 0).r;
                float hr = HeightTex.SampleLevel(PointSamp, t + float2(CB_TexelX, 0.0), 0).r;
                float slope = pow(max(hl, 0.0), CB_ZCurve) - pow(max(hr, 0.0), CB_ZCurve);
                float shade = clamp(1.0 + 0.55 * slope, 0.68, 1.32);

                float v = i.uv.y;
                float dim = 0.72 + 0.28 * (1.0 - v);             // depth dimming
                if (CB_Sphere > 1.5)                             // inside: older rows overhead darker
                    dim = 0.45 + 0.55 * cos(v * 1.5707963);
                else if (CB_Sphere > 0.5)                        // globe shading: darker toward the limb
                    dim = 0.35 + 0.65 * cos(v * 1.5707963) * cos(SphereLon(i.uv.x));
                float haze = v * CB_Haze;                        // linear fog toward background

                // gradient column: each pixel takes the palette colour of its own height,
                // from the noise colour at the baseline up to the crest's colour
                float lvl = min(pow(saturate(i.lift.x), 1.0 / max(CB_ZCurve, 1e-3)), saturate(s));
                if (CB_CapPx > 0.0)
                {
                    // tops only: a cap in the crest colour, the rest of the curtain is not
                    // drawn at all (no depth write) so the rows behind show through it
                    float below = (pow(max(s, 0.0), CB_ZCurve) - i.lift.x) * i.lift.y;
                    if (below > CB_CapPx)
                        discard;
                    lvl = saturate(s);
                }
                float4 col = PaletteTex.SampleLevel(LinearSamp, float2(lvl, 0.5), 0);
                bool phased = false;
                if (CB_Phase > 0.5)
                {
                    float4 pc = PhaseTex.SampleLevel(PointSamp, t, 0);
                    phased = pc.a > 0.5;
                    if (phased) col = float4(pc.rgb * (0.55 + 0.45 * lvl), 1.0);  // keep the height gradient
                }
                col.rgb = SphereMark(col.rgb, t.x);
                col.rgb *= dim * shade;
                if (CB_Hemi < 0.0 && !phased) col.rgb *= 0.5;   // no phase data (Diversity off): south = levels mirrored, a floor reflection
                col.rgb = lerp(col.rgb, CB_Background, saturate(haze));
                // curtains tile edge-to-edge below the crests and are drawn front row
                // first with depth write, so each pixel is blended exactly once (by the
                // nearest row) - Floor transparency lets the backdrop through the low
                // levels without accumulating across rows. Premultiplied output.
                float a = FloorAlpha(s);
                return float4(col.rgb * a, a);
            }

            // crest hairline (PASS 2 parity): same geometry as the sheet but drawn as
            // LineList with the D2D outline formula - palette colour brightened by
            // outlineBright * dim * 1.5, hazed, alpha (1-tS*0.4)*0.85.
            // Output is PREMULTIPLIED so the shared AlphaBlend state (SrcBlend One /
            // DestBlend InvSrcAlpha) composites as true SourceOver, matching D2D.
            float4 ps_line(PSIN i) : SV_Target
            {
                float4 lc;
                if (SphereLine(TexelAt(i.uv.x, i.uv.y).x, lc)) return lc;
                float s = HeightTex.SampleLevel(PointSamp, TexelAt(i.uv.x, i.uv.y), 0).r;
                float v = i.uv.y;

                float4 col = PaletteTex.SampleLevel(LinearSamp, float2(saturate(s), 0.5), 0);
                bool phased = false;
                if (CB_Phase > 0.5)
                {
                    float4 pc = PhaseTex.SampleLevel(PointSamp, TexelAt(i.uv.x, i.uv.y), 0);
                    phased = pc.a > 0.5;
                    if (phased) col = pc;
                }
                col.rgb = SphereMark(col.rgb, TexelAt(i.uv.x, i.uv.y).x);
                float bright = (0.35 + 0.65 * s) * (0.72 + 0.28 * (1.0 - v)) * 1.5;
                float3 rgb = min(col.rgb * bright, 1.0);
                if (CB_Hemi < 0.0 && !phased) rgb *= 0.5;

                float haze = v * CB_Haze;
                rgb = lerp(rgb, CB_Background, saturate(haze));

                float alpha = (1.0 - v * 0.4) * 0.85 * FloorAlpha(s);
                return float4(rgb * alpha, alpha);
            }

            // flat vertex-colour pipeline for the perspective grid floor, side rails
            // and side walls. Positions arrive in pixel space and share the plot
            // transform; colours are premultiplied CPU-side.
            struct FLATIN
            {
                float4 pos : SV_POSITION;
                float4 col : COLOR0;
            };

            FLATIN vs_flat(float2 px : POSITION, float4 col : COLOR0)
            {
                FLATIN o;
                o.pos = float4(px.x / CB_W * 2.0 - 1.0, 1.0 - px.y / CB_TargetH * 2.0, 0.0, 1.0);
                o.col = col;
                return o;
            }

            float4 ps_flat(FLATIN i) : SV_Target
            {
                return i.col;
            }

            // clear helpers: scissored fullscreen quad that repaints only the plot
            // strip, so a skin background image drawn by D2D beforehand survives
            float4 vs_clear(float2 uv : POSITION) : SV_Position
            {
                return float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            }

            float4 ps_clear() : SV_Target
            {
                // same overlay the 2D frame lays over the skin image (display
                // background colour at its own alpha), premultiplied
                return float4(CB_Overlay.rgb * CB_Overlay.a, CB_Overlay.a);
            }
            ";

        #endregion

        #region GPU mesh implementation

        private static void ReleaseGpuMeshDeviceObjects()
        {
            _meshRTV?.Dispose(); _meshRTV = null;
            _meshHeightSRV?.Dispose(); _meshHeightSRV = null;
            _meshHeightTex?.Dispose(); _meshHeightTex = null;
            _meshPaletteSRV?.Dispose(); _meshPaletteSRV = null;
            _meshPaletteTex?.Dispose(); _meshPaletteTex = null;
            _meshPhaseSRV?.Dispose(); _meshPhaseSRV = null;
            _meshPhaseTex?.Dispose(); _meshPhaseTex = null;
            _meshPhaseScratch = null;
            _meshMarkSRV?.Dispose(); _meshMarkSRV = null;
            _meshMarkTex?.Dispose(); _meshMarkTex = null;
            _meshMarkScratch = null;
            _meshIL?.Dispose(); _meshIL = null;
            _meshVS?.Dispose(); _meshVS = null;
            _meshPS?.Dispose(); _meshPS = null;
            _meshPSLine?.Dispose(); _meshPSLine = null;
            _meshVB?.Dispose(); _meshVB = null;
            _meshIB?.Dispose(); _meshIB = null;
            _meshLineIB?.Dispose(); _meshLineIB = null;
            _meshVSFlat?.Dispose(); _meshVSFlat = null;
            _meshPSFlat?.Dispose(); _meshPSFlat = null;
            _meshFlatIL?.Dispose(); _meshFlatIL = null;
            _meshAuxVB?.Dispose(); _meshAuxVB = null;
            _meshCB?.Dispose(); _meshCB = null;
            _meshBlend?.Dispose(); _meshBlend = null;
            _meshRS?.Dispose(); _meshRS = null;
            _sampPoint?.Dispose(); _sampPoint = null;
            _sampLinear?.Dispose(); _sampLinear = null;
            _meshClearVS?.Dispose(); _meshClearVS = null;
            _meshClearPS?.Dispose(); _meshClearPS = null;
            _meshClearVB?.Dispose(); _meshClearVB = null;
            _meshClearIB?.Dispose(); _meshClearIB = null;
            _meshBlendOpaque?.Dispose(); _meshBlendOpaque = null;
            _meshRSScissor?.Dispose(); _meshRSScissor = null;
            _dssWrite?.Dispose(); _dssWrite = null;
            _dssTestOnly?.Dispose(); _dssTestOnly = null;
            ReleaseMeshDepth();
            _meshRows = -1; _meshCols = -1;
            _meshHeightScratch = null;
            _meshAuxScratch = null;
            _meshShadersBuilt = false;
        }

        /// <summary>Called from resize/shutdown paths alongside releaseGlowLayer().</summary>
        private static void ReleaseGpuMeshFrameState()
        {
            _meshRTV?.Dispose();
            _meshRTV = null;
            ReleaseMeshDepth();
            _meshParams.Valid = false;
            _meshParamsRX[1].Valid = false;
            _meshParamsRX[2].Valid = false;
        }

        private static void ReleaseMeshDepth()
        {
            _meshDSV?.Dispose(); _meshDSV = null;
            _meshDepthTex?.Dispose(); _meshDepthTex = null;
            _meshDepthW = 0; _meshDepthH = 0;
        }

        private static bool EnsureMeshDepth(ID3D11Device device)
        {
            int w = (int)displayTargetWidth, h = (int)displayTargetHeight;
            if (_meshDSV != null && _meshDepthW == w && _meshDepthH == h) return true;
            ReleaseMeshDepth();
            if (w <= 0 || h <= 0) return false;
            try
            {
                _meshDepthTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.D32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.DepthStencil,
                });
                _meshDSV = device.CreateDepthStencilView(_meshDepthTex, null);
                _meshDepthW = w; _meshDepthH = h;
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU mesh: depth buffer creation failed - " + e.Message);
                ReleaseMeshDepth();
                return false;
            }
        }

        // Backdrop ownership for ALL mesh passes (3D surface + waterfall): exactly
        // one pass per frame draws the skin background image (via D2D) or performs
        // the full background-colour clear; later passes composite over it. Reset
        // at the top of every frame in RenderDX2D before the mesh dispatches.
        private static bool _bGpuBackdropDone;

        private static void EnsureGpuBackdrop(ID3D11DeviceContext dc)
        {
            if (_bGpuBackdropDone || _meshRTV == null) return;
            _bGpuBackdropDone = true;

            if (_bitmapBackground != null)
            {
                // same order as the 2D frame: clear, skin image, then (per pane) the
                // translucent background-colour overlay - the clear keeps letterbox
                // bars from holding stale frames now that the overlay is not opaque
                dc.ClearRenderTargetView(_meshRTV, new Color4(
                    m_cDX2_display_background_clear_colour.R,
                    m_cDX2_display_background_clear_colour.G,
                    m_cDX2_display_background_clear_colour.B, 1f));
                DrawSkinBackgroundPrepass();
            }
            else
            {
                dc.ClearRenderTargetView(_meshRTV, new Color4(
                    m_cDX2_display_background_clear_colour.R,
                    m_cDX2_display_background_clear_colour.G,
                    m_cDX2_display_background_clear_colour.B, 1f));
            }
        }

        private static bool BuildMeshPipeline(ID3D11Device device)
        {
            try
            {
                byte[] vsBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "vs_main", "pan3dmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] psBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "ps_main", "pan3dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _meshVS = device.CreateVertexShader(vsBytes, null);
                _meshPS = device.CreatePixelShader(psBytes);

                // crest hairline pixel shader (shares vs_main + UV grid)
                byte[] psLineBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "ps_line", "pan3dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _meshPSLine = device.CreatePixelShader(psLineBytes);

                // clear variants (scissored quad repaint of the plot strip)
                byte[] cvsBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "vs_clear", "pan3dmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] cpsBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "ps_clear", "pan3dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _meshClearVS = device.CreateVertexShader(cvsBytes, null);
                _meshClearPS = device.CreatePixelShader(cpsBytes);

                // flat vertex-colour pipeline (grid floor / rails / walls)
                byte[] vsFlatBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "vs_flat", "pan3dmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] psFlatBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "ps_flat", "pan3dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _meshVSFlat = device.CreateVertexShader(vsFlatBytes, null);
                _meshPSFlat = device.CreatePixelShader(psFlatBytes);
                _meshFlatIL = device.CreateInputLayout(new Vortice.Direct3D11.InputElementDescription[]
                {
                    new Vortice.Direct3D11.InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new Vortice.Direct3D11.InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 8, 0),
                }, vsFlatBytes);

                        Vortice.Direct3D11.InputElementDescription[] layout = new Vortice.Direct3D11.InputElementDescription[]
                        {
                            new Vortice.Direct3D11.InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0),
                        };
                _meshIL = device.CreateInputLayout(layout, vsBytes);

                _meshCB = device.CreateBuffer(new BufferDescription(128, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                _meshBlend = device.CreateBlendState(Vortice.Direct3D11.BlendDescription.AlphaBlend);
                // winding ends up CCW in NDC (y-flip in the VS) - disable culling entirely
                _meshRS = device.CreateRasterizerState(new Vortice.Direct3D11.RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid));
                // samplers MUST be created and bound - without them every texture read returns 0
                _sampPoint = device.CreateSamplerState(new SamplerDescription(
                    Vortice.Direct3D11.Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
                _sampLinear = device.CreateSamplerState(new SamplerDescription(
                    Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));

                // clear quad geometry (unit uv square) + opaque blend + scissor rasterizer
                float[] clearVerts = new float[] { 0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f };
                uint[] clearIdx = new uint[] { 0, 1, 2, 2, 1, 3 };
                _meshClearVB = device.CreateBuffer(clearVerts, new BufferDescription(
                    (uint)(clearVerts.Length * sizeof(float)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
                _meshClearIB = device.CreateBuffer(clearIdx, new BufferDescription(
                    (uint)(clearIdx.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));
                _meshBlendOpaque = device.CreateBlendState(new Vortice.Direct3D11.BlendDescription());
                Vortice.Direct3D11.RasterizerDescription rsScissor =
                    new Vortice.Direct3D11.RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid);
                rsScissor.ScissorEnable = true;
                _meshRSScissor = device.CreateRasterizerState(rsScissor);
                _dssWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Less));
                _dssTestOnly = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Less));
                _meshShadersBuilt = true;
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU mesh: shader build failed - " + e.Message);
                ReleaseGpuMeshDeviceObjects();
                return false;
            }
        }

        private static bool EnsureMeshGrid(ID3D11Device device, int rows, int cols)
        {
            if (_meshRows == rows && _meshCols == cols && _meshVB != null && _meshIB != null && _meshHeightTex != null)
                return true;

            try
            {
                _meshVB?.Dispose(); _meshVB = null;
                _meshIB?.Dispose(); _meshIB = null;
                _meshLineIB?.Dispose(); _meshLineIB = null;
                    _meshAuxVB?.Dispose(); _meshAuxVB = null;
                _meshHeightSRV?.Dispose(); _meshHeightSRV = null;
                _meshHeightTex?.Dispose(); _meshHeightTex = null;
                _meshPhaseSRV?.Dispose(); _meshPhaseSRV = null;
                _meshPhaseTex?.Dispose(); _meshPhaseTex = null;
                _meshMarkSRV?.Dispose(); _meshMarkSRV = null;
                _meshMarkTex?.Dispose(); _meshMarkTex = null;

                // Curtain grid (Aether-style): per (row,col) a PAIR of vertices -
                // corner 0 = crest above the column, corner 1 = directly below at
                // the absolute plot bottom. float4(u, v, corner, 0), stride 16.
                var verts = new float[rows * cols * 2 * 4];
                for (int r = 0; r < rows; r++)
                {
                    float v = r / (float)(rows - 1);
                    for (int c = 0; c < cols; c++)
                    {
                        float u = c / (float)(cols - 1);
                        int o = (r * cols + c) * 8;
                        verts[o] = u; verts[o + 1] = v; verts[o + 2] = 0f; verts[o + 3] = 0f;
                        verts[o + 4] = u; verts[o + 5] = v; verts[o + 6] = 1f; verts[o + 7] = 0f;
                    }
                }
                _meshVB = device.CreateBuffer(verts, new BufferDescription((uint)(verts.Length * sizeof(float)), BindFlags.VertexBuffer, ResourceUsage.Immutable));

                // curtain quads: one cell [c,c+1] per row, top edge joins the two
                // crests, bottom edge lies on the absolute floor line
                uint[] idx = new uint[rows * (cols - 1) * 6];
                int ii = 0;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols - 1; c++)
                    {
                        uint tl = (uint)((r * cols + c) * 2);   // crest left
                        uint bl = tl + 1;                        // floor left
                        uint tr = tl + 2;                        // crest right
                        uint br = tr + 1;                        // floor right
                        idx[ii++] = tl; idx[ii++] = bl; idx[ii++] = tr;
                        idx[ii++] = tr; idx[ii++] = bl; idx[ii++] = br;
                    }
                }
                _meshIB = device.CreateBuffer(idx, new BufferDescription((uint)(idx.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));

                // crest hairline line-list: per row, connect adjacent CREST vertices
                // (even indices). Back row first, so the single hairline draw blends
                // back-to-front exactly like the old per-row submission.
                uint[] lidx = new uint[rows * (cols - 1) * 2];
                int li = 0;
                for (int r = rows - 1; r >= 0; r--)
                {
                    for (int c = 0; c < cols - 1; c++)
                    {
                        uint a = (uint)((r * cols + c) * 2);
                        lidx[li++] = a;
                        lidx[li++] = a + 2;
                    }
                }
                _meshLineIB = device.CreateBuffer(lidx, new BufferDescription((uint)(lidx.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));

                // dynamic aux vertices (grid floor + rails lines, then wall triangles),
                // stride float2 pos + float4 premultiplied colour
                uint auxVerts = AuxVertexBudget(rows);
                _meshAuxVB = device.CreateBuffer(new BufferDescription(auxVerts * 32, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

                _meshHeightTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)cols,
                    Height = (uint)rows,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                _meshHeightSRV = device.CreateShaderResourceView(_meshHeightTex);
                _meshPhaseTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)cols,
                    Height = (uint)rows,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                _meshPhaseSRV = device.CreateShaderResourceView(_meshPhaseTex);
                _meshMarkTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)cols,
                    Height = 2,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                _meshMarkSRV = device.CreateShaderResourceView(_meshMarkTex);

                if (_meshPaletteTex == null)
                {
                    _meshPaletteTex = device.CreateTexture2D(new Texture2DDescription()
                    {
                        Width = (uint)MeshPaletteSize,
                        Height = 1,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Dynamic,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.Write,
                    });
                    _meshPaletteSRV = device.CreateShaderResourceView(_meshPaletteTex);
                }

                _meshRows = rows;
                _meshCols = cols;
                _meshHeightScratch = new float[rows * cols];
                _meshPhaseScratch = new uint[rows * cols];
                _meshMarkScratch = new uint[cols * 2];   // row 0 north, row 1 south
                _meshAuxScratch = new float[AuxVertexBudget(rows) * 8];
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU mesh: grid build failed (" + rows + "x" + cols + ") - " + e.Message);
                ReleaseGpuMeshDeviceObjects();
                return false;
            }
        }

        private static bool EnsureMeshRTV(ID3D11Device device)
        {
            if (_meshRTV != null) return true;
            try
            {
                using (ID3D11Texture2D bb = _swapChain1.GetBuffer<ID3D11Texture2D>(0))
                    _meshRTV = device.CreateRenderTargetView(bb);
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU mesh: RTV creation failed - " + e.Message);
                return false;
            }
        }

        private struct MeshConstants
        {
            public float W, TargetH, BottomY, DepthSpan;   // 0-15
            public float FrontRidge, BackW, ZCurve, Haze;  // 16-31
            public float BgR, BgG, BgB, TexelX;            // 32-47 (float3 aligned to 16)
            public float TexelY;                           // 48-51
            public float ZBias;                            // 52-55
            public float FloorT;                           // 56-59
            public float CapPx;                            // 60-63
            public float OvR, OvG, OvB, OvA;               // 64-79
            public float Phase, Sphere, Hemi, PlotH;       // 80-95
            public float InD, InH, InK, LineA;             // 96-111
            public float GridA, Stand, Tilt, Pad2;         // 112-127
        }

        /// <summary>
        /// Draws the history surface as a GPU mesh into the swapchain backbuffer.
        /// Returns false when the frame should fall back to the D2D line renderer.
        /// Runs under _objDX2Lock BEFORE the D2D BeginDraw of the same frame; the D3D
        /// flush below guarantees queue ordering against the subsequent D2D pass.
        /// </summary>
        private static bool RenderGpuMesh3D()
        {
            // panes captured during the previous frame's D2D pass are rendered now
            int prevFrame = _meshFrameNo;
            _meshFrameNo++;

            bool any = false;
            for (int r = 1; r <= 2; r++)
            {
                if (!_meshParamsRX[r].Valid || _meshParamsRX[r].Frame != prevFrame) continue;
                _meshParams = _meshParamsRX[r];
                if (RenderGpuMesh3DPane())
                {
                    GpuMesh3DOwnerRX |= 1 << r;   // this pane's D2D 3D fallback is skipped
                    any = true;
                }
            }
            return any;
        }

        private static bool RenderGpuMesh3DPane()
        {
            if (!GpuMeshEnabled || m_eRenderPath != DXRenderPath.Hardware || _device == null || !_bDX2Setup)
                return false;
            Pan3DHistory hist3D = Hist3D(_meshParams.RX);
            if (!_pan3DEnabled || hist3D.Buf == null || hist3D.Count < 3 || !_meshParams.Valid)
                return false;
            // TX/RX must not change the renderer path. 3D GPU acceleration is governed only\n            // by the global 3D/GPU controls; MOX changes data/levels, not Hardware vs CPU.\n            if (_paused_display) return false;

            try
            {
                float[][] histBuf = hist3D.Buf;
                int histHead = hist3D.Head;
                int histCount = hist3D.Count;

                int linesToDraw = Math.Min(histCount, _pan3DLineCount);
                if (linesToDraw < 3) return false;
                int rowCount = linesToDraw - 1; // front-most stored row is covered by the live trace
                // grid sized by the Depth Lines setting (not by the history fill level) so two
                // panes, or a history still filling up, never force a grid rebuild per frame;
                // rows beyond rowCount stay flat and are not drawn
                int gridRows = Math.Max(2, Math.Min(_pan3DLineCount, Max3DHistoryLines) - 1);
                if (rowCount > gridRows) rowCount = gridRows;

                int yRange = _meshParams.GridMax - _meshParams.GridMin;
                if (yRange <= 0 || _meshParams.Cols < 2) return false;

                ID3D11DeviceContext dc = _device.ImmediateContext;
                if (!_meshShadersBuilt && !BuildMeshPipeline(_device)) return false;
                if (!EnsureMeshRTV(_device)) return false;
                if (!EnsureMeshGrid(_device, gridRows, _meshParams.Cols)) return false;
                if (!EnsureMeshDepth(_device)) return false;
                bool sphere = _pan3DSphere;

                // ---- temporal phase (identical to the D2D path) ----
                float phase = History3DPhase(hist3D);

                // ---- stream heights (raw strengths; lift happens in the shaders) ----
                float[] scratch = _meshHeightScratch;
                int cols = _meshCols;
                for (int r = 0; r < rowCount; r++)
                {
                    int line = r + 1;
                    float fIdx = line - phase;
                    int i0 = (int)fIdx;
                    if (i0 < 0) i0 = 0;
                    float w = fIdx - i0;
                    int i1 = i0 + 1;
                    if (i1 > linesToDraw - 1) { i1 = i0; w = 0f; }

                    int idx0 = (histHead - 1 - i0 + Max3DHistoryLines * 2) % Max3DHistoryLines;
                    int idx1 = (histHead - 1 - i1 + Max3DHistoryLines * 2) % Max3DHistoryLines;
                    float[] f0 = histBuf[idx0];
                    float[] f1 = histBuf[idx1];
                    int rowOff = r * cols;

                    if (f0 == null || f0.Length < cols)
                    {
                        for (int c = 0; c < cols; c++) scratch[rowOff + c] = 0f;
                        continue;
                    }

                    if (w > 0.0001f && f1 != null && f1.Length >= cols)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            float dBm = f0[c] + (f1[c] - f0[c]) * w;
                            float s = (dBm - _meshParams.GridMin) / (float)yRange;
                            scratch[rowOff + c] = s < 0f ? 0f : (s > 1f ? 1f : s);
                        }
                    }
                    else
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            float s = (f0[c] - _meshParams.GridMin) / (float)yRange;
                            scratch[rowOff + c] = s < 0f ? 0f : (s > 1f ? 1f : s);
                        }
                    }
                }

                Array.Clear(scratch, rowCount * cols, (gridRows - rowCount) * cols);

                MappedSubresource mapped = dc.Map(_meshHeightTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    fixed (float* src = scratch)
                    {
                        int copyBytes = cols * sizeof(float);
                        byte* dst = (byte*)mapped.DataPointer;
                        for (int r = 0; r < gridRows; r++)
                            Buffer.MemoryCopy(src + r * cols, dst + r * mapped.RowPitch, copyBytes, copyBytes);
                    }
                }
                dc.Unmap((ID3D11Resource)_meshHeightTex, 0);

                // ---- phase colours: nearest stored row per grid row (no blending of hues) ----
                bool phaseCols = _pan3DPhaseColours && _meshParams.RX == 1;
                bool spherePhase = sphere && _meshParams.RX == 1;   // southern hemisphere = phase view
                if (phaseCols || spherePhase)
                {
                    uint[] ps = _meshPhaseScratch;
                    uint[][] phRows = hist3D.Phase;
                    for (int r = 0; r < gridRows; r++)
                    {
                        int rowOff = r * cols;
                        uint[] src = null;
                        if (r < rowCount)
                        {
                            int i = (int)Math.Round(r + 1 - phase);
                            if (i < 0) i = 0;
                            if (i > linesToDraw - 1) i = linesToDraw - 1;
                            src = phRows[(histHead - 1 - i + Max3DHistoryLines * 2) % Max3DHistoryLines];
                        }
                        if (src != null && src.Length >= cols) Array.Copy(src, 0, ps, rowOff, cols);
                        else Array.Clear(ps, rowOff, cols);
                    }
                    MappedSubresource pm = dc.Map(_meshPhaseTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                    unsafe
                    {
                        fixed (uint* src = ps)
                        {
                            int copyBytes = cols * sizeof(uint);
                            byte* dst = (byte*)pm.DataPointer;
                            for (int r = 0; r < gridRows; r++)
                                Buffer.MemoryCopy(src + r * cols, dst + r * pm.RowPitch, copyBytes, copyBytes);
                        }
                    }
                    dc.Unmap((ID3D11Resource)_meshPhaseTex, 0);
                }

                if (sphere)
                {
                    FillSphereMarks(_meshMarkScratch, cols, _meshParams.RX, _meshParams.Decimation);
                    MappedSubresource mm = dc.Map(_meshMarkTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                    unsafe
                    {
                        fixed (uint* src = _meshMarkScratch)
                            for (int r = 0; r < 2; r++)
                                Buffer.MemoryCopy(src + r * cols, (byte*)mm.DataPointer + r * mm.RowPitch, cols * sizeof(uint), cols * sizeof(uint));
                    }
                    dc.Unmap((ID3D11Resource)_meshMarkTex, 0);
                }

                // ---- palette texture (same colour selection rules as SelectSurfaceColour) ----
                uint[] palette = ComputePaletteArray(yRange);
                UploadPalette(dc, palette);

                // ---- constants (hoisted so the CPU-side aux geometry mirrors vs_main) ----
                float bottomY = _meshParams.Shift + _meshParams.PlotH;
                float depthSpan = _meshParams.PlotH * _pan3DDepth;
                float frontRidge = _meshParams.PlotH * _pan3DRidgeHeight;
                float zCurve = Math.Max(0.05f, _pan3DZCurve);
                var cb = new MeshConstants
                {
                    W = _meshParams.W,
                    TargetH = _meshParams.TargetH,
                    BottomY = bottomY,
                    DepthSpan = depthSpan,
                    FrontRidge = frontRidge,
                    BackW = _pan3DPerspective,
                    ZCurve = zCurve,
                    Haze = _pan3DDepthFade,
                    BgR = m_cDX2_display_background_clear_colour.R,
                    BgG = m_cDX2_display_background_clear_colour.G,
                    BgB = m_cDX2_display_background_clear_colour.B,
                    TexelX = 1f / cols,
                    TexelY = 1f / gridRows,
                    FloorT = _pan3DFloorTransparency,
                    CapPx = _pan3DTopsOnly ? _pan3DTopsWidth : 0f,
                    OvR = m_cDX2_display_background_colour.R,
                    OvG = m_cDX2_display_background_colour.G,
                    OvB = m_cDX2_display_background_colour.B,
                    OvA = m_cDX2_display_background_colour.A,
                    Phase = phaseCols ? 1f : 0f,   // sphere: northern hemisphere only
                    Sphere = sphere ? (_pan3DSphereInside ? 2f : 1f) : 0f,
                    Hemi = 1f,
                    PlotH = _meshParams.PlotH,
                    InD = _pan3DSphereBack,
                    InH = _pan3DSphereBend,
                    InK = _pan3DSphereRecede,
                    LineA = _pan3DSphereLineAlpha,
                    GridA = _pan3DSphereGridAlpha,
                    Stand = _pan3DSphereStanding ? 1f : 0f,
                    Tilt = (_pan3DSphereStanding ? 90f - _pan3DSphereTilt : _pan3DSphereTilt) * (float)(Math.PI / 180.0),
                };
                MappedSubresource cbMap = dc.Map((ID3D11Resource)_meshCB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe { System.Runtime.CompilerServices.Unsafe.Write((void*)cbMap.DataPointer, cb); }
                dc.Unmap((ID3D11Resource)_meshCB, 0);

                dc.IASetInputLayout(_meshIL);
                dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                // CRITICAL: bind the render target to the output-merger stage -
                // without this every fragment is discarded (clear works regardless)
                dc.OMSetRenderTargets(new[] { _meshRTV }, null);

                if (_bitmapBackground != null)
                {
                    // skin image drawn by the shared backdrop step (once per frame
                    // across all mesh passes); repaint ONLY the plot strip with a
                    // scissored opaque quad so the 3D scene sits on flat background
                    EnsureGpuBackdrop(dc);

                    dc.RSSetState(_meshRSScissor);
                    dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, (int)_meshParams.Shift,
                        (int)displayTargetWidth, (int)_meshParams.Shift + (int)_meshParams.PlotH) });
                    // premultiplied blend: identical to opaque at 0% floor transparency
                    dc.OMSetBlendState(_meshBlend);
                    dc.VSSetShader(_meshClearVS);
                    dc.PSSetShader(_meshClearPS);
                    dc.VSSetConstantBuffer(0, _meshCB);
                    dc.PSSetConstantBuffer(0, _meshCB);   // ps_clear reads CB_Background / CB_FloorT
                    dc.IASetVertexBuffer(0, _meshClearVB, 8, 0);
                    dc.IASetIndexBuffer(_meshClearIB, Format.R32_UInt, 0);
                    dc.DrawIndexed(6, 0, 0);

                    // restore surface state
                    dc.VSSetShader(_meshVS);
                    dc.PSSetShader(_meshPS);
                    dc.OMSetBlendState(_meshBlend);
                    dc.RSSetState(_meshRS);
                    dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, 0,
                        (int)displayTargetWidth, (int)displayTargetHeight) });
                }
                else
                {
                    // no skin image: the shared backdrop step full-clears to the
                    // configured background colour (once per frame)
                    EnsureGpuBackdrop(dc);
                }

                // ---- flat pass: perspective grid floor + rails, then side walls.
                // Drawn BEFORE the surface so rows occlude them (D2D draw order). ----
                int auxCount = sphere ? 0 : FillAuxVertices(gridRows, cols, palette);
                if (auxCount > 0 && _meshAuxVB != null && _meshAuxScratch != null)
                {
                    MappedSubresource auxMap = dc.Map((ID3D11Resource)_meshAuxVB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                    unsafe
                    {
                        fixed (float* src = _meshAuxScratch)
                        {
                            uint copyBytes = (uint)auxCount * 32;
                            Buffer.MemoryCopy(src, (void*)auxMap.DataPointer, copyBytes, copyBytes);
                        }
                    }
                    dc.Unmap((ID3D11Resource)_meshAuxVB, 0);

                    dc.IASetInputLayout(_meshFlatIL);
                    dc.IASetVertexBuffer(0, _meshAuxVB, 32, 0);
                    dc.VSSetShader(_meshVSFlat);
                    dc.PSSetShader(_meshPSFlat);
                    dc.VSSetConstantBuffer(0, _meshCB);
                    dc.OMSetBlendState(_meshBlend);
                    dc.RSSetState(_meshRS);

                    dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.LineList);
                    dc.Draw(16u, 0u);   // 6 floor gridlines + 2 rails
                    if (_pan3DSideWalls && auxCount > 16)
                    {
                        dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                        dc.Draw((uint)(auxCount - 16), 16u);
                    }
                }

                // ---- curtains + crest hairlines: two draw calls for the whole surface.
                // The depth buffer (z = row depth) makes a nearer row's curtain occlude
                // farther outlines exactly like the old per-row fill/outline interleave;
                // hairlines carry a small z bias so their own curtain still covers them. ----
                dc.ClearDepthStencilView(_meshDSV, DepthStencilClearFlags.Depth, 1.0f, 0);
                dc.OMSetRenderTargets(new[] { _meshRTV }, _meshDSV);
                dc.IASetInputLayout(_meshIL);
                dc.IASetVertexBuffer(0, _meshVB, 16, 0);
                dc.VSSetShader(_meshVS);
                dc.VSSetConstantBuffer(0, _meshCB);
                dc.PSSetConstantBuffer(0, _meshCB);
                dc.PSSetShaderResource(0, _meshHeightSRV);
                dc.PSSetShaderResource(1, _meshPaletteSRV);
                dc.PSSetShaderResource(2, _meshPhaseSRV);
                dc.PSSetShaderResource(3, _meshMarkSRV);
                // the VS samples the height texture itself - needs its own SRV + sampler binding
                dc.VSSetShaderResource(0, _meshHeightSRV);
                dc.VSSetShaderResource(3, _meshMarkSRV);   // sphere: line columns stay flat
                dc.VSSetSamplers(0, new[] { _sampPoint });
                dc.PSSetSamplers(0, new[] { _sampPoint });
                dc.PSSetSamplers(1, new[] { _sampLinear });
                dc.OMSetBlendState(_meshBlend);
                dc.RSSetState(_meshRS);
                if (sphere && _pan3DSphereInside)
                {
                    // the phase hemisphere below the horizon reaches past the plot bottom
                    dc.RSSetState(_meshRSScissor);
                    dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, (int)_meshParams.Shift,
                        (int)displayTargetWidth, (int)_meshParams.Shift + (int)_meshParams.PlotH) });
                }
                dc.RSSetViewport(new Viewport(0f, 0f, displayTargetWidth, displayTargetHeight));

                uint quadsPerRow = (uint)(cols - 1) * 6;
                uint linesPerRow = (uint)(cols - 1) * 2;

                // northern hemisphere = levels, southern = phase (RX1); flat 3D = one pass
                for (int hemi = 0; hemi < (sphere ? 2 : 1); hemi++)
                {
                    if (sphere)
                    {
                        cb.Hemi = hemi == 0 ? 1f : -1f;
                        cb.Phase = hemi == 0 ? (phaseCols ? 1f : 0f) : (spherePhase ? 1f : 0f);
                        cb.ZBias = 0f;
                        cbMap = dc.Map((ID3D11Resource)_meshCB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                        unsafe { System.Runtime.CompilerServices.Unsafe.Write((void*)cbMap.DataPointer, cb); }
                        dc.Unmap((ID3D11Resource)_meshCB, 0);
                    }
                    // all curtains (opaque, depth write)
                    dc.OMSetDepthStencilState(_dssWrite, 0);
                    dc.PSSetShader(_meshPS);
                    dc.IASetIndexBuffer(_meshIB, Format.R32_UInt, 0);
                    dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                    dc.DrawIndexed(quadsPerRow * (uint)rowCount, 0, 0);

                    // all crest hairlines (blended, depth test only, back row first)
                    cb.ZBias = MeshLineZBias;
                    cbMap = dc.Map((ID3D11Resource)_meshCB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                    unsafe { System.Runtime.CompilerServices.Unsafe.Write((void*)cbMap.DataPointer, cb); }
                    dc.Unmap((ID3D11Resource)_meshCB, 0);
                    dc.OMSetDepthStencilState(_dssTestOnly, 0);
                    dc.PSSetShader(_meshPSLine);
                    dc.IASetIndexBuffer(_meshLineIB, Format.R32_UInt, 0);
                    dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.LineList);
                    dc.DrawIndexed(linesPerRow * (uint)rowCount, linesPerRow * (uint)(gridRows - rowCount), 0);
                }

                // leave no depth target bound for the waterfall mesh / D2D passes
                dc.OMSetDepthStencilState(null, 0);
                dc.OMSetRenderTargets(new[] { _meshRTV }, null);
                dc.Flush();

                if (!_meshFailedLogged)
                {
                    _meshFailedLogged = true;
                    Common.MeshDiagLog("GPU mesh surface active (" + rowCount + "x" + cols + ")");
                }
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU mesh render failed - falling back to D2D lines : " + e.Message);
                ReleaseGpuMeshDeviceObjects();
                ReleaseGpuMeshFrameState();
                return false;
            }
        }

        /// <summary>
        /// Waterfall sphere: per-column mark colours (BGRA, alpha = strength) from the
        /// positions the 2D overlays captured last frame (_3dFloor, pixel x): RX/TX filter
        /// band, frequency grid, TX filter / CW lines, VFO line and the dragged filter edge,
        /// later ones on top. The shaders tint the curtains and crests of those columns.
        /// </summary>
        private static void FillSphereMarks(uint[] m, int cols, int rx, int decimation)
        {
            Array.Clear(m, 0, cols * 2);
            Pan3DFloorMarkSet f = _3dFloor[rx == 2 ? 2 : 1];
            float dec = Math.Max(1, decimation);
            // row 0 = northern hemisphere, row 1 = southern (phase) - southOff skips row 1
            void Put(int c0, int c1, System.Drawing.Color c, float a, bool southOff = false)
            {
                if (c0 < 0) c0 = 0;
                if (c1 > cols - 1) c1 = cols - 1;
                uint v = ((uint)Math.Round(Math.Max(0f, Math.Min(1f, a)) * 255f) << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                for (int i = c0; i <= c1; i++) { m[i] = v; if (!southOff) m[cols + i] = v; }
            }
            // one column wide, alpha 1 = line (254/255 = grid line): the shaders draw only the line there
            void Line(float x, System.Drawing.Color c, float a = 1f)
            {
                int cc = (int)Math.Round(x / dec);
                Put(cc, cc, c, a);
            }

            if (f.HasFilter)
            {
                System.Drawing.Color fc = ((System.Drawing.SolidBrush)(f.FilterTx ? tx_filter_brush : display_filter_brush)).Color;
                Put((int)Math.Round(f.FilterL / dec), (int)Math.Round(f.FilterR / dec), fc, Math.Min(0.9f, Math.Max(0.35f, fc.A / 255f)), true);   // south: white lines only; < 1 = tint, not a line
            }
            // grid meridians: line colour lifted 40% toward white
            System.Drawing.Color gc = System.Drawing.Color.FromArgb(
                _pan3DLineColor.R + (255 - _pan3DLineColor.R) * 2 / 5,
                _pan3DLineColor.G + (255 - _pan3DLineColor.G) * 2 / 5,
                _pan3DLineColor.B + (255 - _pan3DLineColor.B) * 2 / 5);
            if (_pan3DSphereGridAlpha > 0f)   // 0 = hidden, signals show in those columns
                for (int i = 0; i < f.GridCount; i++)
                    Line(f.Grid[i], gc, 254f / 255f);
            if (f.HasTxLines)
            {
                // TX lines white: stand out against any palette colour
                Line(f.TxL, System.Drawing.Color.White);
                Line(f.TxR, System.Drawing.Color.White);
            }
            if (f.HasCwZero) Line(f.CwZeroX, cw_zero_pen.Color);
            if (f.HasCwTx) Line(f.CwTxX, System.Drawing.Color.White);
            if (f.HasVfo) Line(f.VfoX, System.Drawing.Color.Red);   // VFO: red
            if (f.HasFilter && f.EdgeHighlight != 0)
                Line(f.EdgeHighlight < 0 ? f.FilterL : f.FilterR, cw_zero_pen.Color);
        }

        /// <summary>
        /// Draws the skin background image through D2D immediately before the mesh
        /// pass, replicating the aspect-ratio logic of the normal frame so the
        /// image stays visible around the mesh trapezoid.
        /// </summary>
        private static void DrawSkinBackgroundPrepass()
        {
            try
            {
                System.Numerics.Matrix3x2 t = _d2dRenderTarget.Transform;
                t.Translation = m_pixelShift;
                _d2dRenderTarget.Transform = t;

                System.Drawing.RectangleF rectDest;
                if (_maintain_background_aspectratio)
                {
                    float imageWidth = _bitmapBackground.PixelSize.Width;
                    float imageHeight = _bitmapBackground.PixelSize.Height;
                    float aspectRatio = imageWidth / imageHeight;
                    float targetAspectRatio = displayTargetWidth / displayTargetHeight;

                    if (aspectRatio > targetAspectRatio)
                    {
                        float scaledHeight = displayTargetWidth / aspectRatio;
                        rectDest = new System.Drawing.RectangleF(0, (displayTargetHeight - scaledHeight) / 2, displayTargetWidth, scaledHeight);
                    }
                    else
                    {
                        float scaledWidth = displayTargetHeight * aspectRatio;
                        rectDest = new System.Drawing.RectangleF((displayTargetWidth - scaledWidth) / 2, 0, scaledWidth, displayTargetHeight);
                    }
                }
                else
                {
                    rectDest = new System.Drawing.RectangleF(0, 0, displayTargetWidth, displayTargetHeight);
                }

                _d2dRenderTarget.BeginDraw();
                _d2dRenderTarget.DrawBitmap(_bitmapBackground,
                    new Vortice.RawRectF(rectDest.X, rectDest.Y, rectDest.Right, rectDest.Bottom),
                    1f, BitmapInterpolationMode.Linear, null);
                _d2dRenderTarget.EndDraw();
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU mesh: background prepass failed - " + e.Message);
            }
        }

        /// <summary>Per-frame palette replicating SelectSurfaceColour priorities:
        /// waterfall sync > perceptual colormap > gradient > line colour brightness.
        /// Returns BGRA-packed uints (A&lt;&lt;24 | R&lt;&lt;16 | G&lt;&lt;8 | B).</summary>
        /// <summary>Per-rx waterfall-sync thresholds for the 3D surface colouring
        /// (shared by the GPU palette and the D2D fallback). Returns false when
        /// the threshold range is degenerate, which disables sync colouring.</summary>
        private static bool Get3DWfSyncThresholds(int rx, out float lo, out float hi)
        {
            // prefer the exact levels the waterfall painted with (incl. Waterfall Pro
            // Auto Threshold / Auto High); fall back below when no waterfall is drawn
            int r = rx == 2 ? 2 : 1;
            if (DateTime.UtcNow.Ticks - _3dWfThrTicks[r] < Gpu3DRowStaleTicks)
            {
                lo = _3dWfThrLow[r];
                hi = _3dWfThrHigh[r];
                if (hi - lo > 0) return true;
            }

            if (rx == 2)
            {
                lo = rx2_waterfall_low_threshold;
                hi = rx2_waterfall_high_threshold;
                if (rx2_waterfall_agc && !m_bRX2_spectrum_thresholds)
                    lo = _RX2waterfallPreviousMinValue - m_fWaterfallAGCOffsetRX2;
            }
            else
            {
                lo = waterfall_low_threshold;
                hi = waterfall_high_threshold;
                if (rx1_waterfall_agc && !m_bRX1_spectrum_thresholds)
                    lo = _RX1waterfallPreviousMinValue - m_fWaterfallAGCOffsetRX1;
            }
            return hi - lo > 0;
        }

        private static uint[] ComputePaletteArray(int yRange)
        {
            int grid_min = _meshParams.GridMin;
            var palette = new uint[MeshPaletteSize];

            // 3D palette is a GLOBAL visual setting. MOX may change the spectrum data
            // and amplitude scale, but must not switch the 3D surface to a TX/RX-specific
            // colour source. Build one normalized palette and apply it identically in RX/TX.
            bool useWaterfallSync = _pan3DWaterfallSync;
            float wfLowThreshold = 0f, wfHighThreshold = 0f;
            if (useWaterfallSync)
                useWaterfallSync = Get3DWfSyncThresholds(_meshParams.RX, out wfLowThreshold, out wfHighThreshold);

            int colorMapIdx = _pan3DColorMap;
            bool useColormap = colorMapIdx > 0 && !useWaterfallSync;
            if (useColormap && _colormapLUT == null) BuildColormapLUT();

            const int gradPaletteSize = 64;
            System.Drawing.Color[] gradPalette = null;
            bool useGradient = !useWaterfallSync && !useColormap &&
                m_bUseLinearGradient && console.SetupForm?.RX1GradPicker != null;
            if (useGradient)
            {
                try
                {
                    gradPalette = new System.Drawing.Color[gradPaletteSize];
                    for (int i = 0; i < gradPaletteSize; i++)
                    {
                        float t = (float)i / (gradPaletteSize - 1);
                        float dBm = grid_min + t * yRange;
                        gradPalette[i] = console.SetupForm.RX1GradPicker.GetColourForDBM(dBm);
                    }
                }
                catch
                {
                    useGradient = false;
                    gradPalette = null;
                }
            }

            for (int i = 0; i < MeshPaletteSize; i++)
            {
                float strength = i / (float)(MeshPaletteSize - 1);
                int R, G, B;

                if (useColormap)
                {
                    int ci = (int)(strength * 255f);
                    if (ci < 0) ci = 0; else if (ci > 255) ci = 255;
                    int o = ((colorMapIdx - 1) * 256 + ci) * 3;
                    R = _colormapLUT[o];
                    G = _colormapLUT[o + 1];
                    B = _colormapLUT[o + 2];
                }
                else if (useWaterfallSync)
                {
                    // Preserve the RX waterfall palette visually, but sample it by
                    // normalized strength. Never feed TX dBm values into RX thresholds.
                    float paletteDbm = wfLowThreshold + strength * (wfHighThreshold - wfLowThreshold);
                    if (_meshParams.RX == 2)
                        GetWaterfallColor(paletteDbm, wfLowThreshold, wfHighThreshold, _rx2_color_scheme,
                            rx2_waterfall_low_color, _rx2_waterfall_grad, _rx2_waterfall_grad_ok, out R, out G, out B);
                    else
                        GetWaterfallColor(paletteDbm, wfLowThreshold, wfHighThreshold, _rx1_color_scheme,
                            waterfall_low_color, _rx1_waterfall_grad, _rx1_waterfall_grad_ok, out R, out G, out B);
                }
                else if (useGradient)
                {
                    int pIdx = (int)(strength * (gradPaletteSize - 1));
                    if (pIdx < 0) pIdx = 0;
                    if (pIdx >= gradPaletteSize) pIdx = gradPaletteSize - 1;
                    R = gradPalette[pIdx].R;
                    G = gradPalette[pIdx].G;
                    B = gradPalette[pIdx].B;
                }
                else
                {
                    float bright = 0.25f + 0.75f * strength;
                    R = (int)(_pan3DLineColor.R * bright);
                    G = (int)(_pan3DLineColor.G * bright);
                    B = (int)(_pan3DLineColor.B * bright);
                }

                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;

                palette[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
            }
            return palette;
        }

        private static void UploadPalette(ID3D11DeviceContext dc, uint[] palette)
        {
            MappedSubresource map = dc.Map(_meshPaletteTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                fixed (uint* src = palette)
                {
                    uint copyBytes = MeshPaletteSize * sizeof(uint);
                    Buffer.MemoryCopy(src, (void*)map.DataPointer, copyBytes, copyBytes);
                }
            }
            dc.Unmap((ID3D11Resource)_meshPaletteTex, 0);
        }

        /// <summary>Vertex capacity of _meshAuxVB: grid floor + rails lines then wall triangles.</summary>
        private static uint AuxVertexBudget(int rows)
        {
            return 16u + (uint)Math.Max(0, rows - 1) * 12u;
        }

        /// <summary>
        /// Fills the managed staging array for the flat pass: 8 LineList pairs
        /// (6 perspective floor gridlines + 2 side rails) followed by the two side-wall
        /// triangle meshes. Geometry mirrors the D2D Tier-1 path exactly; colours are
        /// premultiplied by alpha CPU-side so the shared AlphaBlend state composites
        /// as SourceOver.
        /// </summary>
        private static int FillAuxVertices(int rows, int cols, uint[] palette)
        {
            float[] v = _meshAuxScratch;
            if (v == null) return 0;

            float W = _meshParams.W;
            float bottomY = _meshParams.Shift + _meshParams.PlotH;
            float depthSpan = _meshParams.PlotH * _pan3DDepth;
            float backW = _pan3DPerspective;

            float lr = _pan3DLineColor.R / 255f;
            float lg = _pan3DLineColor.G / 255f;
            float lb = _pan3DLineColor.B / 255f;
            float bgR = m_cDX2_display_background_clear_colour.R;
            float bgG = m_cDX2_display_background_clear_colour.G;
            float bgB = m_cDX2_display_background_clear_colour.B;

            int n = 0;
            void Emit(float x, float y, float a)
            {
                int o = n * 8;
                v[o] = x; v[o + 1] = y;
                v[o + 2] = lr * a; v[o + 3] = lg * a; v[o + 4] = lb * a; v[o + 5] = a;
                n++;
            }

            // --- perspective grid floor: 6 receding gridlines along smoothstep baselines ---
            const int gridCount = 5;
            for (int g = 0; g <= gridCount; g++)
            {
                float tg = g / (float)gridCount;
                float tsG = tg * tg * (3.0f - 2.0f * tg);
                float yG = bottomY - tsG * depthSpan;
                float wfG = 1.0f - tsG * (1.0f - backW);
                float insG = W * (1.0f - wfG) * 0.5f;
                float aG = 0.10f * (1.0f - tsG) + 0.03f;
                Emit(insG, yG, aG);
                Emit(W - insG, yG, aG);
            }

            // --- side rails: insets/baselines are linear in tS so rails are straight ---
            float backInset = W * (1.0f - backW) * 0.5f;
            Emit(0f, bottomY, 0.12f);
            Emit(backInset, bottomY - depthSpan, 0.12f);
            Emit(W, bottomY, 0.12f);
            Emit(W - backInset, bottomY - depthSpan, 0.12f);

            // --- side walls / end caps ---
            if (_pan3DSideWalls && rows >= 2)
            {
                // wall colour: front-row left-edge surface colour, darkened, blended
                // toward bg by mid fog — identical to the D2D path's flat wall brush
                float sE = rows > 0 ? _meshHeightScratch[0] : 0f;
                int pi = (int)(sE * 255f);
                if (pi < 0) pi = 0; else if (pi > 255) pi = 255;
                uint pe = palette[pi];
                float wr = ((pe >> 16) & 255) / 255f;
                float wg = ((pe >> 8) & 255) / 255f;
                float wb = (pe & 255) / 255f;
                float wh = Math.Min(1f, 0.5f * _pan3DDepthFade);   // FogFor(0.5)
                wr = wr * 0.32f * (1 - wh) + bgR * wh;
                wg = wg * 0.32f * (1 - wh) + bgG * wh;
                wb = wb * 0.32f * (1 - wh) + bgB * wh;

                void EmitWallTri(float ax, float ay, float bx, float by, float cx, float cy)
                {
                    int o = n * 8;
                    v[o] = ax; v[o + 1] = ay;
                    v[o + 2] = wr; v[o + 3] = wg; v[o + 4] = wb; v[o + 5] = 1f;
                    n++;
                    o = n * 8;
                    v[o] = bx; v[o + 1] = by;
                    v[o + 2] = wr; v[o + 3] = wg; v[o + 4] = wb; v[o + 5] = 1f;
                    n++;
                    o = n * 8;
                    v[o] = cx; v[o + 1] = cy;
                    v[o + 2] = wr; v[o + 3] = wg; v[o + 4] = wb; v[o + 5] = 1f;
                    n++;
                }

                // edge crest Y per row must match vs_main exactly (v = r/(rows-1))
                float EdgeX(int r, bool left)
                {
                    float vv = r / (float)(rows - 1);
                    float rwf = 1f - vv * (1f - backW);
                    float inset = W * (1f - rwf) * 0.5f;
                    return left ? inset : W - inset;
                }
                float EdgeY(int r, bool left)
                {
                    float vv = r / (float)(rows - 1);
                    float rwf = 1f - vv * (1f - backW);
                    float baseline = bottomY - vv * depthSpan;
                    float s = _meshHeightScratch[r * cols + (left ? 0 : cols - 1)];
                    if (s < 0f) s = 0f; else if (s > 1f) s = 1f;
                    float lift = (float)Math.Pow(s, Math.Max(0.05f, _pan3DZCurve));
                    float y = baseline - lift * (_meshParams.PlotH * _pan3DRidgeHeight) * rwf;
                    if (y < _meshParams.Shift) y = _meshParams.Shift;
                    if (y > bottomY) y = bottomY;
                    return y;
                }

                for (int side = 0; side < 2; side++)
                {
                    bool left = side == 0;
                    for (int r = 0; r < rows - 1; r++)
                    {
                        float xt = EdgeX(r, left), yt = EdgeY(r, left);
                        float xb = EdgeX(r + 1, left), yb = EdgeY(r + 1, left);
                        EmitWallTri(xt, yt, xb, yb, xt, bottomY);
                        EmitWallTri(xb, yb, xb, bottomY, xt, bottomY);
                    }
                }
            }
            return n;
        }

        #endregion
    }
}






