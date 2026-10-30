/*  bin2.c

BIN2 (local addition): spectral binaural, see bin2.h.

The mono audio (I) goes through one complex FIR (fir_bin2 in fir.c) whose real part is the
left filter and whose imaginary part is the right one: out.I = left, out.Q = right. Both are
linear phase with the same length, so the two channels stay time aligned. Spatial mode
(fir_bin2_spatial, for speakers) is not linear phase and has a ~10.7 ms bulk delay.

*/

#include "comm.h"

#define BIN2_MIN_NC 2048	// ~23 Hz resolution at 48 kHz, ~21 ms delay

static double* impulse_bin2 (BIN2 a)
{
	// centre of the filter = both channels at unity; spatial keeps the same mean loudness
	double scale = sqrt (2.0) / (double)(2 * a->size);
	if (a->spatial)
		return fir_bin2_spatial (a->nc, a->rate, a->f_low, a->f_high, a->width, a->speaker, a->room, scale);
	return fir_bin2 (a->nc, a->rate, a->f_low, a->f_high, scale);
}

// new impulse outside the DSP lock (it takes FFTs), swapped in under it; only while BIN2 runs
static void rebuild_bin2 (int channel)
{
	BIN2 a = rxa[channel].bin2.p;
	if (a->run)
	{
		double* imp = impulse_bin2 (a);
		setImpulse_fircore (a->p, imp, 0);
		_aligned_free (imp);
		EnterCriticalSection (&ch[channel].csDSP);
		a->dirty = 0;
		setUpdate_fircore (a->p);
		LeaveCriticalSection (&ch[channel].csDSP);
	}
}

static void build_bin2 (BIN2 a, int update)
{
	double* imp = impulse_bin2 (a);
	setImpulse_fircore (a->p, imp, update);
	_aligned_free (imp);
	a->dirty = 0;
}

static void create_core_bin2 (BIN2 a)
{
	double* imp;
	a->nc = max (BIN2_MIN_NC, a->size);
	a->buff = (double *) malloc0 (a->size * sizeof (complex));
	imp = impulse_bin2 (a);
	a->p = create_fircore (a->size, a->buff, a->out, a->nc, 0, 16, imp);
	_aligned_free (imp);
	a->dirty = 0;
}

BIN2 create_bin2 (int size, double* in, double* out, double rate, double f_low, double f_high)
{
	BIN2 a = (BIN2) malloc0 (sizeof (bin2));
	a->size = size;
	a->in = in;
	a->out = out;
	a->rate = rate;
	a->f_low = f_low;
	a->f_high = f_high;
	a->width = 60.0;
	a->speaker = 30.0;
	a->room = 0.3;
	create_core_bin2 (a);
	return a;
}

void destroy_bin2 (BIN2 a)
{
	destroy_fircore (a->p);
	_aligned_free (a->buff);
	_aligned_free (a);
}

void flush_bin2 (BIN2 a)
{
	flush_fircore (a->p);
}

void xbin2 (BIN2 a, int binaural)
{
	int i;
	if (a->run && binaural)
	{
		for (i = 0; i < a->size; i++)
		{
			a->buff[2 * i + 0] = a->in[2 * i + 0];
			a->buff[2 * i + 1] = 0.0;
		}
		xfircore (a->p);
	}
	else if (a->out != a->in)
		memcpy (a->out, a->in, a->size * sizeof (complex));
}

void setBuffers_bin2 (BIN2 a, double* in, double* out)
{
	a->in = in;
	a->out = out;
	setBuffers_fircore (a->p, a->buff, a->out);
}

void setSamplerate_bin2 (BIN2 a, int rate)
{
	a->rate = rate;
	build_bin2 (a, 1);
}

void setSize_bin2 (BIN2 a, int size)
{
	// the buffer and possibly nc follow the size: rebuild the core (channel is stopped here)
	destroy_fircore (a->p);
	_aligned_free (a->buff);
	a->size = size;
	create_core_bin2 (a);
}

// called from SetRXABandpassFreqs (UI thread); the impulse is only rebuilt while BIN2 runs
void setFreqs_bin2 (int channel, double f_low, double f_high)
{
	BIN2 a = rxa[channel].bin2.p;
	EnterCriticalSection (&ch[channel].csDSP);
	a->f_low = f_low;
	a->f_high = f_high;
	a->dirty = 1;
	LeaveCriticalSection (&ch[channel].csDSP);
	rebuild_bin2 (channel);
}

/********************************************************************************************************
*																										*
*											RXA Properties												*
*																										*
********************************************************************************************************/

PORT
void SetRXABin2Run (int channel, int run)
{
	BIN2 a = rxa[channel].bin2.p;
	if (run && !a->run && a->dirty)
	{
		double* imp = impulse_bin2 (a);
		setImpulse_fircore (a->p, imp, 0);
		_aligned_free (imp);
		EnterCriticalSection (&ch[channel].csDSP);
		a->dirty = 0;
		setUpdate_fircore (a->p);
		LeaveCriticalSection (&ch[channel].csDSP);
	}
	EnterCriticalSection (&ch[channel].csDSP);
	if (run && !a->run) flush_fircore (a->p);
	a->run = run;
	LeaveCriticalSection (&ch[channel].csDSP);
}

// spatial = 1: frontal arc +-width deg for speakers at +-speaker deg, room 0..1 (fir_bin2_spatial)
PORT
void SetRXABin2Spatial (int channel, int spatial, double width, double speaker, double room)
{
	BIN2 a = rxa[channel].bin2.p;
	EnterCriticalSection (&ch[channel].csDSP);
	a->spatial = spatial;
	a->width = width;
	a->speaker = speaker;
	a->room = room;
	a->dirty = 1;
	LeaveCriticalSection (&ch[channel].csDSP);
	rebuild_bin2 (channel);
}
