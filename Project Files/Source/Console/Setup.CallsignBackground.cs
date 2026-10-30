using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Thetis
{
    // Callsign background (CallsignBackground.cs) on Setup > Appearance > General. Not saved
    // in the DB: the callsign is kept in the generated folder (callsign.txt).
    public partial class Setup
    {
        private GroupBoxTS grpCallsignBackground;
        private TextBoxTS txtCallsignBackground;
        private ButtonTS btnCallsignBackgroundMake;
        private ButtonTS btnCallsignBackgroundRemove;

        private void initCallsignBackgroundControls()
        {
            grpCallsignBackground = new GroupBoxTS();
            grpCallsignBackground.Text = "Callsign Background";
            grpCallsignBackground.Location = new Point(613, 104);
            grpCallsignBackground.Size = new Size(105, 98);
            grpCallsignBackground.Name = "grpCallsignBackground";

            txtCallsignBackground = new TextBoxTS();
            txtCallsignBackground.Name = "txtCallsignBackground";
            txtCallsignBackground.Location = new Point(6, 18);
            txtCallsignBackground.Size = new Size(93, 20);
            txtCallsignBackground.MaxLength = 12;
            txtCallsignBackground.CharacterCasing = CharacterCasing.Upper;
            txtCallsignBackground.Text = CallsignBackground.SavedCallsign() ?? "";

            btnCallsignBackgroundMake = new ButtonTS();
            btnCallsignBackgroundMake.Text = "Generate";
            btnCallsignBackgroundMake.Location = new Point(6, 43);
            btnCallsignBackgroundMake.Size = new Size(93, 23);
            btnCallsignBackgroundMake.Click += btnCallsignBackgroundMake_Click;

            btnCallsignBackgroundRemove = new ButtonTS();
            btnCallsignBackgroundRemove.Text = "Use skin";
            btnCallsignBackgroundRemove.Location = new Point(6, 69);
            btnCallsignBackgroundRemove.Size = new Size(93, 23);
            btnCallsignBackgroundRemove.Click += btnCallsignBackgroundRemove_Click;

            toolTip1.SetToolTip(btnCallsignBackgroundMake,
                "Draws this callsign on the display background: a static red picture and 60 animation frames " +
                "from red (weak signal / low power) to green (S9+30 / TX max W). Also shown on the start window.");
            toolTip1.SetToolTip(btnCallsignBackgroundRemove, "Back to the skin's own display background.");

            grpCallsignBackground.Controls.Add(txtCallsignBackground);
            grpCallsignBackground.Controls.Add(btnCallsignBackgroundMake);
            grpCallsignBackground.Controls.Add(btnCallsignBackgroundRemove);
            tpAppearanceGeneral.Controls.Add(grpCallsignBackground);
        }

        private async void btnCallsignBackgroundMake_Click(object sender, EventArgs e)
        {
            string call = txtCallsignBackground.Text.Trim().ToUpperInvariant();
            if (call.Length == 0) return;

            await changeCallsignBackground("Working...", () =>
            {
                CallsignBackground.Generate(call);
                CallsignBackground.Install();
            });
        }

        private async void btnCallsignBackgroundRemove_Click(object sender, EventArgs e)
        {
            await changeCallsignBackground("Removing...", CallsignBackground.Remove);
            txtCallsignBackground.Text = "";
        }

        private async Task changeCallsignBackground(string busyText, Action work)
        {
            grpCallsignBackground.Enabled = false;
            string old = grpCallsignBackground.Text;
            grpCallsignBackground.Text = busyText;
            try
            {
                Display.SetBackgroundAnimationFrames(null);  // stop reading the old frames before they go
                await Task.Delay(300);                       // let a frame decode in progress finish
                await Task.Run(work);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Callsign background: " + ex.Message, "Callsign Background",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                console.ReapplyDisplayBackground();
                grpCallsignBackground.Text = old;
                grpCallsignBackground.Enabled = true;
            }
        }
    }
}
