/*  secdiv.c

Diversity per frequency section (own addition, prototype; offline twin: tools/divtest).
Replaces the radar's single complex weight of the EXT diversity (div.c, id 0, mixing two
receivers) by one weight per FFT bin inside the RX1 filter, averaged over nb/sections bins:
  mode 1 (MRC):      y = v^H z, v = main eigenvector of the 2x2 covariance, phase of RX1 -
                     RX2 rotated onto RX1 and weighted by its share of the signal
  mode 2 (co-phase): y = (z1 + g e^jp z2) / 2 - RX2 rotated onto RX1 and raised to its level,
                     so after the mix RX2-RX1 phase is 0 across the whole filter
  mode 3 (null):     y = z1 - w z2, w = <z1 z2*> / <|z2|^2> from pauses (whole filter at the noise
                     floor on both, 2 s) - RX2 as a noise antenna, local noise nulled per section;
                     |w| at most +20 dB over the noise-matched RX2
  mode 4 (auto):     max-SINR per section with RX1 as the main antenna: the station = what RX1
                     hears (its excess over the noise covariance from pauses, Rx from all cells over 1 s),
                     kept at its RX1 level; MRC where the noise of the two antennas is independent, null
                     where it is shared, and what only RX2 hears is treated as interference
Outside the filter one weight from the whole filter. Covariance only from cells with signal
(speech pauses hold the weights; null: only cells without it), RX2 scaled to the RX1 noise floor.
Streaming STFT, sqrt-Hann at 50% overlap, ~47 Hz bins: delay = one FFT (21 ms at 192 kHz).

*/

#include "comm.h"
#include <float.h>

typedef struct _secdiv
{
	int mode, sections, rate, lo, hi;		// lo/hi: filter as native IQ offsets, Hz
	double tau;
	// noise floor = minimum of the in-filter frame power over the last ~1.5 s (6 blocks of 0.25 s),
	// RX1 / RX2: speech always has a pause in there, so it follows a noisier band within 1.5 s and
	// can never be held up by the station
	double bm1[6], bm2[6], m1cur, m2cur;
	int bi, bcnt;
	volatile long freeze;				// TX and just after: keep the weights, learn nothing
	int settle;							// frames after a reset without learning (old samples, retune transient)
	int N, hop, pos;
	double *win, *in1, *in2, *outq, *ola, *x1, *x2, *y;
	double *cnt, *r11, *r22, *cr, *ci, *s11, *s22, *scr, *sci, *wr, *wi, *wv;
	double *q11, *q22, *qr, *qi, *t11, *t22, *tr, *ti;		// auto: noise covariance and its prefix sums
	fftw_plan p1, p2, pr;
	CRITICAL_SECTION cs;
} secdiv;

static secdiv sd;
static volatile long sd_init;

static void sd_free (void)
{
	if (sd.N == 0) return;
	fftw_destroy_plan (sd.p1); fftw_destroy_plan (sd.p2); fftw_destroy_plan (sd.pr);
	double **b[] = { &sd.win, &sd.in1, &sd.in2, &sd.outq, &sd.ola, &sd.x1, &sd.x2, &sd.y, &sd.cnt,
		&sd.r11, &sd.r22, &sd.cr, &sd.ci, &sd.s11, &sd.s22, &sd.scr, &sd.sci, &sd.wr, &sd.wi, &sd.wv,
		&sd.q11, &sd.q22, &sd.qr, &sd.qi, &sd.t11, &sd.t22, &sd.tr, &sd.ti };
	for (int i = 0; i < sizeof (b) / sizeof (b[0]); i++) { _aligned_free (*b[i]); *b[i] = 0; }
	sd.N = 0;
}

