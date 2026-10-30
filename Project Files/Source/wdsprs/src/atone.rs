//  atone.rs
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

//! Analog tone: the gentle treble roll-off of an analog receiver's audio chain (AF amplifier,
//! output transformer, speaker). Flat up to the corner, then -6 dB per octave, levelling off
//! at -depth dB. One first-order IIR shelf, so no pre-ringing and no added delay.
//!
//! Analog prototype H(s) = (1 + s/wz) / (1 + s/wp): pole at the corner, zero at corner / g
//! (g = 10^(-depth/20)), gain 1 at DC and g at the top. Bilinear transform with both
//! frequencies prewarped: a = tan(pi fp / rate), b = tan(pi fz / rate),
//! H(z) = (a/b) [(b+1) + (b-1) z^-1] / [(a+1) + (a-1) z^-1].

//! Crystal ripple (optional): the uneven passband of a 6-pole crystal ladder filter, which is
//! close to a Chebyshev response - gentle peaks and dips across the RX filter, denser toward the
//! edges (at the Chebyshev extrema cos(k pi / 6)), alternating +-ripple/2. Peaking biquads, so
//! like the real filter it is minimum phase: the group delay wanders across the band too.

use std::f64::consts::PI;

const CRYSTAL_POLES: usize = 6;

/// y = b0 x + b1 x[-1] - a1 y[-1], separately on I and Q; then the crystal ripple
#[derive(Debug, Default, Clone)]
pub struct AnalogTone {
    b0: f64,
    b1: f64,
    a1: f64,
    x: [f64; 2],
    y: [f64; 2],
    ripple: Vec<Biquad>,
}

/// one peaking section (RBJ), direct form I, state for I and Q
#[derive(Debug, Default, Clone)]
struct Biquad {
    b: [f64; 3],
    a: [f64; 2],
    x: [[f64; 2]; 2],
    y: [[f64; 2]; 2],
}

impl Biquad {
    fn peaking(rate: f64, f0: f64, q: f64, gain_db: f64) -> Self {
        let amp = 10f64.powf(gain_db / 40.0);
        let w = 2.0 * PI * f0 / rate;
        let alpha = w.sin() / (2.0 * q);
        let a0 = 1.0 + alpha / amp;
        Biquad {
            b: [(1.0 + alpha * amp) / a0, -2.0 * w.cos() / a0, (1.0 - alpha * amp) / a0],
            a: [-2.0 * w.cos() / a0, (1.0 - alpha / amp) / a0],
            ..Default::default()
        }
    }

    fn step(&mut self, k: usize, x: f64) -> f64 {
        let y = self.b[0] * x + self.b[1] * self.x[k][0] + self.b[2] * self.x[k][1]
            - self.a[0] * self.y[k][0] - self.a[1] * self.y[k][1];
        self.x[k] = [x, self.x[k][0]];
        self.y[k] = [y, self.y[k][0]];
        y
    }
}

/// peaking sections for a ripple of `ripple_db` peak to peak across lo..hi Hz (audio)
fn crystal_sections(rate: f64, lo: f64, hi: f64, ripple_db: f64) -> Vec<Biquad> {
    if ripple_db <= 0.0 || hi - lo < 200.0 || hi >= 0.45 * rate {
        return Vec::new();
    }
    let at = |k: usize| lo + (hi - lo) * (1.0 - (k as f64 * PI / CRYSTAL_POLES as f64).cos()) / 2.0;
    (1..CRYSTAL_POLES)
        .map(|k| {
            let f = at(k);
            // as wide as the gap to the neighbouring extrema
            let df = (at(k + 1) - at(k - 1)) / 2.0;
            let q = (f / df).clamp(0.5, 20.0);
            let sign = if k % 2 == 1 { -1.0 } else { 1.0 };
            Biquad::peaking(rate, f, q, sign * ripple_db / 2.0)
        })
        .collect()
}

/// (b0, b1, a1) for a sample rate, corner (Hz) and depth (dB)
pub fn coefs(rate: f64, corner: f64, depth: f64) -> (f64, f64, f64) {
    let g = 10f64.powf(-depth.max(0.0) / 20.0);
    let fp = corner.max(10.0).min(0.45 * rate);
    let fz = (fp / g).min(0.45 * rate);
    let a = (PI * fp / rate).tan();
    let b = (PI * fz / rate).tan();
    ((a / b) * (b + 1.0) / (a + 1.0), (a / b) * (b - 1.0) / (a + 1.0), (a - 1.0) / (a + 1.0))
}

impl AnalogTone {
    pub fn new(rate: f64, corner: f64, depth: f64) -> Self {
        let mut t = AnalogTone::default();
        t.set(rate, corner, depth);
        t
    }

