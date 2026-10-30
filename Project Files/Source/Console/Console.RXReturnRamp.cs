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
