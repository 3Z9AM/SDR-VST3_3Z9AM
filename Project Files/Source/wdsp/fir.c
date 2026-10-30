/*  fir.c

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2013, 2016, 2022, 2025, 2026 Warren Pratt, NR0V
Portions Copyright (C) 2026 Daniel Poznanski, 3Z9AM - changes in this fork (see its git history).

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

The author can be reached by email at  

warren@pratt.one
*/

#define _CRT_SECURE_NO_WARNINGS
#include "comm.h"

double* fftcv_mults (int NM, double* c_impulse)
{
	double* mults        = (double *) malloc0 (NM * sizeof (complex));
	double* cfft_impulse = (double *) malloc0 (NM * sizeof (complex));
	fftw_plan ptmp = fftw_plan_dft_1d(NM, (fftw_complex *) cfft_impulse,
			(fftw_complex *) mults, FFTW_FORWARD, FFTW_PATIENT);
	memset (cfft_impulse, 0, NM * sizeof (complex));
	// store complex coefs right-justified in the buffer
	memcpy (&(cfft_impulse[NM - 2]), c_impulse, (NM / 2 + 1) * sizeof(complex));
	fftw_execute (ptmp);
	fftw_destroy_plan (ptmp);
	_aligned_free (cfft_impulse);
	return mults;
}

static double bessel_I0(double x)
{
	double sum = 1.0;
	double term = 1.0;
	double xhalf = x * 0.5;
	for (int k = 1; k <= 40; ++k)
	{
		term *= (xhalf / k);
		term *= (xhalf / k);
		sum += term;
		if (term < 1e-20 * sum) break;
	}
	return sum;
}

static double* get_fsamp_window(int N, int wintype)
{
	int i;
	double arg0, arg1;
	double* window = (double *) malloc0 (N * sizeof(double));
	switch (wintype)
	{
	case 0:
		arg0 = 2.0 * PI / ((double)N - 1.0);
		for (i = 0; i < N; i++)
		{
			arg1 = cos(arg0 * (double)i);
			window[i]  =   +0.21747
				+ arg1 *  (-0.45325
				+ arg1 *  (+0.28256
				+ arg1 *  (-0.04672)));
		}
		break;
	case 1:
		arg0 = 2.0 * PI / ((double)N - 1.0);
		for (i = 0; i < N; ++i)
		{
			arg1 = cos(arg0 * (double)i);
			window[i]  =   +6.3964424114390378e-02
				+ arg1 *  (-2.3993864599352804e-01
				+ arg1 *  (+3.5015956323820469e-01
				+ arg1 *  (-2.4774111897080783e-01
				+ arg1 *  (+8.5438256055858031e-02
				+ arg1 *  (-1.2320203369293225e-02
				+ arg1 *  (+4.3778825791773474e-04))))));
		}
		break;
	case 2:
	{
		const double beta = 8.0;
		const double inv_I0_beta = 1.0 / bessel_I0(beta);
		const double N1 = (double)(N - 1);
		for (int n = 0; n < N; n++)
		{
			double x = 2.0 * n / N1 - 1.0;
			double arg = beta * sqrt(1.0 - x * x);
			window[n] = bessel_I0(arg) * inv_I0_beta;
		}
	}
		break;
	default:
		for (i = 0; i < N; i++)
			window[i] = 1.0;
	}
	return window;
}

double* fir_fsamp_odd (int N, double* A, int rtype, double scale, int wintype)
{
	int i, j;
	int mid = (N - 1) / 2;
	double mag, phs;
	double* window;
	double *fcoef     = (double *) malloc0 (N * sizeof (complex));
	double *c_impulse = (double *) malloc0 (N * sizeof (complex));
	fftw_plan ptmp = fftw_plan_dft_1d(N, (fftw_complex *)fcoef, (fftw_complex *)c_impulse, FFTW_BACKWARD, FFTW_PATIENT);
	double local_scale = 1.0 / (double)N;
	for (i = 0; i <= mid; i++)
	{
		mag = A[i] * local_scale;
		phs = - (double)mid * TWOPI * (double)i / (double)N;
		fcoef[2 * i + 0] = mag * cos (phs);
		fcoef[2 * i + 1] = mag * sin (phs);
	}
	for (i = mid + 1, j = 0; i < N; i++, j++)
	{
		fcoef[2 * i + 0] = + fcoef[2 * (mid - j) + 0];
		fcoef[2 * i + 1] = - fcoef[2 * (mid - j) + 1];
	}
	fftw_execute (ptmp);
	fftw_destroy_plan (ptmp);
	_aligned_free (fcoef);
	window = get_fsamp_window(N, wintype);
	switch (rtype)
	{
	case 0:
		for (i = 0; i < N; i++)
			c_impulse[i] = scale * c_impulse[2 * i] * window[i];
		break;
	case 1:
		for (i = 0; i < N; i++)
		{
			c_impulse[2 * i + 0] *= scale * window[i];
			c_impulse[2 * i + 1] = 0.0;
		}
		break;
	}
	_aligned_free (window);
	return c_impulse;
}

