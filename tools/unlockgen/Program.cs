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
using System.Collections.Generic;
using System.IO;
using Thetis;

// Unlock codes for tester copies: the 6 character request code a tester sent -> the unlock code.
// Extra features per callsign come from the tester list (not in git), one line per callsign:
//   SP1ABC diversity softedge      (names: diversity, softedge, all; # = comment)
// unlockgen REQUEST                       basic code (as before)
// unlockgen CALL REQUEST                  features of CALL from the list
// unlockgen --features a,b REQUEST        given features
// unlockgen [--list file]                 asks in a loop: request, callsign, features (a/d/s/b; the list suggests)
// unlockgen --selftest
string listPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SDR-VST3-testers.txt");
List<string> rest = new List<string>(args);
int li = rest.IndexOf("--list");
if (li >= 0 && li + 1 < rest.Count) { listPath = rest[li + 1]; rest.RemoveRange(li, 2); }

if (rest.Count == 1 && rest[0] == "--selftest")
{
    string req = ActivationCode.Request("3z9am ", "machine-1");
    check(req == ActivationCode.Request("3Z9AM", "machine-1"), "callsign case/spaces ignored");
    check(req != ActivationCode.Request("3Z9AM", "machine-2"), "other PC, other request");
    check(ActivationCode.IsValid(req) && ActivationCode.IsValid(ActivationCode.Unlock(req)), "codes use the alphabet");
    check(ActivationCode.Unlock(req) == ActivationCode.Unlock(req.ToLowerInvariant().Insert(3, "-")), "unlock input normalised");
    check(!ActivationCode.IsValid("ABC10O"), "0/1/O rejected");
    ActivationFeature both = ActivationFeature.SectionDiversity | ActivationFeature.SoftEdges;
    check(ActivationCode.Check(req, ActivationCode.Unlock(req)) == ActivationFeature.None, "old 6 character code = basic");
    check(ActivationCode.Check(req, ActivationCode.Unlock(req, both)) == both, "7 character code carries the features");
    check(ActivationCode.Check(req, ActivationCode.Unlock(req, ActivationFeature.SoftEdges)) == ActivationFeature.SoftEdges, "single feature");
    string swapped = ActivationCode.Alphabet[(int)both] + ActivationCode.Unlock(req, ActivationFeature.SoftEdges).Substring(1);
    check(ActivationCode.Check(req, swapped) == null, "feature character cannot be swapped");
    check(ActivationCode.Check(ActivationCode.Request("3Z9AM", "machine-2"), ActivationCode.Unlock(req, both)) == null, "code of another PC rejected");
    check(ActivationCode.Parse(new[] { "all" }) == both && ActivationCode.Parse(new[] { "x" }) == null, "feature names");
    Console.WriteLine("PASS");
    return 0;
}

Dictionary<string, ActivationFeature> testers = readList(listPath);
if (testers == null) return 1;

if (rest.Count == 1) return print(rest[0], ActivationFeature.None, null) ? 0 : 1;
if (rest.Count == 2) return print(rest[1], featuresOf(rest[0]), rest[0]) ? 0 : 1;
if (rest.Count == 3 && rest[0] == "--features")
{
    ActivationFeature? f = ActivationCode.Parse(rest[1].Split(','));
    if (f == null) { Console.WriteLine("  unknown feature in '" + rest[1] + "' (diversity, softedge, all)"); return 1; }
    return print(rest[2], f.Value, null) ? 0 : 1;
}
if (rest.Count > 0) { Console.WriteLine("usage: unlockgen [--list file] [CALL] REQUEST | --features a,b REQUEST | --selftest"); return 1; }

Console.WriteLine($"Tester list: {listPath} ({testers.Count} callsigns)");
while (true)
{
    Console.Write("Request code (Enter = exit): ");
    string line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) return 0;
    if (!ActivationCode.IsValid(line)) { print(line, ActivationFeature.None, null); continue; }
    Console.Write("Callsign (optional, Enter = skip): ");
    string call = Console.ReadLine() ?? "";
    // the list only suggests: whatever is chosen here goes into the code
    ActivationFeature suggested = featuresOf(call);
    ActivationFeature? f = null;
    while (f == null)
    {
        Console.Write($"Features - a = all, d = diversity, s = soft edges, b = basic (Enter = {ActivationCode.Describe(suggested)}): ");
        f = chooseFeatures(Console.ReadLine(), suggested);
        if (f == null) Console.WriteLine("  letters a, d, s or b (e.g. 'd s')");
    }
    print(line, f.Value, call);
}

static ActivationFeature? chooseFeatures(string input, ActivationFeature suggested)
{
    string s = (input ?? "").Trim().ToLowerInvariant();
    if (s.Length == 0) return suggested;
    ActivationFeature f = ActivationFeature.None;
    foreach (char c in s)
    {
        if (c == ' ' || c == ',') continue;
        if (c == 'a') f |= ActivationFeature.SectionDiversity | ActivationFeature.SoftEdges;
        else if (c == 'd') f |= ActivationFeature.SectionDiversity;
        else if (c == 's') f |= ActivationFeature.SoftEdges;
        else if (c != 'b') return null;
    }
    return f;
}

ActivationFeature featuresOf(string call)
{
    string c = ActivationCode.NormalizeCall(call);
    return c.Length > 0 && testers.TryGetValue(c, out ActivationFeature f) ? f : ActivationFeature.None;
}

static Dictionary<string, ActivationFeature> readList(string path)
{
    var d = new Dictionary<string, ActivationFeature>();
    if (!File.Exists(path)) return d;
    int n = 0;
    foreach (string raw in File.ReadAllLines(path))
    {
        n++;
        string line = raw.Split('#')[0].Trim();
        if (line.Length == 0) continue;
        string[] parts = line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        ActivationFeature? f = ActivationCode.Parse(parts[1..]);
        if (f == null) { Console.WriteLine($"{path}:{n}: unknown feature in '{raw}' (diversity, softedge, all)"); return null; }
        d[ActivationCode.NormalizeCall(parts[0])] = f.Value;
    }
    return d;
}

static bool print(string request, ActivationFeature features, string call)
{
    if (!ActivationCode.IsValid(request))
    {
        Console.WriteLine($"  '{request}' is not a request code ({ActivationCode.Length} characters from {ActivationCode.Alphabet})");
        return false;
    }
    string who = string.IsNullOrWhiteSpace(call) ? "" : ActivationCode.NormalizeCall(call) + ", ";
    Console.WriteLine($"  {ActivationCode.Normalize(request)} ({who}{ActivationCode.Describe(features)}) -> unlock code: {ActivationCode.Unlock(request, features)}");
    return true;
}

static void check(bool ok, string what)
{
    if (ok) return;
    Console.WriteLine("FAIL: " + what);
    Environment.Exit(1);
}
