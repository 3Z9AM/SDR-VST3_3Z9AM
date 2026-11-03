//  mbagc.rs
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

//! Multiband RX AGC, after the normal (broadband) WDSP AGC. Bands across the RX filter (audio |f| of
//! the complex RXA signal, so USB, LSB and AM alike): 1.2 ERB wide, but never narrower than 2 bins
//! (188 Hz), so below ~1.2 kHz they are 2 bins each and above that follow the ear's ERB scale;
//! at most 24 (CW 500 Hz 2, SSB 2.7 kHz 12, 4 kHz 15, 6 kHz 17, 8 kHz 20).
//! Streaming STFT 512 @ 48 kHz (sqrt-Hann, 50 %), delay = one frame (10.7 ms). The gain is smooth
//! across the spectrum (linear in dB between band centres), so formants keep their shape.
//!
//! Reference = the station's own long-term band shares w_b (5 s, frames with signal only). Short
//! shares q_b from fast envelopes; d_b = 10 log10(w_b / q_b) = how much band b is below its usual
//! share right now.
//!  - protect (strength 0): when one band is persistently > 6 dB over its usual share (a strong
//!    station, carrier or CW tone in the filter, which made the broadband AGC pull everything
//!    down), the other bands get back min(d_b, excess - 6 dB, max). Nothing happens otherwise.
//!  - process (strength 1): every band pulled toward its usual share, +-max dB (dense, "processed").
//!  - target = (1 - s) protect + s process. A band less than 5 dB over its noise is never raised.
//!    Shares and dominance from the full band power (a steady carrier fools a noise tracker);
//!    the noise floor (minimum, up 2 dB/s) only gates raising and detects pauses.
//!    In speech pauses the gains are held. Gain down 20 ms, up 150 ms. A band's share in the
//!    reference rises at most 1 dB/s, so a carrier that appears stays 'foreign' for 10-20 s.
//!    A new filter = new bands, learning starts again (1 s).

const MAX_BANDS: usize = 24;
const ERB_PER_BAND: f64 = 1.2;
const F_MIN: f64 = 100.0;

fn erb(f: f64) -> f64 {
    21.4 * (1.0 + 0.00437 * f).log10()
}

fn inv_erb(e: f64) -> f64 {
    (10f64.powf(e / 21.4) - 1.0) / 0.00437
}

/// band edges flo..fhi: each band max(step ERB, minw Hz) wide, a narrow rest merged into the last
/// band; the ERB step grows until there are at most MAX_BANDS
fn band_edges(flo: f64, fhi: f64, minw: f64) -> Vec<f64> {
    let mut step = ERB_PER_BAND;
    loop {
        let mut edges = vec![flo];
        let mut f = flo;
        while fhi - f >= 2.0 * minw {
            f = (f + minw).max(inv_erb(erb(f) + step));
            if fhi - f < minw {
                break;
            }
            edges.push(f);
        }
        edges.push(fhi);
        if edges.len() - 1 <= MAX_BANDS {
            return edges;
        }
        step *= 1.1;
    }
}

/// radix-2 complex FFT, in place, sign -1 (forward)
#[derive(Debug, Clone)]
struct Fft {
    n: usize,
    tw: Vec<(f64, f64)>,
    rev: Vec<usize>,
}

impl Fft {
    fn new(n: usize) -> Self {
        let bits = n.trailing_zeros();
        let rev = (0..n).map(|i| i.reverse_bits() >> (usize::BITS - bits)).collect();
        let tw = (0..n / 2)
            .map(|k| {
                let a = -2.0 * std::f64::consts::PI * k as f64 / n as f64;
                (a.cos(), a.sin())
            })
            .collect();
        Fft { n, tw, rev }
    }

    fn forward(&self, x: &mut [(f64, f64)]) {
        let n = self.n;
        for i in 0..n {
            let j = self.rev[i];
            if j > i {
                x.swap(i, j);
            }
        }
        let mut len = 2;
        while len <= n {
            let step = n / len;
            for s in (0..n).step_by(len) {
                for k in 0..len / 2 {
                    let (wr, wi) = self.tw[k * step];
                    let (br, bi) = x[s + k + len / 2];
                    let t = (br * wr - bi * wi, br * wi + bi * wr);
                    let a = x[s + k];
                    x[s + k] = (a.0 + t.0, a.1 + t.1);
                    x[s + k + len / 2] = (a.0 - t.0, a.1 - t.1);
                }
            }
            len <<= 1;
        }
    }

