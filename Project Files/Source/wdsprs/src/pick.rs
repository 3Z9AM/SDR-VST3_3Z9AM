//  pick.rs
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

//! Station pick by the RX2-RX1 phase signature (user idea: a slider chooses which station to hear).
//! Per STFT cell of the RX filter, z1 conj(z2) of a causal patch (3 bins x 2 frames) is compared with a
//! reference phase per bin that starts at the slider and follows the cells that agree with it (a sky-wave
//! station's phase wanders). Strong cells (patch over 3x the noise) that point elsewhere go down to the
//! floor; weak cells stay as they are (no NR-like gating of the noise). Offline twin: tools/divtest pick
//! (MaskKeep) - two real stations at A/B 0 dB, 110 deg apart: SDR +9.6 dB, the other one -13 dB.
//! The station as a whole is tracked too (mean of the agreeing cells, `phase()`, the console slider follows it):
//! bins the station has not sounded in lately drift to that phase, so when it wanders far a syllable in a
//! quiet bin still finds it.
//! Constants from divtest picktune (12 real pairs x A/B 0/-6 dB x slider set at the station / +-45 deg, mean SDR
//! gain over the mix): A0 0.3, track 0.6 / 0.5 s, pull 0.25, -20 dB gave -1.6 dB (worst -16, tracking slipped onto
//! the other station); A0 -0.5, track 0.9 / 2 s, pull 0.05, -15 dB +2.5 dB (worst -2.3).

const PATCH_ON: f64 = 3.0; // patch power / noise over this: a signal there, the mask may act
const TRACK_ON: f64 = 4.0; // cell power / noise over this and agreeing: the reference follows it
const A0: f64 = -0.5; // agreement at or under: floor (a soft slope from well below 0: divtest picktune)
const A1: f64 = 0.8; // agreement at or over: 1
const TRACK_AG: f64 = 0.9; // only clearly agreeing cells move the reference (lower: it slips onto the other station)
const PULL: f64 = 0.05; // bins without the station this frame: toward the station phase at this x the tracking step
// Is there a phase to pick at all? Steadiness = per bin |mean unit z1 conj z2| of the strong cells over ~3 s, mean over
// the bins weighted by how often they are strong. divtest picksteady (medians): "rainbow" stations 0.18-0.21 (PICK only
// turned everything down 3-5 dB), stations PICK helped 0.24-0.48. Under ST_OFF the mask fades out, over ST_ON back in.
const ST_OFF: f64 = 0.22;
const ST_ON: f64 = 0.27;
const ST_SLOW: f64 = 1.5; // steadiness average = tracking time x this (2 s -> 3 s)
const RAMP: f64 = 4.0; // the mask fades in / out over tracking time / this (2 s -> 0.5 s)

#[derive(Debug, Clone, Default)]
pub struct Pick {
    phi: f64,
    floor: f64,
    alpha: f64,
    rr: Vec<f64>,
    ri: Vec<f64>,
    hit: Vec<bool>, // bin tracked this frame
    gr: f64,        // the station's phase as a whole (mean unit vector of the tracked cells)
    gi: f64,
    prev1: Vec<f64>, // last frame's z1 / z2 (re, im), for the causal patch
    prev2: Vec<f64>,
    has_prev: bool,
    vr: Vec<f64>, // per bin: mean unit z1 conj z2 of the strong cells, and how often the bin is strong
    vi: Vec<f64>,
    vw: Vec<f64>,
    steady: f64,
    warm: f64, // frames x steadiness step: under 1 = still measuring (the mask stays on)
    active: bool,
    mix: f64, // 0 = mask off .. 1 = on
}

impl Pick {
    pub fn new() -> Self {
        Pick { floor: 0.1, active: true, mix: 1.0, ..Default::default() }
    }

    /// phase steadiness in the filter (0 random .. 1 fixed), and whether the mask acts (false: phase too random)
    pub fn steadiness(&self) -> (f64, bool) {
        (self.steady, self.active)
    }

