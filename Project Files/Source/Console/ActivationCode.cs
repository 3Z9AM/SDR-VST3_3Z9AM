//  ActivationCode.cs
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Thetis
{
    // Tester activation codes, shared by Thetis (Console.Activation.cs) and tools/unlockgen.
    // Request = hash of callsign + machine id; unlock = HMAC of the request with the key in ActivationSecret.cs.
    // shortcut: the key ships inside Thetis.dll, so a decompiler can pull it out or a patch can skip the check;
    // this stops passing the program on, not cracking - upgrade to a signed licence file if that ever matters
    // Extra features for chosen testers (one bit each, at most 31 - one alphabet character)
    [Flags]
    internal enum ActivationFeature
    {
        None = 0,
        SectionDiversity = 1,   // diversity per frequency section (Diversity window: MRC / co-phase / null / max-SINR)
        SoftEdges = 2,          // soft filter edges, RX/TX and carrier side (Setup > DSP > Options)
    }

    internal static partial class ActivationCode
    {
        public const int Length = 6;
        // no 0/O, 1/I: codes are read out and typed by hand
        public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        // what this copy's unlock code allows; set from the stored code at start and after activation
        public static ActivationFeature Unlocked { get; set; }
        public static bool Has(ActivationFeature f) => (Unlocked & f) == f;

        public static string Request(string callsign, string machineId) =>
            encode(SHA256.HashData(Encoding.UTF8.GetBytes("SDR-VST3|" + NormalizeCall(callsign) + "|" + machineId)));

        // 6 characters = basic copy (the codes testers already have); with features one more character in
        // front carries them and the HMAC covers request and features, so the character cannot be swapped
        public static string Unlock(string request, ActivationFeature features = ActivationFeature.None) =>
            features == ActivationFeature.None
                ? encode(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(Normalize(request))))
                : Alphabet[(int)features] + encode(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(Normalize(request) + "|" + (int)features)));

        // features of an unlock code that fits the request, null when it does not
        public static ActivationFeature? Check(string request, string unlock)
        {
            string u = Normalize(unlock);
            if (u.Length == Length)
                return u == Unlock(request) ? ActivationFeature.None : (ActivationFeature?)null;
            if (u.Length != Length + 1)
                return null;
            int f = Alphabet.IndexOf(u[0]);
            return f > 0 && u == Unlock(request, (ActivationFeature)f) ? (ActivationFeature)f : (ActivationFeature?)null;
        }

        // names in the tester list of tools/unlockgen, labels in the activation window
        private static readonly (ActivationFeature Feature, string Name, string Label)[] FeatureNames =
        {
            (ActivationFeature.SectionDiversity, "diversity", "diversity per section"),
            (ActivationFeature.SoftEdges, "softedge", "soft filter edges"),
        };

        public static string Describe(ActivationFeature f) =>
            f == ActivationFeature.None ? "basic" : string.Join(", ", FeatureNames.Where(n => (f & n.Feature) != 0).Select(n => n.Label));

        // "all" = every feature; null when a name is unknown
        public static ActivationFeature? Parse(IEnumerable<string> names)
        {
            ActivationFeature f = ActivationFeature.None;
            foreach (string name in names)
            {
                string n = name.Trim().ToLowerInvariant();
                if (n.Length == 0) continue;
                if (n == "all") { f |= FeatureNames.Aggregate(ActivationFeature.None, (a, x) => a | x.Feature); continue; }
                int i = Array.FindIndex(FeatureNames, x => x.Name == n);
                if (i < 0) return null;
                f |= FeatureNames[i].Feature;
            }
            return f;
        }

        public static bool IsValid(string code)
        {
            string c = Normalize(code);
            return c.Length == Length && c.All(ch => Alphabet.IndexOf(ch) >= 0);
        }

        public static string NormalizeCall(string callsign) => (callsign ?? "").Trim().ToUpperInvariant();

        // upper case, spaces and dashes dropped
        public static string Normalize(string code) => (code ?? "").ToUpperInvariant().Replace(" ", "").Replace("-", "");

        private static string encode(byte[] hash)
        {
            uint v = BitConverter.ToUInt32(hash, 0);
            char[] c = new char[Length];
            for (int i = 0; i < Length; i++, v >>= 5) c[i] = Alphabet[(int)(v & 31)];
            return new string(c);
        }
    }
}