static void sd_alloc (int rate)
{
	int N = 1024;
	while (N * 46.875 < rate) N <<= 1;
	sd.N = N; sd.hop = N / 2; sd.pos = 0;
	sd.win = (double *) malloc0 (N * sizeof (double));
	for (int i = 0; i < N; i++) sd.win[i] = sqrt (0.5 - 0.5 * cos (TWOPI * i / N));
	double **c[] = { &sd.in1, &sd.in2, &sd.ola, &sd.x1, &sd.x2, &sd.y };
	for (int i = 0; i < sizeof (c) / sizeof (c[0]); i++) *c[i] = (double *) malloc0 (N * sizeof (complex));
	sd.outq = (double *) malloc0 (sd.hop * sizeof (complex));
	double **r[] = { &sd.cnt, &sd.r11, &sd.r22, &sd.cr, &sd.ci, &sd.wr, &sd.wi, &sd.wv, &sd.q11, &sd.q22, &sd.qr, &sd.qi };
	for (int i = 0; i < sizeof (r) / sizeof (r[0]); i++) *r[i] = (double *) malloc0 (N * sizeof (double));
	double **s[] = { &sd.s11, &sd.s22, &sd.scr, &sd.sci, &sd.t11, &sd.t22, &sd.tr, &sd.ti };
	for (int i = 0; i < sizeof (s) / sizeof (s[0]); i++) *s[i] = (double *) malloc0 ((N + 1) * sizeof (double));
	sd.p1 = fftw_plan_dft_1d (N, (fftw_complex *) sd.x1, (fftw_complex *) sd.x1, FFTW_FORWARD, FFTW_ESTIMATE);
	sd.p2 = fftw_plan_dft_1d (N, (fftw_complex *) sd.x2, (fftw_complex *) sd.x2, FFTW_FORWARD, FFTW_ESTIMATE);
	sd.pr = fftw_plan_dft_1d (N, (fftw_complex *) sd.y, (fftw_complex *) sd.y, FFTW_BACKWARD, FFTW_ESTIMATE);
	sd.rate = rate;
}

// b = 0: RX1 alone, 1: the weight as computed
static void sd_blend (double b, double *v1, double *wr, double *wi)
{
	*v1 = 1.0 - b + b * *v1; *wr *= b; *wi *= b;
}

// auto, RX1 = main antenna: the station is what RX1 hears, h = (1, h2), h2 = conj(Cs) / As from the
// excess of Rx over Rn (Cs = R12 - Rn12, As = R11 - Rn11; 20% of the RX1 noise added to As keeps h2
// small where the station is under the noise). Interference = Rn plus what RX2 has over the noise that
// RX1 does not share (E). Distortionless max-SINR w = Ri^-1 h / (h^H Ri^-1 h), y = w^H z: the station
// stays at its RX1 level and the output noise is never over RX1 alone. Equal independent noise gives
// MRC, shared noise the Wiener null, and something only RX2 hears (local QRM on the noise antenna)
// is not taken for the station - the plain eigenvector pulled it in. Rotated so the RX1 coefficient is real.
static void sd_auto (double A, double B, double Cr, double Ci, double a, double b, double cr, double ci,
	double *v1, double *wr, double *wi)
{
	if (a <= 0.0 || b <= 0.0) { *v1 = 1.0; *wr = *wi = 0.0; return; }
	double den = max (A - a, 0.0) + 0.2 * a, Sr = Cr - cr, Si = Ci - ci;
	double hr = Sr / den, hi = -Si / den;
	double E = max (0.0, B - b - (Sr * Sr + Si * Si) / den);
	double p = 1.01 * a, r = 1.01 * (b + E);								// 1% diagonal loading
	if (p * r - cr * cr - ci * ci <= 0.0) { *v1 = 1.0; *wr = *wi = 0.0; return; }
	// w = adj(Ri) h: w1 = r - c h2, w2 = p h2 - conj(c)
	double w1r = r - (cr * hr - ci * hi), w1i = -(cr * hi + ci * hr);
	double w2r = p * hr - cr, w2i = p * hi + ci;
	double D = w1r + w2r * hr + w2i * hi;									// w^H h, real and > 0
	if (D <= 0.0) { *v1 = 1.0; *wr = *wi = 0.0; return; }
	// coefficients conj(w) / D; RX1 rotated real: v1 = |w1| / D, w = conj(w2) w1 / (|w1| D)
	double m1 = sqrt (w1r * w1r + w1i * w1i);
	if (m1 <= 0.0) { *v1 = 0.0; *wr = w2r / D; *wi = -w2i / D; return; }
	*v1 = m1 / D;
	*wr = (w2r * w1r + w2i * w1i) / (m1 * D); *wi = (w2r * w1i - w2i * w1r) / (m1 * D);
}

