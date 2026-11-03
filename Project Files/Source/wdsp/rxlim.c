/*  rxlim.c

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

/*  rxlim.c

RX peak limiter (local addition), see rxlim.h. The limiter is in Rust (wdsprs/src/limit.rs).

*/

#include "comm.h"

RXLIM create_rxlim (int run, int size, double* in, double* out, double rate, double thresh, double release)
{
	RXLIM a = (RXLIM) malloc0 (sizeof (rxlim));
	a->run = run;
	a->size = size;
	a->in = in;
	a->out = out;
	a->rate = rate;
	a->thresh = thresh;
	a->release = release;
	a->rs = limit_create (rate, thresh, release);
	return a;
}

void destroy_rxlim (RXLIM a)
{
	limit_destroy (a->rs);
	_aligned_free (a);
}

void flush_rxlim (RXLIM a)
{
	limit_flush (a->rs);
}

void xrxlim (RXLIM a)
{
	if (a->out != a->in)
		memcpy (a->out, a->in, a->size * sizeof (complex));
	if (a->run)
		limit_process (a->rs, a->out, a->size);
}

void setBuffers_rxlim (RXLIM a, double* in, double* out)
{
	a->in = in;
	a->out = out;
}

void setSamplerate_rxlim (RXLIM a, int rate)
{
	a->rate = rate;
	limit_set (a->rs, a->rate, a->thresh, a->release);
	limit_flush (a->rs);
}

void setSize_rxlim (RXLIM a, int size)
{
	a->size = size;
}

/********************************************************************************************************
*																										*
*											RXA Properties												*
*																										*
********************************************************************************************************/

// thresh dBFS (-20..0), release ms
PORT
void SetRXALimiter (int channel, int run, double thresh, double release)
{
	RXLIM a = rxa[channel].rxlim.p;
	EnterCriticalSection (&ch[channel].csDSP);
	if (run && !a->run) limit_flush (a->rs);
	a->run = run;
	a->thresh = thresh;
	a->release = release;
	limit_set (a->rs, a->rate, thresh, release);
	LeaveCriticalSection (&ch[channel].csDSP);
}