    /// inverse with 1/n
    fn inverse(&self, x: &mut [(f64, f64)]) {
        for v in x.iter_mut() {
            v.1 = -v.1;
        }
        self.forward(x);
        let s = 1.0 / self.n as f64;
        for v in x.iter_mut() {
            *v = (v.0 * s, -v.1 * s);
        }
    }
}

#[derive(Debug, Clone)]
pub struct MbAgc {
    rate: f64,
    strength: f64,
    max_db: f64,
    n: usize,
    h: usize,
    fft: Fft,
    win: Vec<f64>,
    lo: f64,              // RX filter as audio Hz
    hi: f64,
    nb: usize,
    pos: Vec<f64>,        // per bin: band position (0..nb-1, fractional) for the gain
    band: Vec<i32>,       // per bin: band for the levels, -1 = outside the filter
    inp: Vec<(f64, f64)>,
    acc: Vec<(f64, f64)>,
    out: Vec<(f64, f64)>,
    frame: Vec<(f64, f64)>,
    fill: usize,
    ps: Vec<f64>,         // band power, 20 ms
    noise: Vec<f64>,
    env: Vec<f64>,        // band power envelope
    long: Vec<f64>,       // long-term share reference
    gain: Vec<f64>,       // dB
    act: f64,             // seconds of signal frames seen
    started: bool,
    // per-frame smoothing
    a_ps: f64,
    a_nd: f64,
    nu: f64,
    a_att: f64,
    a_rel: f64,
    a_long: f64,
    a_gdn: f64,
    a_gup: f64,
    lup: f64,
}

fn mult(h: usize, rate: f64, tau: f64) -> f64 {
    1.0 - (-(h as f64) / (rate * tau)).exp()
}

impl MbAgc {
    pub fn new(rate: f64, strength: f64, max_db: f64) -> Self {
        let rate = rate.max(8000.0);
        let n = ((rate * 512.0 / 48000.0).round() as usize).next_power_of_two().max(128);
        let h = n / 2;
        let win: Vec<f64> = (0..n).map(|i| (0.5 - 0.5 * (2.0 * std::f64::consts::PI * i as f64 / n as f64).cos()).sqrt()).collect();
        let mut m = MbAgc {
            rate,
            strength: 0.0,
            max_db: 12.0,
            n,
            h,
            fft: Fft::new(n),
            win,
            lo: 0.0,
            hi: 0.0,
            nb: 0,
            pos: vec![0.0; n],
            band: vec![-1; n],
            inp: vec![(0.0, 0.0); n],
            acc: vec![(0.0, 0.0); n],
            out: vec![(0.0, 0.0); h],
            frame: vec![(0.0, 0.0); n],
            fill: n - h,
            ps: Vec::new(),
            noise: Vec::new(),
            env: Vec::new(),
            long: Vec::new(),
            gain: Vec::new(),
            act: 0.0,
            started: false,
            a_ps: mult(h, rate, 0.02),
            a_nd: mult(h, rate, 0.05),
            nu: 10f64.powf(2.0 * h as f64 / rate / 10.0), // noise floor creeps up 2 dB/s
            a_att: mult(h, rate, 0.01),
            a_rel: mult(h, rate, 0.3),
            a_long: mult(h, rate, 5.0),
            a_gdn: mult(h, rate, 0.02),
            a_gup: mult(h, rate, 0.15),
            lup: 10f64.powf(h as f64 / rate / 10.0),
        };
        m.set(strength, max_db);
        m.layout(150.0, 3500.0);
        m
    }

    /// RX filter edges as RXA gives them (Hz, signed: USB +, LSB -, AM/FM across 0)
    pub fn set_band(&mut self, f_low: f64, f_high: f64) {
        let (a, b) = (f_low.abs(), f_high.abs());
        let (lo, hi) = if f_low < 0.0 && f_high > 0.0 { (0.0, a.max(b)) } else { (a.min(b), a.max(b)) };
        if lo != self.lo || hi != self.hi {
            self.layout(lo, hi);
        }
    }

