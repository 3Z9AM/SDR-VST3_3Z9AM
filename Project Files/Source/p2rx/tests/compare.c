// Compares the Rust unpackers with the original ChannelMaster C loops (copied from network.c)
// on random packets, bit for bit. Build and run: tests\run_compare.bat
#include <stdio.h>
#include <string.h>
#include "../p2rx.h"

static const double const_1_div_2147483648_ = 1.0 / 2147483648.0;
static unsigned int rng = 12345;
static unsigned char rnd(void) { rng = rng * 1103515245u + 12345u; return (unsigned char)(rng >> 16); }

int main(void)
{
	unsigned char pkt[1444];
	double a[2 * 240], b[2 * 240];
	int fails = 0, iter, i, k;

	for (iter = 0; iter < 20000; iter++)
	{
		for (i = 0; i < (int)sizeof(pkt); i++) pkt[i] = rnd();
		if (iter == 0) memset(pkt, 0x80, sizeof(pkt));   // most negative values
		if (iter == 1) memset(pkt, 0x7f, sizeof(pkt));   // most positive values

		// sequence number (ReadUDPFrame)
		unsigned int seqnum; unsigned char* seqbytep = (unsigned char*)&seqnum;
		seqbytep[3] = pkt[0]; seqbytep[2] = pkt[1]; seqbytep[1] = pkt[2]; seqbytep[0] = pkt[3];
		if (seqnum != p2_seqnum(pkt, 1444)) fails++;

		// DDC I/Q, 238 pairs from readbuf + 16 (ReadThreadMainLoop)
		unsigned char* ReadBufp = pkt + 16;
		memset(a, 0, sizeof(a)); memset(b, 0, sizeof(b));
		for (i = 0, k = 0; i < 238; i++, k += 6)
		{
			a[2 * i + 0] = const_1_div_2147483648_ * (double)(ReadBufp[k + 0] << 24 | ReadBufp[k + 1] << 16 | ReadBufp[k + 2] << 8);
			a[2 * i + 1] = const_1_div_2147483648_ * (double)(ReadBufp[k + 3] << 24 | ReadBufp[k + 4] << 16 | ReadBufp[k + 5] << 8);
		}
		if (p2_unpack_iq24(ReadBufp, 1428, 238, b, 2 * 240) != 238 || memcmp(a, b, sizeof(a))) fails++;

		// mic, 64 samples from readbuf + 4
		ReadBufp = pkt + 4;
		memset(a, 0, sizeof(a)); memset(b, 0, sizeof(b));
		for (i = 0, k = 0; i < 64; i++, k += 2)
		{
			a[2 * i] = const_1_div_2147483648_ * (double)(ReadBufp[k + 0] << 24 | ReadBufp[k + 1] << 16);
			a[2 * i + 1] = 0.0;
		}
		if (p2_unpack_mic16(ReadBufp, 128, 64, b, 2 * 240) != 64 || memcmp(a, b, sizeof(a))) fails++;

		// wideband, 512 samples from readbuf + 4 (only the first 2*240 doubles fit a/b, so 480 here)
		memset(a, 0, sizeof(a)); memset(b, 0, sizeof(b));
		int ii, jj;
		for (ii = 0, jj = 4; ii < 480; ii++, jj += 2)
			a[ii] = const_1_div_2147483648_ * (double)(pkt[jj + 0] << 24 | pkt[jj + 1] << 16);
		if (p2_unpack_s16(pkt + 4, 1024, 480, b, 2 * 240) != 480 || memcmp(a, b, sizeof(a))) fails++;
	}

	printf(fails ? "FAIL: %d mismatches\n" : "PASS: Rust == C on %d packets\n", fails ? fails : iter);
	return fails != 0;
}
