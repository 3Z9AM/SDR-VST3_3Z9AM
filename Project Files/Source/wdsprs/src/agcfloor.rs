//  agcfloor.rs
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

//! Dual-loop RX AGC ("pause hold"): a second, slow detector of the station level next to the
//! WDSP AGC. In speech pauses the normal AGC decays toward the noise and the noise "breathes"
//! up; here the level the gain is computed from never drops more than `rise` dB below the slow
//! station level, so the gain in a pause is at most `rise` dB above the gain during speech.
//!
//! Slow level: follows the AGC look-ahead peak up with 0.2 s, each step at most 12 dB over itself
//! (a single impulse barely moves it),
//! down with `tau` s (default 3 s), so after the station stops for good the gain still comes up
//! to the band noise within seconds. The WDSP AGC state machine (attack, hang, decay, impulse
//! recovery) is left untouched; only the level used for the gain is raised.

const UP_TAU_S: f64 = 0.2;

#[derive(Debug, Clone)]
pub struct AgcFloor {
    rate: f64,
    ratio: f64,
    tau: f64,
    up: f64,
    down: f64,
    slow: f64,
}

impl AgcFloor {
    pub fn new(rate: f64, rise_db: f64, tau_s: f64) -> Self {
        let mut f = AgcFloor { rate, ratio: 0.0, tau: tau_s, up: 0.0, down: 0.0, slow: 0.0 };
        f.set(rate, rise_db, tau_s);
        f
    }

    pub fn set(&mut self, rate: f64, rise_db: f64, tau_s: f64) {
        self.rate = rate.max(1.0);
        self.tau = tau_s.clamp(0.5, 30.0);
        self.ratio = 10f64.powf(-rise_db.clamp(0.0, 60.0) / 20.0);
        self.up = 1.0 - (-1.0 / (self.rate * UP_TAU_S)).exp();
        self.down = 1.0 - (-1.0 / (self.rate * self.tau)).exp();
    }

    pub fn flush(&mut self) {
        self.slow = 0.0;
    }

    /// peak = AGC look-ahead peak (ring_max), volts = the AGC level; returns the level for the gain
    #[inline]
    pub fn step(&mut self, peak: f64, volts: f64) -> f64 {
        if peak > self.slow {
            // at most 12 dB over the slow level per step: a click moves it < 1 dB, a new station still in ~0.3 s
            let t = peak.min(4.0 * self.slow.max(1e-9));
            self.slow += (t - self.slow) * self.up;
        } else {
            self.slow += (peak - self.slow) * self.down;
        }
        volts.max(self.slow * self.ratio)
    }
}

/// free it with agcfloor_destroy
#[no_mangle]
pub extern "C" fn agcfloor_create(rate: f64, rise_db: f64, tau_s: f64) -> *mut AgcFloor {
    Box::into_raw(Box::new(AgcFloor::new(rate, rise_db, tau_s)))
}

/// # Safety
/// `p` is null or from agcfloor_create, not yet destroyed
#[no_mangle]
pub unsafe extern "C" fn agcfloor_destroy(p: *mut AgcFloor) {
    if !p.is_null() {
        drop(Box::from_raw(p));
    }
}

/// # Safety
/// `p` is null or live
#[no_mangle]
pub unsafe extern "C" fn agcfloor_set(p: *mut AgcFloor, rate: f64, rise_db: f64, tau_s: f64) {
    if let Some(f) = p.as_mut() {
        f.set(rate, rise_db, tau_s);
    }
}

/// new sample rate, same rise / tau
/// # Safety
/// `p` is null or live
#[no_mangle]
pub unsafe extern "C" fn agcfloor_rate(p: *mut AgcFloor, rate: f64) {
    if let Some(f) = p.as_mut() {
        let rise = -20.0 * f.ratio.log10();
        let tau = f.tau;
        f.set(rate, rise, tau);
    }
}

/// # Safety
/// `p` is null or live
#[no_mangle]
pub unsafe extern "C" fn agcfloor_flush(p: *mut AgcFloor) {
    if let Some(f) = p.as_mut() {
        f.flush();
    }
}

/// # Safety
/// `p` is null (then volts comes back) or live
#[no_mangle]
pub unsafe extern "C" fn agcfloor_step(p: *mut AgcFloor, peak: f64, volts: f64) -> f64 {
    match p.as_mut() {
        Some(f) => f.step(peak, volts),
        None => volts,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const RATE: f64 = 48000.0;

    /// minimal stand-in for the WDSP AGC level: instant attack, 0.25 s decay
    fn run(f: &mut AgcFloor, env: &[f64]) -> Vec<(f64, f64)> {
        let d = 1.0 - (-1.0 / (RATE * 0.25f64)).exp();
        let mut v = 0.0;
        env.iter()
            .map(|&p| {
                v = if p > v { p } else { v + (p - v) * d };
                (v, f.step(p, v))
            })
            .collect()
    }

    fn speech_then_silence() -> Vec<f64> {
        // 3 s of syllables (0.2 s at 1.0, 0.1 s pause), then 0.6 s pause, all over noise 0.001
        let mut e = Vec::new();
        for _ in 0..10 {
            e.extend(std::iter::repeat(1.0).take((0.2 * RATE) as usize));
            e.extend(std::iter::repeat(0.001).take((0.1 * RATE) as usize));
        }
        e.extend(std::iter::repeat(0.001).take((0.6 * RATE) as usize));
        e
    }

    #[test]
    fn pause_gain_rise_is_limited() {
        let mut f = AgcFloor::new(RATE, 10.0, 3.0);
        let out = run(&mut f, &speech_then_silence());
        let n = out.len();
        let pause = &out[n - (0.6 * RATE) as usize..];
        // plain AGC: level falls far (gain up > 20 dB); with the floor the level stays within ~10 dB of speech
        let plain = pause.last().unwrap().0;
        let held = pause.iter().map(|x| x.1).fold(f64::MAX, f64::min);
        assert!(20.0 * plain.log10() < -9.0, "plain {plain}");
        assert!(20.0 * held.log10() > -13.0, "held {held}");
        // during speech nothing changes
        let mid = (2.0 * RATE) as usize + 100;
        assert_eq!(out[mid].0, out[mid].1);
    }

    #[test]
    fn long_silence_releases() {
        let mut f = AgcFloor::new(RATE, 10.0, 3.0);
        let mut e = speech_then_silence();
        e.extend(std::iter::repeat(0.001).take((20.0 * RATE) as usize));
        let out = run(&mut f, &e);
        // after 20 s without the station the floor is gone: the gain is the plain AGC one
        let (v, eff) = *out.last().unwrap();
        assert!(eff < 2.0 * v, "eff {eff} v {v}");
    }

    #[test]
    fn impulse_barely_moves_slow_level() {
        let mut f = AgcFloor::new(RATE, 10.0, 3.0);
        let mut e = vec![0.01; (2.0 * RATE) as usize];
        e.extend(std::iter::repeat(1.0).take((0.005 * RATE) as usize)); // 40 dB click, 5 ms
        e.extend(std::iter::repeat(0.01).take((0.01 * RATE) as usize));
        for &p in &e {
            f.step(p, p);
        }
        // the slow level rose by under 1.2 dB
        assert!(f.slow < 0.0115, "slow {}", f.slow);
    }
}