    /// phase rad (of z1 conj z2), depth dB (> 0), alpha = tracking step per frame; a new phase restarts the reference
    pub fn set(&mut self, phase: f64, depth_db: f64, alpha: f64) {
        self.floor = 10f64.powf(-depth_db.clamp(0.0, 60.0) / 20.0);
        self.alpha = alpha.clamp(0.0, 1.0);
        if phase != self.phi {
            self.phi = phase;
            self.reset_refs();
        }
    }

    /// retune: references back to the slider, the steadiness measured again (the mask on meanwhile)
    pub fn reset(&mut self) {
        self.reset_refs();
        self.vr.iter_mut().for_each(|x| *x = 0.0);
        self.vi.iter_mut().for_each(|x| *x = 0.0);
        self.vw.iter_mut().for_each(|x| *x = 0.0);
        self.steady = 0.0;
        self.warm = 0.0;
        self.active = true;
    }

    fn reset_refs(&mut self) {
        let (c, s) = (self.phi.cos(), self.phi.sin());
        self.rr.iter_mut().for_each(|x| *x = c);
        self.ri.iter_mut().for_each(|x| *x = s);
        self.gr = c;
        self.gi = s;
        self.has_prev = false;
    }

    /// the tracked phase of the station, rad (z1 conj z2)
    pub fn phase(&self) -> f64 {
        self.gi.atan2(self.gr)
    }