// weight for z2 (y = v1 z1 + w z2) from summed covariance A = R11, B = R22, C = R12 = <z1 z2*>
static void sd_weight (int mode, double A, double B, double Cr, double Ci, double *v1, double *wr, double *wi)
{
	double cm = sqrt (Cr * Cr + Ci * Ci);
	if (cm <= 0.0 || B <= 0.0) { *v1 = 1.0; *wr = *wi = 0.0; return; }	// nothing learned yet: RX1
	if (mode == 3)
	{
		double g = cm / B > 10.0 ? 10.0 / cm : 1.0 / B;					// |w| <= 10 (+20 dB)
		*v1 = 1.0; *wr = -g * Cr; *wi = -g * Ci;
		return;
	}
	if (mode == 2)
	{
		double g = sqrt (A / B) / cm;
		*v1 = 0.5; *wr = 0.5 * g * Cr; *wi = 0.5 * g * Ci;
		return;
	}
	double l = 0.5 * (A + B) + sqrt (0.25 * (A - B) * (A - B) + cm * cm);
	double nrm = sqrt (cm * cm + (l - A) * (l - A));
	*v1 = cm / nrm; *wr = (l - A) * Cr / cm / nrm; *wi = (l - A) * Ci / cm / nrm;
}

static void sd_frame (void)
{
	int N = sd.N, hop = sd.hop;
	double a = sd.tau > 0.0 ? min (1.0, hop / (sd.tau * sd.rate)) : 1.0;
	double an2 = min (1.0, hop / (2.0 * sd.rate));						// null weights: ~2 s
	for (int i = 0; i < N; i++)
	{
		sd.x1[2 * i] = sd.in1[2 * i] * sd.win[i]; sd.x1[2 * i + 1] = sd.in1[2 * i + 1] * sd.win[i];
		sd.x2[2 * i] = sd.in2[2 * i] * sd.win[i]; sd.x2[2 * i + 1] = sd.in2[2 * i + 1] * sd.win[i];
	}
	fftw_execute (sd.p1);
	fftw_execute (sd.p2);

	int freeze = sd.freeze;													// one value for the whole frame
	// after a reset the first ~0.3 s still carry the old frequency and the retune transient: no learning
	int learn = !freeze && sd.settle == 0;
	if (!freeze && sd.settle > 0) sd.settle--;

	// in-filter power of the frame per antenna (filter bins in the native frame, may wrap below 0)
	int k0 = (int) ceil (sd.lo * (double) N / sd.rate), k1 = (int) floor (sd.hi * (double) N / sd.rate);
	int nb = max (1, min (N, k1 - k0 + 1));
	double P1 = 0.0, P2 = 0.0;
	for (int j = 0; j < nb; j++)
	{
		int k = (k0 + j) & (N - 1);
		P1 += sd.x1[2 * k] * sd.x1[2 * k] + sd.x1[2 * k + 1] * sd.x1[2 * k + 1];
		P2 += sd.x2[2 * k] * sd.x2[2 * k] + sd.x2[2 * k + 1] * sd.x2[2 * k + 1];
	}
	if (!freeze)
	{
		if (sd.bcnt == 0) sd.m1cur = sd.m2cur = DBL_MAX;
		sd.m1cur = min (sd.m1cur, P1); sd.m2cur = min (sd.m2cur, P2);
		if (++sd.bcnt >= (int) (0.25 * sd.rate / hop))
		{
			sd.bm1[sd.bi] = sd.m1cur; sd.bm2[sd.bi] = sd.m2cur;
			sd.bi = (sd.bi + 1) % 6; sd.bcnt = 0;
		}
	}
	double T1 = sd.bcnt > 0 ? sd.m1cur : DBL_MAX, T2 = sd.bcnt > 0 ? sd.m2cur : DBL_MAX;
	for (int i = 0; i < 6; i++) if (sd.bm1[i] > 0.0) { T1 = min (T1, sd.bm1[i]); T2 = min (T2, sd.bm2[i]); }
	int floors = T1 < DBL_MAX && T1 > 0.0 && T2 > 0.0;
	// the minimum of ~140 sums of nb exponential bins sits ~2.4 sigma under their mean
	double sg = 1.0 / sqrt ((double) nb), mean1 = floors ? T1 / max (0.4, 1.0 - 2.4 * sg) : 0.0;
	// RX2 to the RX1 noise floor with one factor (a per-bin factor goes wrong where a carrier sits)
	double g = floors ? sqrt (T1 / T2) : 1.0;
	double nbin = mean1 / nb;												// noise per bin, both antennas after g
	// pause = the whole filter at the noise floor on both antennas. Null/auto learn the noise there: chosen
	// by the sum of nb bins, the cells themselves are not biased low (picking quiet cells shrinks the noise
	// covariance, auto then takes the noise for the station)
	double th = mean1 * (1.0 + 3.0 * sg);
	int pause = learn && floors && P1 < th && g * g * P2 < th;

	for (int k = 0; k < N; k++)
	{
		double ar = sd.x1[2 * k], ai = sd.x1[2 * k + 1], br = g * sd.x2[2 * k], bi = g * sd.x2[2 * k + 1];
		double p1 = ar * ar + ai * ai, p2 = br * br + bi * bi;
		sd.x2[2 * k] = br; sd.x2[2 * k + 1] = bi;
		if (!learn) continue;													// TX leak, relay or retune transients are not the sky
		// only the filter bins learn: the pause detector looks there only, so outside it a station
		// counted as noise (tuned to it with CTUN, the null then cut it out and left the noise)
		if (((k - k0) & (N - 1)) >= nb) continue;
		if (sd.mode == 3)
		{
			if (!pause) continue;												// the station must not be nulled with it
			double ak = max (an2, 1.0 / ++sd.cnt[k]);						// plain mean until the average is full
			sd.r22[k] += ak * (p2 - sd.r22[k]);
			sd.cr[k] += ak * (ar * br + ai * bi - sd.cr[k]);
			sd.ci[k] += ak * (ai * br - ar * bi - sd.ci[k]);
			continue;
		}
		if (sd.mode == 4 && pause)												// noise covariance
		{
			double ak = max (an2, 1.0 / ++sd.cnt[k]);
			sd.q11[k] += ak * (p1 - sd.q11[k]); sd.q22[k] += ak * (p2 - sd.q22[k]);
			sd.qr[k] += ak * (ar * br + ai * bi - sd.qr[k]);
			sd.qi[k] += ak * (ai * br - ar * bi - sd.qi[k]);
		}
		if (sd.mode == 4)		// all cells: the station is the excess over Rn, gating would keep noise peaks only
		{
			double a4 = min (1.0, hop / (max (sd.tau, 1.0) * sd.rate));
			sd.r11[k] += a4 * (p1 - sd.r11[k]); sd.r22[k] += a4 * (p2 - sd.r22[k]);
			sd.cr[k] += a4 * (ar * br + ai * bi - sd.cr[k]);
			sd.ci[k] += a4 * (ai * br - ar * bi - sd.ci[k]);
			continue;
		}
		if (!floors || p1 + p2 < 6.0 * nbin) continue;						// < ~+5 dB over both noises
		sd.cnt[k] += 1.0;
		sd.r11[k] += a * (p1 - sd.r11[k]); sd.r22[k] += a * (p2 - sd.r22[k]);
		sd.cr[k] += a * (ar * br + ai * bi - sd.cr[k]);
		sd.ci[k] += a * (ai * br - ar * bi - sd.ci[k]);
	}

	// prefix sums over the filter bins j = 0..nb-1
	for (int j = 0; j < nb; j++)
	{
		int k = (k0 + j) & (N - 1);
		sd.s11[j + 1] = sd.s11[j] + sd.r11[k]; sd.s22[j + 1] = sd.s22[j] + sd.r22[k];
		sd.scr[j + 1] = sd.scr[j] + sd.cr[k]; sd.sci[j + 1] = sd.sci[j] + sd.ci[k];
		sd.t11[j + 1] = sd.t11[j] + sd.q11[k]; sd.t22[j + 1] = sd.t22[j] + sd.q22[k];
		sd.tr[j + 1] = sd.tr[j] + sd.qr[k]; sd.ti[j + 1] = sd.ti[j] + sd.qi[k];
	}
	// ramp-in: a weight from a handful of frames is random (null up to its +20 dB cap - RX2 noise all
	// over the band after a reset), so it is blended from RX1 alone by how much that bin has learned
	double ramp = 0.2 * sd.rate / hop, cb = 0.0;							// 1 - e^-t/0.2 s: 95% after 0.6 s of learning
	for (int j = 0; j < nb; j++) cb += sd.cnt[(k0 + j) & (N - 1)];
	cb /= nb;
	double v1, wr, wi;
	if (sd.mode == 4)
		sd_auto (sd.s11[nb], sd.s22[nb], sd.scr[nb], sd.sci[nb], sd.t11[nb], sd.t22[nb], sd.tr[nb], sd.ti[nb], &v1, &wr, &wi);
	else
		sd_weight (sd.mode, sd.s11[nb], sd.s22[nb], sd.scr[nb], sd.sci[nb], &v1, &wr, &wi);
	sd_blend (1.0 - exp (-cb / ramp), &v1, &wr, &wi);
	for (int k = 0; k < N; k++) { sd.wv[k] = v1; sd.wr[k] = wr; sd.wi[k] = wi; }
	int half = sd.sections <= 1 ? nb : nb / sd.sections / 2;
	for (int j = 0; j < nb; j++)
	{
		int lo = max (0, j - half), hi = min (nb, j + half + 1), k = (k0 + j) & (N - 1);
		if (sd.mode == 4)
			sd_auto (sd.s11[hi] - sd.s11[lo], sd.s22[hi] - sd.s22[lo], sd.scr[hi] - sd.scr[lo], sd.sci[hi] - sd.sci[lo],
				sd.t11[hi] - sd.t11[lo], sd.t22[hi] - sd.t22[lo], sd.tr[hi] - sd.tr[lo], sd.ti[hi] - sd.ti[lo],
				&sd.wv[k], &sd.wr[k], &sd.wi[k]);
		else sd_weight (sd.mode, sd.s11[hi] - sd.s11[lo], sd.s22[hi] - sd.s22[lo], sd.scr[hi] - sd.scr[lo], sd.sci[hi] - sd.sci[lo],
			&sd.wv[k], &sd.wr[k], &sd.wi[k]);
		sd_blend (1.0 - exp (-sd.cnt[k] / ramp), &sd.wv[k], &sd.wr[k], &sd.wi[k]);
	}

	for (int k = 0; k < N; k++)
	{
		double br = sd.x2[2 * k], bi = sd.x2[2 * k + 1];
		sd.y[2 * k] = sd.wv[k] * sd.x1[2 * k] + sd.wr[k] * br - sd.wi[k] * bi;
		sd.y[2 * k + 1] = sd.wv[k] * sd.x1[2 * k + 1] + sd.wr[k] * bi + sd.wi[k] * br;
	}
	fftw_execute (sd.pr);
	for (int i = 0; i < N; i++)
	{
		sd.ola[2 * i] += sd.y[2 * i] * sd.win[i] / N;
		sd.ola[2 * i + 1] += sd.y[2 * i + 1] * sd.win[i] / N;
	}
	memcpy (sd.outq, sd.ola, hop * sizeof (complex));
	memmove (sd.ola, sd.ola + 2 * hop, (N - hop) * sizeof (complex));
	memset (sd.ola + 2 * (N - hop), 0, hop * sizeof (complex));
	memmove (sd.in1, sd.in1 + 2 * hop, (N - hop) * sizeof (complex));
	memmove (sd.in2, sd.in2 + 2 * hop, (N - hop) * sizeof (complex));
}

