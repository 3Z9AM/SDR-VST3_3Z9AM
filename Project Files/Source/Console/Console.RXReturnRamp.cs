//  Console.RXReturnRamp.cs
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
    // Receiver start for non-CW modes (Setup > DSP > Options, Setup.RXReturnRamp.cs): when a
    // receive channel is switched on - mainly coming back from TX - its input stays silent for
    // a short delay and then fades in. This hides the T/R relay transient and the moment the
    // VAC buffers re-settle. CW keeps its own fast values for QSK (set on the mode change).
    public partial class Console
    {
        private double _rxReturnDelaySec = 0.0;
        private double _rxReturnFadeSec = 0.010;

        public void SetRXReturnRamp(int delayMs, int fadeMs)
        {
            _rxReturnDelaySec = System.Math.Max(0, delayMs) / 1000.0;
            _rxReturnFadeSec = System.Math.Max(10, fadeMs) / 1000.0;

            if (!isCWMode(_rx1_dsp_mode)) setRXReturnRamp(0);
            if (!isCWMode(_rx2_dsp_mode)) setRXReturnRamp(2);
        }

        private static bool isCWMode(DSPMode m) => m == DSPMode.CWL || m == DSPMode.CWU;

        private void setRXReturnRamp(uint thread)
        {
            for (uint sub = 0; sub < 2; sub++)
            {
                WDSP.SetChannelTDelayUp(WDSP.id(thread, sub), _rxReturnDelaySec);
                WDSP.SetChannelTSlewUp(WDSP.id(thread, sub), _rxReturnFadeSec);
            }
        }
    }
}