    /// z1, z2: nb filter bins (re, im interleaved), RX2 noise-matched; noise = mean noise power per bin of RX1
    /// (0 = not known yet: all gains 1); learn = 0 during TX. Writes nb gains.
    pub fn frame(&mut self, z1: &[f64], z2: &[f64], noise: f64, learn: bool, gain: &mut [f64]) {
        let nb = gain.len();
        if self.rr.len() != nb {
            self.rr = vec![0.0; nb];
            self.ri = vec![0.0; nb];
            self.hit = vec![false; nb];
            self.prev1 = vec![0.0; 2 * nb];
            self.prev2 = vec![0.0; 2 * nb];
            self.vr = vec![0.0; nb];
            self.vi = vec![0.0; nb];
            self.vw = vec![0.0; nb];
            self.reset();
        }
        let ast = self.alpha / ST_SLOW;
        let cross = |a: &[f64], b: &[f64], j: usize| (a[2 * j] * b[2 * j] + a[2 * j + 1] * b[2 * j + 1], a[2 * j + 1] * b[2 * j] - a[2 * j] * b[2 * j + 1]);
        let pw = |a: &[f64], j: usize| a[2 * j] * a[2 * j] + a[2 * j + 1] * a[2 * j + 1];
        let (mut tr, mut ti, mut tn) = (0.0, 0.0, 0.0);
        for k in 0..nb {
            self.hit[k] = false;
            if noise <= 0.0 {
                gain[k] = 1.0;
                continue;
            }
            let (lo, hi) = (k.saturating_sub(1), (k + 1).min(nb - 1));
            let (mut sr, mut si, mut sm, mut pp, mut pn) = (0.0, 0.0, 0.0, 0.0, 0.0);
            let frames: [(&[f64], &[f64]); 2] = [(z1, z2), (&self.prev1, &self.prev2)];
            for &(a, b) in &frames[..if self.has_prev { 2 } else { 1 }] {
                for j in lo..=hi {
                    let (xr, xi) = cross(a, b, j);
                    sr += xr;
                    si += xi;
                    sm += (xr * xr + xi * xi).sqrt();
                    pp += pw(a, j);
                    pn += 1.0;
                }
            }
            let (mut rr, mut ri) = (0.0, 0.0);
            for j in k.saturating_sub(2)..=(k + 2).min(nb - 1) {
                rr += self.rr[j];
                ri += self.ri[j];
            }
            let rm = (rr * rr + ri * ri).sqrt();
            let ag = if rm > 0.0 && sm > 0.0 { (sr * rr + si * ri) / (rm * sm) } else { 1.0 };
            gain[k] = if pp / pn < PATCH_ON * noise { 1.0 } else { self.floor + (1.0 - self.floor) * ((ag - A0) / (A1 - A0)).clamp(0.0, 1.0) };
            let strong = pw(z1, k) > TRACK_ON * noise;
            if learn {
                let (xr, xi) = cross(z1, z2, k);
                let m = (xr * xr + xi * xi).sqrt();
                let on = strong && m > 0.0;
                if on {
                    self.vr[k] += ast * (xr / m - self.vr[k]);
                    self.vi[k] += ast * (xi / m - self.vi[k]);
                }
                self.vw[k] += ast * (if on { 1.0 } else { 0.0 } - self.vw[k]);
            }
            if learn && strong && ag > TRACK_AG {
                let (xr, xi) = cross(z1, z2, k);
                let m = (xr * xr + xi * xi).sqrt();
                if m > 0.0 {
                    self.rr[k] += self.alpha * (xr / m - self.rr[k]);
                    self.ri[k] += self.alpha * (xi / m - self.ri[k]);
                    self.hit[k] = true;
                    tr += xr / m;
                    ti += xi / m;
                    tn += 1.0;
                }
            }
        }
        if tn > 0.0 {
            self.gr += self.alpha * (tr / tn - self.gr);
            self.gi += self.alpha * (ti / tn - self.gi);
            let gm = (self.gr * self.gr + self.gi * self.gi).sqrt();
            if gm > 0.0 {
                let (ur, ui, b) = (self.gr / gm, self.gi / gm, self.alpha * PULL);
                for k in 0..nb {
                    if !self.hit[k] {
                        self.rr[k] += b * (ur - self.rr[k]);
                        self.ri[k] += b * (ui - self.ri[k]);
                    }
                }
            }
        }
        if learn && noise > 0.0 {
            let (mut num, mut den) = (0.0, 0.0);
            for k in 0..nb {
                num += self.vw[k] * (self.vr[k] * self.vr[k] + self.vi[k] * self.vi[k]).sqrt();
                den += self.vw[k];
            }
            self.steady = if den > 0.0 { num / den } else { 0.0 };
            self.warm += ast;
            if self.warm >= 1.0 {
                if self.active && self.steady < ST_OFF {
                    self.active = false;
                } else if !self.active && self.steady > ST_ON {
                    self.active = true;
                }
            }
        }
        let target = if self.active { 1.0 } else { 0.0 };
        self.mix += (target - self.mix).clamp(-self.alpha * RAMP, self.alpha * RAMP);
        if self.mix < 1.0 {
            gain.iter_mut().for_each(|g| *g = 1.0 - self.mix * (1.0 - *g));
        }
        self.prev1.copy_from_slice(&z1[..2 * nb]);
        self.prev2.copy_from_slice(&z2[..2 * nb]);
        self.has_prev = true;
    }
}

#[no_mangle]
pub extern "C" fn pick_create() -> *mut Pick {
    Box::into_raw(Box::new(Pick::new()))
}

/// # Safety
/// p from pick_create or null
#[no_mangle]
pub unsafe extern "C" fn pick_destroy(p: *mut Pick) {
    if !p.is_null() {
        drop(Box::from_raw(p));
    }
}

/// # Safety
/// p from pick_create or null
#[no_mangle]
pub unsafe extern "C" fn pick_set(p: *mut Pick, phase: f64, depth_db: f64, alpha: f64) {
    if let Some(p) = p.as_mut() {
        p.set(phase, depth_db, alpha);
    }
}

/// phase steadiness in the filter 0..1; *active = 1 when the mask acts, 0 when the phase is too random
/// # Safety
/// p from pick_create or null, active null or writable
#[no_mangle]
pub unsafe extern "C" fn pick_steady(p: *const Pick, active: *mut i32) -> f64 {
    let (s, a) = p.as_ref().map_or((0.0, false), |p| p.steadiness());
    if let Some(x) = active.as_mut() {
        *x = a as i32;
    }
    s
}

