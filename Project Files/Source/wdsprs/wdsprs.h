/*  wdsprs.h

Copyright (C) 2026 Daniel Poznanski, 3Z9AM

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
*/

// wdsprs: local WDSP additions in Rust (wdsprs/src), linked statically into wdsp.dll.
// Every function accepts a null pointer and then does nothing.
#ifndef _wdsprs_h
#define _wdsprs_h

// analog tone (src/atone.rs): flat to the corner, -6 dB/octave above, at most -depth dB
typedef struct AnalogTone AnalogTone;
AnalogTone* atone_create(double rate, double corner, double depth);
void atone_destroy(AnalogTone* p);
// new coefficients, the filter state is kept
void atone_set(AnalogTone* p, double rate, double corner, double depth);
// crystal-filter ripple across the RX filter (audio lo..hi Hz), peak to peak dB; 0 = none
void atone_set_ripple(AnalogTone* p, double rate, double lo, double hi, double ripple_db);
void atone_set_skirt(AnalogTone* p, double rate, double lo, double hi, double skirt_db);
void atone_set_warmth(AnalogTone* p, double rate, double warmth);
void atone_flush(AnalogTone* p);
// n complex samples (2 n doubles, I/Q interleaved) filtered in place
void atone_process(AnalogTone* p, double* buf, int n);

// RX peak limiter (src/limit.rs): look-ahead 1 ms, I/Q = left/right share one gain, never above thresh dBFS
typedef struct Limiter Limiter;
Limiter* limit_create(double rate, double thresh_db, double release_ms);
void limit_destroy(Limiter* p);
// new threshold / release; a new rate also flushes
void limit_set(Limiter* p, double rate, double thresh_db, double release_ms);
void limit_flush(Limiter* p);
// n complex samples (2 n doubles) in place, delayed by the look-ahead
void limit_process(Limiter* p, double* buf, int n);

// dual-loop AGC "pause hold" (src/agcfloor.rs): slow station level next to the WDSP AGC
typedef struct AgcFloor AgcFloor;
AgcFloor* agcfloor_create(double rate, double rise_db, double tau_s);
void agcfloor_destroy(AgcFloor* p);
void agcfloor_set(AgcFloor* p, double rate, double rise_db, double tau_s);
void agcfloor_rate(AgcFloor* p, double rate);
void agcfloor_flush(AgcFloor* p);
// peak = AGC look-ahead peak, volts = AGC level; returns the level the gain is computed from
double agcfloor_step(AgcFloor* p, double peak, double volts);

// multiband RX AGC (src/mbagc.rs): ERB bands across the RX filter after the broadband AGC, delay one STFT frame (512 @ 48 kHz)
typedef struct MbAgc MbAgc;
MbAgc* mbagc_create(double rate, double strength, double max_db);
void mbagc_destroy(MbAgc* p);
// strength 0 = only protect from one dominating band .. 1 = every band to its usual share
void mbagc_set(MbAgc* p, double strength, double max_db);
// RX filter edges, signed Hz as RXA has them (USB +, LSB -, AM across 0); new bands, learning restarts
void mbagc_set_band(MbAgc* p, double f_low, double f_high);
void mbagc_flush(MbAgc* p);
void mbagc_process(MbAgc* p, double* buf, int n);

#endif