    /// new coefficients; the state is kept (a slider move does not click)
    pub fn set(&mut self, rate: f64, corner: f64, depth: f64) {
        (self.b0, self.b1, self.a1) = coefs(rate, corner, depth);
    }

    /// crystal ripple across the RX filter (audio lo..hi Hz), peak to peak dB; 0 = none.
    /// The state is kept while the number of sections stays the same.
    pub fn set_ripple(&mut self, rate: f64, lo: f64, hi: f64, ripple_db: f64) {
        let mut next = crystal_sections(rate, lo, hi, ripple_db);
        if next.len() == self.ripple.len() {
            for (n, old) in next.iter_mut().zip(&self.ripple) {
                n.x = old.x;
                n.y = old.y;
            }
        }
        self.ripple = next;
    }

    pub fn flush(&mut self) {
        self.x = [0.0; 2];
        self.y = [0.0; 2];
        for s in &mut self.ripple {
            s.x = [[0.0; 2]; 2];
            s.y = [[0.0; 2]; 2];
        }
    }

    /// interleaved complex samples (I, Q, I, Q, ...), filtered in place
    pub fn process(&mut self, buf: &mut [f64]) {
        for pair in buf.chunks_exact_mut(2) {
            for k in 0..2 {
                let x = pair[k];
                let mut y = self.b0 * x + self.b1 * self.x[k] - self.a1 * self.y[k];
                self.x[k] = x;
                self.y[k] = y;
                for s in &mut self.ripple {
                    y = s.step(k, y);
                }
                pair[k] = y;
            }
        }
    }
}

// ---- C interface (wdsprs.h); the C glue (wdsp/atone.c) owns the pointer ----

#[no_mangle]
pub extern "C" fn atone_create(rate: f64, corner: f64, depth: f64) -> *mut AnalogTone {
    Box::into_raw(Box::new(AnalogTone::new(rate, corner, depth)))
}

/// # Safety
/// `p` is null or came from `atone_create` and is not used afterwards
#[no_mangle]
pub unsafe extern "C" fn atone_destroy(p: *mut AnalogTone) {
    if !p.is_null() {
        drop(Box::from_raw(p));
    }
}

/// # Safety
/// `p` is null or a live pointer from `atone_create`
#[no_mangle]
pub unsafe extern "C" fn atone_set(p: *mut AnalogTone, rate: f64, corner: f64, depth: f64) {
    if let Some(t) = p.as_mut() {
        t.set(rate, corner, depth);
    }
}

/// # Safety
/// `p` is null or a live pointer from `atone_create`
#[no_mangle]
pub unsafe extern "C" fn atone_set_ripple(p: *mut AnalogTone, rate: f64, lo: f64, hi: f64, ripple_db: f64) {
    if let Some(t) = p.as_mut() {
        t.set_ripple(rate, lo, hi, ripple_db);
    }
}

/// # Safety
/// `p` is null or a live pointer from `atone_create`
#[no_mangle]
pub unsafe extern "C" fn atone_flush(p: *mut AnalogTone) {
    if let Some(t) = p.as_mut() {
        t.flush();
    }
}

