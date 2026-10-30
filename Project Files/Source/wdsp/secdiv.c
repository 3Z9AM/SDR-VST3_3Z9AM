/*  secdiv.c

This file is part of a program that implements a Software-Defined Radio.

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

Diversity per frequency section (own addition, prototype; offline twin: tools/divtest).
Replaces the radar's single complex weight of the EXT diversity (div.c, id 0, mixing two
receivers) by one weight per FFT bin inside the RX1 filter, averaged over nb/sections bins:
  mode 1 (MRC):      y = v^H z, v = main eigenvector of the 2x2 covariance, phase of RX1 -
                     RX2 rotated onto RX1 and weighted by its share of the signal
  mode 2 (co-phase): y = (z1 + g e^jp z2) / 2 - RX2 rotated onto RX1 and raised to its level,
                     so after the mix RX2-RX1 phase is 0 across the whole filter
  mode 3 (null):     y = z1 - w z2, w = <z1 z2*> / <|z2|^2> from pauses (whole filter at the noise
                     floor on both, 2 s) - RX2 as a noise antenna, local noise nulled per section;
                     |w| at most +20 dB over the noise-matched RX2; until a bin has learned (and while
                     the station never pauses) it uses one weight learned beside the filter (sd_outside)
  mode 4 (auto):     max-SINR per section with RX1 as the main antenna: the station = what RX1
                     hears (its excess over the noise covariance from pauses, Rx from all cells over tau),
                     kept at its RX1 level; MRC where the noise of the two antennas is independent, null
                     where it is shared, and what only RX2 hears is treated as interference; the noise of
                     the free IQ band beside the filter (sd_band) confirms pauses and stands in where a bin
                     has none yet (fast start, CW/digital)
Second pass (user idea, for tests; any mode): the output nulled against RX2 once more, y - w2 z2, w2 from pauses
(2 s) per section. Offline it cannot null a second direction - y is already one mix of z1 and z2, and a Wiener
first pass leaves y uncorrelated with z2 (w2 = 0); max-SINR is not a plain null, so something may be left.
Outside the filter one weight from the whole filter. Covariance only from cells with signal
(speech pauses hold the weights; null: only cells without it), RX2 scaled to the RX1 noise floor.
Streaming STFT, sqrt-Hann at 50% overlap, ~47 Hz bins: delay = one FFT (21 ms at 192 kHz).

*/

#include "comm.h"
#include <float.h>

typedef struct _secdiv
{
	int mode, sections, rate, lo, hi;		// lo/hi: filter as native IQ offsets, Hz
	int fine;							// 1: FFT 4x longer (~12 Hz bins, 4x the sections, +3 FFTs of delay)
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
	double *gc, *g11, *g22, *gr, *gi;					// auto: covariance of the cells the station sits in
	double *u11, *u22, *ucr, *uci;						// clean: prefix sums of the station covariance with it
	double *sp;											// clean auto: share of recent frames with the station in the bin
	double *o1, *o2, *otmp, ocr, oci, o22, ocnt, ofr;		// null: bins beside the filter, their weight
	int *oidx;
	int mcnt;							// band noise: frames until the local medians are refreshed
	double *md1, *md2, *b11, *b22, *bcr, *bci, *bcn;		// band noise: medians, covariance per free bin
	double *n11, *n22, *nr, *ni;							// band noise covariance of one cell at each filter bin
	int pass2;											// second pass on: y - w2 z2, learned in pauses
	double g;											// RX2 scale of the last frame (divtest inject)
	double cshare;										// mean clean share over the filter bins of the last frame (-1 off)
	double *p2n, *p222, *p2r, *p2i;						// its count, <|z2|^2>, <y z2*> per bin
	fftw_plan p1, p2, pr;
	CRITICAL_SECTION cs;
} secdiv;

static secdiv sd;
static volatile long sd_init;

// test knobs (tools/divtest inject k=v): 0 band noise on, 1 max-SINR regularization, 2 noise tau s, 3 pause sigmas,
// 4 weight smoothing Hz, 5 diagonal loading, 6 station covariance from cells over knob 7 x noise only (tau s, 0 off),
// 8 clean share: weights (1 - s) x default + s x clean (mixing the covariances instead flips to clean already at 0.5),
// < 0 auto per bin from the station presence (tau 9 s, share 0 at presence 10, 1 at 11),
// 12/13 weight smoothing Hz with auto where it sees the station / only noise (< 0: knob 4 everywhere) - inject 36 cases:
// 100/100 SDR 1.94, station -0.48, flicker 1.49; 50/200 2.21, -0.26, 1.40; 25/200 2.22, -0.18, 1.43 (25 Hz < 1 bin without fine)
static double sd_k[14] = { 1.0, 0.2, 2.0, 3.0, 100.0, 1.01, 0.0, 2.5, 1.0, 0.3, 0.1, 0.4, 50.0, 200.0 };

PORT
void SetEXTDIVSectionsKnob (int id, double v)
{
	if (id >= 0 && id < 14) sd_k[id] = v;
}

