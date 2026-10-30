/*  VstWavesImportTests.cs

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
using System.Text;
using Xunit;

namespace Thetis.Tests
{
    public class VstWavesImportTests
    {
        private static string Preset(string plugin, string name, string parameters)
        {
            return "<PresetChunkXMLTree version=\"3\"><Preset Name=\"" + name + "\" GenericType=\"LA2A\"><PresetHeader><PluginName>"
                + plugin + "</PluginName></PresetHeader><PresetData Setup=\"CURRENT\"><Parameters Type=\"RealWorld\">"
                + parameters + "</Parameters></PresetData></Preset></PresetChunkXMLTree>";
        }

        // bridge blob: magic, component size, controller size; component = 8 byte prefix
        // (LE xml length, BE bytes to stream end) + xml + 3 trailing bytes; controller = 5 bytes
        private static byte[] Blob(string xml)
        {
            byte[] x = Encoding.UTF8.GetBytes(xml);
            int comp = 8 + x.Length + 3;
            List<byte> b = new List<byte>();
            b.AddRange(BitConverter.GetBytes(0x31535654u));
            b.AddRange(BitConverter.GetBytes((uint)comp));
            b.AddRange(BitConverter.GetBytes(5u));
            b.AddRange(BitConverter.GetBytes((uint)x.Length));
            byte[] remaining = BitConverter.GetBytes((uint)(comp - 8));
            Array.Reverse(remaining);
            b.AddRange(remaining);
            b.AddRange(x);
            b.AddRange(new byte[] { 1, 2, 3, 9, 9, 9, 9, 9 });
            return b.ToArray();
        }

        [Fact]
        public void Patch_replaces_xml_and_moves_length_fields()
        {
            string newXml = Preset("CLA-2A", "Start Me Up", "1 2 3 4 5 6 7 8 9 10 11 12");
            byte[] patched = VstWavesImport.PatchStatePreset(Blob(Preset("CLA-2A", "", "0 0")), "<?xml version=\"1.0\"?>\n" + newXml);

            Assert.Equal(Blob(newXml), patched);
        }

        [Fact]
        public void Patch_refuses_other_plugin_or_missing_xml()
        {
            Assert.Null(VstWavesImport.PatchStatePreset(Blob(Preset("C4", "", "0")), Preset("CLA-2A", "x", "1")));
            Assert.Null(VstWavesImport.PatchStatePreset(new byte[] { 0x54, 0x56, 0x53, 0x31, 1, 0, 0, 0, 0, 0, 0, 0, 7 }, Preset("CLA-2A", "x", "1")));
            Assert.Null(VstWavesImport.PatchStatePreset(Blob(Preset("CLA-2A", "", "0")), "<PresetXMLTree/>"));
        }

        [Fact]
        public void Catalog_match_prefers_stereo_waves_entry()
        {
            List<VstCatalogPlugin> catalog = new List<VstCatalogPlugin>
            {
                new VstCatalogPlugin { Name = "CLA-2A Mono", Vendor = "Waves", Available = true, ClassId = "m" },
                new VstCatalogPlugin { Name = "CLA-2A Stereo", Vendor = "Waves", Available = true, ClassId = "s" },
                new VstCatalogPlugin { Name = "CLA-2A Stereo", Vendor = "Other", Available = true, ClassId = "o" },
                new VstCatalogPlugin { Name = "EKramer GT Mono", Vendor = "Waves Audio", Available = true, ClassId = "k" },
            };

            Assert.Equal("s", VstWavesImport.FindPlugin(catalog, new VstWavesImport.Entry { PluginName = "CLA-2A", Name = "CLA-2A" }).ClassId);
            Assert.Equal("k", VstWavesImport.FindPlugin(catalog, new VstWavesImport.Entry { PluginName = "EKramer GT", Name = "Krmr GT" }).ClassId);
            Assert.Null(VstWavesImport.FindPlugin(catalog, new VstWavesImport.Entry { PluginName = "C4", Name = "C4" }));
        }
    }
}
