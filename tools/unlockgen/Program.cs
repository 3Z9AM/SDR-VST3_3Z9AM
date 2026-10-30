//  Program.cs
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
using Thetis;

// Unlock codes for tester copies: type the 6 character request code a tester sent, give back the unlock code.
// unlockgen [request...] | unlockgen --selftest | unlockgen (asks in a loop)
if (args.Length == 1 && args[0] == "--selftest")
{
    string req = ActivationCode.Request("3z9am ", "machine-1");
    check(req == ActivationCode.Request("3Z9AM", "machine-1"), "callsign case/spaces ignored");
    check(req != ActivationCode.Request("3Z9AM", "machine-2"), "other PC, other request");
    check(ActivationCode.IsValid(req) && ActivationCode.IsValid(ActivationCode.Unlock(req)), "codes use the alphabet");
    check(ActivationCode.Unlock(req) == ActivationCode.Unlock(req.ToLowerInvariant().Insert(3, "-")), "unlock input normalised");
    check(!ActivationCode.IsValid("ABC10O"), "0/1/O rejected");
    Console.WriteLine("PASS");
    return 0;
}

if (args.Length > 0)
{
    int rc = 0;
    foreach (string a in args) rc |= print(a) ? 0 : 1;
    return rc;
}

while (true)
{
    Console.Write("Request code (Enter = exit): ");
    string line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) return 0;
    print(line);
}

static bool print(string request)
{
    if (!ActivationCode.IsValid(request))
    {
        Console.WriteLine($"  '{request}' is not a request code ({ActivationCode.Length} characters from {ActivationCode.Alphabet})");
        return false;
    }
    Console.WriteLine($"  {ActivationCode.Normalize(request)} -> unlock code: {ActivationCode.Unlock(request)}");
    return true;
}

static void check(bool ok, string what)
{
    if (ok) return;
    Console.WriteLine("FAIL: " + what);
    Environment.Exit(1);
}
