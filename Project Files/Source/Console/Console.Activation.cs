//  Console.Activation.cs
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
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Thetis
{
    // Tester activation: POWER stays off until the copy is unlocked. The tester sends the request code
    // (callsign + this PC), 3Z9AM answers with the unlock code from tools/unlockgen; both are kept in
    // the database State table and checked against this PC on every POWER on.
    public partial class Console
    {
        private const string ActivationCallKey = "Activation.Call", ActivationCodeKey = "Activation.Code";
        private bool _activated;

        private static string activationMachineId()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    if (k?.GetValue("MachineGuid") is string id && id.Length > 0) return id;
            }
            catch (Exception ex) { Common.LogException(ex); }
            return Environment.MachineName;
        }

        // features of the unlock code when it fits this callsign and PC, null otherwise
        private static ActivationFeature? activationFeatures(string call, string unlock) =>
            ActivationCode.NormalizeCall(call).Length > 0 ? ActivationCode.Check(ActivationCode.Request(call, activationMachineId()), unlock) : null;

        private ActivationFeature? storedActivation(out string call)
        {
            Dictionary<string, string> st = DB.GetVarsDictionary("State");
            st.TryGetValue(ActivationCallKey, out call);
            st.TryGetValue(ActivationCodeKey, out string unlock);
            return activationFeatures(call, unlock);
        }

        // at start (Console_Shown): extras of the stored code, before anything is switched on
        private void loadActivationFeatures()
        {
            ActivationFeature? f = storedActivation(out _);
            _activated = f.HasValue;
            applyActivationFeatures(f ?? ActivationFeature.None);
        }

        // the gated values go to WDSP again (radio.cs sends 0 for a locked soft edge)
        private void applyActivationFeatures(ActivationFeature f)
        {
            ActivationCode.Unlocked = f;
            for (int t = 0; t < 2; t++)
                for (int s = 0; s < 2; s++)
                {
                    RadioDSPRX rx = radio.GetDSPRX(t, s);
                    rx.RXSoftEdge = rx.RXSoftEdge;
                    rx.RXSoftEdgeCarrier = rx.RXSoftEdgeCarrier;
                }
            RadioDSPTX tx = radio.GetDSPTX(0);
            tx.TXSoftEdge = tx.TXSoftEdge;
            tx.TXSoftEdgeCarrier = tx.TXSoftEdgeCarrier;
        }

        // false = keep POWER off
        private bool ensureActivated()
        {
            if (_activated) return true;
            if (storedActivation(out string call) is ActivationFeature f)
            {
                applyActivationFeatures(f);
                return _activated = true;
            }
            return showActivation(call);
        }

        private bool showActivation(string call)
        {
            using (Form f = activationForm(string.IsNullOrEmpty(call) ? CallsignBackground.SavedCallsign() : call))
                if (f.ShowDialog(this) != DialogResult.OK) return _activated;
            loadActivationFeatures();
            return _activated;
        }

        // menu "Activation": a new code (with extra features) at any time
        private void initActivationMenu()
        {
            ToolStripMenuItem item = new ToolStripMenuItem("Activation");
            item.ForeColor = SystemColors.ControlLightLight;
            item.Click += delegate
            {
                storedActivation(out string call);
                showActivation(call);
            };
            menuStrip1.Items.Add(item);
        }

        private Form activationForm(string call)
        {
            Form f = new Form
            {
                Text = "SDR-VST3 activation",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(420, 270),
                Font = new Font("Segoe UI", 9f),
            };
            Font big = new Font("Consolas", 18f, FontStyle.Bold);
            Label info = new Label
            {
                Location = new Point(12, 10), Size = new Size(396, 48),
                Text = "This test copy needs a one-time activation. Enter your callsign and send the request code to 3Z9AM; " +
                       "type the unlock code you get back.",
            };
            Label lCall = new Label { Text = "Callsign", Location = new Point(12, 68), AutoSize = true };
            TextBox txtCall = new TextBox { Location = new Point(130, 64), Width = 150, CharacterCasing = CharacterCasing.Upper, Text = call ?? "" };
            Label lReq = new Label { Text = "Request code", Location = new Point(12, 110), AutoSize = true };
            TextBox txtReq = new TextBox { Location = new Point(130, 100), Width = 150, ReadOnly = true, Font = big, TextAlign = HorizontalAlignment.Center };
            Button btnCopy = new Button { Text = "Copy", Location = new Point(290, 104), Width = 80 };
            Label lUnlock = new Label { Text = "Unlock code", Location = new Point(12, 162), AutoSize = true };
            TextBox txtUnlock = new TextBox { Location = new Point(130, 152), Width = 150, Font = big, MaxLength = ActivationCode.Length + 3,
                CharacterCasing = CharacterCasing.Upper, TextAlign = HorizontalAlignment.Center };
            Label lErr = new Label { Location = new Point(12, 200), Size = new Size(396, 20), ForeColor = Color.IndianRed };
            if (_activated) lErr.Text = "Now: " + ActivationCode.Describe(ActivationCode.Unlocked);
            Button btnOk = new Button { Text = "Activate", Location = new Point(240, 230), Width = 80 };
            Button btnCancel = new Button { Text = "Cancel", Location = new Point(328, 230), Width = 80, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { info, lCall, txtCall, lReq, txtReq, btnCopy, lUnlock, txtUnlock, lErr, btnOk, btnCancel });
            f.AcceptButton = btnOk;
            f.CancelButton = btnCancel;
            f.Disposed += (s, e) => big.Dispose();

            void refresh()
            {
                string c = ActivationCode.NormalizeCall(txtCall.Text);
                txtReq.Text = c.Length > 0 ? ActivationCode.Request(c, activationMachineId()) : "";
            }
            txtCall.TextChanged += (s, e) => refresh();
            refresh();
            btnCopy.Click += (s, e) => { if (txtReq.Text.Length > 0) Clipboard.SetText(txtReq.Text); };
            btnOk.Click += (s, e) =>
            {
                if (activationFeatures(txtCall.Text, txtUnlock.Text) == null)
                {
                    lErr.Text = "Wrong unlock code for this callsign and PC.";
                    return;
                }
                DB.SaveVars("State", new List<string>
                {
                    ActivationCallKey + "/" + ActivationCode.NormalizeCall(txtCall.Text),
                    ActivationCodeKey + "/" + ActivationCode.Normalize(txtUnlock.Text),
                });
                DB.WriteDB();   // at once: a crash before the normal save on exit would ask again
                f.DialogResult = DialogResult.OK;
            };
            return f;
        }
    }
}