double* fir_fsamp (int N, double* A, int rtype, double scale, int wintype)
{
	int n, i, j, k;
	double sum;
	double* window;
	double *c_impulse = (double *) malloc0 (N * sizeof (complex));

	if (N & 1)
	{
		int M = (N - 1) / 2;
		for (n = 0; n < M + 1; n++)
		{
			sum = 0.0;
			for (k = 1; k < M + 1; k++)
				sum += 2.0 * A[k] * cos(TWOPI * (n - M) * k / N);
			c_impulse[2 * n + 0] = (1.0 / N) * (A[0] + sum);
			c_impulse[2 * n + 1] = 0.0;
		}
		for (n = M + 1, j = 1; n < N; n++, j++)
		{
			c_impulse[2 * n + 0] = c_impulse[2 * (M - j) + 0];
			c_impulse[2 * n + 1] = 0.0;
		}
	}
	else
	{
		double M = (double)(N - 1) / 2.0;
		for (n = 0; n < N / 2; n++)
		{
			sum = 0.0;
			for (k = 1; k < N / 2; k++)
				sum += 2.0 * A[k] * cos(TWOPI * (n - M) * k / N);
			c_impulse[2 * n + 0] = (1.0 / N) * (A[0] + sum);
			c_impulse[2 * n + 1] = 0.0;
		}
		for (n = N / 2, j = 1; n < N; n++, j++)
		{
			c_impulse[2 * n + 0] = c_impulse[2 * (N / 2 - j) + 0];
			c_impulse[2 * n + 1] = 0.0;
		}
	}
	window = get_fsamp_window (N, wintype);
	switch (rtype)
	{
	case 0:
		for (i = 0; i < N; i++)
			c_impulse[i] = scale * c_impulse[2 * i] * window[i];
		break;
	case 1:
		for (i = 0; i < N; i++)
			{
				c_impulse[2 * i + 0] *= scale * window[i];
				c_impulse[2 * i + 1] = 0.0;
			}
		break;
	}
	_aligned_free (window);
	return c_impulse;
}

double* fir_bandpass (int N, double f_low, double f_high, double samplerate, int wintype, int rtype, double scale)
{
	// check for previous in the cache
	struct Params 
	{
		int N;
		int wintype;
		int rtype;
		double f_low;
		double f_high;
		double samplerate;
		double scale;
	};

	struct Params params;
	memset(&params, 0, sizeof (params));
	params.N = N;
	params.wintype = wintype;
	params.rtype = rtype;
	params.f_low = f_low;
	params.f_high = f_high;
	params.samplerate = samplerate;
	params.scale = scale;

	HASH_T h = fnv1a_hash(&params, sizeof(params));
	double* imp = get_impulse_cache_entry(FIR_CACHE, h, N);
	if (imp) return imp;
	//

	double *c_impulse = (double *) malloc0 (N * sizeof (complex));
	double ft = (f_high - f_low) / (2.0 * samplerate);
	double ft_rad = TWOPI * ft;
	double w_osc = PI * (f_high + f_low) / samplerate;
	int i, j;
	double m = 0.5 * (double)(N - 1);
	double delta = PI / m;
	double cosphi;
	double posi, posj;
	double sinc, window, coef;

	if (N & 1)
	{
		switch (rtype)
		{
		case 0:
			c_impulse[N >> 1] = scale * 2.0 * ft;
			break;
		case 1:
			c_impulse[N - 1] = scale * 2.0 * ft;
			c_impulse[  N  ] = 0.0;
			break;
		}
	}
	for (i = (N + 1) / 2, j = N / 2 - 1; i < N; i++, j--)
	{
		posi = (double)i - m;
		posj = (double)j - m;
		sinc = sin (ft_rad * posi) / (PI * posi);
		switch (wintype)
		{
		case 0:	// Blackman-Harris 4-term
			cosphi = cos (delta * i);
			window  =             + 0.21747
					+ cosphi *  ( - 0.45325
					+ cosphi *  ( + 0.28256
					+ cosphi *  ( - 0.04672 )));
			break;
		case 1:	// Blackman-Harris 7-term
		default:
			cosphi = cos (delta * i);
			window	=			  + 6.3964424114390378e-02
					+ cosphi *  ( - 2.3993864599352804e-01
					+ cosphi *  ( + 3.5015956323820469e-01
					+ cosphi *	( - 2.4774111897080783e-01
					+ cosphi *  ( + 8.5438256055858031e-02
					+ cosphi *	( - 1.2320203369293225e-02
					+ cosphi *	( + 4.3778825791773474e-04 ))))));
			break;
		}
		coef = scale * sinc * window;
		switch (rtype)
		{
		case 0:
			c_impulse[i] = + coef * cos (posi * w_osc);
			c_impulse[j] = + coef * cos (posj * w_osc);
			break;
		case 1:
			c_impulse[2 * i + 0] = + coef * cos (posi * w_osc);
			c_impulse[2 * i + 1] = - coef * sin (posi * w_osc);
			c_impulse[2 * j + 0] = + coef * cos (posj * w_osc);
			c_impulse[2 * j + 1] = - coef * sin (posj * w_osc);
			break;
		}
	}

	// store in cache
	add_impulse_to_cache(FIR_CACHE, h, N, c_impulse);

	return c_impulse;
}

/********************************************************************************************************
*																										*
*								Soft-edge complex bandpass (Thetis)										*
*																										*
********************************************************************************************************/

