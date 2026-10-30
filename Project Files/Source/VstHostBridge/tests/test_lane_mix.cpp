/*  test_lane_mix.cpp - parallel VST paths: latency alignment and mix (vst_lane_mix.h)

Copyright (C) 2026 Daniel Poznanski, 3Z9AM

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.
*/

#include "../vst_lane_mix.h"

#include <cmath>
#include <cstdio>
#include <vector>

static int failures = 0;

static void check(bool ok, const char* what)
{
	printf("%s %s\n", ok ? "PASS" : "FAIL", what);
	if (!ok)
		failures++;
}

// impulse at frame `at` on path A; path B = the same impulse `lat_b` frames later (a plugin with that latency);
// returns the output, processed in blocks of `block` frames through a ring of `ring` frames
static std::vector<double> run(int lat_a, int lat_b, int at, int total, int block, int ring, double ga, double gb)
{
	const int ch = 2;
	std::vector<double> out(total * ch), a(block * ch), b(block * ch);
	std::vector<double> la(ring * ch, 0.0), lb(ring * ch, 0.0);
	double* line[2] = { la.data(), lb.data() };
	int latency[2] = { lat_a, lat_b };
	int pos = 0;

	for (int start = 0; start < total; start += block)
	{
		for (int f = 0; f < block; ++f)
			for (int c = 0; c < ch; ++c)
			{
				a[f * ch + c] = (start + f == at + lat_a) ? 1.0 : 0.0;
				b[f * ch + c] = (start + f == at + lat_b) ? 1.0 : 0.0;
			}
		vst_lane_align_and_mix(a.data(), b.data(), block, ch, latency, line, pos, ring, ga, gb);
		for (int i = 0; i < block * ch; ++i)
			out[start * ch + i] = a[i];
	}
	return out;
}

static bool single_impulse(const std::vector<double>& out, int frame, double value)
{
	for (size_t i = 0; i < out.size(); ++i)
	{
		double want = (static_cast<int>(i) / 2 == frame) ? value : 0.0;
		if (std::fabs(out[i] - want) > 1e-12)
			return false;
	}
	return true;
}

int main()
{
	// B 10 frames late: A is delayed by 10, both impulses land at frame 5 + 10, ring wraps several times
	check(single_impulse(run(0, 10, 5, 96, 8, 32, 0.5, 0.5), 15, 1.0), "B later: A delayed to match, sum 0.5 + 0.5");
	// A 7 frames late: B is delayed instead
	check(single_impulse(run(7, 0, 20, 96, 8, 32, 0.25, 0.75), 27, 1.0), "A later: B delayed to match, gains 0.25 + 0.75");
	// equal latency: no delay, plain mix
	check(single_impulse(run(3, 3, 4, 64, 16, 32, 1.0, 0.5), 7, 1.5), "equal latency: plain mix 1.0 + 0.5");
	// B muted
	check(single_impulse(run(0, 10, 5, 96, 8, 32, 1.0, 0.0), 15, 1.0), "B muted: A alone, still aligned");
	// difference above the ring: clamped, no crash, the late path stays where it was
	std::vector<double> o = run(0, 100, 5, 160, 8, 32, 0.0, 1.0);
	check(single_impulse(o, 105, 1.0), "difference above ring: clamped, path B untouched");

	printf(failures ? "FAILED (%d)\n" : "ALL PASS\n", failures);
	return failures ? 1 : 0;
}
