/*  atone.c

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

/*  atone.c

Analog tone (local addition), see atone.h. The filter is in Rust (wdsprs/src/atone.rs).

*/

#include "comm.h"

ATONE create_atone (int run, int size, double* in, double* out, double rate, double corner, double depth)
{
	ATONE a = (ATONE) malloc0 (sizeof (atone));
	a->run = run;
	a->size = size;
	a->in = in;
	a->out = out;
	a->rate = rate;
	a->corner = corner;
	a->depth = depth;
	a->rs = atone_create (rate, corner, depth);
	return a;
}

void destroy_atone (ATONE a)
{
	atone_destroy (a->rs);
	_aligned_free (a);
}

void flush_atone (ATONE a)
{
	atone_flush (a->rs);
}

void xatone (ATONE a)
{
	if (a->out != a->in)
		memcpy (a->out, a->in, a->size * sizeof (complex));
	if (a->run)
		atone_process (a->rs, a->out, a->size);
}

void setBuffers_atone (ATONE a, double* in, double* out)
{
	a->in = in;
	a->out = out;
}

void setSamplerate_atone (ATONE a, int rate)
{
	a->rate = rate;
	atone_set (a->rs, a->rate, a->corner, a->depth);
	atone_set_ripple (a->rs, a->rate, a->lo, a->hi, a->ripple);
	atone_flush (a->rs);
}

void setSize_atone (ATONE a, int size)
{
	a->size = size;
}

/********************************************************************************************************
*																										*
*											RXA Properties												*
*																										*
********************************************************************************************************/

// corner Hz, depth dB; ripple dB peak to peak across the RX filter given as audio lo..hi Hz
PORT
void SetRXAAnalogTone (int channel, int run, double corner, double depth, double ripple, double lo, double hi)
{
	ATONE a = rxa[channel].atone.p;
	EnterCriticalSection (&ch[channel].csDSP);
	if (run && !a->run) atone_flush (a->rs);
	a->run = run;
	a->corner = corner;
	a->depth = depth;
	a->ripple = ripple;
	a->lo = lo;
	a->hi = hi;
	atone_set (a->rs, a->rate, corner, depth);
	atone_set_ripple (a->rs, a->rate, lo, hi, ripple);
	LeaveCriticalSection (&ch[channel].csDSP);
}