// called by xdivEXT when mixing two receivers; 0 = off, the radar weights apply
int xsecdiv (int nsamples, double **in, double *out)
{
	if (!sd_init) return 0;
	EnterCriticalSection (&sd.cs);
	if (sd.mode == 0 || sd.N == 0) { LeaveCriticalSection (&sd.cs); return 0; }
	int base = sd.N - sd.hop;
	for (int i = 0; i < nsamples; i++)
	{
		sd.in1[2 * (base + sd.pos)] = in[0][2 * i]; sd.in1[2 * (base + sd.pos) + 1] = in[0][2 * i + 1];
		sd.in2[2 * (base + sd.pos)] = in[1][2 * i]; sd.in2[2 * (base + sd.pos) + 1] = in[1][2 * i + 1];
		out[2 * i] = sd.outq[2 * sd.pos]; out[2 * i + 1] = sd.outq[2 * sd.pos + 1];
		if (++sd.pos == sd.hop) { sd.pos = 0; sd_frame (); }
	}
	LeaveCriticalSection (&sd.cs);
	return 1;
}

// forget the weights (RX1 alone until relearned); the noise floor (1.5 s minimum) follows by itself
static void sd_reset (void)
{
	if (sd.N == 0) return;
	double *z[] = { sd.r11, sd.r22, sd.cr, sd.ci, sd.cnt, sd.q11, sd.q22, sd.qr, sd.qi };
	for (int i = 0; i < 9; i++) memset (z[i], 0, sd.N * sizeof (double));
	sd.settle = (int) (0.3 * sd.rate / sd.hop);
}

