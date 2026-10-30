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
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Thetis
{
    // Tester activation codes, shared by Thetis (Console.Activation.cs) and tools/unlockgen.
    // Request = hash of callsign + machine id; unlock = HMAC of the request with the key in ActivationSecret.cs.
    // shortcut: the key ships inside Thetis.dll, so a decompiler can pull it out or a patch can skip the check;
    // this stops passing the program on, not cracking - upgrade to a signed licence file if that ever matters
    internal static partial class ActivationCode
    {
        public const int Length = 6;
        // no 0/O, 1/I: codes are read out and typed by hand
        public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        public static string Request(string callsign, string machineId) =>
            encode(SHA256.HashData(Encoding.UTF8.GetBytes("SDR-VST3|" + NormalizeCall(callsign) + "|" + machineId)));

        public static string Unlock(string request) =>
            encode(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(Normalize(request))));

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