/// tracked station phase, rad (0 for null)
/// # Safety
/// p from pick_create or null
#[no_mangle]
pub unsafe extern "C" fn pick_phase(p: *const Pick) -> f64 {
    p.as_ref().map_or(0.0, |p| p.phase())
}

/// # Safety
/// p from pick_create or null
#[no_mangle]
pub unsafe extern "C" fn pick_reset(p: *mut Pick) {
    if let Some(p) = p.as_mut() {
        p.reset();
    }
}

/// # Safety
/// z1, z2: 2 nb doubles, gain: nb doubles
#[no_mangle]
pub unsafe extern "C" fn pick_frame(p: *mut Pick, z1: *const f64, z2: *const f64, nb: i32, noise: f64, learn: i32, gain: *mut f64) {
    let Some(p) = p.as_mut() else { return };
    if nb <= 0 || z1.is_null() || z2.is_null() || gain.is_null() {
        return;
    }
    let n = nb as usize;
    p.frame(std::slice::from_raw_parts(z1, 2 * n), std::slice::from_raw_parts(z2, 2 * n), noise, learn != 0, std::slice::from_raw_parts_mut(gain, n));
}

#[cfg(test)]
mod tests {
    use super::*;

    // two stations in alternate bin groups: A at phase 0.5 rad, B at 0.5 + 2 rad, 20 dB over the noise;
    // slider on A: A's bins kept, B's bins down to the floor, empty bins untouched
    #[test]
    fn keeps_the_slider_station_drops_the_other() {
        let nb = 60;
        let mut p = Pick::new();
        p.set(0.5, 15.0, 0.05);
        let mut g = vec![0.0; nb];
        let mut seed = 1u64;
        let mut rnd = || {
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            ((seed >> 11) as f64 / (1u64 << 53) as f64) * 2.0 - 1.0
        };
        let (mut ga, mut gb, mut ge) = (0.0, 0.0, 0.0);
        let mut n = [0.0; 3];
        for f in 0..200 {
            let (mut z1, mut z2) = (vec![0.0; 2 * nb], vec![0.0; 2 * nb]);
            for k in 0..nb {
                let grp = (k / 6) % 3; // 0: A, 1: B, 2: empty
                let amp = if grp == 2 { 0.0 } else { 10.0 };
                let th = rnd() * 3.14;
                let ph = if grp == 0 { 0.5 } else { 2.5 };
                // z1 = s, z2 = s e^{-j ph} -> z1 conj z2 = |s|^2 e^{j ph}
                z1[2 * k] = amp * th.cos() + rnd();
                z1[2 * k + 1] = amp * th.sin() + rnd();
                z2[2 * k] = amp * (th - ph).cos() + rnd();
                z2[2 * k + 1] = amp * (th - ph).sin() + rnd();
            }
            p.frame(&z1, &z2, 2.0 / 3.0, true, &mut g);
            if f >= 50 {
                for k in 0..nb {
                    if k % 6 == 0 || k % 6 == 5 {
                        continue; // group edges: the patch sees both
                    }
                    n[(k / 6) % 3] += 1.0;
                    match (k / 6) % 3 {
                        0 => ga += g[k],
                        1 => gb += g[k],
                        _ => ge += g[k],
                    }
                }
            }
        }
        let (ga, gb, ge) = (ga / n[0], gb / n[1], ge / n[2]);
        assert!(ga > 0.95, "A kept: {ga}");
        assert!(gb < 0.3, "B down (115 deg apart, soft slope): {gb}");
        assert!(ge > 0.99, "noise untouched: {ge}");
    }

