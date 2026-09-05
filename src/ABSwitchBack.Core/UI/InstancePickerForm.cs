using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ABSwitchBack.Core.Discovery;
using ABSwitchBack.Core.Ipc;

namespace ABSwitchBack.Core.UI
{
    /// <summary>
    /// Shared destination picker. Revit shows it listing Navisworks instances;
    /// Navisworks shows it listing Revit instances. Same code, different role.
    /// </summary>
    public sealed class InstancePickerForm : Form
    {
        private readonly string _role;
        private readonly int _timeoutMs;
        private readonly bool _allowSelect;
        private readonly ListView _list;
        private readonly Button _ok;
        private readonly Button _test;
        private readonly Label _status;

        public InstanceInfo Selected { get; private set; }

        /// <param name="allowSelect">
        /// True in Navisworks, which genuinely chooses a destination. False in Revit, where
        /// this is only a connection check: Revit never sends anything, so offering to
        /// "select" a Navisworks would store a preference nothing acts on.
        /// </param>
        public InstancePickerForm(string role, string title, int timeoutMs, bool allowSelect)
        {
            _role = role;
            _timeoutMs = timeoutMs;
            _allowSelect = allowSelect;

            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(720, 340);
            MinimumSize = new Size(560, 260);
            Font = SystemFonts.MessageBoxFont;

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = false
            };
            _list.Columns.Add("Application", 150);
            _list.Columns.Add("Version", 70);
            _list.Columns.Add("Document", 330);
            _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
            _list.DoubleClick += (s, e) => AcceptSelection();
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();

            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 4, 0),
                ForeColor = SystemColors.GrayText
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(6)
            };

            var cancel = new Button
            {
                Text = _allowSelect ? "Cancel" : "Close",
                DialogResult = DialogResult.Cancel,
                Width = 90,
                Height = 28
            };
            _ok = new Button { Text = "Select", Width = 90, Height = 28, Enabled = false, Visible = _allowSelect };
            _ok.Click += (s, e) => AcceptSelection();
            _test = new Button { Text = "Test", Width = 90, Height = 28, Enabled = false };
            _test.Click += (s, e) => TestSelection();
            var refresh = new Button { Text = "Refresh", Width = 90, Height = 28 };
            refresh.Click += (s, e) => Reload();

            buttons.Controls.Add(cancel);
            if (_allowSelect) buttons.Controls.Add(_ok);
            buttons.Controls.Add(_test);
            buttons.Controls.Add(refresh);

            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(buttons);

            // Without selection there is nothing to accept, so Enter should test instead.
            AcceptButton = _allowSelect ? _ok : _test;
            CancelButton = cancel;

            Reload();
        }

        private void Reload()
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                List<InstanceInfo> instances = InstanceRegistry.List(_role);

                foreach (InstanceInfo i in instances)
                {
                    var item = new ListViewItem(i.AppName);
                    item.SubItems.Add(i.Version);
                    item.SubItems.Add(string.IsNullOrEmpty(i.Document) ? "(no document open)" : i.Document);
                    item.SubItems.Add(i.Pid.ToString(CultureInfo.InvariantCulture));
                    item.Tag = i;
                    _list.Items.Add(item);
                }

                if (_list.Items.Count > 0)
                {
                    _list.Items[0].Selected = true;
                    _list.Select();
                    _status.Text = _list.Items.Count == 1
                        ? "1 running " + _role + " instance found."
                        : _list.Items.Count.ToString(CultureInfo.InvariantCulture) + " running " + _role + " instances found.";
                }
                else
                {
                    _status.Text = "No running " + _role + " instance with SwitchBack loaded was found.";
                }
            }
            finally
            {
                _list.EndUpdate();
                UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            bool has = _list.SelectedItems.Count > 0;
            _ok.Enabled = has && _allowSelect;
            _test.Enabled = has;
        }

        private InstanceInfo Current
        {
            get { return _list.SelectedItems.Count == 0 ? null : (InstanceInfo)_list.SelectedItems[0].Tag; }
        }

        private void TestSelection()
        {
            InstanceInfo info = Current;
            if (info == null) return;

            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            _test.Enabled = false;
            try
            {
                string error;
                bool ok = PipeClient.Ping(info.PipeName, _timeoutMs, out error);
                _status.ForeColor = ok ? Color.FromArgb(0, 110, 40) : Color.FromArgb(170, 20, 20);
                _status.Text = ok
                    ? "Connected to " + info.DisplayName
                    : "Could not reach that instance: " + error;
            }
            finally
            {
                Cursor = previous;
                _test.Enabled = true;
            }
        }

        private void AcceptSelection()
        {
            if (!_allowSelect) { TestSelection(); return; }

            InstanceInfo info = Current;
            if (info == null) return;
            Selected = info;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Picks a destination; returns null when the user cancels.</summary>
        public static InstanceInfo Show(IWin32Window owner, string role, string title, int timeoutMs)
        {
            using (var form = new InstancePickerForm(role, title, timeoutMs, true))
            {
                DialogResult r = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
                return r == DialogResult.OK ? form.Selected : null;
            }
        }

        /// <summary>Read-only view of the running instances, with a connection Test button.</summary>
        public static void ShowReadOnly(IWin32Window owner, string role, string title, int timeoutMs)
        {
            using (var form = new InstancePickerForm(role, title, timeoutMs, false))
            {
                if (owner != null) form.ShowDialog(owner); else form.ShowDialog();
            }
        }
    }
}
