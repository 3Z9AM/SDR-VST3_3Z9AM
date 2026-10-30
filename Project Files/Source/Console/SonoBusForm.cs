using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Thetis
{
    public partial class Console
    {
        private SonoBusForm _sonoBusForm;

        // "SonoBus" menu item after CW Skimmer; created now so SonoBus left enabled starts with Thetis
        private void initSonoBus()
        {
            ToolStripMenuItem item = new ToolStripMenuItem("SonoBus");
            _sonoBusForm = new SonoBusForm(item);
            item.ForeColor = SystemColors.ControlLightLight;
            item.Click += delegate
            {
                if (_sonoBusForm.Visible) _sonoBusForm.BringToFront();
                else _sonoBusForm.Show(this);
            };
            menuStrip1.Items.Insert(menuStrip1.Items.IndexOf(vstToolStripMenuItem) + 2, item);
        }
    }

    // One SonoBus instance outside the VST chains (VstChainKind.Remote, its own host process): its input is the RX
    // audio at the end of the RX chain (sent to the group), its output - the remote operator - is the mic source
    // ahead of the TX chain (VstHost.feedRemote / readRemote).
    internal sealed class SonoBusForm : Form
    {
        private const string ClassId = "ABCDEF019182FAEB536F6E6F4E427573";    // DefaultVstCatalogPlugins.json
        private static readonly string PluginPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3", "SonoBus.vst3");

        private readonly CheckBoxTS chkSonoBusEnable;
        private readonly CheckBoxTS chkSonoBusSendMon;
        private readonly Button btnSonoBusOpen;
        private readonly LabelTS lblSonoBusStatus;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ToolStripMenuItem _menuItem;       // its text: green = running, red = a peer is connected
        private readonly bool _restoring;
        private bool _addTried;

        public SonoBusForm(ToolStripMenuItem menuItem)
        {
            _menuItem = menuItem;
            Text = "SonoBus";
            ClientSize = new Size(460, 156);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            chkSonoBusEnable = new CheckBoxTS { Name = "chkSonoBusEnable", Text = "Enable", Location = new Point(12, 12), AutoSize = true };
            btnSonoBusOpen = new Button { Text = "Open SonoBus", Location = new Point(100, 8), Size = new Size(110, 24) };
            chkSonoBusSendMon = new CheckBoxTS { Name = "chkSonoBusSendMon", Text = "Send TX monitor (MON) to the group", Location = new Point(230, 12), AutoSize = true, Checked = true };
            lblSonoBusStatus = new LabelTS { Location = new Point(12, 42), Size = new Size(436, 34), Text = "Off" };
            LabelTS lblHelp = new LabelTS
            {
                Location = new Point(12, 80),
                Size = new Size(436, 70),
                Text = "RX audio (after the RX VST chain) goes to the group; the group is the TX mic (before the TX VST chain). " +
                       "Turn input monitoring off in SonoBus, or RX goes on the air (and with MON the voice loops endlessly).",
            };
            ToolTip tip = new ToolTip();
            tip.SetToolTip(chkSonoBusEnable, "Load SonoBus outside the VST chains. Off removes it (disconnects).");
            tip.SetToolTip(btnSonoBusOpen, "Show the SonoBus window (group, users, levels).");
            tip.SetToolTip(chkSonoBusSendMon, "While transmitting with MON on, the group hears the transmitted audio. Off = silence to the group during TX.");
            Controls.AddRange(new Control[] { chkSonoBusEnable, btnSonoBusOpen, chkSonoBusSendMon, lblSonoBusStatus, lblHelp });
            chkSonoBusSendMon.CheckedChanged += delegate
            {
                VstHost.RemoteSendMon = chkSonoBusSendMon.Checked;
                if (!_restoring) Common.SaveForm(this, "SonoBus");
            };

            chkSonoBusEnable.CheckedChanged += delegate
            {
                _addTried = false;
                if (!_restoring) Common.SaveForm(this, "SonoBus");
                sync();
            };
            btnSonoBusOpen.Click += delegate
            {
                if (!VstHost.OpenPluginEditorWindow(VstChainKind.Remote, 0))
                    lblSonoBusStatus.Text = "SonoBus is not running.";
            };
            FormClosing += (s, e) =>
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Common.SaveForm(this, "SonoBus");
                VstHost.ScheduleUiStateSave();      // SonoBus group/password now, not only at exit
                Hide();
            };

            _restoring = true;
            Common.RestoreForm(this, "SonoBus", false);
            _restoring = false;
            VstHost.RemoteSendMon = chkSonoBusSendMon.Checked;

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += delegate
            {
                sync();
                Color c = VstHost.RemotePeerHeard ? Color.Red : VstHost.RemoteOn ? Color.LimeGreen : SystemColors.ControlLightLight;
                if (_menuItem.ForeColor != c) _menuItem.ForeColor = c;
            };
            _timer.Start();
        }

        // brings the Remote chain to the checkbox and lets audio through only while the plugin runs
        private void sync()
        {
            if (!VstHost.NativeAvailable) { VstHost.RemoteOn = false; lblSonoBusStatus.Text = "VST host unavailable."; return; }
            VstChainInfo info = VstHost.GetChainInfo(VstChainKind.Remote);
            int count = info.Plugins == null ? 0 : info.Plugins.Count;
            bool on = chkSonoBusEnable.Checked;

            if (!on && count > 0) { VstHost.RemoteOn = false; VstHost.RemovePlugin(VstChainKind.Remote, 0); count = 0; }
            if (on && count == 0 && !_addTried)
            {
                _addTried = true;   // once per enable: a failed load is not retried every tick
                VstOperationResult r = VstHost.AddPlugin(VstChainKind.Remote, PluginPath, ClassId);
                VstHost.RemoteOn = false;
                lblSonoBusStatus.Text = r.Success ? "Loading..." : r.Message + " (" + PluginPath + ")";
                return;     // state shows on the next tick
            }

            if (on && count > 0 && info.Bypass) { VstHost.SetChainBypass(VstChainKind.Remote, false); info.Bypass = false; }   // a new chain starts bypassed
            bool running = on && count > 0 && info.HostState == VstHostState.Running && !info.Bypass &&
                           info.Plugins[0].LoadState == VstPluginLoadState.Active;
            VstHost.RemoteOn = running;

            btnSonoBusOpen.Enabled = running;
            if (!on) lblSonoBusStatus.Text = "Off";
            else if (count == 0) { }                                // keeps the load error shown
            else if (!running) lblSonoBusStatus.Text = "Starting... (" + VstHost.GetHostStateDisplayName(info.HostState) + ")";
            else if (cmaster.GetInputRate(1, 0) != Audio.OutRate)
                lblSonoBusStatus.Text = "Running, but mic rate " + cmaster.GetInputRate(1, 0) + " Hz <> RX audio " + Audio.OutRate + " Hz: TX gets silence.";
            else lblSonoBusStatus.Text = "Running: RX -> group, group -> TX mic.";
        }
    }
}