    // one station whose phase turns 0.3 -> 2.3 rad over 10 s in bins 0..19, then (phase kept) moves to bins 20..39
    // it has not sounded in since the start; a second one fixed at -1.5 rad in 40..47. The new bins must find it at
    // its new phase (pulled to the tracked phase; without the pull they hold 0.3 rad and cut it), B stays down
    #[test]
    fn follows_a_wandering_station() {
        let nb = 48;
        let mut p = Pick::new();
        p.set(0.3, 15.0, 0.02);
        let mut g = vec![0.0; nb];
        let mut seed = 7u64;
        let mut rnd = || {
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            ((seed >> 11) as f64 / (1u64 << 53) as f64) * 2.0 - 1.0
        };
        let (mut ga, mut gb, mut n) = (0.0, 0.0, 0.0);
        for f in 0..1300 {
            let pa = 0.3 + 2.0 * (f.min(1000) as f64) / 1000.0;
            let (a0, a1) = if f < 1000 { (0, 20) } else { (20, 40) };
            let (mut z1, mut z2) = (vec![0.0; 2 * nb], vec![0.0; 2 * nb]);
            for k in 0..nb {
                let (amp, ph) = if k >= 40 { (10.0, -1.5) } else if k >= a0 && k < a1 { (10.0, pa) } else { (0.0, 0.0) };
                let th = rnd() * 3.14;
                z1[2 * k] = amp * th.cos() + rnd();
                z1[2 * k + 1] = amp * th.sin() + rnd();
                z2[2 * k] = amp * (th - ph).cos() + rnd();
                z2[2 * k + 1] = amp * (th - ph).sin() + rnd();
            }
            p.frame(&z1, &z2, 2.0 / 3.0, true, &mut g);
            if (1000..1050).contains(&f) {
                for k in 22..38 {
                    ga += g[k];
                    n += 1.0;
                }
                gb += g[44] / 50.0;
            }
        }
        let ga = ga / n;
        assert!((p.phase() - 2.3).abs() < 0.3, "tracked phase {} vs 2.3", p.phase());
        assert!(ga > 0.5, "A kept in the new bins (0.10 without the pull): {ga}");
        assert!(gb < 0.25, "B down: {gb}");
    }

    // a "rainbow" station (random RX2-RX1 phase in every cell) next to nothing else: after ~3 s the mask must step
    // aside (all gains 1, steadiness low); a steady one keeps it on
    #[test]
    fn steps_aside_on_a_random_phase() {
        for (random, want_on) in [(true, false), (false, true)] {
            let nb = 40;
            let mut p = Pick::new();
            p.set(0.5, 15.0, 0.02); // 2 s tracking at 100 frames/s -> steadiness over 3 s
            let mut g = vec![0.0; nb];
            let mut seed = 3u64;
            let mut rnd = || {
                seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
                ((seed >> 11) as f64 / (1u64 << 53) as f64) * 2.0 - 1.0
            };
            for _ in 0..800 {
                let (mut z1, mut z2) = (vec![0.0; 2 * nb], vec![0.0; 2 * nb]);
                for k in 0..nb {
                    let th = rnd() * 3.14;
                    let ph = if random { rnd() * 3.14 } else { 0.5 };
                    z1[2 * k] = 10.0 * th.cos() + rnd();
                    z1[2 * k + 1] = 10.0 * th.sin() + rnd();
                    z2[2 * k] = 10.0 * (th - ph).cos() + rnd();
                    z2[2 * k + 1] = 10.0 * (th - ph).sin() + rnd();
                }
                p.frame(&z1, &z2, 2.0 / 3.0, true, &mut g);
            }
            let (st, on) = p.steadiness();
            assert_eq!(on, want_on, "random {random}: steadiness {st}");
            if !want_on {
                assert!(g.iter().all(|&x| x > 0.999), "mask faded out");
            } else {
                assert!(st > 0.9, "steady: {st}");
            }
        }
    }

    #[test]
    fn unknown_noise_passes_everything() {
        let mut p = Pick::new();
        p.set(1.0, 20.0, 0.05);
        let mut g = vec![0.0; 4];
        p.frame(&[1.0; 8], &[-1.0; 8], 0.0, true, &mut g);
        assert!(g.iter().all(|&x| x == 1.0));
    }
}
