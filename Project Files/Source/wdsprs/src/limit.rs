//  limit.rs
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

//! RX peak limiter: the last block of RXA (after the panel, I/Q = left/right), so peaks the
//! stages after the AGC add (APF, post-AGC NR, crystal ripple, warmth, BIN2 / spatial) never
//! clip in the radio codec or VAC. Look-ahead 1 ms, both channels share one gain.
//!
//! Gain the sample needs: r = min(1, thr / max(|L|, |R|)). Hold = minimum of r over the last
//! W + 1 samples, release = instant down, exponential up, then a moving average over W + 1.
//! Audio is delayed by W, so at the peak every value in the average is <= its r: the output
//! never exceeds thr, and the gain reaches it as a smooth ramp instead of a step.

const LOOKAHEAD_S: f64 = 0.001;

#[derive(Debug, Clone)]
pub struct Limiter {
    thr: f64,
    rel: f64,
    w: usize,
    need: Vec<f64>,   // r, ring of w + 1
    held: Vec<f64>,   // after hold + release, ring of w + 1
    delay: Vec<f64>,  // audio, ring of w + 1 (I/Q pairs)
    sum: f64,
    env: f64,
    pos: usize,
    count: usize,
}

impl Limiter {
    pub fn new(rate: f64, thr_db: f64, release_ms: f64) -> Self {
        let w = ((rate * LOOKAHEAD_S).round() as usize).max(1);
        let mut l = Limiter {
            thr: 1.0,
            rel: 0.0,
            w,
            need: vec![1.0; w + 1],
            held: vec![1.0; w + 1],
            delay: vec![0.0; 2 * (w + 1)],
            sum: (w + 1) as f64,
            env: 1.0,
            pos: 0,
            count: 0,
        };
        l.set(rate, thr_db, release_ms);
        l
    }

    /// new threshold / release; a different rate resizes and flushes
    pub fn set(&mut self, rate: f64, thr_db: f64, release_ms: f64) {
        let w = ((rate * LOOKAHEAD_S).round() as usize).max(1);
        if w != self.w {
            *self = Limiter::new(rate, thr_db, release_ms);
            return;
        }
        self.thr = 10f64.powf(thr_db.clamp(-20.0, 0.0) / 20.0);
        self.rel = 1.0 - (-1.0 / (rate * release_ms.clamp(1.0, 2000.0) / 1000.0)).exp();
    }

    pub fn flush(&mut self) {
        self.need.iter_mut().for_each(|v| *v = 1.0);
        self.held.iter_mut().for_each(|v| *v = 1.0);
        self.delay.iter_mut().for_each(|v| *v = 0.0);
        self.sum = (self.w + 1) as f64;
        self.env = 1.0;
        self.pos = 0;
        self.count = 0;
    }

    /// I/Q interleaved, in place; delays by the look-ahead
    pub fn process(&mut self, buf: &mut [f64]) {
        let n = self.w + 1;
        for s in buf.chunks_exact_mut(2) {
            let peak = s[0].abs().max(s[1].abs());
            let r = if peak > self.thr { self.thr / peak } else { 1.0 };
            self.need[self.pos] = r;
            let hold = self.need.iter().cloned().fold(1.0, f64::min);
            self.env = if hold < self.env { hold } else { self.env + (hold - self.env) * self.rel };
            self.sum += self.env - self.held[self.pos];
            self.held[self.pos] = self.env;
            // shortcut: running sum drifts by rounding, recomputed once per window
            self.count += 1;
            if self.count >= n {
                self.count = 0;
                self.sum = self.held.iter().sum();
            }
            let g = (self.sum / n as f64).min(1.0);
            // oldest sample in the ring = the one W samples back
            let old = (self.pos + 1) % n;
            let (i, q) = (self.delay[2 * old], self.delay[2 * old + 1]);
            self.delay[2 * self.pos] = s[0];
            self.delay[2 * self.pos + 1] = s[1];
            s[0] = i * g;
            s[1] = q * g;
            self.pos = old;
        }
    }
}

