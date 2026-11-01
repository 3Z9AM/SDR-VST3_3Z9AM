/*  frm3DPanadapter.cs

This code/file can be found on GitHub : https://github.com/ramdor/Thetis

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

Portions Copyright (C) 2026 Daniel Poznanski, 3Z9AM:
Waterfall sphere and its inside (planetarium) view on the 3D panadapter.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    public partial class frm3DPanadapter : Form
    {
        private bool _initializing;
        private int _lineCountBeforeCap = -1;
        private ToolTip toolTip1;

        private CheckBoxTS chk3DWaterfallSync;
        private CheckBoxTS chk3DSideWalls;
        private LabelTS lbl3DXOffset;
        private NumericUpDownTS ud3DXOffset;
        private LabelTS lbl3DYOffset;
        private NumericUpDownTS ud3DYOffset;
        private LabelTS lbl3DRidgeHeight;
        private NumericUpDownTS ud3DRidgeHeight;
        private LabelTS lbl3DHaze;
        private NumericUpDownTS ud3DHaze;
        private LabelTS lbl3DLineCount;
        private NumericUpDownTS ud3DLineCount;
        private LabelTS lbl3DSpeed;
        private NumericUpDownTS ud3DSpeed;
        private LabelTS lbl3DLineColor;
        private ColorButton clrbtn3DLineColor;
        private CheckBoxTS chk3DFillColorEnable;
        private ColorButton clrbtn3DFillColor;
        private TrackBarTS tb3DFillOpacity;
        private LabelTS lbl3DColorMap;
        private ComboBoxTS combo3DColorMap;
        private LabelTS lbl3DZCurve;
        private NumericUpDownTS ud3DZCurve;
        private ButtonTS btn3DResetDefaults;
        private CheckBoxTS chk3DMedian;
        private CheckBoxTS chk3DSpeedGpu;
        private CheckBoxTS chk3DFloorMarks;
        private CheckBoxTS chk3DPeakHold;
        private CheckBoxTS chk3DPhaseColours;
        private CheckBoxTS chk3DTopsOnly;
        private CheckBoxTS chk3DSphere;
        private CheckBoxTS chk3DSphereInside;
        private CheckBoxTS chk3DSphereStanding;
        private CheckBoxTS chk3DSphereDoppler;
        private CheckBoxTS chk3DIonoDrift;
        private LabelTS lbl3DTopsWidth;
        private TrackBarTS tb3DTopsWidth;
        private LabelTS lbl3DSphereBack;
        private TrackBarTS tb3DSphereBack;
        private LabelTS lbl3DSphereBend;
        private TrackBarTS tb3DSphereBend;
        private LabelTS lbl3DSphereRecede;
        private TrackBarTS tb3DSphereRecede;
        private LabelTS lbl3DSphereLineAlpha;
        private TrackBarTS tb3DSphereLineAlpha;
        private LabelTS lbl3DSphereGridAlpha;
        private TrackBarTS tb3DSphereGridAlpha;
        private LabelTS lbl3DSphereTilt;
        private TrackBarTS tb3DSphereTilt;
        private LabelTS lbl3DFloorTransparency;
        private TrackBarTS tb3DFloorTransparency;

        public frm3DPanadapter()
        {
            _initializing = true;

            InitializeComponent();

            Common.RestoreForm(this, "3DPanadapter", false);
            Common.ForceFormOnScreen(this);
            if (chk3DSphereInside.Checked) InsideSeen = true;

            _initializing = false;

            PushAllSettings();

            ApplyRenderPathLimits();
        }

        public void ApplyRenderPathLimits()
        {
            bool bCap = Display.RenderPath == Display.DXRenderPath.WarpSoftware;

            _initializing = true;

            if (bCap)
            {
                if (ud3DLineCount.Maximum > Display.Max3DLinesSoftwareRender)
                {
                    if (ud3DLineCount.Value > Display.Max3DLinesSoftwareRender)
                        _lineCountBeforeCap = (int)ud3DLineCount.Value;
                    ud3DLineCount.Maximum = Display.Max3DLinesSoftwareRender;
                }
                toolTip1.SetToolTip(ud3DLineCount, "Number of historical spectrum traces receding into the distance.\nLimited to " + Display.Max3DLinesSoftwareRender + " while software rendering is active (WARP rasterises every stroke on the CPU).");
            }
            else
            {
                if (ud3DLineCount.Maximum == Display.Max3DLinesSoftwareRender)
                {
                    ud3DLineCount.Maximum = Display.Max3DHistoryLines;
                    if (_lineCountBeforeCap > 0)
                    {
                        ud3DLineCount.Value = _lineCountBeforeCap;
                        _lineCountBeforeCap = -1;
                    }
                }
                toolTip1.SetToolTip(ud3DLineCount, "Number of historical spectrum traces receding into the distance.");
            }

            _initializing = false;
        }

        private void InitializeComponent()
        {
            this.toolTip1 = new System.Windows.Forms.ToolTip();
            this.chk3DWaterfallSync = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSideWalls = new System.Windows.Forms.CheckBoxTS();
            this.lbl3DXOffset = new System.Windows.Forms.LabelTS();
            this.ud3DXOffset = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DYOffset = new System.Windows.Forms.LabelTS();
            this.ud3DYOffset = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DRidgeHeight = new System.Windows.Forms.LabelTS();
            this.ud3DRidgeHeight = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DHaze = new System.Windows.Forms.LabelTS();
            this.ud3DHaze = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DLineCount = new System.Windows.Forms.LabelTS();
            this.ud3DLineCount = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DSpeed = new System.Windows.Forms.LabelTS();
            this.ud3DSpeed = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DLineColor = new System.Windows.Forms.LabelTS();
            this.clrbtn3DLineColor = new Thetis.ColorButton();
            this.chk3DFillColorEnable = new System.Windows.Forms.CheckBoxTS();
            this.clrbtn3DFillColor = new Thetis.ColorButton();
            this.tb3DFillOpacity = new System.Windows.Forms.TrackBarTS();
            this.lbl3DColorMap = new System.Windows.Forms.LabelTS();
            this.combo3DColorMap = new System.Windows.Forms.ComboBoxTS();
            this.lbl3DZCurve = new System.Windows.Forms.LabelTS();
            this.chk3DMedian = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSpeedGpu = new System.Windows.Forms.CheckBoxTS();
            this.chk3DFloorMarks = new System.Windows.Forms.CheckBoxTS();
            this.chk3DPeakHold = new System.Windows.Forms.CheckBoxTS();
            this.chk3DTopsOnly = new System.Windows.Forms.CheckBoxTS();
            this.chk3DPhaseColours = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSphere = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSphereInside = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSphereStanding = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSphereDoppler = new System.Windows.Forms.CheckBoxTS();
            this.chk3DIonoDrift = new System.Windows.Forms.CheckBoxTS();
            this.lbl3DTopsWidth = new System.Windows.Forms.LabelTS();
            this.tb3DTopsWidth = new System.Windows.Forms.TrackBarTS();
            this.lbl3DSphereBack = new System.Windows.Forms.LabelTS();
            this.tb3DSphereBack = new System.Windows.Forms.TrackBarTS();
            this.lbl3DSphereBend = new System.Windows.Forms.LabelTS();
            this.tb3DSphereBend = new System.Windows.Forms.TrackBarTS();
            this.lbl3DSphereRecede = new System.Windows.Forms.LabelTS();
            this.tb3DSphereRecede = new System.Windows.Forms.TrackBarTS();
            this.lbl3DSphereLineAlpha = new System.Windows.Forms.LabelTS();
            this.tb3DSphereLineAlpha = new System.Windows.Forms.TrackBarTS();
            this.lbl3DSphereGridAlpha = new System.Windows.Forms.LabelTS();
            this.tb3DSphereGridAlpha = new System.Windows.Forms.TrackBarTS();
            this.lbl3DSphereTilt = new System.Windows.Forms.LabelTS();
            this.tb3DSphereTilt = new System.Windows.Forms.TrackBarTS();
            this.lbl3DFloorTransparency = new System.Windows.Forms.LabelTS();
            this.tb3DFloorTransparency = new System.Windows.Forms.TrackBarTS();
            this.ud3DZCurve = new System.Windows.Forms.NumericUpDownTS();
            this.btn3DResetDefaults = new System.Windows.Forms.ButtonTS();
            this.SuspendLayout();
            //
            // chk3DWaterfallSync
            //
            this.chk3DWaterfallSync.AutoSize = true;
            this.chk3DWaterfallSync.Checked = true;
            this.chk3DWaterfallSync.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chk3DWaterfallSync.Image = null;
            this.chk3DWaterfallSync.Location = new System.Drawing.Point(12, 12);
            this.chk3DWaterfallSync.Name = "chk3DWaterfallSync";
            this.chk3DWaterfallSync.Size = new System.Drawing.Size(105, 17);
            this.chk3DWaterfallSync.TabIndex = 0;
            this.chk3DWaterfallSync.Text = "Waterfall Sync";
            this.toolTip1.SetToolTip(this.chk3DWaterfallSync, "Use waterfall palette and levels for 3D colors (overrides Color and Colormap). A enabled Fill Color overrides this for the front fill.");
            this.chk3DWaterfallSync.CheckedChanged += new System.EventHandler(this.chk3DWaterfallSync_CheckedChanged);
            //
            // chk3DSideWalls
            //
            this.chk3DSideWalls.AutoSize = true;
            this.chk3DSideWalls.Checked = true;
            this.chk3DSideWalls.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chk3DSideWalls.Image = null;
            this.chk3DSideWalls.Location = new System.Drawing.Point(12, 34);
            this.chk3DSideWalls.Name = "chk3DSideWalls";
            this.chk3DSideWalls.Size = new System.Drawing.Size(89, 17);
            this.chk3DSideWalls.TabIndex = 1;
            this.chk3DSideWalls.Text = "Side Walls";
            this.toolTip1.SetToolTip(this.chk3DSideWalls, "Draw darkened left/right side walls for a solid object look.");
            this.chk3DSideWalls.CheckedChanged += new System.EventHandler(this.chk3DSideWalls_CheckedChanged);
            //
            // lbl3DXOffset (Perspective)
            //
            this.lbl3DXOffset.Image = null;
            this.lbl3DXOffset.Location = new System.Drawing.Point(12, 62);
            this.lbl3DXOffset.Name = "lbl3DXOffset";
            this.lbl3DXOffset.Size = new System.Drawing.Size(90, 16);
            this.lbl3DXOffset.TabIndex = 2;
            this.lbl3DXOffset.Text = "Perspective:";
            //
            // ud3DXOffset (Perspective: 0.1-1.0, default 0.60)
            //
            this.ud3DXOffset.DecimalPlaces = 2;
            this.ud3DXOffset.Increment = 0.05m;
            this.ud3DXOffset.Location = new System.Drawing.Point(120, 59);
            this.ud3DXOffset.Maximum = 1.00m;
            this.ud3DXOffset.Minimum = 0.10m;
            this.ud3DXOffset.Name = "ud3DXOffset";
            this.ud3DXOffset.Size = new System.Drawing.Size(56, 20);
            this.ud3DXOffset.TabIndex = 3;
            this.toolTip1.SetToolTip(this.ud3DXOffset, "How much back rows narrow (0.1=wide, 1.0=no narrowing).");
            this.ud3DXOffset.Value = 0.60m;
            this.ud3DXOffset.ValueChanged += new System.EventHandler(this.ud3DXOffset_ValueChanged);
            //
            // lbl3DYOffset (Depth)
            //
            this.lbl3DYOffset.Image = null;
            this.lbl3DYOffset.Location = new System.Drawing.Point(12, 84);
            this.lbl3DYOffset.Name = "lbl3DYOffset";
            this.lbl3DYOffset.Size = new System.Drawing.Size(90, 16);
            this.lbl3DYOffset.TabIndex = 4;
            this.lbl3DYOffset.Text = "Depth:";
            //
            // ud3DYOffset (Depth: 0.0-1.0, default 0.58)
            //
            this.ud3DYOffset.DecimalPlaces = 2;
            this.ud3DYOffset.Increment = 0.05m;
            this.ud3DYOffset.Location = new System.Drawing.Point(120, 81);
            this.ud3DYOffset.Maximum = 1.00m;
            this.ud3DYOffset.Minimum = 0.00m;
            this.ud3DYOffset.Name = "ud3DYOffset";
            this.ud3DYOffset.Size = new System.Drawing.Size(56, 20);
            this.ud3DYOffset.TabIndex = 5;
            this.toolTip1.SetToolTip(this.ud3DYOffset, "How far back rows rise (0=flat, 1.0=maximum depth).");
            this.ud3DYOffset.Value = 0.58m;
            this.ud3DYOffset.ValueChanged += new System.EventHandler(this.ud3DYOffset_ValueChanged);
            //
            // lbl3DRidgeHeight
            //
            this.lbl3DRidgeHeight.Image = null;
            this.lbl3DRidgeHeight.Location = new System.Drawing.Point(12, 106);
            this.lbl3DRidgeHeight.Name = "lbl3DRidgeHeight";
            this.lbl3DRidgeHeight.Size = new System.Drawing.Size(90, 16);
            this.lbl3DRidgeHeight.TabIndex = 6;
            this.lbl3DRidgeHeight.Text = "Ridge Ht:";
            //
            // ud3DRidgeHeight
            //
            this.ud3DRidgeHeight.DecimalPlaces = 2;
            this.ud3DRidgeHeight.Increment = 0.02m;
            this.ud3DRidgeHeight.Location = new System.Drawing.Point(120, 103);
            this.ud3DRidgeHeight.Maximum = 1.00m;
            this.ud3DRidgeHeight.Minimum = 0.10m;
            this.ud3DRidgeHeight.Name = "ud3DRidgeHeight";
            this.ud3DRidgeHeight.Size = new System.Drawing.Size(56, 20);
            this.ud3DRidgeHeight.TabIndex = 7;
            this.toolTip1.SetToolTip(this.ud3DRidgeHeight, "Height of the front ridge as a fraction of plot height (0.1-1.0).");
            this.ud3DRidgeHeight.Value = 0.46m;
            this.ud3DRidgeHeight.ValueChanged += new System.EventHandler(this.ud3DRidgeHeight_ValueChanged);
            //
            // lbl3DHaze
            //
            this.lbl3DHaze.Image = null;
            this.lbl3DHaze.Location = new System.Drawing.Point(12, 128);
            this.lbl3DHaze.Name = "lbl3DHaze";
            this.lbl3DHaze.Size = new System.Drawing.Size(90, 16);
            this.lbl3DHaze.TabIndex = 8;
            this.lbl3DHaze.Text = "Haze:";
            //
            // ud3DHaze
            //
            this.ud3DHaze.DecimalPlaces = 2;
            this.ud3DHaze.Increment = 0.02m;
            this.ud3DHaze.Location = new System.Drawing.Point(120, 125);
            this.ud3DHaze.Maximum = 1.00m;
            this.ud3DHaze.Minimum = 0.00m;
            this.ud3DHaze.Name = "ud3DHaze";
            this.ud3DHaze.Size = new System.Drawing.Size(56, 20);
            this.ud3DHaze.TabIndex = 9;
            this.toolTip1.SetToolTip(this.ud3DHaze, "Atmospheric haze strength - dims back rows (0=none, 1.0=full haze).");
            this.ud3DHaze.Value = 0.16m;
            this.ud3DHaze.ValueChanged += new System.EventHandler(this.ud3DHaze_ValueChanged);
            //
            // lbl3DLineCount
            //
            this.lbl3DLineCount.Image = null;
            this.lbl3DLineCount.Location = new System.Drawing.Point(12, 150);
            this.lbl3DLineCount.Name = "lbl3DLineCount";
            this.lbl3DLineCount.Size = new System.Drawing.Size(90, 16);
            this.lbl3DLineCount.TabIndex = 10;
            this.lbl3DLineCount.Text = "Depth Lines:";
            //
            // ud3DLineCount
            //
            this.ud3DLineCount.Location = new System.Drawing.Point(120, 147);
            this.ud3DLineCount.Maximum = new decimal(new int[] { 500, 0, 0, 0 });
            this.ud3DLineCount.Minimum = new decimal(new int[] { 2, 0, 0, 0 });
            this.ud3DLineCount.Name = "ud3DLineCount";
            this.ud3DLineCount.Size = new System.Drawing.Size(56, 20);
            this.ud3DLineCount.TabIndex = 11;
            this.toolTip1.SetToolTip(this.ud3DLineCount, "Number of historical spectrum traces receding into the distance.");
            this.ud3DLineCount.Value = new decimal(new int[] { 35, 0, 0, 0 });
            this.ud3DLineCount.ValueChanged += new System.EventHandler(this.ud3DLineCount_ValueChanged);
            //
            // lbl3DSpeed
            //
            this.lbl3DSpeed.Image = null;
            this.lbl3DSpeed.Location = new System.Drawing.Point(12, 172);
            this.lbl3DSpeed.Name = "lbl3DSpeed";
            this.lbl3DSpeed.Size = new System.Drawing.Size(90, 16);
            this.lbl3DSpeed.TabIndex = 12;
            this.lbl3DSpeed.Text = "Speed:";
            //
            // ud3DSpeed
            //
            this.ud3DSpeed.Location = new System.Drawing.Point(120, 169);
            this.ud3DSpeed.Maximum = new decimal(new int[] { 60, 0, 0, 0 });
            this.ud3DSpeed.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            this.ud3DSpeed.Name = "ud3DSpeed";
            this.ud3DSpeed.Size = new System.Drawing.Size(56, 20);
            this.ud3DSpeed.TabIndex = 13;
            this.toolTip1.SetToolTip(this.ud3DSpeed, "How fast new rows are pushed (1-60 FPS). Low values give a slow, cinematic scroll.");
            this.ud3DSpeed.Value = new decimal(new int[] { 25, 0, 0, 0 });
            this.ud3DSpeed.ValueChanged += new System.EventHandler(this.ud3DSpeed_ValueChanged);
            //
            // lbl3DLineColor
            //
            this.lbl3DLineColor.Image = null;
            this.lbl3DLineColor.Location = new System.Drawing.Point(12, 198);
            this.lbl3DLineColor.Name = "lbl3DLineColor";
            this.lbl3DLineColor.Size = new System.Drawing.Size(90, 16);
            this.lbl3DLineColor.TabIndex = 14;
            this.lbl3DLineColor.Text = "Color:";
            //
            // clrbtn3DLineColor
            //
            this.clrbtn3DLineColor.Automatic = "Automatic";
            this.clrbtn3DLineColor.Color = System.Drawing.Color.Aquamarine;
            this.clrbtn3DLineColor.Image = null;
            this.clrbtn3DLineColor.Location = new System.Drawing.Point(120, 195);
            this.clrbtn3DLineColor.MoreColors = "More Colors...";
            this.clrbtn3DLineColor.Name = "clrbtn3DLineColor";
            this.clrbtn3DLineColor.Selectable = true;
            this.clrbtn3DLineColor.Size = new System.Drawing.Size(40, 23);
            this.clrbtn3DLineColor.TabIndex = 15;
            this.toolTip1.SetToolTip(this.clrbtn3DLineColor, "Ridge outline color (used when waterfall palette is off).");
            this.clrbtn3DLineColor.Changed += new System.EventHandler(this.clrbtn3DLineColor_Changed);
            //
            // chk3DFillColorEnable
            //
            this.chk3DFillColorEnable.AutoSize = true;
            this.chk3DFillColorEnable.Image = null;
            this.chk3DFillColorEnable.Location = new System.Drawing.Point(12, 226);
            this.chk3DFillColorEnable.Name = "chk3DFillColorEnable";
            this.chk3DFillColorEnable.Size = new System.Drawing.Size(100, 17);
            this.chk3DFillColorEnable.TabIndex = 16;
            this.chk3DFillColorEnable.Text = "Fill Color";
            this.toolTip1.SetToolTip(this.chk3DFillColorEnable, "Draw the front wall as a solid panel in this color, like the 2D pan fill (opacity slider below). The 3D history receding behind it is not affected.");
            this.chk3DFillColorEnable.CheckedChanged += new System.EventHandler(this.chk3DFillColorEnable_CheckedChanged);
            //
            // clrbtn3DFillColor
            //
            this.clrbtn3DFillColor.Automatic = "Automatic";
            this.clrbtn3DFillColor.Color = System.Drawing.Color.Aquamarine;
            this.clrbtn3DFillColor.Image = null;
            this.clrbtn3DFillColor.Location = new System.Drawing.Point(120, 225);
            this.clrbtn3DFillColor.MoreColors = "More Colors...";
            this.clrbtn3DFillColor.Name = "clrbtn3DFillColor";
            this.clrbtn3DFillColor.Selectable = true;
            this.clrbtn3DFillColor.Size = new System.Drawing.Size(40, 23);
            this.clrbtn3DFillColor.TabIndex = 17;
            this.toolTip1.SetToolTip(this.clrbtn3DFillColor, "Front data fill color (3D only). Does not affect the data line.");
            this.clrbtn3DFillColor.Changed += new System.EventHandler(this.clrbtn3DFillColor_Changed);
            //
            // tb3DFillOpacity
            //
            this.tb3DFillOpacity.AutoSize = false;
            this.tb3DFillOpacity.Location = new System.Drawing.Point(165, 225);
            this.tb3DFillOpacity.Maximum = 100;
            this.tb3DFillOpacity.Minimum = 0;
            this.tb3DFillOpacity.Name = "tb3DFillOpacity";
            this.tb3DFillOpacity.Size = new System.Drawing.Size(60, 23);
            this.tb3DFillOpacity.SmallChange = 5;
            this.tb3DFillOpacity.LargeChange = 10;
            this.tb3DFillOpacity.TabIndex = 18;
            this.tb3DFillOpacity.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DFillOpacity.Value = 55;
            this.toolTip1.SetToolTip(this.tb3DFillOpacity, "Fill Color opacity (0-100%). Default 55.");
            this.tb3DFillOpacity.Scroll += new System.EventHandler(this.tb3DFillOpacity_Scroll);
            //
            // lbl3DColorMap
            //
            this.lbl3DColorMap.Image = null;
            this.lbl3DColorMap.Location = new System.Drawing.Point(12, 258);
            this.lbl3DColorMap.Name = "lbl3DColorMap";
            this.lbl3DColorMap.Size = new System.Drawing.Size(90, 16);
            this.lbl3DColorMap.TabIndex = 18;
            this.lbl3DColorMap.Text = "Colormap:";
            //
            // combo3DColorMap
            //
            this.combo3DColorMap.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.combo3DColorMap.Items.AddRange(new object[] {
                "Classic",
                "Turbo",
                "Viridis",
                "Inferno"});
            this.combo3DColorMap.Location = new System.Drawing.Point(120, 255);
            this.combo3DColorMap.Name = "combo3DColorMap";
            this.combo3DColorMap.Size = new System.Drawing.Size(100, 21);
            this.combo3DColorMap.TabIndex = 19;
            this.toolTip1.SetToolTip(this.combo3DColorMap, "Perceptual surface colormap (ignored while Waterfall Sync is checked).");
            this.combo3DColorMap.SelectedIndexChanged += new System.EventHandler(this.combo3DColorMap_SelectedIndexChanged);
            //
            // lbl3DZCurve (Floor Lift)
            //
            this.lbl3DZCurve.Image = null;
            this.lbl3DZCurve.Location = new System.Drawing.Point(12, 280);
            this.lbl3DZCurve.Name = "lbl3DZCurve";
            this.lbl3DZCurve.Size = new System.Drawing.Size(90, 16);
            this.lbl3DZCurve.TabIndex = 20;
            this.lbl3DZCurve.Text = "Floor Lift:";
            //
            // ud3DZCurve (Floor Lift: 0.05-1.0, default 0.90)
            //
            this.ud3DZCurve.DecimalPlaces = 2;
            this.ud3DZCurve.Increment = 0.05m;
            this.ud3DZCurve.Location = new System.Drawing.Point(120, 277);
            this.ud3DZCurve.Maximum = 1.00m;
            this.ud3DZCurve.Minimum = 0.05m;
            this.ud3DZCurve.Name = "ud3DZCurve";
            this.ud3DZCurve.Size = new System.Drawing.Size(56, 20);
            this.ud3DZCurve.TabIndex = 21;
            this.toolTip1.SetToolTip(this.ud3DZCurve, "Lifts the noise floor up into the surface (lower = more floor).");
            this.ud3DZCurve.Value = 0.90m;
            this.ud3DZCurve.ValueChanged += new System.EventHandler(this.ud3DZCurve_ValueChanged);
            //
            // chk3DMedian
            //
            this.chk3DMedian.AutoSize = true;
            this.chk3DMedian.Checked = true;
            this.chk3DMedian.Image = null;
            this.chk3DMedian.Location = new System.Drawing.Point(12, 304);
            this.chk3DMedian.Name = "chk3DMedian";
            this.chk3DMedian.Size = new System.Drawing.Size(90, 17);
            this.chk3DMedian.TabIndex = 23;
            this.chk3DMedian.Text = "Median 3-pt";
            this.toolTip1.SetToolTip(this.chk3DMedian, "3-point temporal median on new 3D rows (removes single-row impulses). Off = raw rows.");
            this.chk3DMedian.CheckedChanged += new System.EventHandler(this.chk3DMedian_CheckedChanged);
            //
            // chk3DSpeedGpu
            //
            this.chk3DSpeedGpu.AutoSize = true;
            this.chk3DSpeedGpu.Image = null;
            this.chk3DSpeedGpu.Location = new System.Drawing.Point(120, 304);
            this.chk3DSpeedGpu.Name = "chk3DSpeedGpu";
            this.chk3DSpeedGpu.Size = new System.Drawing.Size(90, 17);
            this.chk3DSpeedGpu.TabIndex = 24;
            this.chk3DSpeedGpu.Text = "Speed = GPU";
            this.toolTip1.SetToolTip(this.chk3DSpeedGpu, "Push one 3D row per GPU waterfall FFT row (same scroll rate as the GPU waterfall).\nUses the Speed value while no GPU rows are available.");
            this.chk3DSpeedGpu.CheckedChanged += new System.EventHandler(this.chk3DSpeedGpu_CheckedChanged);
            //
            // chk3DFloorMarks
            //
            this.chk3DFloorMarks.AutoSize = true;
            this.chk3DFloorMarks.Checked = true;
            this.chk3DFloorMarks.Image = null;
            this.chk3DFloorMarks.Location = new System.Drawing.Point(12, 326);
            this.chk3DFloorMarks.Name = "chk3DFloorMarks";
            this.chk3DFloorMarks.Size = new System.Drawing.Size(90, 17);
            this.chk3DFloorMarks.TabIndex = 25;
            this.chk3DFloorMarks.Text = "Floor marks";
            this.toolTip1.SetToolTip(this.chk3DFloorMarks, "Frequency grid, VFO line and filter band drawn in perspective on the 3D floor.");
            this.chk3DFloorMarks.CheckedChanged += new System.EventHandler(this.chk3DFloorMarks_CheckedChanged);
            //
            // chk3DPeakHold
            //
            this.chk3DPeakHold.AutoSize = true;
            this.chk3DPeakHold.Image = null;
            this.chk3DPeakHold.Location = new System.Drawing.Point(120, 326);
            this.chk3DPeakHold.Name = "chk3DPeakHold";
            this.chk3DPeakHold.Size = new System.Drawing.Size(90, 17);
            this.chk3DPeakHold.TabIndex = 26;
            this.chk3DPeakHold.Text = "Peak hold";
            this.toolTip1.SetToolTip(this.chk3DPeakHold, "Peak hold in depth: maximum of every frequency over the visible 3D history, drawn as a skyline on the back row.");
            this.chk3DPeakHold.CheckedChanged += new System.EventHandler(this.chk3DPeakHold_CheckedChanged);
            //
            // chk3DTopsOnly
            //
            this.chk3DTopsOnly.AutoSize = true;
            this.chk3DTopsOnly.Image = null;
            this.chk3DTopsOnly.Location = new System.Drawing.Point(12, 378);
            this.chk3DTopsOnly.Name = "chk3DTopsOnly";
            this.chk3DTopsOnly.Size = new System.Drawing.Size(90, 17);
            this.chk3DTopsOnly.TabIndex = 29;
            this.chk3DTopsOnly.Text = "Tops only";
            this.toolTip1.SetToolTip(this.chk3DTopsOnly, "GPU: paint only the top of each column in its gradient colour, the rest transparent.\nOff: whole column painted as a colour gradient from the floor up.");
            this.chk3DTopsOnly.CheckedChanged += new System.EventHandler(this.chk3DTopsOnly_CheckedChanged);
            //
            // chk3DPhaseColours
            //
            this.chk3DPhaseColours.AutoSize = true;
            this.chk3DPhaseColours.Image = null;
            this.chk3DPhaseColours.Location = new System.Drawing.Point(120, 378);
            this.chk3DPhaseColours.Name = "chk3DPhaseColours";
            this.chk3DPhaseColours.Size = new System.Drawing.Size(90, 17);
            this.chk3DPhaseColours.TabIndex = 33;
            this.chk3DPhaseColours.Text = "Phase view";
            this.toolTip1.SetToolTip(this.chk3DPhaseColours, "GPU, RX1: colour = phase of RX2 against RX1 (diversity), grey = no coherence.\nNeeds diversity on and the Diversity window open.");
            this.chk3DPhaseColours.CheckedChanged += new System.EventHandler(this.chk3DPhaseColours_CheckedChanged);
            //
            // chk3DSphere
            //
            this.chk3DSphere.AutoSize = true;
            this.chk3DSphere.Image = null;
            this.chk3DSphere.Location = new System.Drawing.Point(12, 430);
            this.chk3DSphere.Name = "chk3DSphere";
            this.chk3DSphere.Size = new System.Drawing.Size(110, 17);
            this.chk3DSphere.TabIndex = 34;
            this.chk3DSphere.Text = "Waterfall sphere";
            this.toolTip1.SetToolTip(this.chk3DSphere, "GPU: flattened globe - upper half = 3D waterfall (levels) from the equator up to the north pole,\nlower half = phase view RX2-RX1 (RX1, Diversity window open) from the equator down to the south pole.");
            this.chk3DSphere.CheckedChanged += new System.EventHandler(this.chk3DSphere_CheckedChanged);
            //
            // chk3DSphereInside
            //
            this.chk3DSphereInside.AutoSize = true;
            this.chk3DSphereInside.Image = null;
            this.chk3DSphereInside.Location = new System.Drawing.Point(130, 430);
            this.chk3DSphereInside.Name = "chk3DSphereInside";
            this.chk3DSphereInside.Size = new System.Drawing.Size(80, 17);
            this.chk3DSphereInside.TabIndex = 35;
            this.chk3DSphereInside.Text = "Inside view";
            this.toolTip1.SetToolTip(this.chk3DSphereInside, "Waterfall sphere seen from inside, like a planetarium: the observer hangs in the centre,\nthe equator (newest row) is the horizon, history rises above it, phase view below it.");
            this.chk3DSphereInside.CheckedChanged += new System.EventHandler(this.chk3DSphereInside_CheckedChanged);
            //
            // chk3DSphereStanding
            //
            this.chk3DSphereStanding.AutoSize = true;
            this.chk3DSphereStanding.Image = null;
            this.chk3DSphereStanding.Location = new System.Drawing.Point(12, 586);
            this.chk3DSphereStanding.Name = "chk3DSphereStanding";
            this.chk3DSphereStanding.Size = new System.Drawing.Size(95, 17);
            this.chk3DSphereStanding.TabIndex = 46;
            this.chk3DSphereStanding.Text = "Standing bars";
            this.toolTip1.SetToolTip(this.chk3DSphereStanding, "Inside view: signals as vertical walls facing the observer.\nOff: signals lie along the horizon toward the middle of the sphere.");
            this.chk3DSphereStanding.CheckedChanged += new System.EventHandler(this.chk3DSphereStanding_CheckedChanged);
            //
            // chk3DSphereDoppler
            //
            this.chk3DSphereDoppler.AutoSize = true;
            this.chk3DSphereDoppler.Image = null;
            this.chk3DSphereDoppler.Location = new System.Drawing.Point(120, 586);
            this.chk3DSphereDoppler.Name = "chk3DSphereDoppler";
            this.chk3DSphereDoppler.Size = new System.Drawing.Size(95, 17);
            this.chk3DSphereDoppler.TabIndex = 47;
            this.chk3DSphereDoppler.Text = "Doppler";
            this.toolTip1.SetToolTip(this.chk3DSphereDoppler, "Waterfall sphere lower half (also below the horizon in Inside view), or flat 3D with Phase view: RX1 frequency change against its 5 s mean instead of the RX2-RX1 phase.\nRed = up, green = steady, blue = down (full colour at +-1 Hz), grey = no steady carrier.");
            this.chk3DSphereDoppler.CheckedChanged += new System.EventHandler(this.chk3DSphereDoppler_CheckedChanged);
            //
            // chk3DIonoDrift
            //
            this.chk3DIonoDrift.AutoSize = true;
            this.chk3DIonoDrift.Image = null;
            this.chk3DIonoDrift.Location = new System.Drawing.Point(12, 638);
            this.chk3DIonoDrift.Name = "chk3DIonoDrift";
            this.chk3DIonoDrift.Size = new System.Drawing.Size(150, 17);
            this.chk3DIonoDrift.TabIndex = 48;
            this.chk3DIonoDrift.Text = "Ionosphere drift arrows";
            this.toolTip1.SetToolTip(this.chk3DIonoDrift, "Waterfall sphere: an arrow on the equator (Inside view: the horizon) at every steady carrier (RX1).\nDown (red) = frequency rising = the reflecting layer comes down; up (blue) = it rises; green dot = steady.\nMeasured against the last minute; the label gives the mean as vertical speed (vertical incidence).\nWorks with or without the Doppler colours; always on while diversity is enabled.");
            this.chk3DIonoDrift.CheckedChanged += new System.EventHandler(this.chk3DIonoDrift_CheckedChanged);
            //
            // lbl3DTopsWidth
            //
            this.lbl3DTopsWidth.Image = null;
            this.lbl3DTopsWidth.Location = new System.Drawing.Point(12, 404);
            this.lbl3DTopsWidth.Name = "lbl3DTopsWidth";
            this.lbl3DTopsWidth.Size = new System.Drawing.Size(105, 16);
            this.lbl3DTopsWidth.TabIndex = 30;
            this.lbl3DTopsWidth.Text = "Tops width: 3 px";
            //
            // tb3DTopsWidth
            //
            this.tb3DTopsWidth.AutoSize = false;
            this.tb3DTopsWidth.Location = new System.Drawing.Point(115, 401);
            this.tb3DTopsWidth.Maximum = 50;
            this.tb3DTopsWidth.Minimum = 1;
            this.tb3DTopsWidth.Name = "tb3DTopsWidth";
            this.tb3DTopsWidth.Size = new System.Drawing.Size(110, 23);
            this.tb3DTopsWidth.SmallChange = 1;
            this.tb3DTopsWidth.LargeChange = 5;
            this.tb3DTopsWidth.TabIndex = 31;
            this.tb3DTopsWidth.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DTopsWidth.Value = 3;
            this.toolTip1.SetToolTip(this.tb3DTopsWidth, "Tops only: thickness in pixels of the coloured top of each column (1-50 px).");
            this.tb3DTopsWidth.Scroll += new System.EventHandler(this.tb3DTopsWidth_Scroll);
            //
            // lbl3DSphereBack
            //
            this.lbl3DSphereBack.Image = null;
            this.lbl3DSphereBack.Location = new System.Drawing.Point(12, 456);
            this.lbl3DSphereBack.Name = "lbl3DSphereBack";
            this.lbl3DSphereBack.Size = new System.Drawing.Size(105, 16);
            this.lbl3DSphereBack.TabIndex = 36;
            this.lbl3DSphereBack.Text = "Observer back: 0.30";
            //
            // tb3DSphereBack
            //
            this.tb3DSphereBack.AutoSize = false;
            this.tb3DSphereBack.Location = new System.Drawing.Point(115, 453);
            this.tb3DSphereBack.Maximum = 90;
            this.tb3DSphereBack.Minimum = 0;
            this.tb3DSphereBack.Name = "tb3DSphereBack";
            this.tb3DSphereBack.Size = new System.Drawing.Size(110, 23);
            this.tb3DSphereBack.SmallChange = 5;
            this.tb3DSphereBack.LargeChange = 10;
            this.tb3DSphereBack.TabIndex = 37;
            this.tb3DSphereBack.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DSphereBack.Value = 30;
            this.toolTip1.SetToolTip(this.tb3DSphereBack, "Inside view: how far the observer hangs behind the sphere centre (0 = centre, 0.90 = near the back wall).");
            this.tb3DSphereBack.Scroll += new System.EventHandler(this.tb3DSphereBack_Scroll);
            //
            // lbl3DSphereBend
            //
            this.lbl3DSphereBend.Image = null;
            this.lbl3DSphereBend.Location = new System.Drawing.Point(12, 482);
            this.lbl3DSphereBend.Name = "lbl3DSphereBend";
            this.lbl3DSphereBend.Size = new System.Drawing.Size(105, 16);
            this.lbl3DSphereBend.TabIndex = 38;
            this.lbl3DSphereBend.Text = "Horizon bend: 0.30";
            //
            // tb3DSphereBend
            //
            this.tb3DSphereBend.AutoSize = false;
            this.tb3DSphereBend.Location = new System.Drawing.Point(115, 479);
            this.tb3DSphereBend.Maximum = 80;
            this.tb3DSphereBend.Minimum = -50;
            this.tb3DSphereBend.Name = "tb3DSphereBend";
            this.tb3DSphereBend.Size = new System.Drawing.Size(110, 23);
            this.tb3DSphereBend.SmallChange = 5;
            this.tb3DSphereBend.LargeChange = 10;
            this.tb3DSphereBend.TabIndex = 39;
            this.tb3DSphereBend.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DSphereBend.Value = 30;
            this.toolTip1.SetToolTip(this.tb3DSphereBend, "Inside view: horizon arc - the observer's height over the equator plane.\n0 = straight horizon, + = arc bulging up, - = arc sagging down.");
            this.tb3DSphereBend.Scroll += new System.EventHandler(this.tb3DSphereBend_Scroll);
            //
            // lbl3DSphereRecede
            //
            this.lbl3DSphereRecede.Image = null;
            this.lbl3DSphereRecede.Location = new System.Drawing.Point(12, 508);
            this.lbl3DSphereRecede.Name = "lbl3DSphereRecede";
            this.lbl3DSphereRecede.Size = new System.Drawing.Size(105, 16);
            this.lbl3DSphereRecede.TabIndex = 40;
            this.lbl3DSphereRecede.Text = "History: -2.0";
            //
            // tb3DSphereRecede
            //
            this.tb3DSphereRecede.AutoSize = false;
            this.tb3DSphereRecede.Location = new System.Drawing.Point(115, 505);
            this.tb3DSphereRecede.Maximum = 40;
            this.tb3DSphereRecede.Minimum = -40;
            this.tb3DSphereRecede.Name = "tb3DSphereRecede";
            this.tb3DSphereRecede.Size = new System.Drawing.Size(110, 23);
            this.tb3DSphereRecede.SmallChange = 1;
            this.tb3DSphereRecede.LargeChange = 5;
            this.tb3DSphereRecede.TabIndex = 41;
            this.tb3DSphereRecede.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DSphereRecede.Value = -20;
            this.toolTip1.SetToolTip(this.tb3DSphereRecede, "Inside view, history flow:\n- (left) = rows are born far away at the horizon and approach the observer, growing overhead\n+ (right) = older history recedes, crowding toward the top\n0 = rows evenly spaced on screen.");
            this.tb3DSphereRecede.Scroll += new System.EventHandler(this.tb3DSphereRecede_Scroll);
            //
            // lbl3DSphereLineAlpha
            //
            this.lbl3DSphereLineAlpha.Image = null;
            this.lbl3DSphereLineAlpha.Location = new System.Drawing.Point(12, 534);
            this.lbl3DSphereLineAlpha.Name = "lbl3DSphereLineAlpha";
            this.lbl3DSphereLineAlpha.Size = new System.Drawing.Size(105, 16);
            this.lbl3DSphereLineAlpha.TabIndex = 42;
            this.lbl3DSphereLineAlpha.Text = "Line opacity: 100%";
            //
            // tb3DSphereLineAlpha
            //
            this.tb3DSphereLineAlpha.AutoSize = false;
            this.tb3DSphereLineAlpha.Location = new System.Drawing.Point(115, 531);
            this.tb3DSphereLineAlpha.Maximum = 100;
            this.tb3DSphereLineAlpha.Minimum = 10;
            this.tb3DSphereLineAlpha.Name = "tb3DSphereLineAlpha";
            this.tb3DSphereLineAlpha.Size = new System.Drawing.Size(110, 23);
            this.tb3DSphereLineAlpha.SmallChange = 5;
            this.tb3DSphereLineAlpha.LargeChange = 10;
            this.tb3DSphereLineAlpha.TabIndex = 43;
            this.tb3DSphereLineAlpha.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DSphereLineAlpha.Value = 100;
            this.toolTip1.SetToolTip(this.tb3DSphereLineAlpha, "Waterfall sphere: opacity of the grid, VFO, TX and CW lines on the surface (10-100%).");
            this.tb3DSphereLineAlpha.Scroll += new System.EventHandler(this.tb3DSphereLineAlpha_Scroll);
            //
            // lbl3DSphereGridAlpha
            //
            this.lbl3DSphereGridAlpha.Image = null;
            this.lbl3DSphereGridAlpha.Location = new System.Drawing.Point(12, 560);
            this.lbl3DSphereGridAlpha.Name = "lbl3DSphereGridAlpha";
            this.lbl3DSphereGridAlpha.Size = new System.Drawing.Size(105, 16);
            this.lbl3DSphereGridAlpha.TabIndex = 44;
            this.lbl3DSphereGridAlpha.Text = "Freq. lines: 100%";
            //
            // tb3DSphereGridAlpha
            //
            this.tb3DSphereGridAlpha.AutoSize = false;
            this.tb3DSphereGridAlpha.Location = new System.Drawing.Point(115, 557);
            this.tb3DSphereGridAlpha.Maximum = 100;
            this.tb3DSphereGridAlpha.Minimum = 0;
            this.tb3DSphereGridAlpha.Name = "tb3DSphereGridAlpha";
            this.tb3DSphereGridAlpha.Size = new System.Drawing.Size(110, 23);
            this.tb3DSphereGridAlpha.SmallChange = 5;
            this.tb3DSphereGridAlpha.LargeChange = 10;
            this.tb3DSphereGridAlpha.TabIndex = 45;
            this.tb3DSphereGridAlpha.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DSphereGridAlpha.Value = 100;
            this.toolTip1.SetToolTip(this.tb3DSphereGridAlpha, "Waterfall sphere: opacity of the frequency grid lines (from the frequency scale marks).\n0% = hidden, signals show in those columns.");
            this.tb3DSphereGridAlpha.Scroll += new System.EventHandler(this.tb3DSphereGridAlpha_Scroll);
            //
            // lbl3DSphereTilt
            //
            this.lbl3DSphereTilt.Image = null;
            this.lbl3DSphereTilt.Location = new System.Drawing.Point(12, 612);
            this.lbl3DSphereTilt.Name = "lbl3DSphereTilt";
            this.lbl3DSphereTilt.Size = new System.Drawing.Size(105, 16);
            this.lbl3DSphereTilt.TabIndex = 47;
            this.lbl3DSphereTilt.Text = "Bar tilt: 0°";
            //
            // tb3DSphereTilt
            //
            this.tb3DSphereTilt.AutoSize = false;
            this.tb3DSphereTilt.Location = new System.Drawing.Point(115, 609);
            this.tb3DSphereTilt.Maximum = 90;
            this.tb3DSphereTilt.Minimum = 0;
            this.tb3DSphereTilt.Name = "tb3DSphereTilt";
            this.tb3DSphereTilt.Size = new System.Drawing.Size(110, 23);
            this.tb3DSphereTilt.SmallChange = 1;
            this.tb3DSphereTilt.LargeChange = 10;
            this.tb3DSphereTilt.TabIndex = 48;
            this.tb3DSphereTilt.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DSphereTilt.Value = 0;
            this.toolTip1.SetToolTip(this.tb3DSphereTilt, "Inside view: bar angle 0-90°. Lying bars tilt up from the horizon,\nstanding bars tilt from vertical toward the middle of the sphere.");
            this.tb3DSphereTilt.Scroll += new System.EventHandler(this.tb3DSphereTilt_Scroll);
            //
            // lbl3DFloorTransparency
            //
            this.lbl3DFloorTransparency.Image = null;
            this.lbl3DFloorTransparency.Location = new System.Drawing.Point(12, 352);
            this.lbl3DFloorTransparency.Name = "lbl3DFloorTransparency";
            this.lbl3DFloorTransparency.Size = new System.Drawing.Size(105, 16);
            this.lbl3DFloorTransparency.TabIndex = 27;
            this.lbl3DFloorTransparency.Text = "Floor transp.: 0%";
            //
            // tb3DFloorTransparency
            //
            this.tb3DFloorTransparency.AutoSize = false;
            this.tb3DFloorTransparency.Location = new System.Drawing.Point(115, 349);
            this.tb3DFloorTransparency.Maximum = 100;
            this.tb3DFloorTransparency.Minimum = 0;
            this.tb3DFloorTransparency.Name = "tb3DFloorTransparency";
            this.tb3DFloorTransparency.Size = new System.Drawing.Size(110, 23);
            this.tb3DFloorTransparency.SmallChange = 5;
            this.tb3DFloorTransparency.LargeChange = 10;
            this.tb3DFloorTransparency.TabIndex = 28;
            this.tb3DFloorTransparency.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DFloorTransparency.Value = 0;
            this.toolTip1.SetToolTip(this.tb3DFloorTransparency, "Floor transparency (0-100%): the lowest levels of the 3D surface become see-through so the spectral-area image shows through; signals stay opaque. GPU renderer only.");
            this.tb3DFloorTransparency.Scroll += new System.EventHandler(this.tb3DFloorTransparency_Scroll);
            //
            // btn3DResetDefaults
            //
            this.btn3DResetDefaults.Image = null;
            this.btn3DResetDefaults.Location = new System.Drawing.Point(12, 664);
            this.btn3DResetDefaults.Name = "btn3DResetDefaults";
            this.btn3DResetDefaults.Size = new System.Drawing.Size(208, 26);
            this.btn3DResetDefaults.TabIndex = 22;
            this.btn3DResetDefaults.Text = "Reset Defaults";
            this.toolTip1.SetToolTip(this.btn3DResetDefaults, "Reset all 3D panadapter settings to defaults.");
            this.btn3DResetDefaults.Click += new System.EventHandler(this.btn3DResetDefaults_Click);
            //
            // frm3DPanadapter
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(232, 702);
            this.Controls.Add(this.chk3DWaterfallSync);
            this.Controls.Add(this.chk3DSideWalls);
            this.Controls.Add(this.lbl3DXOffset);
            this.Controls.Add(this.ud3DXOffset);
            this.Controls.Add(this.lbl3DYOffset);
            this.Controls.Add(this.ud3DYOffset);
            this.Controls.Add(this.lbl3DRidgeHeight);
            this.Controls.Add(this.ud3DRidgeHeight);
            this.Controls.Add(this.lbl3DHaze);
            this.Controls.Add(this.ud3DHaze);
            this.Controls.Add(this.lbl3DLineCount);
            this.Controls.Add(this.ud3DLineCount);
            this.Controls.Add(this.lbl3DSpeed);
            this.Controls.Add(this.ud3DSpeed);
            this.Controls.Add(this.lbl3DLineColor);
            this.Controls.Add(this.clrbtn3DLineColor);
            this.Controls.Add(this.chk3DFillColorEnable);
            this.Controls.Add(this.clrbtn3DFillColor);
            this.Controls.Add(this.tb3DFillOpacity);
            this.Controls.Add(this.lbl3DColorMap);
            this.Controls.Add(this.combo3DColorMap);
            this.Controls.Add(this.lbl3DZCurve);
            this.Controls.Add(this.ud3DZCurve);
            this.Controls.Add(this.chk3DMedian);
            this.Controls.Add(this.chk3DSpeedGpu);
            this.Controls.Add(this.chk3DFloorMarks);
            this.Controls.Add(this.chk3DPeakHold);
            this.Controls.Add(this.chk3DTopsOnly);
            this.Controls.Add(this.chk3DPhaseColours);
            this.Controls.Add(this.chk3DSphere);
            this.Controls.Add(this.chk3DSphereInside);
            this.Controls.Add(this.chk3DSphereStanding);
            this.Controls.Add(this.chk3DSphereDoppler);
            this.Controls.Add(this.chk3DIonoDrift);
            this.Controls.Add(this.lbl3DTopsWidth);
            this.Controls.Add(this.tb3DTopsWidth);
            this.Controls.Add(this.lbl3DSphereBack);
            this.Controls.Add(this.tb3DSphereBack);
            this.Controls.Add(this.lbl3DSphereBend);
            this.Controls.Add(this.tb3DSphereBend);
            this.Controls.Add(this.lbl3DSphereRecede);
            this.Controls.Add(this.tb3DSphereRecede);
            this.Controls.Add(this.lbl3DSphereLineAlpha);
            this.Controls.Add(this.tb3DSphereLineAlpha);
            this.Controls.Add(this.lbl3DSphereGridAlpha);
            this.Controls.Add(this.tb3DSphereGridAlpha);
            this.Controls.Add(this.lbl3DSphereTilt);
            this.Controls.Add(this.tb3DSphereTilt);
            this.Controls.Add(this.lbl3DFloorTransparency);
            this.Controls.Add(this.tb3DFloorTransparency);
            this.Controls.Add(this.btn3DResetDefaults);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "frm3DPanadapter";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "3D Panadapter Settings";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.frm3DPanadapter_FormClosing);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void PushAllSettings()
        {
            Display.Pan3DWaterfallSync = chk3DWaterfallSync.Checked;
            Display.Pan3DSideWalls = chk3DSideWalls.Checked;
            Display.Pan3DPerspective = (float)ud3DXOffset.Value;
            Display.Pan3DDepth = (float)ud3DYOffset.Value;
            Display.Pan3DRidgeHeight = (float)ud3DRidgeHeight.Value;
            Display.Pan3DDepthFade = (float)ud3DHaze.Value;
            Display.Pan3DLineCount = (int)ud3DLineCount.Value;
            Display.Pan3DZCurve = (float)ud3DZCurve.Value;
            Display.Pan3DSpeed = (int)ud3DSpeed.Value;
            Display.Pan3DLineColor = clrbtn3DLineColor.Color;
            Display.Pan3DFillColorEnabled = chk3DFillColorEnable.Checked;
            Display.Pan3DFillColor = clrbtn3DFillColor.Color;
            Display.Pan3DFillAlpha = tb3DFillOpacity.Value / 100f;
            Display.Pan3DColorMap = Math.Max(0, combo3DColorMap.SelectedIndex);
            Display.Pan3DMedian = chk3DMedian.Checked;
            Display.Pan3DSpeedGpu = chk3DSpeedGpu.Checked;
            Display.Pan3DFloorMarks = chk3DFloorMarks.Checked;
            Display.Pan3DPeakHold = chk3DPeakHold.Checked;
            Display.Pan3DTopsOnly = chk3DTopsOnly.Checked;
            Display.Pan3DPhaseColours = chk3DPhaseColours.Checked;
            Display.Pan3DSphere = chk3DSphere.Checked;
            Display.Pan3DSphereInside = chk3DSphereInside.Checked;
            Display.Pan3DSphereStanding = chk3DSphereStanding.Checked;
            Display.Pan3DSphereDoppler = chk3DSphereDoppler.Checked;
            Display.Pan3DIonoDrift = chk3DIonoDrift.Checked;
            Display.Pan3DTopsWidth = tb3DTopsWidth.Value;
            UpdateTopsWidthLabel();
            Display.Pan3DSphereBack = tb3DSphereBack.Value / 100f;
            UpdateSphereBackLabel();
            Display.Pan3DSphereBend = tb3DSphereBend.Value / 100f;
            UpdateSphereBendLabel();
            Display.Pan3DSphereRecede = tb3DSphereRecede.Value / 10f;
            UpdateSphereRecedeLabel();
            Display.Pan3DSphereLineAlpha = tb3DSphereLineAlpha.Value / 100f;
            UpdateSphereLineAlphaLabel();
            Display.Pan3DSphereGridAlpha = tb3DSphereGridAlpha.Value / 100f;
            UpdateSphereGridAlphaLabel();
            Display.Pan3DSphereTilt = tb3DSphereTilt.Value;
            UpdateSphereTiltLabel();
            Display.Pan3DFloorTransparency = tb3DFloorTransparency.Value / 100f;
            UpdateFloorTransparencyLabel();
        }

        private void UpdateFloorTransparencyLabel()
        {
            lbl3DFloorTransparency.Text = "Floor transp.: " + tb3DFloorTransparency.Value + "%";
        }

        private void tb3DFloorTransparency_Scroll(object sender, EventArgs e)
        {
            UpdateFloorTransparencyLabel();
            if (_initializing) return;
            Display.Pan3DFloorTransparency = tb3DFloorTransparency.Value / 100f;
        }

        private void chk3DMedian_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DMedian = chk3DMedian.Checked;
        }

        private void chk3DSpeedGpu_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSpeedGpu = chk3DSpeedGpu.Checked;
        }

        private void chk3DFloorMarks_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFloorMarks = chk3DFloorMarks.Checked;
        }

        private void chk3DPeakHold_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DPeakHold = chk3DPeakHold.Checked;
        }

        private void chk3DTopsOnly_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DTopsOnly = chk3DTopsOnly.Checked;
        }

        private void chk3DPhaseColours_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DPhaseColours = chk3DPhaseColours.Checked;
        }

        private void chk3DSphere_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            // flat 3D and the sphere keep their own settings: park the mode being left,
            // bring back the other one (first sphere use = ApplySpherePreset)
            bool sphere = chk3DSphere.Checked;
            Common.SaveForm(this, sphere ? "3DPanadapterFlat" : "3DPanadapterSphere");
            string table = sphere ? "3DPanadapterSphere" : "3DPanadapterFlat";
            _initializing = true;
            if (DB.GetVars(table).Count > 0)
            {
                Point loc = Location;
                Common.RestoreForm(this, table, false);
                Location = loc;
            }
            else if (sphere)
                ApplySpherePreset();
            chk3DSphere.Checked = sphere;
            _initializing = false;
            PushAllSettings();
        }

        private void chk3DSphereInside_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            if (chk3DSphereInside.Checked && !InsideSeen)
            {
                _initializing = true;
                ApplyDefaultSphere(k => Array.IndexOf(InsideKeys, k) >= 0);
                _initializing = false;
                InsideSeen = true;
                PushAllSettings();
            }
            Display.Pan3DSphereInside = chk3DSphereInside.Checked;
        }

        private void chk3DSphereStanding_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSphereStanding = chk3DSphereStanding.Checked;
        }

        private void chk3DSphereDoppler_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSphereDoppler = chk3DSphereDoppler.Checked;
        }

        private void chk3DIonoDrift_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DIonoDrift = chk3DIonoDrift.Checked;
        }

        private static readonly string[] InsideKeys = { "tb3DSphereBack", "tb3DSphereBend", "tb3DSphereRecede", "chk3DSphereStanding", "tb3DSphereTilt" };

        // the 3Z9AM station's sphere look (Console.DefaultLayout), the sphere's settings until changed
        private void ApplySpherePreset()
        {
            ApplyDefaultSphere(k => k != "Width" && k != "Height" && k != "chk3DSphere" && k != "chk3DSphereInside");
            chk3DSphereInside.Checked = false;
        }

        // restores the picked keys through a scratch table, RestoreForm knows every control type
        private void ApplyDefaultSphere(Func<string, bool> pick)
        {
            Dictionary<string, string> rows = Console.DefaultLookSphere().Where(kv => pick(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            DB.SaveVarsDictionary("3DPanadapterDefault", ref rows, true);
            Common.RestoreForm(this, "3DPanadapterDefault", false);
        }

        // first time inside view is turned on it gets the 3Z9AM inside settings
        private static bool InsideSeen
        {
            get { return DB.GetVarsDictionary("State").ContainsKey("Pan3DInsideSeen"); }
            set { Dictionary<string, string> d = new Dictionary<string, string> { { "Pan3DInsideSeen", "True" } }; DB.SaveVarsDictionary("State", ref d, true); }
        }

        private void UpdateTopsWidthLabel()
        {
            lbl3DTopsWidth.Text = "Tops width: " + tb3DTopsWidth.Value + " px";
        }

        private void UpdateSphereBackLabel()
        {
            lbl3DSphereBack.Text = "Observer back: " + (tb3DSphereBack.Value / 100f).ToString("0.00");
        }

        private void tb3DSphereBack_Scroll(object sender, EventArgs e)
        {
            UpdateSphereBackLabel();
            if (_initializing) return;
            Display.Pan3DSphereBack = tb3DSphereBack.Value / 100f;
        }

        private void UpdateSphereBendLabel()
        {
            lbl3DSphereBend.Text = "Horizon bend: " + (tb3DSphereBend.Value / 100f).ToString("0.00");
        }

        private void tb3DSphereBend_Scroll(object sender, EventArgs e)
        {
            UpdateSphereBendLabel();
            if (_initializing) return;
            Display.Pan3DSphereBend = tb3DSphereBend.Value / 100f;
        }

        private void UpdateSphereRecedeLabel()
        {
            lbl3DSphereRecede.Text = "History: " + (tb3DSphereRecede.Value / 10f).ToString("0.0");
        }

        private void UpdateSphereLineAlphaLabel()
        {
            lbl3DSphereLineAlpha.Text = "Line opacity: " + tb3DSphereLineAlpha.Value + "%";
        }

        private void UpdateSphereGridAlphaLabel()
        {
            lbl3DSphereGridAlpha.Text = "Freq. lines: " + tb3DSphereGridAlpha.Value + "%";
        }

        private void UpdateSphereTiltLabel()
        {
            lbl3DSphereTilt.Text = "Bar tilt: " + tb3DSphereTilt.Value + "°";
        }

        private void tb3DSphereTilt_Scroll(object sender, EventArgs e)
        {
            UpdateSphereTiltLabel();
            if (_initializing) return;
            Display.Pan3DSphereTilt = tb3DSphereTilt.Value;
        }

        private void tb3DSphereGridAlpha_Scroll(object sender, EventArgs e)
        {
            UpdateSphereGridAlphaLabel();
            if (_initializing) return;
            Display.Pan3DSphereGridAlpha = tb3DSphereGridAlpha.Value / 100f;
        }

        private void tb3DSphereLineAlpha_Scroll(object sender, EventArgs e)
        {
            UpdateSphereLineAlphaLabel();
            if (_initializing) return;
            Display.Pan3DSphereLineAlpha = tb3DSphereLineAlpha.Value / 100f;
        }

        private void tb3DSphereRecede_Scroll(object sender, EventArgs e)
        {
            UpdateSphereRecedeLabel();
            if (_initializing) return;
            Display.Pan3DSphereRecede = tb3DSphereRecede.Value / 10f;
        }

        private void tb3DTopsWidth_Scroll(object sender, EventArgs e)
        {
            UpdateTopsWidthLabel();
            if (_initializing) return;
            Display.Pan3DTopsWidth = tb3DTopsWidth.Value;
        }

        private void frm3DPanadapter_FormClosing(object sender, FormClosingEventArgs e)
        {
            Common.SaveForm(this, "3DPanadapter");

            if (e.CloseReason == CloseReason.UserClosing)
            {
                Hide();
                e.Cancel = true;
            }
        }

        private void chk3DWaterfallSync_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DWaterfallSync = chk3DWaterfallSync.Checked;
        }

        private void chk3DSideWalls_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSideWalls = chk3DSideWalls.Checked;
        }

        private void ud3DXOffset_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DPerspective = (float)ud3DXOffset.Value;
        }

        private void ud3DYOffset_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DDepth = (float)ud3DYOffset.Value;
        }

        private void ud3DRidgeHeight_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DRidgeHeight = (float)ud3DRidgeHeight.Value;
        }

        private void ud3DHaze_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DDepthFade = (float)ud3DHaze.Value;
        }

        private void ud3DLineCount_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            _lineCountBeforeCap = -1;
            Display.Pan3DLineCount = (int)ud3DLineCount.Value;
        }

        private void ud3DZCurve_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DZCurve = (float)ud3DZCurve.Value;
        }

        private void ud3DSpeed_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSpeed = (int)ud3DSpeed.Value;
        }

        private void clrbtn3DLineColor_Changed(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DLineColor = clrbtn3DLineColor.Color;
        }

        private void chk3DFillColorEnable_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFillColorEnabled = chk3DFillColorEnable.Checked;
        }

        private void clrbtn3DFillColor_Changed(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFillColor = clrbtn3DFillColor.Color;
        }

        private void tb3DFillOpacity_Scroll(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFillAlpha = tb3DFillOpacity.Value / 100f;
        }

        private void combo3DColorMap_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DColorMap = Math.Max(0, combo3DColorMap.SelectedIndex);
        }

        private void btn3DResetDefaults_Click(object sender, EventArgs e)
        {
            _initializing = true;

            chk3DWaterfallSync.Checked = true;
            chk3DSideWalls.Checked = true;
            ud3DXOffset.Value = 0.60m;
            ud3DYOffset.Value = 0.58m;
            ud3DRidgeHeight.Value = 0.46m;
            ud3DHaze.Value = 0.16m;
            ud3DLineCount.Value = 35;
            ud3DSpeed.Value = 25;
            ud3DZCurve.Value = 0.90m;
            clrbtn3DLineColor.Color = Color.Aquamarine;
            chk3DFillColorEnable.Checked = false;
            clrbtn3DFillColor.Color = Color.Aquamarine;
            tb3DFillOpacity.Value = 55;
            combo3DColorMap.SelectedIndex = 0;
            chk3DMedian.Checked = true;
            chk3DSpeedGpu.Checked = false;
            chk3DFloorMarks.Checked = true;
            chk3DPeakHold.Checked = false;
            chk3DTopsOnly.Checked = false;
            chk3DPhaseColours.Checked = false;
            chk3DSphere.Checked = false;
            chk3DSphereInside.Checked = false;
            chk3DSphereStanding.Checked = false;
            chk3DSphereDoppler.Checked = false;
            chk3DIonoDrift.Checked = false;
            tb3DSphereBack.Value = 30;
            tb3DSphereBend.Value = 30;
            tb3DSphereRecede.Value = -20;
            tb3DSphereLineAlpha.Value = 100;
            tb3DSphereGridAlpha.Value = 100;
            tb3DSphereTilt.Value = 0;
            tb3DTopsWidth.Value = 3;
            tb3DFloorTransparency.Value = 0;

            _initializing = false;

            PushAllSettings();
        }
    }
}
