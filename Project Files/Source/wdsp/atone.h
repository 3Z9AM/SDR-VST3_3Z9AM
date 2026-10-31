/*  atone.h

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

/*  atone.h

Analog tone (local addition): the gentle treble roll-off of an analog receiver's audio chain,
flat to the corner, -6 dB/octave above, at most -depth dB. The filter itself is in Rust
(wdsprs/src/atone.rs); this is the RXA block around it. RX audio after the AGC, before BIN2.

*/

#ifndef _atone_h
#define _atone_h

#include "../wdsprs/wdsprs.h"

typedef struct _atone
{
	int run;
	int size;
	double* in;
	double* out;
	double rate;
	double corner;		// Hz where the roll-off starts
	double depth;		// dB, the most it takes off at the top
	double ripple;		// crystal-filter ripple across the RX filter, dB peak to peak
	double lo, hi;		// RX filter as audio Hz (for the ripple and skirts)
	double skirt;		// crystal skirts: dB down at the RX filter edges
	double warmth;		// AF saturation 0..1
	AnalogTone* rs;
} atone, *ATONE;

extern ATONE create_atone (int run, int size, double* in, double* out, double rate, double corner, double depth);

extern void destroy_atone (ATONE a);

extern void flush_atone (ATONE a);

extern void xatone (ATONE a);

extern void setBuffers_atone (ATONE a, double* in, double* out);

extern void setSamplerate_atone (ATONE a, int rate);

extern void setSize_atone (ATONE a, int size);

extern __declspec (dllexport) void SetRXAAnalogTone (int channel, int run, double corner, double depth, double ripple, double lo, double hi, double skirt, double warmth);

#endif
