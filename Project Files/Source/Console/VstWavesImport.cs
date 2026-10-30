/*  VstWavesImport.cs

This file is part of a program that implements a Software-Defined Radio.

Copyright (C) 2026 Daniel Poznanski, 3Z9AM

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Thetis
{
    /// <summary>
    /// Import of a Waves SuperRack (.sprk) or MultiRack (.mrdb) session - both are SQLite files - into a VST chain:
    /// the session's Waves plugins in slot order, matched to WaveShell VST3 entries of the plugin catalog, each with
    /// its stored preset XML written into the plugin state. A preset that cannot be applied is saved as .xps so it
    /// can be loaded from the plugin's own Waves menu.
    /// </summary>
    static class VstWavesImport
    {
        internal sealed class Entry
        {
            public string Name;         // rack slot label, e.g. "Krmr GT"
            public string PluginName;   // from the preset XML, e.g. "EKramer GT"
            public string PresetName;
            public string PresetXml;
            public bool Bypass;
            public bool Disabled;
        }

        private const string SessionQuery =
            "SELECT p.chainer_id, p.plugin_name, p.disabled, s.bypass, pp.preset " +
            "FROM plug p JOIN snapshot_plugin s ON s.plug_id = p.id AND s.snapshot_id = -1 " +
            "LEFT JOIN plugin_preset pp ON pp.id = s.preset_id " +
            "WHERE p.slot >= 0 AND lower(p.vendor_name) LIKE 'waves%' " +
            "ORDER BY p.chainer_id, p.slot";

        /// <summary>Plugins of the first rack/channel strip that holds any; otherChains = how many further ones were skipped.</summary>
        internal static List<Entry> ReadSession(string path, out int otherChains)
        {
            List<Entry> entries = new List<Entry>();
            HashSet<string> chains = new HashSet<string>();
            string firstChain = null;

            foreach (string[] row in QuerySqlite(path, SessionQuery))
            {
                chains.Add(row[0]);
                if (firstChain == null)
                    firstChain = row[0];
                if (row[0] != firstChain)
                    continue;

                string xml = row[4];
                entries.Add(new Entry
                {
                    Name = row[1] ?? "",
                    PluginName = Tag(xml, "PluginName") ?? row[1] ?? "",
                    PresetName = xml == null ? null : WebUtility.HtmlDecode(Regex.Match(xml, "<Preset Name=\"([^\"]*)\"").Groups[1].Value),
                    PresetXml = xml,
                    Disabled = IsTrue(row[2]),
                    Bypass = IsTrue(row[3])
                });
            }

            otherChains = Math.Max(0, chains.Count - 1);
            return entries;
        }

        /// <summary>Adds the session's plugins to the chain; returns one report line per plugin.</summary>
        internal static List<string> Import(VstChainKind kind, string sessionPath, int freeSlots)
        {
            List<string> report = new List<string>();
            int otherChains;
            List<Entry> entries = ReadSession(sessionPath, out otherChains);
            List<VstCatalogPlugin> catalog = VstHost.LoadPluginCatalog().Plugins;
            string presetDir = Path.Combine(VstHost.GetVstDirectoryPath(), "WavesImport", Path.GetFileNameWithoutExtension(sessionPath));

            if (entries.Count == 0)
                report.Add("No Waves plugins found in the session.");
            if (otherChains > 0)
                report.Add(string.Format("The session has {0} more rack(s)/channel(s) - only the first was imported.", otherChains));

            for (int n = 0; n < entries.Count; n++)
            {
                Entry e = entries[n];
                string label = e.PluginName + (string.IsNullOrEmpty(e.PresetName) ? "" : " (" + e.PresetName + ")");

                if (freeSlots <= 0)
                {
                    report.Add(label + ": skipped - the chain is full.");
                    continue;
                }

                VstCatalogPlugin plugin = FindPlugin(catalog, e);
                if (plugin == null)
                {
                    report.Add(label + ": no Waves VST3 with this name in the plugin catalog (install it or rescan plugins).");
                    continue;
                }

                VstOperationResult added = VstHost.AddPlugin(kind, plugin.Path, plugin.ClassId);
                if (!added.Success || added.PluginIndex < 0)
                {
                    report.Add(label + ": " + added.Message);
                    continue;
                }
                freeSlots--;
                int index = added.PluginIndex;

                string presetResult = "";
                if (e.PresetXml != null && !ApplyPreset(kind, index, e))
                {
                    string file = SavePresetFile(presetDir, n + 1, e);
                    presetResult = file != null
                        ? " - preset NOT applied, load it in the plugin's window (Waves Load): " + file
                        : " - preset NOT applied";
                }

                if (e.Bypass)
                    VstHost.SetPluginBypass(kind, index, true);
                if (e.Disabled)
                    VstHost.SetPluginEnabled(kind, index, false);

                report.Add(label + ": added as " + plugin.Name + presetResult);
            }

            return report;
        }

        private static bool ApplyPreset(VstChainKind kind, int index, Entry e)
        {
            byte[] original = VstHost.GetPluginStateBlob(kind, index);
            byte[] patched = PatchStatePreset(original, e.PresetXml);
            if (patched == null)
                return false;

            if (!VstHost.SetPluginStateBlob(kind, index, patched))
            {
                VstHost.SetPluginStateBlob(kind, index, original);
                return false;
            }

            // a plugin may accept the stream yet ignore the preset - check that it now reports the new one
            Match name = Regex.Match(e.PresetXml, "<Preset Name=\"[^\"]+\"");
            if (!name.Success)
                return true;
            byte[] now = VstHost.GetPluginStateBlob(kind, index);
            return now != null && Encoding.UTF8.GetString(now).Contains(name.Value);
        }

        private static string SavePresetFile(string dir, int number, Entry e)
        {
            try
            {
                string name = string.Format("{0:00} {1}{2}.xps", number, e.PluginName,
                    string.IsNullOrEmpty(e.PresetName) ? "" : " - " + e.PresetName);
                foreach (char c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, name);
                File.WriteAllText(file, e.PresetXml, new UTF8Encoding(false));
                return file;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #region catalog match

        private static readonly Regex VariantSuffix = new Regex(@"\s+(mono/stereo|mono|stereo)$", RegexOptions.IgnoreCase);

        /// <summary>Waves VST3 entry whose name without " Mono"/" Stereo" equals the preset's plugin name; stereo preferred (chains are 2-channel).</summary>
        internal static VstCatalogPlugin FindPlugin(IEnumerable<VstCatalogPlugin> catalog, Entry e)
        {
            string key1 = Key(e.PluginName), key2 = Key(e.Name);
            VstCatalogPlugin best = null;
            int bestScore = -1;

            foreach (VstCatalogPlugin p in catalog)
            {
                if (p == null || !p.Available || string.IsNullOrWhiteSpace(p.Name) ||
                    (p.Vendor ?? "").IndexOf("Waves", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string name = p.Name.Trim();
                Match m = VariantSuffix.Match(name);
                string key = Key(m.Success ? name.Substring(0, m.Index) : name);
                if (key.Length == 0 || (key != key1 && key != key2))
                    continue;

                string variant = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
                int score = variant == "stereo" ? 3 : variant == "mono/stereo" ? 2 : variant == "" ? 1 : 0;
                if (score > bestScore)
                {
                    best = p;
                    bestScore = score;
                }
            }

            return best;
        }

        private static string Key(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "")
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        #endregion

        #region state blob

        private const uint StateBlobMagic = 0x31535654;    // VstHostBridge: magic, component size, controller size (LE), streams
        private const int StateBlobHeader = 12;
        private static readonly byte[] XmlOpen = Encoding.ASCII.GetBytes("<PresetChunkXMLTree");
        private static readonly byte[] XmlClose = Encoding.ASCII.GetBytes("</PresetChunkXMLTree>");

        /// <summary>
        /// The bridge state blob with the Waves preset XML in its component/controller streams replaced by the one
        /// from presetXml; null when the state holds no preset XML of the same plugin. 32-bit length fields (LE or BE)
        /// in front of the XML equal to the old XML length or to the bytes left to the end of the stream are moved
        /// by the size change.
        /// </summary>
        internal static byte[] PatchStatePreset(byte[] blob, string presetXml)
        {
            if (blob == null || blob.Length < StateBlobHeader || BitConverter.ToUInt32(blob, 0) != StateBlobMagic || presetXml == null)
                return null;

            int x0 = presetXml.IndexOf("<PresetChunkXMLTree", StringComparison.Ordinal);
            int x1 = presetXml.LastIndexOf("</PresetChunkXMLTree>", StringComparison.Ordinal);
            if (x0 < 0 || x1 < x0)
                return null;
            string xml = presetXml.Substring(x0, x1 + XmlClose.Length - x0);

            // controller stream first - it lies behind the component one, so the component offsets stay valid
            byte[] result = PatchStream(blob, 1, xml);
            byte[] both = PatchStream(result ?? blob, 0, xml);
            return both ?? result;
        }

        private static byte[] PatchStream(byte[] blob, int stream, string xml)
        {
            long comp = BitConverter.ToUInt32(blob, 4), ctrl = BitConverter.ToUInt32(blob, 8);
            if (StateBlobHeader + comp + ctrl != blob.Length)
                return null;

            int start = (int)(stream == 0 ? StateBlobHeader : StateBlobHeader + comp);
            int end = (int)(stream == 0 ? StateBlobHeader + comp : blob.Length);
            int a = IndexOf(blob, XmlOpen, start, end);
            int b = a < 0 ? -1 : IndexOf(blob, XmlClose, a, end);
            if (b < 0)
                return null;
            b += XmlClose.Length;

            string oldXml = Encoding.UTF8.GetString(blob, a, b - a);
            if (!string.Equals(Tag(oldXml, "PluginName"), Tag(xml, "PluginName"), StringComparison.OrdinalIgnoreCase))
                return null;

            byte[] repl = Encoding.UTF8.GetBytes(xml);
            int oldLen = b - a, delta = repl.Length - oldLen;
            byte[] output = new byte[blob.Length + delta];
            Buffer.BlockCopy(blob, 0, output, 0, a);
            Buffer.BlockCopy(repl, 0, output, a, repl.Length);
            Buffer.BlockCopy(blob, b, output, a + repl.Length, blob.Length - b);

            for (int o = start; o + 4 <= a; o++)
            {
                for (int be = 0; be < 2; be++)
                {
                    long v = ReadU32(blob, o, be == 1);
                    if (v == oldLen || v == oldLen + 1 || v == end - (o + 4) || v == end - o)
                        WriteU32(output, o, (uint)(v + delta), be == 1);
                }
            }

            WriteU32(output, stream == 0 ? 4 : 8, (uint)((stream == 0 ? comp : ctrl) + delta), false);
            return output;
        }

        private static int IndexOf(byte[] data, byte[] pattern, int start, int end)
        {
            for (int i = start; i + pattern.Length <= end; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j])
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }

        private static long ReadU32(byte[] d, int o, bool bigEndian)
        {
            return bigEndian
                ? ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3]
                : ((long)d[o + 3] << 24) | ((long)d[o + 2] << 16) | ((long)d[o + 1] << 8) | d[o];
        }

        private static void WriteU32(byte[] d, int o, uint v, bool bigEndian)
        {
            for (int i = 0; i < 4; i++)
                d[o + (bigEndian ? 3 - i : i)] = (byte)(v >> (8 * i));
        }

        #endregion

        private static string Tag(string xml, string tag)
        {
            if (xml == null)
                return null;
            Match m = Regex.Match(xml, "<" + tag + ">(.*?)</" + tag + ">", RegexOptions.Singleline);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
        }

        private static bool IsTrue(string v)
        {
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        #region SQLite (winsqlite3.dll ships with Windows 10/11)

        private const string Sqlite = "winsqlite3.dll";
        private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_OPEN_READONLY = 1;

        [DllImport(Sqlite)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Sqlite)] private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Sqlite)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Sqlite)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Sqlite)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Sqlite)] private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Sqlite)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Sqlite)] private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private static List<string[]> QuerySqlite(string path, string sql)
        {
            List<string[]> rows = new List<string[]>();
            IntPtr db, stmt = IntPtr.Zero;

            int rc = sqlite3_open_v2(Utf8z(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            try
            {
                if (rc == SQLITE_OK)
                    rc = sqlite3_prepare_v2(db, Utf8z(sql), -1, out stmt, IntPtr.Zero);
                if (rc != SQLITE_OK)
                    throw new InvalidDataException("Not a Waves SuperRack/MultiRack session: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));

                int cols = sqlite3_column_count(stmt);
                while ((rc = sqlite3_step(stmt)) == SQLITE_ROW)
                {
                    string[] row = new string[cols];
                    for (int i = 0; i < cols; i++)
                        row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, i));
                    rows.Add(row);
                }
                if (rc != SQLITE_DONE)
                    throw new InvalidDataException("Session read failed: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
            }
            finally
            {
                if (stmt != IntPtr.Zero)
                    sqlite3_finalize(stmt);
                sqlite3_close_v2(db);
            }

            return rows;
        }

        private static byte[] Utf8z(string s)
        {
            return Encoding.UTF8.GetBytes(s + "\0");
        }

        #endregion
    }
}