/// a limiter for `rate` Hz; free it with limit_destroy
#[no_mangle]
pub extern "C" fn limit_create(rate: f64, thr_db: f64, release_ms: f64) -> *mut Limiter {
    Box::into_raw(Box::new(Limiter::new(rate, thr_db, release_ms)))
}

/// # Safety
/// `p` is null or from limit_create, not yet destroyed
#[no_mangle]
pub unsafe extern "C" fn limit_destroy(p: *mut Limiter) {
    if !p.is_null() {
        drop(Box::from_raw(p));
    }
}

/// # Safety
/// `p` is null or a live limiter
#[no_mangle]
pub unsafe extern "C" fn limit_set(p: *mut Limiter, rate: f64, thr_db: f64, release_ms: f64) {
    if let Some(l) = p.as_mut() {
        l.set(rate, thr_db, release_ms);
    }
}

/// # Safety
/// `p` is null or a live limiter
#[no_mangle]
pub unsafe extern "C" fn limit_flush(p: *mut Limiter) {
    if let Some(l) = p.as_mut() {
        l.flush();
    }
}

/// # Safety
/// `p` is null or a live limiter, `buf` holds 2 n doubles
#[no_mangle]
pub unsafe extern "C" fn limit_process(p: *mut Limiter, buf: *mut f64, n: i32) {
    if let (Some(l), false, true) = (p.as_mut(), buf.is_null(), n > 0) {
        l.process(std::slice::from_raw_parts_mut(buf, 2 * n as usize));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const RATE: f64 = 48000.0;

    fn tone(n: usize, f: f64, amp: f64) -> Vec<f64> {
        (0..n)
            .flat_map(|k| {
                let x = amp * (2.0 * std::f64::consts::PI * f * k as f64 / RATE).sin();
                [x, 0.7 * x]
            })
            .collect()
    }

    #[test]
    fn below_threshold_is_only_delayed() {
        let mut l = Limiter::new(RATE, -1.0, 50.0);
        let x = tone(4800, 700.0, 0.8);
        let mut y = x.clone();
        l.process(&mut y);
        let w = 48;
        for k in w..4800 {
            assert!((y[2 * k] - x[2 * (k - w)]).abs() < 1e-12);
            assert!((y[2 * k + 1] - x[2 * (k - w) + 1]).abs() < 1e-12);
        }
    }

    #[test]
    fn never_exceeds_threshold() {
        let thr = 10f64.powf(-1.0 / 20.0);
        let mut l = Limiter::new(RATE, -1.0, 50.0);
        // quiet tone, then a +12 dB burst, a single huge click, and back to quiet
        let mut x = tone(2400, 700.0, 0.5);
        x.extend(tone(2400, 1300.0, 4.0));
        x.extend(tone(1200, 700.0, 0.5));
        x[2 * 5500] = 30.0;
        x.extend(tone(14400, 700.0, 0.5));
        // in blocks of 64 like WDSP
        let mut y = x.clone();
        for b in y.chunks_mut(128) {
            l.process(b);
        }
        let max = y.iter().fold(0.0f64, |m, v| m.max(v.abs()));
        assert!(max <= thr + 1e-12, "peak {max} > {thr}");
        // released again: the quiet tone comes back at full level
        let tail = &y[2 * 18000..];
        let tmax = tail.iter().step_by(2).fold(0.0f64, |m, v| m.max(v.abs()));
        assert!((tmax - 0.5).abs() < 0.01, "tail {tmax}");
    }

    #[test]
    fn rate_change_resizes() {
        let mut l = Limiter::new(RATE, -1.0, 50.0);
        l.set(96000.0, -3.0, 50.0);
        assert_eq!(l.w, 96);
        let mut y = vec![2.0; 2 * 1000];
        l.process(&mut y);
        let thr = 10f64.powf(-3.0 / 20.0);
        assert!(y.iter().all(|v| v.abs() <= thr + 1e-12));
    }
}
