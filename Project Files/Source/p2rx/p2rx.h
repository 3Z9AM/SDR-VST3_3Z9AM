/*  p2rx.h

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

// p2rx: Protocol 2 receive-packet parsing in Rust (p2rx/src/lib.rs), linked statically into ChannelMaster.
// Every function checks the lengths it is given and never reads or writes past them.
#ifndef _p2rx_h
#define _p2rx_h

// big-endian sequence number from the first 4 bytes; 0 when len < 4
unsigned int p2_seqnum(const unsigned char* buf, int len);
// expected UDP length for a port index (port - p2_custom_port_base), -1 for an unknown port
int p2_expected_len(int port_idx);
// n 24-bit big-endian I/Q pairs -> doubles (I, Q interleaved, full scale = 1.0); returns pairs unpacked
int p2_unpack_iq24(const unsigned char* src, int src_len, int n, double* dst, int dst_len);
// n 16-bit big-endian samples -> doubles; returns samples unpacked
int p2_unpack_s16(const unsigned char* src, int src_len, int n, double* dst, int dst_len);
// n 16-bit big-endian mic samples -> complex doubles (I = sample, Q = 0); returns samples unpacked
int p2_unpack_mic16(const unsigned char* src, int src_len, int n, double* dst, int dst_len);

#endif