static void sd_free (void)
{
	if (sd.N == 0) return;
	fftw_destroy_plan (sd.p1); fftw_destroy_plan (sd.p2); fftw_destroy_plan (sd.pr);
	double **b[] = { &sd.win, &sd.in1, &sd.in2, &sd.outq, &sd.ola, &sd.x1, &sd.x2, &sd.y, &sd.cnt,
		&sd.r11, &sd.r22, &sd.cr, &sd.ci, &sd.s11, &sd.s22, &sd.scr, &sd.sci, &sd.wr, &sd.wi, &sd.wv,
		&sd.q11, &sd.q22, &sd.qr, &sd.qi, &sd.t11, &sd.t22, &sd.tr, &sd.ti, &sd.o1, &sd.o2, &sd.otmp,
		&sd.md1, &sd.md2, &sd.b11, &sd.b22, &sd.bcr, &sd.bci, &sd.bcn, &sd.n11, &sd.n22, &sd.nr, &sd.ni,
		&sd.p2n, &sd.p222, &sd.p2r, &sd.p2i, &sd.gc, &sd.g11, &sd.g22, &sd.gr, &sd.gi, &sd.u11, &sd.u22, &sd.ucr, &sd.uci, &sd.sp };
	for (int i = 0; i < sizeof (b) / sizeof (b[0]); i++) { _aligned_free (*b[i]); *b[i] = 0; }
	_aligned_free (sd.oidx); sd.oidx = 0;
	sd.N = 0;
}

static void sd_alloc (int rate)
{
	int N = 1024;
	while (N * 46.875 < rate) N <<= 1;
	if (sd.fine) N <<= 2;					// offline (divtest A12 vs A47): +0.3..1.1 dB S/N
	sd.N = N; sd.hop = N / 2; sd.pos = 0;
	sd.win = (double *) malloc0 (N * sizeof (double));
	for (int i = 0; i < N; i++) sd.win[i] = sqrt (0.5 - 0.5 * cos (TWOPI * i / N));
	double **c[] = { &sd.in1, &sd.in2, &sd.ola, &sd.x1, &sd.x2, &sd.y };
	for (int i = 0; i < sizeof (c) / sizeof (c[0]); i++) *c[i] = (double *) malloc0 (N * sizeof (complex));
	sd.outq = (double *) malloc0 (sd.hop * sizeof (complex));
	double **r[] = { &sd.cnt, &sd.r11, &sd.r22, &sd.cr, &sd.ci, &sd.wr, &sd.wi, &sd.wv, &sd.q11, &sd.q22, &sd.qr, &sd.qi,
		&sd.o1, &sd.o2, &sd.otmp, &sd.md1, &sd.md2, &sd.b11, &sd.b22, &sd.bcr, &sd.bci, &sd.bcn, &sd.n11, &sd.n22, &sd.nr, &sd.ni,
		&sd.p2n, &sd.p222, &sd.p2r, &sd.p2i, &sd.gc, &sd.g11, &sd.g22, &sd.gr, &sd.gi, &sd.sp };
	for (int i = 0; i < sizeof (r) / sizeof (r[0]); i++) *r[i] = (double *) malloc0 (N * sizeof (double));
	sd.oidx = (int *) malloc0 (N * sizeof (int));
	double **s[] = { &sd.s11, &sd.s22, &sd.scr, &sd.sci, &sd.t11, &sd.t22, &sd.tr, &sd.ti, &sd.u11, &sd.u22, &sd.ucr, &sd.uci };
	for (int i = 0; i < sizeof (s) / sizeof (s[0]); i++) *s[i] = (double *) malloc0 ((N + 1) * sizeof (double));
	sd.p1 = fftw_plan_dft_1d (N, (fftw_complex *) sd.x1, (fftw_complex *) sd.x1, FFTW_FORWARD, FFTW_ESTIMATE);
	sd.p2 = fftw_plan_dft_1d (N, (fftw_complex *) sd.x2, (fftw_complex *) sd.x2, FFTW_FORWARD, FFTW_ESTIMATE);
	sd.pr = fftw_plan_dft_1d (N, (fftw_complex *) sd.y, (fftw_complex *) sd.y, FFTW_BACKWARD, FFTW_ESTIMATE);
	sd.rate = rate;
}

// b = 0: the base weight (bv, bwr, bwi: RX1 alone, or the null learned beside the filter), 1: the weight as computed
static void sd_blend (double b, double bv, double bwr, double bwi, double *v1, double *wr, double *wi)
{
	*v1 = (1.0 - b) * bv + b * *v1; *wr = (1.0 - b) * bwr + b * *wr; *wi = (1.0 - b) * bwi + b * *wi;
}

static int sd_cmp (const void *a, const void *b)
{
	double x = *(const double *) a, y = *(const double *) b;
	return (x > y) - (x < y);
}