// Raised-cosine edge taper: the time-domain factor that turns an ideal (brick-wall)
// frequency step into a raised-cosine transition of total width 'd' (normalised).
static double soft_edge_taper (double d, double t)
{
	double x;
	if (d <= 0.0) return 1.0;
	x = 2.0 * d * t;
	if (fabs (fabs (x) - 1.0) < 1.0e-9) return PI / 4.0;
	return cos (PI * d * t) / (1.0 - x * x);
}

// Which edges are 'high audio' edges and get the soft transition: an edge is soft
// when it lies on the far side of 0 Hz (away from the carrier) and is at least half
// as far out as the other edge. USB/CW: high edge only; LSB: low edge only; AM/DSB:
// both. The carrier-side edge (passband on one side of 0 Hz: the edge nearer the
// carrier) gets its own width soft_c (0 = sharp); its transition also lies inside the
// passband, so nothing leaks into the other sideband.
// The transitions are clamped to the passband width so the two edges never overlap.
void fir_soft_edges (double f_low, double f_high, double soft, double soft_c, double* soft_low, double* soft_high)
{
	double m = fmax (fabs (f_low), fabs (f_high));
	double bw = f_high - f_low;
	int n;
	*soft_low  = (soft > 0.0 && f_low  < 0.0 && -f_low >= 0.5 * m) ? soft : 0.0;
	*soft_high = (soft > 0.0 && f_high > 0.0 &&  f_high >= 0.5 * m) ? soft : 0.0;
	if (soft_c > 0.0 && f_low >= 0.0 && f_high > 0.0) *soft_low  = soft_c;		// USB: carrier at the low edge
	if (soft_c > 0.0 && f_high <= 0.0 && f_low < 0.0) *soft_high = soft_c;		// LSB: carrier at the high edge
	n = (*soft_low > 0.0) + (*soft_high > 0.0);
	if (n > 0 && bw > 0.0)
	{
		double lim = bw / n;
		if (*soft_low  > lim) *soft_low  = lim;
		if (*soft_high > lim) *soft_high = lim;
	}
}

// Complex bandpass with independent raised-cosine edges. The transition of each
// soft edge lies entirely INSIDE the nominal passband (full rejection still starts
// at f_low / f_high), so the occupied bandwidth never grows. Same window, phase
// convention and scaling as fir_bandpass (rtype 1).
double* fir_bandpass_edges (int N, double f_low, double f_high, double samplerate, int wintype,
	double scale, double soft_low, double soft_high)
{
	double* c_impulse;
	double fa, fb, da, db, m, delta, t, re, im, cosphi, window;
	int i;
	if (soft_low <= 0.0 && soft_high <= 0.0)
		return fir_bandpass (N, f_low, f_high, samplerate, wintype, 1, scale);

	c_impulse = (double *) malloc0 (N * sizeof (complex));
	fa = (f_low  + 0.5 * soft_low)  / samplerate;
	fb = (f_high - 0.5 * soft_high) / samplerate;
	da = soft_low  / samplerate;
	db = soft_high / samplerate;
	m = 0.5 * (double)(N - 1);
	delta = PI / m;
	for (i = 0; i < N; i++)
	{
		t = (double)i - m;
		if (fabs (t) < 1.0e-9)
		{
			re = fb - fa;
			im = 0.0;
		}
		else
		{
			double ta = soft_edge_taper (da, t);
			double tb = soft_edge_taper (db, t);
			double thb = TWOPI * fb * t;
			double tha = TWOPI * fa * t;
			re = (sin (thb) * tb - sin (tha) * ta) / (TWOPI * t);
			im = (cos (thb) * tb - cos (tha) * ta) / (TWOPI * t);
		}
		cosphi = cos (delta * i);
		switch (wintype)
		{
		case 0:	// Blackman-Harris 4-term
			window  =             + 0.21747
					+ cosphi *  ( - 0.45325
					+ cosphi *  ( + 0.28256
					+ cosphi *  ( - 0.04672 )));
			break;
		case 1:	// Blackman-Harris 7-term
		default:
			window	=			  + 6.3964424114390378e-02
					+ cosphi *  ( - 2.3993864599352804e-01
					+ cosphi *  ( + 3.5015956323820469e-01
					+ cosphi *	( - 2.4774111897080783e-01
					+ cosphi *  ( + 8.5438256055858031e-02
					+ cosphi *	( - 1.2320203369293225e-02
					+ cosphi *	( + 4.3778825791773474e-04 ))))));
			break;
		}
		c_impulse[2 * i + 0] = scale * re * window;
		c_impulse[2 * i + 1] = scale * im * window;
	}
	return c_impulse;
}

