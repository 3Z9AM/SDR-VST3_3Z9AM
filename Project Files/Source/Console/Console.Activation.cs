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

        private static bool activationMatches(string call, string unlock) =>
            ActivationCode.NormalizeCall(call).Length > 0 &&
            ActivationCode.Normalize(unlock) == ActivationCode.Unlock(ActivationCode.Request(call, activationMachineId()));

        // false = keep POWER off
        private bool ensureActivated()
        {
            if (_activated) return true;
            Dictionary<string, string> st = DB.GetVarsDictionary("State");
            st.TryGetValue(ActivationCallKey, out string call);
            st.TryGetValue(ActivationCodeKey, out string unlock);
            if (activationMatches(call, unlock)) return _activated = true;

            using (Form f = activationForm(string.IsNullOrEmpty(call) ? CallsignBackground.SavedCallsign() : call))
                return _activated = f.ShowDialog(this) == DialogResult.OK;
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
            TextBox txtUnlock = new TextBox { Location = new Point(130, 152), Width = 150, Font = big, MaxLength = ActivationCode.Length + 2,
                CharacterCasing = CharacterCasing.Upper, TextAlign = HorizontalAlignment.Center };
            Label lErr = new Label { Location = new Point(12, 200), Size = new Size(396, 20), ForeColor = Color.IndianRed };
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
                if (!activationMatches(txtCall.Text, txtUnlock.Text))
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
