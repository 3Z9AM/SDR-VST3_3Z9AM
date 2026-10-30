/*  bin2.h

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

/*  bin2.h

BIN2 (local addition): spectral binaural. Spreads the audio across the stereo stage by
where it sits in the RX filter: lowest RF at the left, highest at the right (USB: low
tones left, high right; LSB: low tones right, high left). Only acts while the panel is
binaural (BIN on); the classic BIN is used when BIN2 is off.

*/

#ifndef _bin2_h
#define _bin2_h

typedef struct _bin2
{
	int run;
	int size;
	int nc;
	double* in;
	double* out;
	double* buff;			// I of the input, Q = 0
	double rate;
	double f_low, f_high;	// RX filter, Hz from the carrier (negative = LSB)
	int dirty;				// impulse not built for the current filter/rate/size
	int spatial;			// 1 = virtual sources on a frontal arc for stereo speakers (fir_bin2_spatial)
	double width;			// spatial: arc half width, deg
	double speaker;			// spatial: speaker angle from the centre, deg
	double room;			// spatial: early reflections 0..1
	FIRCORE p;
} bin2, *BIN2;

extern BIN2 create_bin2 (int size, double* in, double* out, double rate, double f_low, double f_high);

extern void destroy_bin2 (BIN2 a);

extern void flush_bin2 (BIN2 a);

extern void xbin2 (BIN2 a, int binaural);

extern void setBuffers_bin2 (BIN2 a, double* in, double* out);

extern void setSamplerate_bin2 (BIN2 a, int rate);

extern void setSize_bin2 (BIN2 a, int size);

extern void setFreqs_bin2 (int channel, double f_low, double f_high);

extern __declspec (dllexport) void SetRXABin2Run (int channel, int run);

extern __declspec (dllexport) void SetRXABin2Spatial (int channel, int spatial, double width, double speaker, double room);

#endif