// BIN2 (local addition, bin2.c): left/right pair of linear-phase filters for real audio, the left
// one in the real and the right one in the imaginary part of the complex impulse. Equal-power pan
// by the place in the RX filter (f_low..f_high, Hz from the carrier): lowest RF left, highest
// right. LSB filters (both edges <= 0) run the audio band the other way; filters across the
// carrier (AM/DSB/FM) pan 0..max edge with low tones left. Outside the filter the pan holds.
// Frequency sampling like fir_fsamp, but through one real inverse FFT (fir_fsamp is O(N^2)).
double* fir_bin2 (int N, double samplerate, double f_low, double f_high, double scale)
{
	int k, n, side, invert;
	double a0, a1, p, g, phs;
	double* imp = (double *) malloc0 (N * sizeof (complex));
	double* window = get_fsamp_window (N, 0);	// 4-term: less smoothing of the pan curve
	fftw_complex* X = (fftw_complex *) fftw_malloc ((N / 2 + 1) * sizeof (fftw_complex));
	double* x = (double *) fftw_malloc (N * sizeof (double));
	fftw_plan plan = fftw_plan_dft_c2r_1d (N, X, x, FFTW_ESTIMATE);
	if (f_high <= 0.0)		{ a0 = -f_high; a1 = -f_low; invert = 1; }
	else if (f_low >= 0.0)	{ a0 = f_low; a1 = f_high; invert = 0; }
	else					{ a0 = 0.0; a1 = fmax (-f_low, f_high); invert = 0; }
	for (side = 0; side < 2; side++)		// 0 = left, 1 = right
	{
		for (k = 0; k < N / 2; k++)
		{
			p = a1 > a0 ? (k * samplerate / N - a0) / (a1 - a0) : 0.5;
			p = fmin (1.0, fmax (0.0, p));
			if (invert) p = 1.0 - p;
			g = side ? sin (0.5 * PI * p) : cos (0.5 * PI * p);
			phs = -PI * k * (N - 1) / N;	// delay (N - 1) / 2: linear phase, both sides alike
			X[k][0] = g * cos (phs);
			X[k][1] = g * sin (phs);
		}
		X[N / 2][0] = X[N / 2][1] = 0.0;
		fftw_execute (plan);
		for (n = 0; n < N; n++)
			imp[2 * n + side] = scale * x[n] / N * window[n];
	}
	fftw_destroy_plan (plan);
	fftw_free (x);
	fftw_free (X);
	_aligned_free (window);
	return imp;
}

// ---- BIN2 spatial (speakers) ----

typedef struct { double re, im; } b2c;
static b2c b2c_mul (b2c a, b2c b) { b2c r = { a.re * b.re - a.im * b.im, a.re * b.im + a.im * b.re }; return r; }
static b2c b2c_add (b2c a, b2c b) { b2c r = { a.re + b.re, a.im + b.im }; return r; }
static b2c b2c_sub (b2c a, b2c b) { b2c r = { a.re - b.re, a.im - b.im }; return r; }
static b2c b2c_scale (b2c a, double s) { b2c r = { a.re * s, a.im * s }; return r; }
static b2c b2c_expj (double ph) { b2c r = { cos (ph), sin (ph) }; return r; }
static double b2c_abs2 (b2c a) { return a.re * a.re + a.im * a.im; }
// a / (|b|^2 + beta) * conj(b): regularised a / b
static b2c b2c_rdiv (b2c a, b2c b, double beta)
{
	b2c bc = { b.re, -b.im };
	return b2c_scale (b2c_mul (a, bc), 1.0 / (b2c_abs2 (b) + beta));
}

// Spherical head (Brown & Duda 1998): one-pole head shadow + Woodworth delay from the head
// centre, for a far source at 'inc' rad from the ear axis (0 = straight at the ear).
// Returns re/im of the transfer at angular frequency w.
void bin2_ear (double w, double inc, double* re, double* im)
{
	const double a = 0.0875, c = 343.0, w0 = c / a, amin = 0.1, tmin = 150.0 * PI / 180.0;
	double alpha, T, x;
	b2c num, den, h;
	inc = fmod (fabs (inc), TWOPI);
	if (inc > PI) inc = TWOPI - inc;
	alpha = (1.0 + 0.5 * amin) + (1.0 - 0.5 * amin) * cos (inc / tmin * PI);
	T = inc < 0.5 * PI ? -a / c * cos (inc) : a / c * (inc - 0.5 * PI);
	x = w / (2.0 * w0);
	num.re = 1.0; num.im = alpha * x;
	den.re = 1.0; den.im = x;
	h = b2c_mul (b2c_rdiv (num, den, 0.0), b2c_expj (-w * T));
	*re = h.re; *im = h.im;
}

static b2c b2_ear (double w, double inc)
{
	b2c h;
	bin2_ear (w, inc, &h.re, &h.im);
	return h;
}

// a far source at azimuth az (rad, + = right) as heard at the left / right ear
static void b2_binaural (double w, double az, b2c* l, b2c* r)
{
	*l = b2_ear (w, az + 0.5 * PI);
	*r = b2_ear (w, az - 0.5 * PI);
}

// Light room: early reflections (no late reverb, so weak signals stay readable).
// Direction of each image source; walls depend on the source side through the delay only.
typedef struct { double az, delay_ms, gain, lp_hz; int follows_source; double side_ms; } b2refl;
static const b2refl b2_room[] =
{
	{   0.0,  3.0, 0.50, 5000.0, 1,  0.0 },		// floor (same direction as the source)
	{   0.0,  5.5, 0.35, 4000.0, 1,  0.0 },		// ceiling
	{ -75.0,  7.5, 0.35, 3500.0, 0, +3.0 },		// left wall: further when the source is on the right
	{  75.0,  7.5, 0.35, 3500.0, 0, -3.0 },		// right wall
	{ -140.0, 12.5, 0.20, 2500.0, 0, 0.0 },		// back, left
	{  140.0, 14.0, 0.20, 2500.0, 0, 0.0 },		// back, right
};

