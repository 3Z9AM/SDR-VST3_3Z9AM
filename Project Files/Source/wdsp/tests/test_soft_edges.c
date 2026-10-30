// Test of the soft filter edge (fir.c: fir_soft_edges, fir_bandpass_soft).
// Build and run: wdsp\tests\run_test_soft_edges.bat (prints PASS/FAIL, exit code 0 = pass).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

void fir_soft_edges (double f_low, double f_high, double soft, double soft_c, double* soft_low, double* soft_high);
double* fir_bandpass (int N, double f_low, double f_high, double samplerate, int wintype, int rtype, double scale);
double* fir_bandpass_soft (int N, double f_low, double f_high, double samplerate, int wintype, double scale, double soft, double soft_c);

// fir.c allocates through WDSP's malloc0 (utilities.c); the test needs only this one
void* malloc0 (int size)
{
	void* p = _aligned_malloc (size, 16);
	if (p) memset (p, 0, size);
	return p;
}

static int failures = 0;

static void check (int ok, const char* what)
{
	printf ("%s  %s\n", ok ? "ok  " : "FAIL", what);
	if (!ok) failures++;
}

static void check_edges_c (double lo, double hi, double soft, double soft_c, double want_lo, double want_hi, const char* what)
{
	double sl, sh;
	fir_soft_edges (lo, hi, soft, soft_c, &sl, &sh);
	char buf[200];
	snprintf (buf, sizeof buf, "%s: soft_low=%.0f soft_high=%.0f (want %.0f / %.0f)", what, sl, sh, want_lo, want_hi);
	check (fabs (sl - want_lo) < 1e-9 && fabs (sh - want_hi) < 1e-9, buf);
}

static void check_edges (double lo, double hi, double soft, double want_lo, double want_hi, const char* what)
{
	check_edges_c (lo, hi, soft, 0.0, want_lo, want_hi, what);
}

// magnitude of the complex impulse at frequency f
static double mag (const double* h, int N, double f, double fs)
{
	double re = 0, im = 0, m = 0.5 * (N - 1);
	for (int i = 0; i < N; i++)
	{
		double ph = -2.0 * 3.14159265358979323846 * f * (i - m) / fs;
		re += h[2 * i] * cos (ph) - h[2 * i + 1] * sin (ph);
		im += h[2 * i] * sin (ph) + h[2 * i + 1] * cos (ph);
	}
	return sqrt (re * re + im * im);
}

int main (void)
{
	// which edges are soft: the one away from the carrier only (both for AM)
	check_edges (100, 2900, 600, 0, 600, "USB");
	check_edges (-2900, -100, 600, 600, 0, "LSB");
	check_edges (-4000, 4000, 600, 600, 600, "AM");
	check_edges (400, 800, 600, 0, 400, "CW narrow (clamped to bandwidth)");
	check_edges (-3000, 3000, 4000, 3000, 3000, "AM wide soft (clamped to half bandwidth)");
	check_edges (100, 2900, 0, 0, 0, "soft off");
	// carrier-side edge with its own width (SSB only; AM has no carrier-side edge)
	check_edges_c (100, 2900, 300, 100, 100, 300, "USB carrier edge");
	check_edges_c (-2900, -100, 300, 100, 300, 100, "LSB carrier edge");
	check_edges_c (-4000, 4000, 300, 100, 300, 300, "AM ignores carrier edge");
	check_edges_c (100, 2900, 0, 100, 100, 0, "carrier edge only");

	// USB 100..2900 Hz with a 600 Hz soft top edge, compared to the plain filter
	const int N = 2048;
	const double fs = 48000.0;
	double* plain = fir_bandpass (N, 100, 2900, fs, 1, 1, 1.0);
	double* soft = fir_bandpass_soft (N, 100, 2900, fs, 1, 1.0, 600, 0.0);

	// the passband side follows WDSP's phase convention; find it from the plain filter
	double s = mag (plain, N, 1500, fs) > mag (plain, N, -1500, fs) ? 1.0 : -1.0;
	double ref = mag (plain, N, s * 1500, fs);
	double r1500 = mag (soft, N, s * 1500, fs) / ref;
	double r2600 = mag (soft, N, s * 2600, fs) / ref;
	double r3200 = mag (soft, N, s * 3200, fs) / ref;
	double r150 = mag (soft, N, s * 150, fs) / mag (plain, N, s * 150, fs);
	double rOther = mag (soft, N, -s * 1500, fs) / ref;

	char buf[200];
	snprintf (buf, sizeof buf, "passband 1500 Hz unchanged (%.3f)", r1500);
	check (fabs (r1500 - 1.0) < 0.02, buf);
	snprintf (buf, sizeof buf, "inside the soft edge 2600 Hz about half (%.3f)", r2600);
	check (r2600 > 0.35 && r2600 < 0.65, buf);
	snprintf (buf, sizeof buf, "above f_high 3200 Hz rejected (%.5f)", r3200);
	check (r3200 < 0.01, buf);
	snprintf (buf, sizeof buf, "carrier-side edge 150 Hz stays like the plain filter (%.3f)", r150);
	check (fabs (r150 - 1.0) < 0.05, buf);
	snprintf (buf, sizeof buf, "opposite sideband -1500 Hz rejected (%.5f)", rOther);
	check (rOther < 0.01, buf);

	// carrier-side soft edge 100 Hz: transition inside the passband, nothing leaks below the carrier
	double* softc = fir_bandpass_soft (N, 100, 2900, fs, 1, 1.0, 600, 100);
	double c150 = mag (softc, N, s * 150, fs) / ref;
	double c400 = mag (softc, N, s * 400, fs) / ref;
	double cNeg = mag (softc, N, -s * 300, fs) / ref;
	snprintf (buf, sizeof buf, "carrier edge 150 Hz about half (%.3f)", c150);
	check (c150 > 0.35 && c150 < 0.65, buf);
	snprintf (buf, sizeof buf, "above the carrier edge 400 Hz full (%.3f)", c400);
	check (fabs (c400 - 1.0) < 0.02, buf);
	snprintf (buf, sizeof buf, "below the carrier -300 Hz rejected (%.5f)", cNeg);
	check (cNeg < 0.01, buf);

	_aligned_free (plain);
	_aligned_free (soft);
	_aligned_free (softc);
	printf (failures ? "FAIL (%d)\n" : "PASS\n", failures);
	return failures ? 1 : 0;
}