/// `n` complex samples at `buf` (2 n doubles), filtered in place
///
/// # Safety
/// `p` is null or a live pointer from `atone_create`; `buf` is null or holds 2 n doubles
#[no_mangle]
pub unsafe extern "C" fn atone_process(p: *mut AnalogTone, buf: *mut f64, n: i32) {
    if let (Some(t), false, true) = (p.as_mut(), buf.is_null(), n > 0) {
        t.process(std::slice::from_raw_parts_mut(buf, 2 * n as usize));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// steady-state gain (dB) of a tone at f Hz through the filter, measured on the samples
    fn measured_db(rate: f64, corner: f64, depth: f64, f: f64) -> f64 {
        measured_db_with(AnalogTone::new(rate, corner, depth), rate, f)
    }

    fn measured_db_with(mut t: AnalogTone, rate: f64, f: f64) -> f64 {
        let n = (rate as usize) / 2; // 0.5 s; the first half settles
        let mut buf: Vec<f64> = (0..n)
            .flat_map(|i| {
                let s = (2.0 * PI * f * i as f64 / rate).sin();
                [s, s]
            })
            .collect();
        t.process(&mut buf);
        let tail = &buf[n..];
        let rms_out = (tail.iter().step_by(2).map(|v| v * v).sum::<f64>() / (n / 2) as f64).sqrt();
        20.0 * (rms_out / std::f64::consts::FRAC_1_SQRT_2).log10()
    }

    /// the analog prototype the filter follows
    fn analog_db(corner: f64, depth: f64, f: f64) -> f64 {
        let fz = corner / 10f64.powf(-depth / 20.0);
        10.0 * ((1.0 + (f / fz).powi(2)) / (1.0 + (f / corner).powi(2))).log10()
    }

    #[test]
    fn follows_the_analog_prototype() {
        for &rate in &[48000.0, 96000.0, 192000.0] {
            for &f in &[100.0, 500.0, 1000.0, 2000.0, 3000.0, 4000.0] {
                let got = measured_db(rate, 2000.0, 8.0, f);
                let want = analog_db(2000.0, 8.0, f);
                assert!((got - want).abs() < 0.3, "rate {rate} f {f}: {got:.2} dB vs {want:.2} dB");
            }
        }
    }

    #[test]
    fn flat_at_dc_and_never_below_depth() {
        assert!(measured_db(48000.0, 2000.0, 8.0, 50.0).abs() < 0.05);
        // the bilinear transform squeezes the shelf near Nyquist: up to ~0.25 dB under depth at 20 kHz / 48 kHz
        for &f in &[8000.0, 12000.0, 20000.0] {
            let got = measured_db(48000.0, 2000.0, 8.0, f);
            assert!(got >= -8.3 && got < -6.0, "f {f}: {got:.2} dB");
        }
    }

    fn rippled(rate: f64, lo: f64, hi: f64, ripple: f64) -> AnalogTone {
        let mut t = AnalogTone::new(rate, 2000.0, 0.0); // shelf off: only the ripple
        t.set_ripple(rate, lo, hi, ripple);
        t
    }

    #[test]
    fn crystal_ripple_across_the_filter_only() {
        for &(rate, lo, hi) in &[(48000.0, 300.0, 2700.0), (48000.0, 100.0, 5000.0), (192000.0, 300.0, 2700.0)] {
            let gains: Vec<f64> = (0..=40)
                .map(|i| measured_db_with(rippled(rate, lo, hi, 1.0), rate, lo + (hi - lo) * i as f64 / 40.0))
                .collect();
            let (mn, mx) = gains.iter().fold((f64::MAX, f64::MIN), |(a, b), &g| (a.min(g), b.max(g)));
            assert!(mx - mn > 0.6 && mx - mn < 1.6, "rate {rate} {lo}-{hi}: ripple {:.2} dB", mx - mn);
            assert!(mx < 0.8 && mn > -0.8, "rate {rate} {lo}-{hi}: {mn:.2}..{mx:.2} dB");
            // well outside the filter it leaves the level alone
            for &f in &[20.0, 3.0 * hi] {
                let g = measured_db_with(rippled(rate, lo, hi, 1.0), rate, f);
                assert!(g.abs() < 0.3, "rate {rate} {lo}-{hi} at {f}: {g:.2} dB");
            }
        }
    }

    #[test]
    fn no_ripple_no_sections() {
        let mut t = AnalogTone::new(48000.0, 2000.0, 8.0);
        t.set_ripple(48000.0, 300.0, 2700.0, 0.0);
        assert!(t.ripple.is_empty());
        t.set_ripple(48000.0, 300.0, 30000.0, 1.0); // filter past 0.45 rate: none
        assert!(t.ripple.is_empty());
        t.set_ripple(48000.0, 300.0, 2700.0, 1.0);
        assert_eq!(t.ripple.len(), CRYSTAL_POLES - 1);
    }

    #[test]
    fn zero_depth_is_transparent() {
        let mut t = AnalogTone::new(48000.0, 2000.0, 0.0);
        let src: Vec<f64> = (0..2000).map(|i| ((i * 7919) % 1000) as f64 / 500.0 - 1.0).collect();
        let mut buf = src.clone();
        t.process(&mut buf);
        for (a, b) in src.iter().zip(&buf) {
            assert!((a - b).abs() < 1e-12);
        }
    }

    #[test]
    fn i_and_q_are_independent() {
        let mut t = AnalogTone::new(48000.0, 2000.0, 8.0);
        let mut buf = vec![0.0; 200];
        buf[0] = 1.0; // impulse on I only
        t.process(&mut buf);
        assert!(buf.iter().skip(1).step_by(2).all(|&q| q == 0.0));
        assert!(buf[0] > 0.0);
    }

    #[test]
    fn c_interface_round_trip() {
        unsafe {
            let p = atone_create(48000.0, 2000.0, 8.0);
            let mut buf = vec![1.0; 64];
            atone_process(p, buf.as_mut_ptr(), 32);
            atone_set(p, 96000.0, 1500.0, 12.0);
            atone_set_ripple(p, 96000.0, 300.0, 2700.0, 1.0);
            atone_process(p, buf.as_mut_ptr(), 32);
            atone_flush(p);
            atone_process(p, std::ptr::null_mut(), 32);
            atone_process(std::ptr::null_mut(), buf.as_mut_ptr(), 32);
            atone_destroy(p);
            assert!(buf.iter().all(|v| v.is_finite()));
        }
    }
}
