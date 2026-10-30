using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    // Soft (raised-cosine) high-audio filter edge for the RX and TX bandpass filters.
    // Controls live on Setup > DSP > Options; they are created before getOptions() so
    // the generic control walk saves/restores them like designer controls.
    public partial class Setup
    {
        private GroupBoxTS grpDSPSoftEdge;
        private LabelTS lblDSPSoftEdgeRX;
        private LabelTS lblDSPSoftEdgeTX;
        private TrackBarTS tbDSPSoftEdgeRX;
        private TrackBarTS tbDSPSoftEdgeTX;
        private LabelTS lblDSPSoftEdgeSdrRX, lblDSPSoftEdgeAnalogRX, lblDSPSoftEdgeSdrTX, lblDSPSoftEdgeAnalogTX;

        private const int SoftEdgeStepHz = 10;   // trackbar units -> Hz

        private GroupBoxTS grpDSPSoftEdgeCarrier;
        private TrackBarTS tbDSPSoftEdgeCarrierRX, tbDSPSoftEdgeCarrierTX;
        private LabelTS lblDSPSoftEdgeCarrierRX, lblDSPSoftEdgeCarrierTX;

        private void initSoftEdgeControls()
        {
            grpDSPSoftEdge = new GroupBoxTS();
            grpDSPSoftEdge.Text = "Soft Filter Edge (high audio side)";
            grpDSPSoftEdge.Location = new Point(419, 162);
            grpDSPSoftEdge.Size = new Size(299, 64);
            grpDSPSoftEdge.Name = "grpDSPSoftEdge";

            lblDSPSoftEdgeRX = endLabel("RX:", "lblDSPSoftEdgeRX", 6, 19, 26, ContentAlignment.TopLeft);
            lblDSPSoftEdgeSdrRX = endLabel("SDR", "lblDSPSoftEdgeSdrRX", 32, 19, 32, ContentAlignment.TopRight);
            lblDSPSoftEdgeAnalogRX = endLabel("ANALOG", "lblDSPSoftEdgeAnalogRX", 238, 19, 56, ContentAlignment.TopLeft);

            tbDSPSoftEdgeRX = new TrackBarTS();
            tbDSPSoftEdgeRX.AutoSize = false;
            tbDSPSoftEdgeRX.Location = new Point(66, 16);
            tbDSPSoftEdgeRX.Size = new Size(170, 20);
            tbDSPSoftEdgeRX.Minimum = 0;
            tbDSPSoftEdgeRX.Maximum = 50;
            tbDSPSoftEdgeRX.Value = 25;     // default: half way
            tbDSPSoftEdgeRX.SmallChange = 1;
            tbDSPSoftEdgeRX.LargeChange = 5;
            tbDSPSoftEdgeRX.TickStyle = TickStyle.None;
            tbDSPSoftEdgeRX.Name = "tbDSPSoftEdgeRX";
            tbDSPSoftEdgeRX.ValueChanged += tbDSPSoftEdgeRX_ValueChanged;

            lblDSPSoftEdgeTX = endLabel("TX:", "lblDSPSoftEdgeTX", 6, 41, 26, ContentAlignment.TopLeft);
            lblDSPSoftEdgeSdrTX = endLabel("SDR", "lblDSPSoftEdgeSdrTX", 32, 41, 32, ContentAlignment.TopRight);
            lblDSPSoftEdgeAnalogTX = endLabel("ANALOG", "lblDSPSoftEdgeAnalogTX", 238, 41, 56, ContentAlignment.TopLeft);

            tbDSPSoftEdgeTX = new TrackBarTS();
            tbDSPSoftEdgeTX.AutoSize = false;
            tbDSPSoftEdgeTX.Location = new Point(66, 38);
            tbDSPSoftEdgeTX.Size = new Size(170, 20);
            tbDSPSoftEdgeTX.Minimum = 0;
            tbDSPSoftEdgeTX.Maximum = 50;
            tbDSPSoftEdgeTX.Value = 25;     // default: half way
            tbDSPSoftEdgeTX.SmallChange = 1;
            tbDSPSoftEdgeTX.LargeChange = 5;
            tbDSPSoftEdgeTX.TickStyle = TickStyle.None;
            tbDSPSoftEdgeTX.Name = "tbDSPSoftEdgeTX";
            tbDSPSoftEdgeTX.ValueChanged += tbDSPSoftEdgeTX_ValueChanged;

            string tip = "Soft (raised-cosine) filter edge, from SDR (sharp, 0 Hz) to ANALOG (500 Hz soft roll-off). Only the high-audio edge is softened " +
                         "(upper edge on USB, lower on LSB); the carrier-side edge stays sharp. SSB only - CW, AM, FM and digital modes keep the sharp filter. " +
                         "The transition lies inside the filter, so the bandwidth never grows. Reduces edge ringing.";
            toolTip1.SetToolTip(tbDSPSoftEdgeRX, tip);
            toolTip1.SetToolTip(tbDSPSoftEdgeTX, tip);

            grpDSPSoftEdge.Controls.AddRange(new Control[] {
                lblDSPSoftEdgeRX, lblDSPSoftEdgeSdrRX, tbDSPSoftEdgeRX, lblDSPSoftEdgeAnalogRX,
                lblDSPSoftEdgeTX, lblDSPSoftEdgeSdrTX, tbDSPSoftEdgeTX, lblDSPSoftEdgeAnalogTX });
            tpDSPOptions.Controls.Add(grpDSPSoftEdge);

            // carrier-side (low audio) edge: 0..150 Hz, 0 = sharp; shortens the low-frequency ringing of long filters
            grpDSPSoftEdgeCarrier = new GroupBoxTS { Text = "Soft Edge (carrier side)", Name = "grpDSPSoftEdgeCarrier", Location = new Point(534, 56), Size = new Size(184, 60) };
            lblDSPSoftEdgeCarrierRX = endLabel("RX:", "lblDSPSoftEdgeCarrierRX", 6, 18, 26, ContentAlignment.TopLeft);
            lblDSPSoftEdgeCarrierTX = endLabel("TX:", "lblDSPSoftEdgeCarrierTX", 6, 38, 26, ContentAlignment.TopLeft);
            tbDSPSoftEdgeCarrierRX = carrierSlider("tbDSPSoftEdgeCarrierRX", 15);
            tbDSPSoftEdgeCarrierTX = carrierSlider("tbDSPSoftEdgeCarrierTX", 35);
            tbDSPSoftEdgeCarrierRX.ValueChanged += tbDSPSoftEdgeCarrierRX_ValueChanged;
            tbDSPSoftEdgeCarrierTX.ValueChanged += tbDSPSoftEdgeCarrierTX_ValueChanged;
            string tipC = "Soft (raised-cosine) carrier-side filter edge, from sharp (left) to soft (right). SSB only: the low-audio edge (lower on USB, upper on LSB). " +
                          "The transition lies inside the filter, so nothing reaches the other sideband; further right thins the lowest tones a little. " +
                          "Shortens the low-frequency ringing of long filters (4096 / Low Latency).";
            toolTip1.SetToolTip(tbDSPSoftEdgeCarrierRX, tipC);
            toolTip1.SetToolTip(tbDSPSoftEdgeCarrierTX, tipC);
            grpDSPSoftEdgeCarrier.Controls.AddRange(new Control[] {
                lblDSPSoftEdgeCarrierRX, tbDSPSoftEdgeCarrierRX,
                lblDSPSoftEdgeCarrierTX, tbDSPSoftEdgeCarrierTX });
            tpDSPOptions.Controls.Add(grpDSPSoftEdgeCarrier);
        }

        private static TrackBarTS carrierSlider(string name, int y)
        {
            return new TrackBarTS
            {
                AutoSize = false, Location = new Point(32, y), Size = new Size(144, 20),
                Minimum = 0, Maximum = 15, Value = 0, SmallChange = 1, LargeChange = 5,
                TickStyle = TickStyle.None, Name = name
            };
        }

        private static LabelTS endLabel(string text, string name, int x, int y, int w, ContentAlignment align)
        {
            return new LabelTS { Text = text, Name = name, Location = new Point(x, y), Size = new Size(w, 16), TextAlign = align };
        }

        private void tbDSPSoftEdgeRX_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            double hz = tbDSPSoftEdgeRX.Value * SoftEdgeStepHz;
            console.radio.GetDSPRX(0, 0).RXSoftEdge = hz;
            console.radio.GetDSPRX(0, 1).RXSoftEdge = hz;
            console.radio.GetDSPRX(1, 0).RXSoftEdge = hz;
        }

        private void tbDSPSoftEdgeTX_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPTX(0).TXSoftEdge = tbDSPSoftEdgeTX.Value * SoftEdgeStepHz;
        }

        private void tbDSPSoftEdgeCarrierRX_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            double hz = tbDSPSoftEdgeCarrierRX.Value * SoftEdgeStepHz;
            console.radio.GetDSPRX(0, 0).RXSoftEdgeCarrier = hz;
            console.radio.GetDSPRX(0, 1).RXSoftEdgeCarrier = hz;
            console.radio.GetDSPRX(1, 0).RXSoftEdgeCarrier = hz;
        }

        private void tbDSPSoftEdgeCarrierTX_ValueChanged(object sender, EventArgs e)
        {
            if (initializing) return;
            console.radio.GetDSPTX(0).TXSoftEdgeCarrier = tbDSPSoftEdgeCarrierTX.Value * SoftEdgeStepHz;
        }
    }
}
