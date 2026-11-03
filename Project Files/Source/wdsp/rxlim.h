/*  rxlim.h

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

/*  rxlim.h

RX peak limiter (local addition): the last block of RXA, after the panel (I/Q = left/right),
look-ahead 1 ms. The limiter itself is in Rust (wdsprs/src/limit.rs).

*/

#ifndef _rxlim_h
#define _rxlim_h

#include "../wdsprs/wdsprs.h"

typedef struct _rxlim
{
	int run;
	int size;
	double* in;
	double* out;
	double rate;
	double thresh;		// dBFS
	double release;		// ms
	Limiter* rs;
} rxlim, *RXLIM;

extern RXLIM create_rxlim (int run, int size, double* in, double* out, double rate, double thresh, double release);

extern void destroy_rxlim (RXLIM a);

extern void flush_rxlim (RXLIM a);

extern void xrxlim (RXLIM a);

extern void setBuffers_rxlim (RXLIM a, double* in, double* out);

extern void setSamplerate_rxlim (RXLIM a, int rate);

extern void setSize_rxlim (RXLIM a, int size);

extern __declspec (dllexport) void SetRXALimiter (int channel, int run, double thresh, double release);

#endif
