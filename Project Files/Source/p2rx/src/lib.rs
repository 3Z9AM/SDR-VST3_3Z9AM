//  lib.rs
//
//  Copyright (C) 2026 Daniel Poznanski, 3Z9AM
//
//  This program is free software; you can redistribute it and/or
//  modify it under the terms of the GNU General Public License
//  as published by the Free Software Foundation; either version 2
//  of the License, or (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program; if not, write to the Free Software
//  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

//! Protocol 2 receive-packet parsing for ChannelMaster (network.c).
//! Packets come from the network, so every length is checked: a short or malformed packet,
//! or a samples-per-packet setting larger than the packet, can never read or write out of bounds.

use std::slice;

const SCALE: f64 = 1.0 / 2147483648.0;

/// expected UDP payload length per port index (port - p2_custom_port_base)
pub fn expected_len(port_idx: i32) -> i32 {
    match port_idx {
        0 => 60,          // high priority C&C
        1 => 132,         // mic samples
        2..=9 => 1028,    // wideband ADC0..7
        10..=16 => 1444,  // DDC0..6 I/Q
        _ => -1,
    }
}

pub fn seqnum(buf: &[u8]) -> u32 {
    match buf.get(..4) {
        Some(b) => u32::from_be_bytes([b[0], b[1], b[2], b[3]]),
        None => 0,
    }
}

/// 24-bit samples sit in the top bytes of an i32, as the C code did with << 24 | << 16 | << 8
pub fn unpack_iq24(src: &[u8], n: usize, dst: &mut [f64]) -> usize {
    let mut done = 0;
    for (s, d) in src.chunks_exact(6).zip(dst.chunks_exact_mut(2)).take(n) {
        d[0] = i32::from_be_bytes([s[0], s[1], s[2], 0]) as f64 * SCALE;
        d[1] = i32::from_be_bytes([s[3], s[4], s[5], 0]) as f64 * SCALE;
        done += 1;
    }
    done
}

pub fn unpack_s16(src: &[u8], n: usize, dst: &mut [f64]) -> usize {
    let mut done = 0;
    for (s, d) in src.chunks_exact(2).zip(dst.iter_mut()).take(n) {
        *d = i32::from_be_bytes([s[0], s[1], 0, 0]) as f64 * SCALE;
        done += 1;
    }
    done
}

pub fn unpack_mic16(src: &[u8], n: usize, dst: &mut [f64]) -> usize {
    let mut done = 0;
    for (s, d) in src.chunks_exact(2).zip(dst.chunks_exact_mut(2)).take(n) {
        d[0] = i32::from_be_bytes([s[0], s[1], 0, 0]) as f64 * SCALE;
        d[1] = 0.0;
        done += 1;
    }
    done
}

// ---- C ABI: null pointers or negative lengths are treated as empty ----

unsafe fn src<'a>(p: *const u8, len: i32) -> &'a [u8] {
    if p.is_null() || len <= 0 { &[] } else { slice::from_raw_parts(p, len as usize) }
}

unsafe fn dst<'a>(p: *mut f64, len: i32) -> &'a mut [f64] {
    if p.is_null() || len <= 0 { &mut [] } else { slice::from_raw_parts_mut(p, len as usize) }
}

fn count(n: i32) -> usize { n.max(0) as usize }

#[no_mangle]
pub unsafe extern "C" fn p2_seqnum(buf: *const u8, len: i32) -> u32 { seqnum(src(buf, len)) }

#[no_mangle]
pub extern "C" fn p2_expected_len(port_idx: i32) -> i32 { expected_len(port_idx) }

#[no_mangle]
pub unsafe extern "C" fn p2_unpack_iq24(s: *const u8, s_len: i32, n: i32, d: *mut f64, d_len: i32) -> i32 {
    unpack_iq24(src(s, s_len), count(n), dst(d, d_len)) as i32
}

#[no_mangle]
pub unsafe extern "C" fn p2_unpack_s16(s: *const u8, s_len: i32, n: i32, d: *mut f64, d_len: i32) -> i32 {
    unpack_s16(src(s, s_len), count(n), dst(d, d_len)) as i32
}

#[no_mangle]
pub unsafe extern "C" fn p2_unpack_mic16(s: *const u8, s_len: i32, n: i32, d: *mut f64, d_len: i32) -> i32 {
    unpack_mic16(src(s, s_len), count(n), dst(d, d_len)) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn seq_and_lengths() {
        assert_eq!(seqnum(&[0x01, 0x02, 0x03, 0x04, 9]), 0x01020304);
        assert_eq!(seqnum(&[1, 2, 3]), 0);
        assert_eq!(expected_len(0), 60);
        assert_eq!(expected_len(9), 1028);
        assert_eq!(expected_len(16), 1444);
        assert_eq!(expected_len(17), -1);
        assert_eq!(expected_len(-5), -1);
    }

    #[test]
    fn full_scale_values() {
        let mut d = [9.0; 4];
        assert_eq!(unpack_iq24(&[0x80, 0, 0, 0x7f, 0xff, 0xff], 1, &mut d), 1);
        assert_eq!(d[0], -1.0);
        assert_eq!(d[1], (0x7fffff00u32 as f64) * SCALE);
        assert_eq!(d[2], 9.0);   // untouched
    }

    #[test]
    fn never_past_the_buffers() {
        let src = [0x40u8; 10];                       // room for 1 I/Q pair, 5 s16 samples
        let mut d = [0.0; 3];                         // room for 1 pair, 3 samples
        assert_eq!(unpack_iq24(&src, 100, &mut d), 1);
        assert_eq!(unpack_s16(&src, 100, &mut d), 3);
        assert_eq!(unpack_mic16(&src, 100, &mut d), 1);
        unsafe {
            assert_eq!(p2_unpack_iq24(std::ptr::null(), 10, 5, d.as_mut_ptr(), 3), 0);
            assert_eq!(p2_unpack_s16(src.as_ptr(), -1, 5, d.as_mut_ptr(), 3), 0);
            assert_eq!(p2_unpack_s16(src.as_ptr(), 10, -7, d.as_mut_ptr(), 3), 0);
            assert_eq!(p2_seqnum(std::ptr::null(), 4), 0);
        }
    }
}