// the IQ centre moved: every bin now holds another stretch of the band and the NCO phases may have
// moved - the weights learn from scratch
PORT
void SetEXTDIVSectionsReset (void)
{
	if (!sd_init) return;
	EnterCriticalSection (&sd.cs);
	sd_reset ();
	LeaveCriticalSection (&sd.cs);
}

// TX (and the moments after it): the receivers see the transmitter leak and relay transients, which
// would teach the weights and noise floors the wrong thing; 1 = apply the weights but learn nothing
PORT
void SetEXTDIVSectionsFreeze (int freeze)
{
	sd.freeze = freeze;
}

// mode: 0 off, 1 MRC, 2 co-phase, 3 null, 4 auto; sections across the filter; tau: weight averaging, s;
// lo/hi: RX1 filter as offsets from the IQ centre in the native frame (I + jQ), Hz
PORT
void SetEXTDIVSections (int mode, int sections, double tau, int rate, int lo, int hi)
{
	if (InterlockedCompareExchange (&sd_init, 1, 0) == 0) InitializeCriticalSectionAndSpinCount (&sd.cs, 2500);
	EnterCriticalSection (&sd.cs);
	if (mode != 0 && rate > 0 && rate != sd.rate) { sd_free (); sd_alloc (rate); }
	if (mode != sd.mode) sd_reset ();										// fresh start: no stale weights
	sd.mode = mode; sd.sections = max (1, sections); sd.tau = tau; sd.lo = lo; sd.hi = hi;
	LeaveCriticalSection (&sd.cs);
}
