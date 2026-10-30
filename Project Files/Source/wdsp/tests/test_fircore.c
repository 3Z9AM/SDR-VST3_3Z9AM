/* fircore (partitioned overlap-save) against direct convolution, incl. sizes that do not divide nc
   (SunSDR dsp_size 384) - before the fix 4096/384 indexed the partition ring with a non power-of-two mask. */
#include "comm.h"

// WDSP allocates through malloc0 (utilities.c); the test needs only this one
void* malloc0 (int size)
{
	void* p = _aligned_malloc (size, 16);
	if (p) memset (p, 0, size);
	return p;
}

static double err_for (int size, int nc)
{
	int blocks = 2 * nc / size + 8, n = blocks * size, i, b, m;
	double *h = (double *) malloc0 (nc * sizeof (complex));
	double *x = (double *) malloc0 (n * sizeof (complex));
	double *in = (double *) malloc0 (size * sizeof (complex));
	double *out = (double *) malloc0 (2 * size * sizeof (complex));	// xfircore writes the whole 2*size IFFT here
	double *y = (double *) malloc0 (n * sizeof (complex));
	double maxerr = 0, maxref = 0;
	srand (size * 7 + nc);
	for (i = 0; i < 2 * nc; i++) h[i] = rand () / (double) RAND_MAX - 0.5;
	for (i = 0; i < 2 * n; i++) x[i] = rand () / (double) RAND_MAX - 0.5;
	FIRCORE f = create_fircore (size, in, out, nc, 0, 1, h);
	for (b = 0; b < blocks; b++)
	{
		memcpy (in, x + 2 * b * size, size * sizeof (complex));
		xfircore (f);
		memcpy (y + 2 * b * size, out, size * sizeof (complex));
	}
	/* overlap-save: y[k] = sum_m h[m] x[k-m] */
	for (i = nc; i < n; i++)
	{
		double re = 0, im = 0;
		for (m = 0; m < nc; m++)
		{
			double hr = h[2 * m], hi = h[2 * m + 1];
			double xr = x[2 * (i - m)], xi = x[2 * (i - m) + 1];
			re += hr * xr - hi * xi;
			im += hr * xi + hi * xr;
		}
		// unnormalized FFTW: WDSP folds 1 / (2 * size) into its impulses, raw taps here
		maxerr = max (maxerr, fabs (re - y[2 * i] / (2 * size)) + fabs (im - y[2 * i + 1] / (2 * size)));
		maxref = max (maxref, fabs (re) + fabs (im));
	}
	destroy_fircore (f);
	_aligned_free (h); _aligned_free (x); _aligned_free (in); _aligned_free (out); _aligned_free (y);
	return maxerr / maxref;
}

int main (void)
{
	int cases[][2] = { {64, 4096}, {64, 1024}, {384, 4096}, {384, 2048}, {384, 1024}, {768, 4096}, {1536, 4096} };
	int i, fail = 0;
	for (i = 0; i < (int)(sizeof (cases) / sizeof (cases[0])); i++)
	{
		double e = err_for (cases[i][0], cases[i][1]);
		int ok = e < 1e-9;
		fail |= !ok;
		printf ("size %4d nc %4d  rel.err %.2e  %s\n", cases[i][0], cases[i][1], e, ok ? "ok" : "BAD");
	}
	printf (fail ? "FAIL\n" : "PASS\n");
	return fail;
}