// BIN2 spatial for stereo speakers (local addition). Same pan position as fir_bin2 (place in
// the RX filter), but each frequency becomes a virtual source on a frontal arc of +-width deg:
// spherical-head binaural target (+ early reflections scaled by room 0..1) sent through a
// regularised crosstalk canceller for speakers at +-speaker deg, so sources can sit outside the
// speakers. Not linear phase: bulk delay ~10.7 ms instead of N/2. Loudness over the filter matches
// fir_bin2 (mean |L|^2 + |R|^2 = scale^2).
double* fir_bin2_spatial (int N, double samplerate, double f_low, double f_high,
	double width_deg, double speaker_deg, double room, double scale)
{
	const double beta = 0.025;		// CTC regularisation: at most ~+10 dB of low-frequency boost
	int k, n, side, invert, D0, taper, nref = (int)(sizeof (b2_room) / sizeof (b2_room[0])), i;
	double a0, a1, p, w, f, az, spk, wid, power = 0.0, norm, dscale, avail_ms, max_ms = 0.0;
	int npower = 0;
	b2c* S = (b2c *) malloc0 ((N / 2 + 1) * 2 * sizeof (b2c));		// [k][0] = left speaker, [k][1] = right
	double* imp = (double *) malloc0 (N * sizeof (complex));
	fftw_complex* X = (fftw_complex *) fftw_malloc ((N / 2 + 1) * sizeof (fftw_complex));
	double* x = (double *) fftw_malloc (N * sizeof (double));
	fftw_plan plan = fftw_plan_dft_c2r_1d (N, X, x, FFTW_ESTIMATE);

	if (f_high <= 0.0)		{ a0 = -f_high; a1 = -f_low; invert = 1; }
	else if (f_low >= 0.0)	{ a0 = f_low; a1 = f_high; invert = 0; }
	else					{ a0 = 0.0; a1 = fmax (-f_low, f_high); invert = 0; }
	wid = fmin (90.0, fmax (0.0, width_deg)) * PI / 180.0;
	spk = fmin (60.0, fmax (5.0, speaker_deg)) * PI / 180.0;
	room = fmin (1.0, fmax (0.0, room));
	D0 = (int)(0.0107 * samplerate + 0.5);			// bulk delay: acausal part of the CTC and of steep (CW) pan curves
	if (D0 > N / 4) D0 = N / 4;
	taper = N / 8;
	// reflections must end inside the impulse: squeeze them at very short impulses
	for (i = 0; i < nref; i++) max_ms = fmax (max_ms, b2_room[i].delay_ms + fabs (b2_room[i].side_ms));
	avail_ms = 1000.0 * (N - taper - D0) / samplerate - 3.0;
	dscale = avail_ms <= 0.0 ? 0.0 : fmin (1.0, avail_ms / max_ms);

	for (k = 1; k < N / 2; k++)
	{
		b2c bl, br, Hi, Hc, M, D, Bs, Bd, Ss, Sd;
		f = k * samplerate / N;
		w = TWOPI * f;
		p = a1 > a0 ? (f - a0) / (a1 - a0) : 0.5;
		p = fmin (1.0, fmax (0.0, p));
		if (invert) p = 1.0 - p;
		az = (2.0 * p - 1.0) * wid;
		b2_binaural (w, az, &bl, &br);
		for (i = 0; i < nref && room > 0.0 && dscale > 0.0; i++)
		{
			const b2refl* r = &b2_room[i];
			b2c rl, rr, g, one = { 1.0, 0.0 }, pole = { 1.0, f / r->lp_hz };
			double raz = r->follows_source ? az : r->az * PI / 180.0;
			double d = (r->delay_ms + r->side_ms * sin (az)) * dscale * 0.001;
			b2c lp = b2c_rdiv (one, pole, 0.0);		// wall absorption: one-pole low-pass
			b2_binaural (w, raz, &rl, &rr);
			g = b2c_mul (b2c_scale (lp, room * r->gain), b2c_expj (-w * d));
			bl = b2c_add (bl, b2c_mul (g, rl));
			br = b2c_add (br, b2c_mul (g, rr));
		}
		// ears from the speakers: earL = Hi SL + Hc SR, earR = Hc SL + Hi SR; solve on sum/difference
		Hi = b2_ear (w, 0.5 * PI - spk);
		Hc = b2_ear (w, 0.5 * PI + spk);
		M = b2c_add (Hi, Hc);
		D = b2c_sub (Hi, Hc);
		Bs = b2c_scale (b2c_add (bl, br), 0.5);
		Bd = b2c_scale (b2c_sub (bl, br), 0.5);
		Ss = b2c_rdiv (Bs, M, beta);
		Sd = b2c_rdiv (Bd, D, beta);
		S[2 * k + 0] = b2c_add (Ss, Sd);
		S[2 * k + 1] = b2c_sub (Ss, Sd);
		if (f >= a0 && f <= a1)
		{
			power += b2c_abs2 (S[2 * k]) + b2c_abs2 (S[2 * k + 1]);
			npower++;
		}
	}
	norm = npower > 0 && power > 0.0 ? sqrt (npower / power) : 1.0;

	for (side = 0; side < 2; side++)
	{
		X[0][0] = X[0][1] = 0.0;		// no DC
		for (k = 1; k < N / 2; k++)
		{
			b2c v = b2c_mul (b2c_scale (S[2 * k + side], norm), b2c_expj (-TWOPI * k * D0 / N));
			X[k][0] = v.re;
			X[k][1] = v.im;
		}
		X[N / 2][0] = X[N / 2][1] = 0.0;
		fftw_execute (plan);
		for (n = 0; n < N; n++)
		{
			double win = 1.0;
			if (n < D0) win = 0.5 - 0.5 * cos (PI * n / D0);
			else if (n >= N - taper) win = 0.5 + 0.5 * cos (PI * (n - (N - taper)) / taper);
			imp[2 * n + side] = scale * x[n] / N * win;
		}
	}
	fftw_destroy_plan (plan);
	fftw_free (x);
	fftw_free (X);
	_aligned_free (S);
	return imp;
}