// Null fast start (and its weight while the station never pauses - CW, digital): one weight learned beside
// the filter, 300..2300 Hz past each edge, in every frame where those bins sit at the noise - not only in
// speech pauses. The noise there has the RX1/RX2 signature it has in the filter when its transfer is flat
// over a few kHz (one wideband source; recordings 25.10: same coherence outside and in, divtest O). Left out:
// bins a station or carrier occupies (2 s mean over 2x the median of the side bins), the IQ DC spur and the
// band edges. x2 already scaled by g.
static void sd_outside (int k0, int nb)
{
	int N = sd.N, gap = (int) ceil (300.0 * N / sd.rate), w = (int) (2000.0 * N / sd.rate);
	int dc = max (2, (int) (200.0 * N / sd.rate)), edge = (int) (0.45 * N), nc = 0;
	double an2 = min (1.0, sd.hop / (2.0 * sd.rate)), ak = max (an2, 1.0 / ++sd.ofr);
	for (int side = 0; side < 2; side++)
		for (int j = 0; j < w; j++)
		{
			int ks = side == 0 ? k0 - gap - w + j : k0 + nb + gap + j;		// signed native bin
			if (abs (ks) <= dc || abs (ks) > edge) continue;
			int k = ks & (N - 1);
			sd.o1[k] += ak * (sd.x1[2 * k] * sd.x1[2 * k] + sd.x1[2 * k + 1] * sd.x1[2 * k + 1] - sd.o1[k]);
			sd.o2[k] += ak * (sd.x2[2 * k] * sd.x2[2 * k] + sd.x2[2 * k + 1] * sd.x2[2 * k + 1] - sd.o2[k]);
			sd.oidx[nc++] = k;
		}
	if (nc < 8 || sd.ofr * sd.hop < 0.25 * sd.rate) return;				// too few bins / means not settled
	for (int i = 0; i < nc; i++) sd.otmp[i] = sd.o1[sd.oidx[i]];
	qsort (sd.otmp, nc, sizeof (double), sd_cmp);
	double med1 = sd.otmp[nc / 2];
	for (int i = 0; i < nc; i++) sd.otmp[i] = sd.o2[sd.oidx[i]];
	qsort (sd.otmp, nc, sizeof (double), sd_cmp);
	double med2 = sd.otmp[nc / 2];
	double P1 = 0.0, P2 = 0.0, Cr = 0.0, Ci = 0.0; int nf = 0;
	for (int i = 0; i < nc; i++)
	{
		int k = sd.oidx[i];
		if (sd.o1[k] > 2.0 * med1 || sd.o2[k] > 2.0 * med2) continue;		// a station or carrier sits there
		double ar = sd.x1[2 * k], ai = sd.x1[2 * k + 1], br = sd.x2[2 * k], bi = sd.x2[2 * k + 1];
		P1 += ar * ar + ai * ai; P2 += br * br + bi * bi;
		Cr += ar * br + ai * bi; Ci += ai * br - ar * bi;
		nf++;
	}
	// the free bins at the noise in this frame (as the pause detector: sum under 1 + 3 sigma)
	double th = 1.0 + 3.0 / sqrt ((double) max (1, nf));
	if (nf < 8 || P1 > th * nf * med1 || P2 > th * nf * med2) return;
	double ao = max (an2, 1.0 / ++sd.ocnt);
	sd.o22 += ao * (P2 - sd.o22); sd.ocr += ao * (Cr - sd.ocr); sd.oci += ao * (Ci - sd.oci);
}