    /// number of bands now
    pub fn bands(&self) -> usize {
        self.nb
    }

    /// bands for audio lo..hi Hz; resets the learning
    fn layout(&mut self, lo: f64, hi: f64) {
        self.lo = lo;
        self.hi = hi;
        let n = self.n;
        let bin = self.rate / n as f64;
        let flo = lo.max(F_MIN);
        let fhi = hi.min(0.45 * self.rate).max(flo + 2.0 * bin);
        let edges = band_edges(flo, fhi, 2.0 * bin);
        let nb = edges.len() - 1;
        let centre: Vec<f64> = (0..nb).map(|b| 0.5 * (erb(edges[b]) + erb(edges[b + 1]))).collect();
        for k in 0..n {
            let kk = if k < n / 2 { k as f64 } else { k as f64 - n as f64 };
            let f = kk.abs() * bin;
            self.band[k] = if f >= flo && f <= fhi { (edges.partition_point(|&e| e <= f) as i32 - 1).clamp(0, nb as i32 - 1) } else { -1 };
            let e = erb(f);
            self.pos[k] = if nb == 1 || e <= centre[0] {
                0.0
            } else if e >= centre[nb - 1] {
                (nb - 1) as f64
            } else {
                let b = centre.partition_point(|&c| c <= e) - 1;
                b as f64 + (e - centre[b]) / (centre[b + 1] - centre[b])
            };
        }
        self.nb = nb;
        self.ps = vec![0.0; nb];
        self.noise = vec![0.0; nb];
        self.env = vec![0.0; nb];
        self.long = vec![0.0; nb];
        self.gain = vec![0.0; nb];
        self.act = 0.0;
        self.started = false;
    }

    pub fn set(&mut self, strength: f64, max_db: f64) {
        self.strength = strength.clamp(0.0, 1.0);
        self.max_db = max_db.clamp(0.0, 24.0);
    }

    /// delay in samples
    pub fn delay(&self) -> usize {
        self.n
    }

    pub fn flush(&mut self) {
        let (rate, s, m, lo, hi) = (self.rate, self.strength, self.max_db, self.lo, self.hi);
        *self = MbAgc::new(rate, s, m);
        self.layout(lo, hi);
    }

    pub fn process(&mut self, buf: &mut [f64]) {
        let (n, h) = (self.n, self.h);
        for s in buf.chunks_exact_mut(2) {
            self.inp[self.fill] = (s[0], s[1]);
            let o = self.out[self.fill - (n - h)];
            s[0] = o.0;
            s[1] = o.1;
            self.fill += 1;
            if self.fill == n {
                self.run_frame();
                self.inp.copy_within(h.., 0);
                self.fill = n - h;
            }
        }
    }

    fn run_frame(&mut self) {
        let (n, h) = (self.n, self.h);
        for i in 0..n {
            self.frame[i] = (self.inp[i].0 * self.win[i], self.inp[i].1 * self.win[i]);
        }
        self.fft.forward(&mut self.frame);

        // band powers
        let nb = self.nb;
        let mut p = vec![0.0; nb];
        for k in 0..n {
            if self.band[k] >= 0 {
                let (re, im) = self.frame[k];
                p[self.band[k] as usize] += re * re + im * im;
            }
        }
        self.levels(&p);

        // gain per bin, linear in dB between band centres
        if self.gain.iter().any(|g| g.abs() > 1e-6) {
            for k in 0..n {
                let x = self.pos[k];
                let b0 = x.floor() as usize;
                let b1 = (b0 + 1).min(nb - 1);
                let g = self.gain[b0] + (self.gain[b1] - self.gain[b0]) * (x - b0 as f64);
                let m = 10f64.powf(g / 20.0);
                self.frame[k] = (self.frame[k].0 * m, self.frame[k].1 * m);
            }
        }

        self.fft.inverse(&mut self.frame);
        for i in 0..n {
            self.acc[i].0 += self.frame[i].0 * self.win[i];
            self.acc[i].1 += self.frame[i].1 * self.win[i];
        }
        self.out.copy_from_slice(&self.acc[..h]);
        self.acc.copy_within(h.., 0);
        for v in &mut self.acc[n - h..] {
            *v = (0.0, 0.0);
        }
    }