double* fir_bandpass_soft (int N, double f_low, double f_high, double samplerate, int wintype,
	double scale, double soft, double soft_c)
{
	double sl, sh;
	fir_soft_edges (f_low, f_high, soft, soft_c, &sl, &sh);
	return fir_bandpass_edges (N, f_low, f_high, samplerate, wintype, scale, sl, sh);
}

double *fir_read (int N, const char *filename, int rtype, double scale)
	// N = number of real or complex coefficients (see rtype)
	// *filename = filename
	// rtype = 0:  real coefficients
	// rtype = 1:  complex coefficients
	// scale = a scale factor that will be applied to the returned coefficients;
	//		if this is not needed, set it to 1.0
	// NOTE:  The number of values in the file must NOT exceed those implied by N and rtype
{
	FILE *file;
	int i;
	double I, Q;
	double *c_impulse = (double *) malloc0 (N * sizeof (complex));
	if (file = fopen(filename, "r"))
	{
		int error = 0;
		for (i = 0; i < N; i++)
		{
			// read in the complex impulse response
			// NOTE:  IF the freq response is symmetrical about 0, the imag coeffs will all be zero.
			switch (rtype)
			{
			case 0:
				if (error == 0 && fscanf(file, "%le", &I) != 1) error = 1;
				if (error == 0) 
					c_impulse[i] = +scale * I;
				break;
			case 1:
				if (error == 0 && (fscanf(file, "%le", &I) != 1 || fscanf(file, "%le", &Q) != 1)) error = 1;
				if (error == 0)
				{
					c_impulse[2 * i + 0] = +scale * I;
					c_impulse[2 * i + 1] = -scale * Q;
				}
				break;
			}
		}
		fclose (file);
	}
	return c_impulse;
}

void analytic (int N, double* in, double* out)
{
	int i;
	double inv_N = 1.0 / (double)N;
	double two_inv_N = 2.0 * inv_N;
	double* x = (double *) malloc0 (N * sizeof (complex));
	fftw_plan pfor = fftw_plan_dft_1d (N, (fftw_complex *) in,
			(fftw_complex *) x, FFTW_FORWARD, FFTW_PATIENT);
	fftw_plan prev = fftw_plan_dft_1d (N, (fftw_complex *) x,
			(fftw_complex *) out, FFTW_BACKWARD, FFTW_PATIENT);
	fftw_execute (pfor);
	x[0] *= inv_N;
	x[1] *= inv_N;
	for (i = 1; i < N / 2; i++)
	{
		x[2 * i + 0] *= two_inv_N;
		x[2 * i + 1] *= two_inv_N;
	}
	x[N + 0] *= inv_N;
	x[N + 1] *= inv_N;
	memset (&x[N + 2], 0, (N - 2) * sizeof (double));
	fftw_execute (prev);
	fftw_destroy_plan (prev);
	fftw_destroy_plan (pfor);
	_aligned_free (x);
}

