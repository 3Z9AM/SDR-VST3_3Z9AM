/*  mbagc.h

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

/*  mbagc.h

Multiband RX AGC (local addition): after the broadband AGC (and APF / NR), before the analog tone.
ERB bands across the RX filter (2..16), STFT 512 @ 48 kHz (10.7 ms delay). The processing is in Rust (wdsprs/src/mbagc.rs).

*/

#ifndef _mbagc_h
#define _mbagc_h

#include "../wdsprs/wdsprs.h"

typedef struct _mbagc
{
	int run;
	int size;
	double* in;
	double* out;
	double rate;
	double strength;	// 0 = only protects from one dominating band, 1 = every band to its usual share
	double maxdb;		// most a band is raised / lowered
	double f_low, f_high;	// RX filter (signed Hz), sets the bands
	MbAgc* rs;
} mbagc, *MBAGC;

extern MBAGC create_mbagc (int run, int size, double* in, double* out, double rate, double strength, double maxdb);

extern void destroy_mbagc (MBAGC a);

extern void flush_mbagc (MBAGC a);

extern void xmbagc (MBAGC a);

extern void setBuffers_mbagc (MBAGC a, double* in, double* out);

extern void setSamplerate_mbagc (MBAGC a, int rate);

extern void setSize_mbagc (MBAGC a, int size);

extern void setFreqs_mbagc (int channel, double f_low, double f_high);

extern __declspec (dllexport) void SetRXAMultibandAGC (int channel, int run, double strength, double maxdb);

#endif