    fn levels(&mut self, p: &[f64]) {
        let nb = self.nb;
        if !self.started {
            self.ps = p.to_vec();
            self.noise = p.iter().map(|x| x.max(1e-30)).collect();
            self.started = true;
        }
        let mut e_sum = 0.0;
        let mut n_sum = 0.0;
        for b in 0..nb {
            self.ps[b] += (p[b] - self.ps[b]) * self.a_ps;
            // minimum tracker: down fast, up 2 dB/s
            self.noise[b] = if self.ps[b] < self.noise[b] { self.noise[b] + (self.ps[b] - self.noise[b]) * self.a_nd } else { self.noise[b] * self.nu };
            self.noise[b] = self.noise[b].max(1e-30);
            // envelope of the whole band power: a steady carrier looks like noise to the minimum
            // tracker, so the shares and the dominance use the full power, the noise only gates
            let a = if self.ps[b] > self.env[b] { self.a_att } else { self.a_rel };
            self.env[b] += (self.ps[b] - self.env[b]) * a;
            e_sum += self.env[b];
            n_sum += self.noise[b];
        }
        // speech pause / no station: gains held
        if e_sum <= 4.0 * n_sum || e_sum <= 0.0 {
            return;
        }
        let fs = self.h as f64 / self.rate;
        self.act += fs;
        let al = self.a_long.max(fs / self.act);
        let warm = self.act < 1.0;
        for b in 0..nb {
            let v = self.long[b] + (self.env[b] / e_sum - self.long[b]) * al;
            // a band's share in the reference rises at most 1 dB/s: a strong carrier / station
            // that appears is not learnt as the station's timbre for 10-20 s
            self.long[b] = if warm { v } else { v.min(self.long[b].max(1e-6) * self.lup) };
        }
        if self.act < 1.0 {
            return;
        }
        let l_sum: f64 = self.long.iter().sum();
        let mut d = vec![0.0; nb];
        let mut ok = vec![false; nb];
        let mut excess: f64 = 0.0;
        for b in 0..nb {
            let q = (self.env[b] / e_sum).max(1e-9);
            let w = (self.long[b] / l_sum).max(1e-9);
            d[b] = 10.0 * (w / q).log10();
            ok[b] = self.env[b] > 4.0 * self.noise[b];
            if ok[b] {
                excess = excess.max(-d[b]);
            }
        }
        let x = (excess - 6.0).clamp(0.0, self.max_db);
        let s = self.strength;
        for b in 0..nb {
            let prot = if ok[b] && d[b] > 0.0 { d[b].min(x) } else { 0.0 };
            let mut proc = d[b].clamp(-self.max_db, self.max_db);
            if !ok[b] {
                proc = proc.min(0.0);
            }
            let t = (1.0 - s) * prot + s * proc;
            let a = if t < self.gain[b] { self.a_gdn } else { self.a_gup };
            self.gain[b] += (t - self.gain[b]) * a;
        }
    }
}

#[no_mangle]
pub extern "C" fn mbagc_create(rate: f64, strength: f64, max_db: f64) -> *mut MbAgc {
    Box::into_raw(Box::new(MbAgc::new(rate, strength, max_db)))
}

/// # Safety
/// `p` is null or from mbagc_create, not yet destroyed
#[no_mangle]
pub unsafe extern "C" fn mbagc_destroy(p: *mut MbAgc) {
    if !p.is_null() {
        drop(Box::from_raw(p));
    }
}

/// # Safety
/// `p` is null or live
#[no_mangle]
pub unsafe extern "C" fn mbagc_set(p: *mut MbAgc, strength: f64, max_db: f64) {
    if let Some(m) = p.as_mut() {
        m.set(strength, max_db);
    }
}

/// RX filter edges as RXA has them (signed Hz)
/// # Safety
/// `p` is null or live
#[no_mangle]
pub unsafe extern "C" fn mbagc_set_band(p: *mut MbAgc, f_low: f64, f_high: f64) {
    if let Some(m) = p.as_mut() {
        m.set_band(f_low, f_high);
    }
}

/// # Safety
/// `p` is null or live
#[no_mangle]
pub unsafe extern "C" fn mbagc_flush(p: *mut MbAgc) {
    if let Some(m) = p.as_mut() {
        m.flush();
    }
}