void mp_imp (int N, double* fir, double* mpfir, int pfactor, int polarity)
{
	// check for previous in the cache
	struct Params 
	{
		int N;
		int pfactor;
		int polarity;
	};

	struct Params params;
	memset(&params, 0, sizeof(params));
	params.N = N;
	params.pfactor = pfactor;
	params.polarity = polarity;

	HASH_T h = fnv1a_hash(&params, sizeof(params));

	size_t arr_len = N * sizeof(complex);
	HASH_T hf = fnv1a_hash((uint8_t*)fir, arr_len);
	h ^= hf + GOLDEN_RATIO + (h << 6) + (h >> 2);

	double* imp = get_impulse_cache_entry(MP_CACHE, h, N);
	if (imp)
	{
		memcpy(mpfir, imp, N * sizeof(complex));
		_aligned_free (imp);
		return;
	}
	//

	int i;
	int size = N * pfactor;
	double inv_PN = 1.0 / (double)size;
	double* firpad  = (double *) malloc0 (size * sizeof (complex));
	double* firfreq = (double *) malloc0 (size * sizeof (complex));
	double* mag     = (double *) malloc0 (size * sizeof (double));
	double* ana     = (double *) malloc0 (size * sizeof (complex));
	double* impulse = (double *) malloc0 (size * sizeof (complex));
	double* newfreq = (double *) malloc0 (size * sizeof (complex));
	memcpy (firpad, fir, N * sizeof (complex));
	fftw_plan pfor = fftw_plan_dft_1d (size, (fftw_complex *) firpad,
			(fftw_complex *) firfreq, FFTW_FORWARD, FFTW_PATIENT);
	fftw_plan prev = fftw_plan_dft_1d (size, (fftw_complex *) newfreq,
			(fftw_complex *) impulse, FFTW_BACKWARD, FFTW_PATIENT);
	// print_impulse("orig_imp.txt", N, fir, 1, 0);
	fftw_execute (pfor);
	for (i = 0; i < size; i++)
	{
		mag[i] = sqrt (firfreq[2 * i + 0] * firfreq[2 * i + 0] + firfreq[2 * i + 1] * firfreq[2 * i + 1]) * inv_PN;
		if (mag[i] > 0.0)
			ana[2 * i + 0] = log (mag[i]);
		else
			ana[2 * i + 0] = log (1.0e-300);
	}
	analytic (size, ana, ana);
	for (i = 0; i < size; i++)
	{
		newfreq[2 * i + 0] = + mag[i] * cos (ana[2 * i + 1]);
		if (polarity)
			newfreq[2 * i + 1] = + mag[i] * sin (ana[2 * i + 1]);
		else
			newfreq[2 * i + 1] = - mag[i] * sin (ana[2 * i + 1]);
	}
	fftw_execute (prev);
	if (polarity)
		memcpy (mpfir, &impulse[2 * (pfactor - 1) * N], N * sizeof (complex));
	else
		memcpy (mpfir, impulse, N * sizeof (complex));
	// print_impulse("min_imp.txt", N, mpfir, 1, 0);
	fftw_destroy_plan (prev);
	fftw_destroy_plan (pfor);
	_aligned_free (newfreq);
	_aligned_free (impulse);
	_aligned_free (ana);
	_aligned_free (mag);
	_aligned_free (firfreq);
	_aligned_free (firpad);

	// store in cache
	add_impulse_to_cache(MP_CACHE, h, N, mpfir);
}

// impulse response of a zero frequency filter comprising a cascade of two resonators, 
//    each followed by a detrending filter
double* zff_impulse(int nc, double scale)
{
	// nc = number of coefficients (power of two)
	int n_resdet = nc / 2 - 1;			// size of single zero-frequency resonator with detrender
	int n_dresdet = 2 * n_resdet - 1;	// size of two cascaded units; when we convolve these we get 2 * n - 1 length
	// allocate the single and make the values
	double* resdet = (double*)malloc0 (n_resdet * sizeof(double));
	for (int i = 1, j = 0, k = n_resdet - 1; i < nc / 4; i++, j++, k--)
		resdet[j] = resdet[k] = (double)(i * (i + 1) / 2);
	resdet[nc / 4 - 1] = (double)(nc / 4 * (nc / 4 + 1) / 2);
	// print_impulse ("resdet", n_resdet, resdet, 0, 0);
	// allocate the double and complex versions and make the values
	double* dresdet = (double*)malloc0 (n_dresdet * sizeof(double));
	double div = (double)((nc / 2 + 1) * (nc / 2 + 1));					// calculate divisor
	double* c_dresdet = (double*)malloc0 (nc * sizeof(complex));
	for (int n = 0; n < n_dresdet; n++)	// convolve to make the cascade
	{
		for (int k = 0; k < n_resdet; k++)
			if ((n - k) >= 0 && (n - k) < n_resdet)
				dresdet[n] += resdet[k] * resdet[n - k];
		dresdet[n] /= div;
		c_dresdet[2 * n + 0] = dresdet[n] * scale;
		c_dresdet[2 * n + 1] = 0.0;
	}
	// print_impulse("dresdet", n_dresdet, dresdet, 0, 0);
	// print_impulse("c_dresdet", nc, c_dresdet, 1, 0);
	_aligned_free(dresdet);
	_aligned_free(resdet);
	return c_dresdet;
}

typedef struct _minphase
{
    int N;
    int pfactor;
    int size;
    double inv_size;
    double* firpad;
    double* firfreq;
    double* mag;
    double* ana;
    double* anax;
    double* newfreq;
    double* impulse;
    fftw_plan p_fir;
    fftw_plan p_anafor;
    fftw_plan p_anainv;
    fftw_plan p_imp;
} minphase, *MINPHASE;

MINPHASE create_minphase (int N, int pfactor)
{
    MINPHASE a  = (MINPHASE) malloc0 (sizeof (minphase));
    a->N        = N;
    a->pfactor  = pfactor;
    a->size     = N * pfactor;
    a->inv_size = 1.0 / (double) a->size;

    a->firpad  = (double *) malloc0 (a->size * sizeof (complex));
    a->firfreq = (double *) malloc0 (a->size * sizeof (complex));
    a->mag     = (double *) malloc0 (a->size * sizeof (double));
    a->ana     = (double *) malloc0 (a->size * sizeof (complex));
    a->anax    = (double *) malloc0 (a->size * sizeof (complex));
    a->newfreq = (double *) malloc0 (a->size * sizeof (complex));
    a->impulse = (double *) malloc0 (a->size * sizeof (complex));

    a->p_fir    = fftw_plan_dft_1d (a->size, (fftw_complex *) a->firpad,  (fftw_complex *) a->firfreq, FFTW_FORWARD,  FFTW_PATIENT);
    a->p_anafor = fftw_plan_dft_1d (a->size, (fftw_complex *) a->ana,     (fftw_complex *) a->anax,    FFTW_FORWARD,  FFTW_PATIENT);
    a->p_anainv = fftw_plan_dft_1d (a->size, (fftw_complex *) a->anax,    (fftw_complex *) a->ana,     FFTW_BACKWARD, FFTW_PATIENT);
    a->p_imp    = fftw_plan_dft_1d (a->size, (fftw_complex *) a->newfreq, (fftw_complex *) a->impulse, FFTW_BACKWARD, FFTW_PATIENT);

    memset (a->firpad, 0, a->size * sizeof (complex));
    return a;
}

