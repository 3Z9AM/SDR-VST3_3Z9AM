/*  vst_lane_mix.h

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
*/

#ifndef _vst_lane_mix_h
#define _vst_lane_mix_h

// Two parallel paths of one block (interleaved): each path goes through its own delay line so the one with less
// plugin latency is delayed to match the other, then out = gain_a * A + gain_b * B, written into path_a.
// line[i] holds ring * channels samples; pos is the shared write position, advanced by frames.
inline void vst_lane_align_and_mix(double* path_a, double* path_b, int frames, int channels,
	const int latency[2], double* const line[2], int& pos, int ring, double gain_a, double gain_b)
{
	double* path[2] = { path_a, path_b };
	int frame, channel, lane, i;

	for (lane = 0; lane < 2; ++lane)
	{
		int delay = (latency[0] > latency[1] ? latency[0] : latency[1]) - latency[lane];
		double* x = path[lane];

		if (delay > ring - 1)
			delay = ring - 1;	// shortcut: longer latency differences are not compensated
		for (frame = 0; frame < frames; ++frame)
		{
			int write = (pos + frame) % ring;
			int read = (write - delay + ring) % ring;

			for (channel = 0; channel < channels; ++channel)
			{
				line[lane][write * channels + channel] = x[frame * channels + channel];
				x[frame * channels + channel] = line[lane][read * channels + channel];
			}
		}
	}
	pos = (pos + frames) % ring;

	for (i = 0; i < frames * channels; ++i)
		path_a[i] = gain_a * path_a[i] + gain_b * path_b[i];
}

#endif
