/*  test_bin2.c

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

// Test of the BIN2 left/right filter pair (fir.c: fir_bin2).
// Build and run: wdsp\tests\run_test_bin2.bat (prints PASS/FAIL, exit code 0 = pass).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

double* fir_bin2 (int N, double samplerate, double f_low, double f_high, double scale);
double* fir_bin2_spatial (int N, double samplerate, double f_low, double f_high,
	double width_deg, double speaker_deg, double room, double scale);
void bin2_ear (double w, double inc, double* re, double* im);

void* malloc0 (int size)
{
	void* p = _aligned_malloc (size, 16);
	if (p) memset (p, 0, size);
	return p;
}

static int failures = 0;
static const int N = 2048;
static const double FS = 48000.0;

// gain at f of the real (part = 0) or imaginary (part = 1) impulse
static double gain (const double* h, int part, double f)
{
	double re = 0, im = 0;
	for (int n = 0; n < N; n++)
	{
		re += h[2 * n + part] * cos (2 * 3.14159265358979 * f * n / FS);
		im -= h[2 * n + part] * sin (2 * 3.14159265358979 * f * n / FS);
	}
	return sqrt (re * re + im * im);
}

// p = expected pan 0 (left) .. 1 (right) at audio frequency f
static void check (const char* mode, double lo, double hi, double f, double p)
{
	double* h = fir_bin2 (N, FS, lo, hi, sqrt (2.0));
	double l = gain (h, 0, f), r = gain (h, 1, f);
	double wl = sqrt (2.0) * cos (1.5707963267949 * p), wr = sqrt (2.0) * sin (1.5707963267949 * p);
	int ok = fabs (l - wl) < 0.03 && fabs (r - wr) < 0.03;
	printf ("%s  %-4s %6.0f Hz: L %.3f R %.3f (want %.3f / %.3f)\n", ok ? "ok  " : "FAIL", mode, f, l, r, wl, wr);
	if (!ok) failures++;
	_aligned_free (h);
}

// ---- spatial (speakers) ----
static const double DEG = 3.14159265358979 / 180.0;
typedef struct { double re, im; } cx;
static cx cmul (cx a, cx b) { cx r = { a.re * b.re - a.im * b.im, a.re * b.im + a.im * b.re }; return r; }
static cx cadd (cx a, cx b) { cx r = { a.re + b.re, a.im + b.im }; return r; }
static cx cdiv (cx a, cx b) { double d = b.re * b.re + b.im * b.im; cx r = { (a.re * b.re + a.im * b.im) / d, (a.im * b.re - a.re * b.im) / d }; return r; }
static cx ear (double f, double inc) { cx h; bin2_ear (2 * 3.14159265358979 * f, inc, &h.re, &h.im); return h; }

static cx dft (const double* h, int part, double f)
{
	cx r = { 0, 0 };
	for (int n = 0; n < N; n++)
	{
		r.re += h[2 * n + part] * cos (2 * 3.14159265358979 * f * n / FS);
		r.im -= h[2 * n + part] * sin (2 * 3.14159265358979 * f * n / FS);
	}
	return r;
}

// ears from speakers at +-spk: the interaural ratio must match a real source at az (deg)
static void check_spatial (const char* mode, double lo, double hi, double f, double az)
{
	const double spk = 30.0 * DEG;
	double* h = fir_bin2_spatial (N, FS, lo, hi, 60.0, 30.0, 0.0, 1.0);
	cx sl = dft (h, 0, f), sr = dft (h, 1, f);
	cx hi_ = ear (f, 90 * DEG - spk), hc = ear (f, 90 * DEG + spk);
	cx el = cadd (cmul (hi_, sl), cmul (hc, sr)), er = cadd (cmul (hc, sl), cmul (hi_, sr));
	cx got = cdiv (el, er), want = cdiv (ear (f, (az + 90) * DEG), ear (f, (az - 90) * DEG));
	double gdb = 10 * log10 ((got.re * got.re + got.im * got.im) / (want.re * want.re + want.im * want.im));
	double dph = atan2 (got.im, got.re) - atan2 (want.im, want.re);
	while (dph > 3.14159265358979) dph -= 2 * 3.14159265358979;
	while (dph < -3.14159265358979) dph += 2 * 3.14159265358979;
	int ok = fabs (gdb) < 1.0 && fabs (dph) < 0.2;
	printf ("%s  %-4s %6.0f Hz az %+5.1f: ILD error %+.2f dB, IPD error %+.3f rad\n", ok ? "ok  " : "FAIL", mode, f, az, gdb, dph);
	if (!ok) failures++;
	_aligned_free (h);
}

static void check_spatial_misc (void)
{
	// loudness over the filter like BIN2: mean |L|^2 + |R|^2 = 1 at scale 1
	double* h = fir_bin2_spatial (N, FS, 100, 2800, 60.0, 30.0, 0.3, 1.0);
	double p = 0;
	int n = 0;
	for (double f = 150; f <= 2750; f += 50, n++)
	{
		cx l = dft (h, 0, f), r = dft (h, 1, f);
		p += l.re * l.re + l.im * l.im + r.re * r.re + r.im * r.im;
	}
	p /= n;
	int ok = fabs (p - 1.0) < 0.15;
	printf ("%s  loudness mean |L|^2+|R|^2 = %.3f (want 1)\n", ok ? "ok  " : "FAIL", p);
	if (!ok) failures++;

	// room: energy after the direct part (bulk delay ~10.7 ms + 2.5 ms) grows with reflections
	double* dry = fir_bin2_spatial (N, FS, 100, 2800, 60.0, 30.0, 0.0, 1.0);
	int from = (int)(0.0132 * FS);
	double ew = 0, ed = 0;
	for (int i = from; i < N; i++)
	{
		ew += h[2 * i] * h[2 * i] + h[2 * i + 1] * h[2 * i + 1];
		ed += dry[2 * i] * dry[2 * i] + dry[2 * i + 1] * dry[2 * i + 1];
	}
	ok = ew > 5.0 * ed;
	printf ("%s  room: late energy %.2e vs dry %.2e\n", ok ? "ok  " : "FAIL", ew, ed);
	if (!ok) failures++;
	_aligned_free (h);
	_aligned_free (dry);
}

int main (void)
{
	// spatial, USB 100..2800, arc +-60: 1000 Hz -> p 0.33 (az -20), 2000 -> 0.70 (+24), 2530 -> 0.9 (+48)
	check_spatial ("USB", 100, 2800, 1000, (2 * (900.0 / 2700) - 1) * 60);
	check_spatial ("USB", 100, 2800, 2000, (2 * (1900.0 / 2700) - 1) * 60);
	check_spatial ("USB", 100, 2800, 2530, (2 * (2430.0 / 2700) - 1) * 60);
	// LSB runs the other way: 2530 Hz audio = lowest RF = left
	check_spatial ("LSB", -2800, -100, 2530, -(2 * (2430.0 / 2700) - 1) * 60);
	check_spatial ("CWU", 500, 700, 650, (2 * (150.0 / 200) - 1) * 60);
	check_spatial_misc ();

	// USB 100..2800: low tones left, high right
	check ("USB", 100, 2800, 370, 0.1);
	check ("USB", 100, 2800, 1450, 0.5);
	check ("USB", 100, 2800, 2530, 0.9);
	// LSB -2800..-100: low tones right, high left
	check ("LSB", -2800, -100, 370, 0.9);
	check ("LSB", -2800, -100, 1450, 0.5);
	check ("LSB", -2800, -100, 2530, 0.1);
	// narrow CW filter around the pitch
	check ("CWU", 500, 700, 550, 0.25);
	check ("CWL", -700, -500, 550, 0.75);
	// AM: 0..max edge
	check ("AM", -4000, 4000, 1000, 0.25);
	printf (failures ? "%d FAIL\n" : "ALL PASS\n", failures);
	return failures != 0;
}