void destroy_minphase (MINPHASE a)
{
    fftw_destroy_plan (a->p_imp);
    fftw_destroy_plan (a->p_anainv);
    fftw_destroy_plan (a->p_anafor);
    fftw_destroy_plan (a->p_fir);
    _aligned_free (a->impulse);
    _aligned_free (a->newfreq);
    _aligned_free (a->anax);
    _aligned_free (a->ana);
    _aligned_free (a->mag);
    _aligned_free (a->firfreq);
    _aligned_free (a->firpad);
    _aligned_free (a);
}

void mp_imp_exec (MINPHASE a, double* fir, double* mpfir)
{
    int i, size = a->size, N = a->N;
    double inv_size = a->inv_size;
    double two_inv_size = 2.0 * inv_size;
    memcpy (a->firpad, fir, N * sizeof (complex));
    fftw_execute (a->p_fir);
    for (i = 0; i < size; i++)
    {
        double re = a->firfreq[2 * i + 0];
        double im = a->firfreq[2 * i + 1];
        double m  = sqrt (re * re + im * im) * inv_size;
        a->mag[i]         = m;
        a->ana[2 * i + 0] = (m > 0.0) ? log (m) : log (1.0e-300);
        a->ana[2 * i + 1] = 0.0;
    }
    fftw_execute (a->p_anafor);
    a->anax[0] *= inv_size;
    a->anax[1] *= inv_size;
    for (i = 1; i < size / 2; i++)
    {
        a->anax[2 * i + 0] *= two_inv_size;
        a->anax[2 * i + 1] *= two_inv_size;
    }
    a->anax[size + 0] *= inv_size;
    a->anax[size + 1] *= inv_size;
    memset (&a->anax[size + 2], 0, (size - 2) * sizeof (double));
    fftw_execute (a->p_anainv);
    for (i = 0; i < size; i++)
    {
        double ph = a->ana[2 * i + 1];
        a->newfreq[2 * i + 0] = + a->mag[i] * cos (ph);
        a->newfreq[2 * i + 1] = - a->mag[i] * sin (ph);
    }
    fftw_execute (a->p_imp);
    memcpy (mpfir,  a->impulse, N * sizeof (complex));
}

typedef struct _fsamp
{
    int N;
    int wintype;
    int nh;
    int even;
    double* cosr;
    double* sinr;
    double* window;
    double* X;
    double* impulse;
    fftw_plan p_c2r;
} fsamp, *FSAMP;

FSAMP create_fsamp (int N, int wintype)
{
    FSAMP a = (FSAMP) malloc0 (sizeof (fsamp));
    a->N       = N;
    a->wintype = wintype;
    a->nh      = N / 2 + 1;
    a->even    = !(N & 1);

    double d     = (double)(N - 1) / 2.0;
    double inv_N = 1.0 / (double) N;

    a->cosr   = (double *) malloc0 (a->nh * sizeof (double));
    a->sinr   = (double *) malloc0 (a->nh * sizeof (double));
    {
        int k;
        for (k = 0; k < a->nh; k++)
        {
            double phs = - TWOPI * d * (double) k / (double) N;
            a->cosr[k] = cos (phs) * inv_N;
            a->sinr[k] = sin (phs) * inv_N;
        }
    }
    a->window  = get_fsamp_window (N, wintype);
    a->X       = (double *) malloc0 (a->nh * sizeof (complex));
    a->impulse = (double *) malloc0 (N      * sizeof (double));
    a->p_c2r = fftw_plan_dft_c2r_1d (N, (fftw_complex *) a->X, a->impulse, FFTW_PATIENT);
    return a;
}

void destroy_fsamp (FSAMP a)
{
    fftw_destroy_plan (a->p_c2r);
    _aligned_free (a->impulse);
    _aligned_free (a->X);
    _aligned_free (a->window);
    _aligned_free (a->sinr);
    _aligned_free (a->cosr);
    _aligned_free (a);
}

void fsamp_exec (FSAMP a, double* A, double* coef, int rtype, double scale)
{
    int k, i, N = a->N, nh = a->nh;
    for (k = 0; k < nh; k++)
    {
        a->X[2 * k + 0] = A[k] * a->cosr[k];
        a->X[2 * k + 1] = A[k] * a->sinr[k];
    }
    if (a->even)
    {
        a->X[2 * (nh - 1) + 0] = 0.0;
        a->X[2 * (nh - 1) + 1] = 0.0;
    }
    fftw_execute (a->p_c2r);
    switch (rtype)
    {
    case 0:
        for (i = 0; i < N; i++)
            coef[i] = scale * a->impulse[i] * a->window[i];
        break;
    case 1:
        for (i = 0; i < N; i++)
        {
            coef[2 * i + 0] = scale * a->impulse[i] * a->window[i];
            coef[2 * i + 1] = 0.0;
        }
        break;
    }
}