/// # Safety
/// `p` is null or live, `buf` holds 2 n doubles
#[no_mangle]
pub unsafe extern "C" fn mbagc_process(p: *mut MbAgc, buf: *mut f64, n: i32) {
    if let (Some(m), false, true) = (p.as_mut(), buf.is_null(), n > 0) {
        m.process(std::slice::from_raw_parts_mut(buf, 2 * n as usize));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::f64::consts::PI;

    const RATE: f64 = 48000.0;

    /// syllables: 0.2 s on, 0.1 s off
    fn syl(t: usize) -> f64 {
        if (t as f64 / RATE) % 0.3 < 0.2 { 1.0 } else { 0.0 }
    }

    /// "voice": 40 tones 250..2590 Hz, 60 Hz apart
    fn voice_f(k: usize) -> f64 {
        250.0 + 60.0 * k as f64
    }
    const NV: usize = 40;

    /// complex (USB) tones: (Hz, amplitude)
    fn tones(t: usize, list: &[(f64, f64)]) -> (f64, f64) {
        let mut z = (0.0, 0.0);
        for (i, &(f, a)) in list.iter().enumerate() {
            let ph = 2.0 * PI * f * t as f64 / RATE + i as f64 * 2.4;
            z.0 += a * ph.cos();
            z.1 += a * ph.sin();
        }
        z
    }

    /// amplitude of a complex tone at f over buf (I/Q interleaved)
    fn level(buf: &[f64], f: f64, t0: usize) -> f64 {
        let n = buf.len() / 2;
        let (mut re, mut im) = (0.0, 0.0);
        for k in 0..n {
            let ph = -2.0 * PI * f * (t0 + k) as f64 / RATE;
            let (x, y) = (buf[2 * k], buf[2 * k + 1]);
            re += x * ph.cos() - y * ph.sin();
            im += x * ph.sin() + y * ph.cos();
        }
        (re * re + im * im).sqrt() / n as f64
    }

    struct Lcg(u64);
    impl Lcg {
        fn next(&mut self) -> f64 {
            self.0 = self.0.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
            ((self.0 >> 11) as f64 / (1u64 << 53) as f64) * 2.0 - 1.0
        }
    }

    /// run in WDSP-like blocks, returns the output
    fn run(m: &mut MbAgc, x: &[f64]) -> Vec<f64> {
        let mut y = x.to_vec();
        for b in y.chunks_mut(2 * 64) {
            m.process(b);
        }
        y
    }

    /// gain in dB at tone f, last second
    fn gain_at(m: &MbAgc, x: &[f64], y: &[f64], f: f64, sec: f64) -> f64 {
        let t0 = ((sec - 1.0) * RATE) as usize;
        let t1 = (sec * RATE) as usize;
        20.0 * (level(&y[2 * t0..2 * t1], f, t0 - m.delay()) / level(&x[2 * t0..2 * t1], f, t0)).log10()
    }

    /// voice (amp per tone via `amp`), optional steady carrier from `carrier_from` s with the
    /// broadband AGC in front keeping the total power
    fn scene(sec: f64, carrier_from: f64, amp: impl Fn(usize, f64) -> f64, rng: &mut Lcg) -> Vec<f64> {
        let n = (sec * RATE) as usize;
        let mut x = Vec::with_capacity(2 * n);
        for t in 0..n {
            let ts = t as f64 / RATE;
            let v = syl(t);
            let mut list: Vec<(f64, f64)> = (0..NV).map(|k| (voice_f(k), amp(k, ts) * v)).collect();
            let car = ts >= carrier_from;
            if car {
                list.push((2900.0, 2.0)); // steady, +16 dB over the whole voice
            }
            let mut z = tones(t, &list);
            if car {
                z = (z.0 * 0.16, z.1 * 0.16);
            }
            z.0 += 0.001 * rng.next();
            z.1 += 0.001 * rng.next();
            x.push(z.0);
            x.push(z.1);
        }
        x
    }

    #[test]
    fn unity_gain_is_exact_reconstruction() {
        let mut m = MbAgc::new(RATE, 0.5, 12.0);
        let mut r = Lcg(1);
        let x: Vec<f64> = (0..2 * 20000).map(|_| r.next()).collect();
        let y = run(&mut m, &x); // < 1 s: no gain yet
        let d = m.delay();
        for k in d..20000 {
            assert!((y[2 * k] - x[2 * (k - d)]).abs() < 1e-9);
            assert!((y[2 * k + 1] - x[2 * (k - d) + 1]).abs() < 1e-9);
        }
    }

    #[test]
    fn bands_follow_the_filter() {
        let mut m = MbAgc::new(RATE, 0.0, 12.0);
        let mut last = 0;
        // CW 500 Hz, SSB 1.8 / 2.7 / 3.5 / 4 / 6 / 8 / 10 kHz, AM 2 x 5 kHz; LSB given negative
        for &(lo, hi) in &[(350.0, 850.0), (-1950.0, -150.0), (150.0, 2850.0), (100.0, 3600.0), (100.0, 4100.0),
                           (-5000.0, 5000.0), (100.0, 6100.0), (100.0, 8100.0), (-10100.0, -100.0)] {
            m.set_band(lo, hi);
            let nb = m.bands();
            assert!(nb >= 2 && nb <= MAX_BANDS, "{lo}..{hi}: {nb}");
            assert!(nb >= last, "{lo}..{hi}: {nb} < {last}");
            last = nb;
            // every band has at least 2 bins
            for b in 0..nb {
                assert!(m.band.iter().filter(|&&x| x == b as i32).count() >= 2 * 2, "{lo}..{hi} band {b}"); // both signs
            }
        }
        m.set_band(150.0, 2850.0);
        assert!(m.bands() >= 11, "2.7 kHz SSB: {}", m.bands());
        m.set_band(100.0, 8100.0);
        assert!(m.bands() >= 18 && m.bands() <= MAX_BANDS, "8 kHz SSB: {}", m.bands());
        m.set_band(350.0, 850.0);
        assert!(m.bands() <= 3, "CW: {}", m.bands());
    }

    #[test]
    fn steady_station_untouched_at_strength_0() {
        let mut m = MbAgc::new(RATE, 0.0, 12.0);
        let mut r = Lcg(2);
        let x = scene(8.0, 1e9, |_, _| 0.05, &mut r);
        let y = run(&mut m, &x);
        for k in [3, 10, 20, 30] {
            let g = gain_at(&m, &x, &y, voice_f(k), 8.0);
            assert!(g.abs() < 0.5, "{} Hz {g} dB", voice_f(k));
        }
    }

    #[test]
    fn strong_carrier_gives_the_rest_back() {
        let mut m = MbAgc::new(RATE, 0.0, 12.0);
        let mut r = Lcg(3);
        let x = scene(10.0, 6.0, |_, _| 0.05, &mut r);
        let y = run(&mut m, &x);
        for k in [8, 16, 24] {
            let g = gain_at(&m, &x, &y, voice_f(k), 10.0);
            assert!(g > 9.0 && g < 12.5, "{} Hz +{g} dB", voice_f(k));
        }
        // the carrier itself is not raised
        assert!(gain_at(&m, &x, &y, 2900.0, 10.0) < 1.0);
    }

    #[test]
    fn processor_restores_a_faded_band() {
        let mut m = MbAgc::new(RATE, 1.0, 12.0);
        let mut r = Lcg(4);
        // 970..1450 Hz fade by 10 dB from 7 s
        let x = scene(9.0, 1e9, |k, ts| if ts >= 7.0 && (12..=20).contains(&k) { 0.0158 } else { 0.05 }, &mut r);
        let y = run(&mut m, &x);
        let g = gain_at(&m, &x, &y, voice_f(16), 9.0);
        assert!(g > 5.0, "+{g} dB");
    }

    #[test]
    fn noise_only_band_never_raised() {
        let mut m = MbAgc::new(RATE, 1.0, 12.0);
        let mut r = Lcg(5);
        // voice only below 1500 Hz: the top bands are plain noise
        let x = scene(8.0, 1e9, |k, _| if k <= 20 { 0.05 } else { 0.0 }, &mut r);
        let y = run(&mut m, &x);
        let top = *m.gain.last().unwrap();
        assert!(top <= 0.5, "top band {top} dB");
        assert!(y.iter().all(|v| v.is_finite()));
    }
}
