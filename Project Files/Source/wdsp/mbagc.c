/*  mbagc.c

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

/*  mbagc.c

Multiband RX AGC (local addition), see mbagc.h. The processing is in Rust (wdsprs/src/mbagc.rs).

*/

#include "comm.h"

MBAGC create_mbagc (int run, int size, double* in, double* out, double rate, double strength, double maxdb)
{
	MBAGC a = (MBAGC) malloc0 (sizeof (mbagc));
	a->run = run;
	a->size = size;
	a->in = in;
	a->out = out;
	a->rate = rate;
	a->strength = strength;
	a->maxdb = maxdb;
	a->rs = mbagc_create (rate, strength, maxdb);
	return a;
}

void destroy_mbagc (MBAGC a)
{
	mbagc_destroy (a->rs);
	_aligned_free (a);
}

void flush_mbagc (MBAGC a)
{
	mbagc_flush (a->rs);
}

void xmbagc (MBAGC a)
{
	if (a->out != a->in)
		memcpy (a->out, a->in, a->size * sizeof (complex));
	if (a->run)
		mbagc_process (a->rs, a->out, a->size);
}

void setBuffers_mbagc (MBAGC a, double* in, double* out)
{
	a->in = in;
	a->out = out;
}

void setSamplerate_mbagc (MBAGC a, int rate)
{
	a->rate = rate;
	mbagc_destroy (a->rs);					// frame size follows the rate
	a->rs = mbagc_create (a->rate, a->strength, a->maxdb);
	mbagc_set_band (a->rs, a->f_low, a->f_high);
}

void setSize_mbagc (MBAGC a, int size)
{
	a->size = size;
}

// new RX filter (from SetRXABandpassFreqs): new bands, the learning starts again
void setFreqs_mbagc (int channel, double f_low, double f_high)
{
	MBAGC a = rxa[channel].mbagc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	a->f_low = f_low;
	a->f_high = f_high;
	mbagc_set_band (a->rs, f_low, f_high);
	LeaveCriticalSection (&ch[channel].csDSP);
}

/********************************************************************************************************
*																										*
*											RXA Properties												*
*																										*
********************************************************************************************************/

// strength 0..1, maxdb 0..24
PORT
void SetRXAMultibandAGC (int channel, int run, double strength, double maxdb)
{
	MBAGC a = rxa[channel].mbagc.p;
	EnterCriticalSection (&ch[channel].csDSP);
	if (run && !a->run) mbagc_flush (a->rs);
	a->run = run;
	a->strength = strength;
	a->maxdb = maxdb;
	mbagc_set (a->rs, strength, maxdb);
	LeaveCriticalSection (&ch[channel].csDSP);
}
