using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ABSwitchBack.Core.UI
{
    /// <summary>
    /// Settings dialog shared by both hosts. Everything here lives in the one
    /// config.txt under %LOCALAPPDATA%, so it can be opened from Revit or Navisworks
    /// and there is never a need to hand-edit the file.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly SwitchBackConfig _config;

        private readonly CheckBox _enabled;
        private readonly CheckBox _ctrl;
        private readonly CheckBox _shift;
        private readonly CheckBox _alt;
        private readonly Label _gesture;
        private readonly Label _warning;

        private readonly CheckBox _sectionBox;
        private readonly NumericUpDown _margin;
        private readonly CheckBox _createView;
        private readonly Label _readOnlyHint;

        private bool _loading;

        public SettingsForm(SwitchBackConfig config)
        {
            _config = config ?? SwitchBackConfig.Load();

            Text = Branding.ProductName + " settings";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(470, 452);

            // ---------------------------------------------------------- trigger
            var triggerGroup = new GroupBox
            {
                Text = "Trigger",
                Location = new Point(12, 12),
                Size = new Size(446, 196)
            };

            _enabled = new CheckBox
            {
                Text = "Enable the switch-back trigger in Navisworks",
                Location = new Point(14, 26),
                Size = new Size(410, 22)
            };
            _enabled.CheckedChanged += (s, e) => Refresh_();

            var modifierLabel = new Label
            {
                Text = "Hold these keys while left-clicking an element:",
                Location = new Point(14, 56),
                Size = new Size(410, 20)
            };

            _ctrl = new CheckBox { Text = "Ctrl", Location = new Point(30, 80), Size = new Size(80, 22) };
            _shift = new CheckBox { Text = "Shift", Location = new Point(120, 80), Size = new Size(80, 22) };
            _alt = new CheckBox { Text = "Alt", Location = new Point(210, 80), Size = new Size(80, 22) };
            _ctrl.CheckedChanged += (s, e) => Refresh_();
            _shift.CheckedChanged += (s, e) => Refresh_();
            _alt.CheckedChanged += (s, e) => Refresh_();

            _gesture = new Label
            {
                Location = new Point(14, 112),
                Size = new Size(410, 24),
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
            };

            _warning = new Label
            {
                Location = new Point(14, 138),
                Size = new Size(418, 48),
                ForeColor = Color.FromArgb(170, 70, 0)
            };

            triggerGroup.Controls.AddRange(new Control[]
            {
                _enabled, modifierLabel, _ctrl, _shift, _alt, _gesture, _warning
            });

            // ---------------------------------------------------------- revit
            var revitGroup = new GroupBox
            {
                Text = "What Revit does when an element arrives",
                Location = new Point(12, 218),
                Size = new Size(446, 158)
            };

            _sectionBox = new CheckBox
            {
                Text = "Create a section box around the element",
                Location = new Point(14, 26),
                Size = new Size(410, 22)
            };
            _sectionBox.CheckedChanged += (s, e) => Refresh_();

            var marginLabel = new Label
            {
                Text = "Section box margin (mm):",
                Location = new Point(34, 54),
                Size = new Size(160, 22),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _margin = new NumericUpDown
            {
                Location = new Point(200, 52),
                Size = new Size(90, 22),
                Minimum = 0,
                Maximum = 100000,
                Increment = 100,
                ThousandsSeparator = true
            };

            _createView = new CheckBox
            {
                Text = "Create a 3D view if the project has none that is usable",
                Location = new Point(14, 84),
                Size = new Size(418, 22)
            };
            _createView.CheckedChanged += (s, e) => Refresh_();

            _readOnlyHint = new Label
            {
                Location = new Point(14, 110),
                Size = new Size(418, 40),
                ForeColor = SystemColors.GrayText
            };

            revitGroup.Controls.AddRange(new Control[]
            {
                _sectionBox, marginLabel, _margin, _createView, _readOnlyHint
            });

            // ---------------------------------------------------------- buttons
            var reset = new Button
            {
                Text = "Reset to defaults",
                Location = new Point(12, 388),
                Size = new Size(130, 28)
            };
            reset.Click += (s, e) => LoadValues(new SwitchBackConfig());

            var save = new Button
            {
                Text = "Save",
                Location = new Point(268, 388),
                Size = new Size(90, 28),
                DialogResult = DialogResult.OK
            };
            save.Click += (s, e) => Save();

            var cancel = new Button
            {
                Text = "Cancel",
                Location = new Point(368, 388),
                Size = new Size(90, 28),
                DialogResult = DialogResult.Cancel
            };

            var pathHint = new Label
            {
                Text = Paths.ConfigFile,
                Location = new Point(12, 424),
                Size = new Size(446, 20),
                ForeColor = SystemColors.GrayText,
                AutoEllipsis = true
            };

            Controls.AddRange(new Control[] { triggerGroup, revitGroup, reset, save, cancel, pathHint });

            AcceptButton = save;
            CancelButton = cancel;

            LoadValues(_config);
        }

        private void LoadValues(SwitchBackConfig config)
        {
            _loading = true;
            try
            {
                _enabled.Checked = config.TriggerEnabled;

                TriggerModifiers modifiers = TriggerGesture.Parse(config.Trigger);
                _ctrl.Checked = (modifiers & TriggerModifiers.Ctrl) != 0;
                _shift.Checked = (modifiers & TriggerModifiers.Shift) != 0;
                _alt.Checked = (modifiers & TriggerModifiers.Alt) != 0;

                _sectionBox.Checked = config.CreateSectionBox;
                _margin.Value = Clamp(config.SectionBoxMarginMm, _margin.Minimum, _margin.Maximum);
                _createView.Checked = config.CreateViewIfMissing;
            }
            finally
            {
                _loading = false;
            }
            Refresh_();
        }

        private static decimal Clamp(double value, decimal min, decimal max)
        {
            try
            {
                decimal d = (decimal)value;
                return d < min ? min : (d > max ? max : d);
            }
            catch { return min; }
        }

        private TriggerModifiers SelectedModifiers
        {
            get
            {
                var modifiers = TriggerModifiers.None;
                if (_ctrl.Checked) modifiers |= TriggerModifiers.Ctrl;
                if (_shift.Checked) modifiers |= TriggerModifiers.Shift;
                if (_alt.Checked) modifiers |= TriggerModifiers.Alt;
                return modifiers;
            }
        }

        /// <summary>Keeps the preview, warnings and enabled states in step with the choices.</summary>
        private void Refresh_()
        {
            if (_loading) return;

            bool on = _enabled.Checked;
            _ctrl.Enabled = on;
            _shift.Enabled = on;
            _alt.Enabled = on;
            _gesture.Enabled = on;

            TriggerModifiers modifiers = SelectedModifiers;
            _gesture.Text = on
                ? "Gesture:  " + TriggerGesture.Describe(modifiers)
                : "The trigger is off. Nothing is sent to Revit.";

            if (!on)
            {
                _warning.Text = string.Empty;
            }
            else if (TriggerGesture.IsReservedByNavisworks(modifiers))
            {
                _warning.Text =
                    "Not recommended. Navisworks reserves Ctrl+Shift+click and expands the pick to " +
                    "the whole model file, so no element id can be found.";
            }
            else if (modifiers == TriggerModifiers.None)
            {
                _warning.Text =
                    "With no modifier, EVERY element you select is sent to Revit. Useful for a " +
                    "dedicated coordination session, noisy otherwise.";
            }
            else
            {
                _warning.Text = string.Empty;
            }

            _margin.Enabled = _sectionBox.Checked;

            _readOnlyHint.Text = (!_sectionBox.Checked && !_createView.Checked)
                ? "Strictly read-only: Revit will only select and zoom, and will not modify the model."
                : "These are the only changes made to the model. Both are undoable with Ctrl+Z.";
        }

        private void Save()
        {
            _config.TriggerEnabled = _enabled.Checked;
            _config.Trigger = TriggerGesture.Format(SelectedModifiers);
            _config.CreateSectionBox = _sectionBox.Checked;
            _config.SectionBoxMarginMm = (double)_margin.Value;
            _config.CreateViewIfMissing = _createView.Checked;
            _config.Save();

            Log.Info("Settings saved. Trigger=" + _config.Trigger +
                     " Enabled=" + _config.TriggerEnabled +
                     " SectionBox=" + _config.CreateSectionBox +
                     " MarginMm=" + _config.SectionBoxMarginMm.ToString("0", CultureInfo.InvariantCulture) +
                     " CreateView=" + _config.CreateViewIfMissing);
        }

        /// <summary>Shows the dialog; returns true when the user saved.</summary>
        public static bool Show(IWin32Window owner)
        {
            SwitchBackConfig config = SwitchBackConfig.Load();
            using (var form = new SettingsForm(config))
            {
                DialogResult result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
                return result == DialogResult.OK;
            }
        }
    }
}
