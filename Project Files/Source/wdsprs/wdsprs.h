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
void atone_flush(AnalogTone* p);
// n complex samples (2 n doubles, I/Q interleaved) filtered in place
void atone_process(AnalogTone* p, double* buf, int n);

#endif