// Band noise (user idea; divtest B5kn / AB): the noise covariance of every free cell over the whole IQ band,
// per bin, ~2 s. Left out: bins a station or carrier occupies (2 s mean over 2x the median of +-1 kHz), cells
// over 4x it (a station keying up), the IQ DC spur, the band edges (> 0.45 rate) and the filter +-300 Hz, so
// nothing the filter hears teaches it. Each filter bin gets the noise covariance of one cell from the free bins
// within +-5 kHz (offline: +-1 kHz too narrow, the whole band pulls in QRM from elsewhere): powers = plain bin
// means, RX1/RX2 coherence averaged with every bin counted alike - power-weighted, a strong local QRM a few kHz
// away that the filter does not hear set it (7.1 MHz 25.10: S/N -11.8 dB vs RX1, -3.5 so). Used by max-SINR
// (pause check, noise where a bin has no pauses yet). x2 already scaled by g.
static void sd_band (int learn, int k0, int nb)
{
	int N = sd.N, h2 = N / 2;
	double bin = (double) sd.rate / N, an2 = min (1.0, sd.hop / (2.0 * sd.rate));
	int gap = (int) ceil (300.0 / bin), dc = max (2, (int) (200.0 / bin)), edge = (int) (0.45 * N);
	if (learn)
	{
		double ak = max (an2, 1.0 / ++sd.ofr);
		for (int k = 0; k < N; k++)
		{
			sd.o1[k] += ak * (sd.x1[2 * k] * sd.x1[2 * k] + sd.x1[2 * k + 1] * sd.x1[2 * k + 1] - sd.o1[k]);
			sd.o2[k] += ak * (sd.x2[2 * k] * sd.x2[2 * k] + sd.x2[2 * k + 1] * sd.x2[2 * k + 1] - sd.o2[k]);
		}
	}
	if (learn && sd.ofr * sd.hop >= 0.25 * sd.rate)						// means settled
	{
		if (--sd.mcnt <= 0)
		{
			// medians over +-1 kHz on a grid of a quarter of that, every 0.25 s (the means move over 2 s)
			int w = max (2, (int) (1000.0 / bin)), st = max (1, w / 4);
			double *m[2] = { sd.o1, sd.o2 }, *d[2] = { sd.md1, sd.md2 };
			for (int i = 0; i < N; i += st)
			{
				int lo = max (0, i - w), hi = min (N, i + w), n = hi - lo;
				for (int a = 0; a < 2; a++)
				{
					for (int j = lo; j < hi; j++) sd.otmp[j - lo] = m[a][(j - h2) & (N - 1)];
					qsort (sd.otmp, n, sizeof (double), sd_cmp);
					for (int j = i; j < min (N, i + st); j++) d[a][(j - h2) & (N - 1)] = sd.otmp[n / 2];
				}
			}
			sd.mcnt = max (1, (int) (0.25 * sd.rate / sd.hop));
		}
		int learned = 0;
		for (int ks = -h2; ks < h2; ks++)										// signed native bin
		{
			if (abs (ks) <= dc || abs (ks) > edge || (ks >= k0 - gap && ks < k0 + nb + gap)) continue;
			int k = ks & (N - 1);
			double m1 = sd.md1[k], m2 = sd.md2[k];
			if (m1 <= 0.0 || sd.o1[k] > 2.0 * m1 || sd.o2[k] > 2.0 * m2) continue;	// a station or carrier sits there
			double ar = sd.x1[2 * k], ai = sd.x1[2 * k + 1], br = sd.x2[2 * k], bi = sd.x2[2 * k + 1];
			double p1 = ar * ar + ai * ai, p2 = br * br + bi * bi;
			if (p1 > 4.0 * m1 || p2 > 4.0 * m2) continue;
			double a = max (an2, 1.0 / ++sd.bcn[k]);
			sd.b11[k] += a * (p1 - sd.b11[k]); sd.b22[k] += a * (p2 - sd.b22[k]);
			sd.bcr[k] += a * (ar * br + ai * bi - sd.bcr[k]);
			sd.bci[k] += a * (ai * br - ar * bi - sd.bci[k]);
			learned = 1;
		}
		if (learned) sd.ocnt += 1.0;
	}
	// prefix sums over the signed bins i = ks + N/2 of the learned free bins (s*, t* as scratch - the filter
	// sums are made after this): count, plain means, normalized covariance
	double *S[7] = { sd.s11, sd.s22, sd.scr, sd.sci, sd.t11, sd.t22, sd.tr };
	for (int i = 0; i < N; i++)
	{
		int k = (i - h2) & (N - 1);
		double v[7] = { 0 };
		if (sd.bcn[k] > 0.0 && sd.o1[k] > 0.0 && sd.o2[k] > 0.0)
		{
			double q = 1.0 / sqrt (sd.o1[k] * sd.o2[k]);
			v[0] = 1.0; v[1] = sd.o1[k]; v[2] = sd.o2[k];
			v[3] = sd.b11[k] / sd.o1[k]; v[4] = sd.b22[k] / sd.o2[k]; v[5] = q * sd.bcr[k]; v[6] = q * sd.bci[k];
		}
		for (int c = 0; c < 7; c++) S[c][i + 1] = S[c][i] + v[c];
	}
	int hw = (int) (5000.0 / bin);
	for (int j = 0; j < nb; j++)
	{
		int i = k0 + j + h2, lo = max (0, min (N, i - hw)), hi = max (0, min (N, i + hw + 1)), k = (k0 + j) & (N - 1);
		double W[7];
		for (int c = 0; c < 7; c++) W[c] = S[c][hi] - S[c][lo];
		sd.n11[k] = sd.n22[k] = sd.nr[k] = sd.ni[k] = 0.0;
		if (W[0] < 1.0 || W[3] <= 0.0 || W[4] <= 0.0) continue;
		sd.n11[k] = W[1] / W[0]; sd.n22[k] = W[2] / W[0];
		double sc = sqrt (sd.n11[k] * sd.n22[k] / (W[3] * W[4]));				// coherence of the gated cells kept
		sd.nr[k] = sc * W[5]; sd.ni[k] = sc * W[6];
	}
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
	double den = max (A - a, 0.0) + sd_k[1] * a, Sr = Cr - cr, Si = Ci - ci;
	double hr = Sr / den, hi = -Si / den;
	double E = max (0.0, B - b - (Sr * Sr + Si * Si) / den);
	double p = sd_k[5] * a, r = sd_k[5] * (b + E);								// 1% diagonal loading
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

// clean slider: the weight (v1, wr, wi: the default from all cells) moved toward the one from the clean station covariance -
// both keep the station at its RX1 level, so does the mix
static void sd_mix (double s, double A, double B, double Cr, double Ci, double a, double b, double cr, double ci,
	double *v1, double *wr, double *wi)
{
	double v, r, i;
	sd_auto (A, B, Cr, Ci, a, b, cr, ci, &v, &r, &i);
	sd_blend (s, *v1, *wr, *wi, &v, &r, &i);
	*v1 = v; *wr = r; *wi = i;
}

// clean share of bin k: the slider, or auto - clean where the station has been lately (its level kept),
// the default where only noise is (it lowers the background)
static double sd_cf = 1.0;							// auto sensitivity: presence thresholds x this
static double sd_share (int k)
{
	if (sd_k[8] >= 0.0) return sd_k[8];
	return min (1.0, max (0.0, (sd.sp[k] - sd_cf * sd_k[10]) / (sd_cf * (sd_k[11] - sd_k[10]))));
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
	double an2 = min (1.0, hop / (sd_k[2] * sd.rate));					// null weights: ~2 s
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
	// the minimum of ~140 sums of nb exponential bins sits ~2.4 sigma under their mean (fine: ~35 sums, ~2.0)
	double sg = 1.0 / sqrt ((double) nb), mean1 = floors ? T1 / max (0.4, 1.0 - (sd.fine ? 2.0 : 2.4) * sg) : 0.0;
	// RX2 to the RX1 noise floor with one factor (a per-bin factor goes wrong where a carrier sits)
	double g = floors ? sqrt (T1 / T2) : 1.0;
	sd.g = g;
	double nbin = mean1 / nb;												// noise per bin, both antennas after g
	// pause = the whole filter at the noise floor on both antennas. Null/auto learn the noise there: chosen
	// by the sum of nb bins, the cells themselves are not biased low (picking quiet cells shrinks the noise
	// covariance, auto then takes the noise for the station)
	double th = mean1 * (1.0 + sd_k[3] * sg);
	int pause = learn && floors && P1 < th && g * g * P2 < th;
	// max-SINR: with the band noise known, a pause must also sit at it (+1 dB for its spread over the band) -
	// a station that never pauses (CW, digital) holds the 1.5 s minimum up and was learned as the noise, the
	// station then counted as gone; without pauses the band noise stands in (blend below)
	if (pause && sd.mode == 4 && sd.ocnt > 0.0 && sd_k[0] != 0.0)
	{
		double B1 = 0.0, B2 = 0.0; int nn = 0;
		for (int j = 0; j < nb; j++) { int k = (k0 + j) & (N - 1); B1 += sd.n11[k]; B2 += sd.n22[k]; nn += sd.n11[k] > 0.0; }
		double tb = 1.26 * (1.0 + sd_k[3] * sg);
		if (nn == nb && (P1 > tb * B1 || g * g * P2 > tb * B2)) pause = 0;
	}
	int outside = learn && sd.mode == 3;									// after x2 is scaled below
	double ramp0 = 0.2 * sd.rate / hop;										// clean: pause frames before the residual gate

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
		// all cells: the station is the excess over Rn, gating would keep noise peaks only. Over tau, not 1 s:
		// the station phase wanders (RX2-RX1 steadiness ~0.9 over 1 s, 0.4-0.6 over 10 s) - recordings 25./28.10
		// through this code (divtest replay): +0.7..3.9 dB S/N on all 17, station level kept
		if (sd.mode == 4)
		{
			sd.r11[k] += a * (p1 - sd.r11[k]); sd.r22[k] += a * (p2 - sd.r22[k]);
			sd.cr[k] += a * (ar * br + ai * bi - sd.cr[k]);
			sd.ci[k] += a * (ai * br - ar * bi - sd.ci[k]);
			// station present: RX1 over its noise once the part RX2 shares is taken out (Wiener null from the
			// pauses) - noise shared by both antennas (local QRM) must not pass, its h would point at the noise
			// (or from the band noise while the bin has no pauses yet - a station that never pauses)
			double e1 = p1, ne = nbin, *Q[4] = { 0 };
			if (sd.cnt[k] > ramp0 && sd.q22[k] > 0.0) { Q[0] = sd.q11; Q[1] = sd.q22; Q[2] = sd.qr; Q[3] = sd.qi; }
			else if (sd.n22[k] > 0.0) { Q[0] = sd.n11; Q[1] = sd.n22; Q[2] = sd.nr; Q[3] = sd.ni; }
			if (Q[0])
			{
				double cr0 = Q[2][k] / Q[1][k], ci0 = Q[3][k] / Q[1][k];
				double er = ar - (cr0 * br - ci0 * bi), ei = ai - (cr0 * bi + ci0 * br);
				e1 = er * er + ei * ei; ne = max (1e-3 * Q[0][k], Q[0][k] - (Q[2][k] * Q[2][k] + Q[3][k] * Q[3][k]) / Q[1][k]);
			}
			if (sd_k[6] > 0.0 && floors && e1 > sd_k[7] * ne)
			{
				double ag = max (min (1.0, hop / (sd_k[6] * sd.rate)), 1.0 / ++sd.gc[k]);
				sd.g11[k] += ag * (p1 - sd.g11[k]); sd.g22[k] += ag * (p2 - sd.g22[k]);
				sd.gr[k] += ag * (ar * br + ai * bi - sd.gr[k]);
				sd.gi[k] += ag * (ai * br - ar * bi - sd.gi[k]);
			}
			if (sd_k[6] > 0.0 && floors) sd.sp[k] += min (1.0, hop / (sd_k[9] * sd.rate)) * ((e1 > sd_k[7] * ne) - sd.sp[k]);
			continue;
		}
		if (!floors || p1 + p2 < 6.0 * nbin) continue;						// < ~+5 dB over both noises
		sd.cnt[k] += 1.0;
		sd.r11[k] += a * (p1 - sd.r11[k]); sd.r22[k] += a * (p2 - sd.r22[k]);
		sd.cr[k] += a * (ar * br + ai * bi - sd.cr[k]);
		sd.ci[k] += a * (ai * br - ar * bi - sd.ci[k]);
	}

	if (outside) sd_outside (k0, nb);
	if (sd.mode == 4) sd_band (learn, k0, nb);

	// ramp-in: a weight from a handful of frames is random (null up to its +20 dB cap - RX2 noise all
	// over the band after a reset), so it is blended from RX1 alone by how much that bin has learned
	double ramp = 0.2 * sd.rate / hop, cb = 0.0;							// 1 - e^-t/0.2 s: 95% after 0.6 s of learning
	// max-SINR: noise covariance from the pauses, the band noise for the share a bin has not learned in pauses
	double ob = sd.mode == 4 && sd_k[0] != 0.0 ? 1.0 - exp (-sd.ocnt / ramp) : 0.0;
	// prefix sums over the filter bins j = 0..nb-1 (u*: the station covariance with the clean one, sd_mix)
	int cl = sd.mode == 4 && sd_k[6] > 0.0 && sd_k[8] != 0.0;
	double sm0 = 0.0;
	for (int j = 0; j < nb; j++)
	{
		int k = (k0 + j) & (N - 1);
		double bp = ob > 0.0 ? 1.0 - exp (-sd.cnt[k] / ramp) : 1.0, bb = (1.0 - bp) * ob;
		sd.s11[j + 1] = sd.s11[j] + sd.r11[k]; sd.s22[j + 1] = sd.s22[j] + sd.r22[k];
		sd.scr[j + 1] = sd.scr[j] + sd.cr[k]; sd.sci[j + 1] = sd.sci[j] + sd.ci[k];
		if (cl)
		{
			double bg = 1.0 - exp (-sd.gc[k] / (0.05 * sd.rate / hop)), br = 1.0 - bg;
			sd.u11[j + 1] = sd.u11[j] + br * sd.r11[k] + bg * sd.g11[k]; sd.u22[j + 1] = sd.u22[j] + br * sd.r22[k] + bg * sd.g22[k];
			sd.ucr[j + 1] = sd.ucr[j] + br * sd.cr[k] + bg * sd.gr[k]; sd.uci[j + 1] = sd.uci[j] + br * sd.ci[k] + bg * sd.gi[k];
			sm0 += sd_share (k) / nb;
		}
		sd.t11[j + 1] = sd.t11[j] + bp * sd.q11[k] + bb * sd.n11[k]; sd.t22[j + 1] = sd.t22[j] + bp * sd.q22[k] + bb * sd.n22[k];
		sd.tr[j + 1] = sd.tr[j] + bp * sd.qr[k] + bb * sd.nr[k]; sd.ti[j + 1] = sd.ti[j] + bp * sd.qi[k] + bb * sd.ni[k];
	}
	// learned so far: pause frames, for max-SINR also the band noise
	double lc = sd.mode == 4 && sd_k[0] != 0.0 ? sd.ocnt : 0.0;
	for (int j = 0; j < nb; j++) cb += sd.cnt[(k0 + j) & (N - 1)] + lc;
	cb /= nb;
	// base the bins start from: RX1 alone, for null the weight from beside the filter (itself ramped in)
	double bv = 1.0, bwr = 0.0, bwi = 0.0;
	if (sd.mode == 3 && sd.ocnt > 0.0)
	{
		sd_weight (3, 0.0, sd.o22, sd.ocr, sd.oci, &bv, &bwr, &bwi);
		sd_blend (1.0 - exp (-sd.ocnt / ramp), 1.0, 0.0, 0.0, &bv, &bwr, &bwi);
	}
	double v1, wr, wi;
	if (sd.mode == 4)
	{
		sd_auto (sd.s11[nb], sd.s22[nb], sd.scr[nb], sd.sci[nb], sd.t11[nb], sd.t22[nb], sd.tr[nb], sd.ti[nb], &v1, &wr, &wi);
		sd.cshare = cl ? sm0 : -1.0;
		if (cl) sd_mix (sm0, sd.u11[nb], sd.u22[nb], sd.ucr[nb], sd.uci[nb], sd.t11[nb], sd.t22[nb], sd.tr[nb], sd.ti[nb], &v1, &wr, &wi);
	}
	else
		sd_weight (sd.mode, sd.s11[nb], sd.s22[nb], sd.scr[nb], sd.sci[nb], &v1, &wr, &wi);
	sd_blend (1.0 - exp (-cb / ramp), bv, bwr, bwi, &v1, &wr, &wi);
	for (int k = 0; k < N; k++) { sd.wv[k] = v1; sd.wr[k] = wr; sd.wi[k] = wi; }
	int half = sd.sections <= 1 ? nb : nb / (sd.sections << (2 * sd.fine)) / 2;
	for (int j = 0; j < nb; j++)
	{
		int lo = max (0, j - half), hi = min (nb, j + half + 1), k = (k0 + j) & (N - 1);
		if (sd.mode == 4)
			sd_auto (sd.s11[hi] - sd.s11[lo], sd.s22[hi] - sd.s22[lo], sd.scr[hi] - sd.scr[lo], sd.sci[hi] - sd.sci[lo],
				sd.t11[hi] - sd.t11[lo], sd.t22[hi] - sd.t22[lo], sd.tr[hi] - sd.tr[lo], sd.ti[hi] - sd.ti[lo],
				&sd.wv[k], &sd.wr[k], &sd.wi[k]);
		if (sd.mode == 4 && cl)
			sd_mix (sd_share (k), sd.u11[hi] - sd.u11[lo], sd.u22[hi] - sd.u22[lo], sd.ucr[hi] - sd.ucr[lo], sd.uci[hi] - sd.uci[lo],
				sd.t11[hi] - sd.t11[lo], sd.t22[hi] - sd.t22[lo], sd.tr[hi] - sd.tr[lo], sd.ti[hi] - sd.ti[lo],
				&sd.wv[k], &sd.wr[k], &sd.wi[k]);
		else if (sd.mode != 4) sd_weight (sd.mode, sd.s11[hi] - sd.s11[lo], sd.s22[hi] - sd.s22[lo], sd.scr[hi] - sd.scr[lo], sd.sci[hi] - sd.sci[lo],
			&sd.wv[k], &sd.wr[k], &sd.wi[k]);
		sd_blend (1.0 - exp (-(sd.cnt[k] + lc) / ramp), bv, bwr, bwi, &sd.wv[k], &sd.wr[k], &sd.wi[k]);
	}
	// weights smoothed over neighbouring filter bins (triangle +-100 Hz): a weight of its own per bin wanders and cut
	// stripes into the band (user: visible on the zoomed panadapter, sounds like NR). Recordings (divtest replay,
	// fine 60): stripes 0.63-0.84 -> 0.41-0.60 dB, S/N +0.6..2.2 dB on 15/17; normal FFT about the same.
	// s11/s22/scr are free here.
	// max-SINR only (measured there; MRC with a fast phase slope across the filter loses - test secdiv)
	int sa = cl && sd_k[8] < 0.0 && sd_k[12] >= 0.0 && sd_k[13] >= 0.0;
	int sm = sd.mode == 4 ? (int) ((sa ? max (sd_k[12], sd_k[13]) : sd_k[4]) * N / sd.rate) : 0;
	if (sm > 0)
	{
		for (int j = 0; j < nb; j++) { int k = (k0 + j) & (N - 1); sd.s11[j] = sd.wv[k]; sd.s22[j] = sd.wr[k]; sd.scr[j] = sd.wi[k]; }
		for (int j = 0; j < nb; j++)
		{
			int k = (k0 + j) & (N - 1);
			int sj = sa ? (int) ((sd_k[13] + (sd_k[12] - sd_k[13]) * sd_share (k)) * N / sd.rate) : sm;
			double v = 0.0, r = 0.0, i = 0.0, ws = 0.0;
			for (int d = -sj; d <= sj; d++)
			{
				int jj = j + d;
				if (jj < 0 || jj >= nb) continue;
				double w = sj + 1 - abs (d);
				v += w * sd.s11[jj]; r += w * sd.s22[jj]; i += w * sd.scr[jj]; ws += w;
			}
			sd.wv[k] = v / ws; sd.wr[k] = r / ws; sd.wi[k] = i / ws;
		}
	}

	for (int k = 0; k < N; k++)
	{
		double br = sd.x2[2 * k], bi = sd.x2[2 * k + 1];
		sd.y[2 * k] = sd.wv[k] * sd.x1[2 * k] + sd.wr[k] * br - sd.wi[k] * bi;
		sd.y[2 * k + 1] = sd.wv[k] * sd.x1[2 * k + 1] + sd.wr[k] * bi + sd.wi[k] * br;
	}
	if (sd.pass2)
	{
		// learn in the pauses of the first pass (the station must not be nulled), sum over the sections, ramp in
		for (int j = 0; j < nb; j++)
		{
			int k = (k0 + j) & (N - 1);
			double yr = sd.y[2 * k], yi = sd.y[2 * k + 1], br = sd.x2[2 * k], bi = sd.x2[2 * k + 1];
			if (pause)
			{
				double ak = max (an2, 1.0 / ++sd.p2n[k]);
				sd.p222[k] += ak * (br * br + bi * bi - sd.p222[k]);
				sd.p2r[k] += ak * (yr * br + yi * bi - sd.p2r[k]);
				sd.p2i[k] += ak * (yi * br - yr * bi - sd.p2i[k]);
			}
			sd.s22[j + 1] = sd.s22[j] + sd.p222[k]; sd.scr[j + 1] = sd.scr[j] + sd.p2r[k]; sd.sci[j + 1] = sd.sci[j] + sd.p2i[k];
		}
		for (int j = 0; j < nb; j++)
		{
			int lo = max (0, j - half), hi = min (nb, j + half + 1), k = (k0 + j) & (N - 1);
			double v, wr, wi, br = sd.x2[2 * k], bi = sd.x2[2 * k + 1];
			sd_weight (3, 0.0, sd.s22[hi] - sd.s22[lo], sd.scr[hi] - sd.scr[lo], sd.sci[hi] - sd.sci[lo], &v, &wr, &wi);
			sd_blend (1.0 - exp (-sd.p2n[k] / ramp), 1.0, 0.0, 0.0, &v, &wr, &wi);
			sd.y[2 * k] += wr * br - wi * bi;
			sd.y[2 * k + 1] += wr * bi + wi * br;
		}
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
	double *z[] = { sd.r11, sd.r22, sd.cr, sd.ci, sd.cnt, sd.q11, sd.q22, sd.qr, sd.qi, sd.o1, sd.o2,
		sd.b11, sd.b22, sd.bcr, sd.bci, sd.bcn, sd.n11, sd.n22, sd.nr, sd.ni, sd.p2n, sd.p222, sd.p2r, sd.p2i, sd.gc, sd.g11, sd.g22, sd.gr, sd.gi, sd.sp };
	for (int i = 0; i < sizeof (z) / sizeof (z[0]); i++) memset (z[i], 0, sd.N * sizeof (double));
	sd.ocr = sd.oci = sd.o22 = sd.ocnt = sd.ofr = 0.0;
	sd.settle = (int) (0.3 * sd.rate / sd.hop);
	sd.mcnt = 0;
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

// the weights applied now, for a display (DiversityForm radar): v1, wr, wi per filter bin j = 0..nb-1 in the
// native frame (as sd_frame: y = v1 z1 + w z2, RX2 noise-matched); returns nb (at most size), 0 when off
PORT
int GetEXTDIVSectionsWeights (double *out, int size)
{
	if (!sd_init) return 0;
	EnterCriticalSection (&sd.cs);
	int n = 0;
	if (sd.mode != 0 && sd.N != 0)
	{
		int N = sd.N, k0 = (int) ceil (sd.lo * (double) N / sd.rate), k1 = (int) floor (sd.hi * (double) N / sd.rate);
		n = min (size, max (1, min (N, k1 - k0 + 1)));
		for (int j = 0; j < n; j++)
		{
			int k = (k0 + j) & (N - 1);
			out[3 * j] = sd.wv[k]; out[3 * j + 1] = sd.wr[k]; out[3 * j + 2] = sd.wi[k];
		}
	}
	LeaveCriticalSection (&sd.cs);
	return n;
}

// clean (max-SINR): the station estimate (h) only from the cells it sits in (RX1 with the noise RX2 shares nulled out over
// 2.5x its noise per bin - a plain RX1 gate let strong local noise in and h pointed at it; 0.3 s), held
// through pauses and empty bins. As computed from all cells over 0.2 s, h there is the noise itself and the weights
// gate like an NR: tools/divtest inject (real noise of both antennas + a real station moved in, 80/40/20 m 29.10,
// 36 cases) SDR +2.0 dB vs RX1 with the noise gain of the weights flickering 1.6 dB in time and the weak station
// -0.7..-1.7 dB; clean +0.75 dB (= pure diversity, as the oracle from the true covariances), flicker 0.3 dB,
// station level kept (worst case +0.1 dB). Test secdiv with clean (6=0.3): noise antenna 13.5 dB (default 14.1),
// no pauses 23.4 (22.9), equal antennas 21.1 (21.2). Every knob of the default (regularization, taus, sections) only trades SDR for flicker.
// pct 0..100 = share of the clean weight, < 0 auto per bin (sd_share) (0 = off, the default h; in between both SDR and flicker in between)
PORT
void SetEXTDIVSectionsClean (int pct)
{
	if (InterlockedCompareExchange (&sd_init, 1, 0) == 0) InitializeCriticalSectionAndSpinCount (&sd.cs, 2500);
	EnterCriticalSection (&sd.cs);
	double v = pct != 0 ? 0.3 : 0.0;
	if (v != sd_k[6] && sd.N != 0)
	{
		double *z[] = { sd.gc, sd.g11, sd.g22, sd.gr, sd.gi, sd.sp };
		for (int i = 0; i < 6; i++) memset (z[i], 0, sd.N * sizeof (double));
	}
	sd_k[6] = v;
	sd_k[8] = pct < 0 ? -1.0 : min (100, pct) / 100.0;
	LeaveCriticalSection (&sd.cs);
}

// the RX2 scale the weights of the last frame apply to (y = v1 z1 + w g z2), for tools/divtest inject
PORT
double GetEXTDIVSectionsGain (void)
{
	return sd.g;
}

// auto: how readily a bin counts as station, 0..100 (50 = presence thresholds 0.1/0.4; each 25 halves them = more clean)
PORT
void SetEXTDIVSectionsCleanSens (int s)
{
	sd_cf = pow (2.0, (50 - min (100, max (0, s))) / 25.0);
}

// mean clean share of the filter bins in the last frame, 0..1 (-1: clean off or not max-SINR)
PORT
double GetEXTDIVSectionsCleanShare (void)
{
	return sd.mode == 4 ? sd.cshare : -1.0;
}

// fine: FFT 4x longer (bins ~12 Hz, delay ~85 ms at 192 kHz instead of ~21); the noise floors are in other
// units at another N, so they start again with the weights
PORT
void SetEXTDIVSectionsFine (int fine)
{
	if (InterlockedCompareExchange (&sd_init, 1, 0) == 0) InitializeCriticalSectionAndSpinCount (&sd.cs, 2500);
	EnterCriticalSection (&sd.cs);
	fine = fine != 0;
	if (fine != sd.fine)
	{
		sd.fine = fine;
		if (sd.N != 0) { int rate = sd.rate; sd_free (); sd_alloc (rate); sd_reset (); }
		memset (sd.bm1, 0, sizeof (sd.bm1)); memset (sd.bm2, 0, sizeof (sd.bm2)); sd.bcnt = 0; sd.bi = 0;
	}
	LeaveCriticalSection (&sd.cs);
}

// second pass on/off (test): starts learning from zero, the first pass keeps its weights
PORT
void SetEXTDIVSectionsPass2 (int on)
{
	if (InterlockedCompareExchange (&sd_init, 1, 0) == 0) InitializeCriticalSectionAndSpinCount (&sd.cs, 2500);
	EnterCriticalSection (&sd.cs);
	on = on != 0;
	if (on != sd.pass2 && sd.N != 0)
	{
		double *z[] = { sd.p2n, sd.p222, sd.p2r, sd.p2i };
		for (int i = 0; i < 4; i++) memset (z[i], 0, sd.N * sizeof (double));
	}
	sd.pass2 = on;
	LeaveCriticalSection (&sd.cs);
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
