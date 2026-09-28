using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MenuPrio
{
    internal sealed class PriorityForm : Form
    {
        private readonly HistoryStore _store;
        private readonly Interceptor _interceptor;

        private readonly ListView _groups = new ListView();
        private readonly ListView _candidates = new ListView();
        private readonly ListView _events = new ListView();
        private readonly CheckBox _strict = new CheckBox();
        private readonly CheckBox _pause = new CheckBox();
        private readonly Timer _timer = new Timer();
        private readonly ImageList _icons = new ImageList();
        private readonly Dictionary<string, int> _iconIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private bool _refreshing;
        private bool _syncing;
        private bool _dragging;
        private string _selectedKey;

        public PriorityForm(HistoryStore store, Interceptor interceptor)
        {
            _store = store;
            _interceptor = interceptor;

            Text = "MenuPrio " + Program.Version() + " - Start Enter priorities";
            ClientSize = new Size(980, 600);
            MinimumSize = new Size(800, 460);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcons.Get();

            _icons.ImageSize = new Size(16, 16);
            _icons.ColorDepth = ColorDepth.Depth32Bit;

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildPrioritiesTab());
            tabs.TabPages.Add(BuildActivityTab());
            Controls.Add(tabs);

            _timer.Interval = 1200;
            _timer.Tick += delegate { RefreshData(); };
            _timer.Start();

            RefreshData();
        }

        // ---------------- layout ----------------

        private TabPage BuildPrioritiesTab()
        {
            var page = new TabPage("Priorities");
            page.Padding = new Padding(8);

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var toolbar = new Panel();
            toolbar.Dock = DockStyle.Fill;

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Left;
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.FlowDirection = FlowDirection.LeftToRight;

            var add = NewButton("Add app...");
            add.Click += delegate { AddApp(); };
            var edit = NewButton("Edit...");
            edit.Click += delegate { EditSelected(); };
            var remove = NewButton("Remove");
            remove.Click += delegate { RemoveSelected(); };
            var up = NewButton("Up");
            up.Click += delegate { MoveSelected(-1); };
            var down = NewButton("Down");
            down.Click += delegate { MoveSelected(1); };
            buttons.Controls.Add(add);
            buttons.Controls.Add(edit);
            buttons.Controls.Add(remove);
            buttons.Controls.Add(up);
            buttons.Controls.Add(down);

            _pause.Text = "Pause interception";
            _pause.AutoSize = true;
            _pause.Dock = DockStyle.Right;
            _pause.Padding = new Padding(0, 8, 4, 0);
            _pause.CheckedChanged += delegate
            {
                if (!_refreshing) _interceptor.Paused = _pause.Checked;
            };

            toolbar.Controls.Add(buttons);
            toolbar.Controls.Add(_pause);

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterDistance = 260;
            split.FixedPanel = FixedPanel.Panel1;
            split.Panel1.Controls.Add(BuildGroupsPanel());
            split.Panel2.Controls.Add(BuildCandidatesPanel());

            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(split, 0, 1);
            page.Controls.Add(root);
            return page;
        }

        private Control BuildGroupsPanel()
        {
            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 1;
            grid.RowCount = 3;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            var label = new Label();
            label.Text = "Typed text - the top one wins when several words match what you type.";
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = SystemColors.GrayText;

            _groups.View = View.Details;
            _groups.HeaderStyle = ColumnHeaderStyle.None;
            _groups.Dock = DockStyle.Fill;
            _groups.FullRowSelect = true;
            _groups.MultiSelect = false;
            _groups.HideSelection = false;
            _groups.AllowDrop = true;
            _groups.Columns.Add("Typed text", 220);
            _groups.SelectedIndexChanged += delegate { GroupSelectionChanged(); };
            _groups.ItemDrag += GroupsItemDrag;
            _groups.DragEnter += GroupsDragEnter;
            _groups.DragOver += GroupsDragOver;
            _groups.DragDrop += GroupsDragDrop;
            _groups.DragLeave += delegate { _groups.InsertionMark.Index = -1; };

            var bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.WrapContents = false;
            bottom.FlowDirection = FlowDirection.LeftToRight;

            var up = NewButton("Up");
            up.Click += delegate { MoveSelectedGroup(-1); };
            var down = NewButton("Down");
            down.Click += delegate { MoveSelectedGroup(1); };
            var del = NewButton("Delete");
            del.Click += delegate { DeleteGroup(); };
            bottom.Controls.Add(up);
            bottom.Controls.Add(down);
            bottom.Controls.Add(del);

            grid.Controls.Add(label, 0, 0);
            grid.Controls.Add(_groups, 0, 1);
            grid.Controls.Add(bottom, 0, 2);
            return grid;
        }

        private Control BuildCandidatesPanel()
        {
            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 1;
            grid.RowCount = 3;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            var hint = new Label();
            hint.Text = "Enter opens the top item. Drag rows to reorder. Apps appear here after you use them, or add manually.";
            hint.Dock = DockStyle.Fill;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.ForeColor = SystemColors.GrayText;

            _candidates.View = View.Details;
            _candidates.Dock = DockStyle.Fill;
            _candidates.FullRowSelect = true;
            _candidates.MultiSelect = false;
            _candidates.HideSelection = false;
            _candidates.AllowDrop = true;
            _candidates.SmallImageList = _icons;
            _candidates.Columns.Add("#", 36);
            _candidates.Columns.Add("Name", 190);
            _candidates.Columns.Add("Target", 340);
            _candidates.Columns.Add("Used", 54);
            _candidates.Columns.Add("Last used", 130);
            _candidates.ItemDrag += CandidatesItemDrag;
            _candidates.DragEnter += CandidatesDragEnter;
            _candidates.DragOver += CandidatesDragOver;
            _candidates.DragDrop += CandidatesDragDrop;
            _candidates.DragLeave += delegate { _candidates.InsertionMark.Index = -1; };
            _candidates.DoubleClick += delegate { EditSelected(); };

            _strict.Text = "Exact match only (don't match while typing)";
            _strict.AutoSize = true;
            _strict.Dock = DockStyle.Left;
            _strict.Padding = new Padding(0, 6, 0, 0);
            _strict.CheckedChanged += delegate
            {
                if (_syncing || _refreshing || string.IsNullOrEmpty(_selectedKey)) return;
                _store.SetStrict(_selectedKey, _strict.Checked);
                RefreshData();
            };

            var bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            bottom.Controls.Add(_strict);

            grid.Controls.Add(hint, 0, 0);
            grid.Controls.Add(_candidates, 0, 1);
            grid.Controls.Add(bottom, 0, 2);
            return grid;
        }

        private TabPage BuildActivityTab()
        {
            var page = new TabPage("Activity");

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 1;
            grid.RowCount = 2;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var label = new Label();
            label.Text = "History of what you typed and what opened (newest first).";
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = SystemColors.GrayText;

            _events.View = View.Details;
            _events.Dock = DockStyle.Fill;
            _events.FullRowSelect = true;
            _events.HideSelection = false;
            _events.Columns.Add("Time", 140);
            _events.Columns.Add("Typed", 110);
            _events.Columns.Add("Opened", 170);
            _events.Columns.Add("Target", 400);
            _events.Columns.Add("Source", 80);

            grid.Controls.Add(label, 0, 0);
            grid.Controls.Add(_events, 0, 1);
            page.Controls.Add(grid);
            return page;
        }

        private static Button NewButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(0, 0, 6, 0);
            return b;
        }

        // ---------------- refresh ----------------

        private void RefreshData()
        {
            if (_refreshing || _dragging || !Visible) return;
            _refreshing = true;
            try
            {
                var selKey = _selectedKey;
                var groups = _store.SnapshotGroups();

                _groups.BeginUpdate();
                _groups.Items.Clear();
                int select = 0;
                for (int i = 0; i < groups.Count; i++)
                {
                    var gi = new GroupItem(groups[i]);
                    var item = new ListViewItem(gi.Text);
                    item.Tag = gi;
                    _groups.Items.Add(item);
                    if (groups[i].Key == selKey) select = i;
                }
                _groups.EndUpdate();

                if (_groups.Items.Count > 0)
                {
                    _groups.Items[select].Selected = true;
                    _selectedKey = ((GroupItem)_groups.Items[select].Tag).Key;
                }
                else
                {
                    _selectedKey = null;
                }

                PopulateCandidates();
                PopulateEvents();

                _syncing = true;
                _pause.Checked = _interceptor.Paused;
                _syncing = false;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void GroupSelectionChanged()
        {
            if (_refreshing) return;
            var gi = SelectedGroupItem();
            _selectedKey = gi == null ? null : gi.Key;
            PopulateCandidates();
        }

        private void PopulateCandidates()
        {
            var g = FindSelectedGroup();
            int prevIndex = _candidates.SelectedIndices.Count > 0 ? _candidates.SelectedIndices[0] : 0;

            _candidates.BeginUpdate();
            _candidates.Items.Clear();

            if (g != null)
            {
                int n = 1;
                foreach (var c in g.Candidates)
                {
                    var it = new ListViewItem(n.ToString());
                    it.SubItems.Add(c.Name ?? "");
                    it.SubItems.Add(c.Target ?? "");
                    it.SubItems.Add(c.Count.ToString());
                    it.SubItems.Add(c.LastUsed == default(DateTime) ? "" : c.LastUsed.ToString("yyyy-MM-dd HH:mm"));
                    it.ImageIndex = IconFor(c.Target);
                    it.Tag = c;
                    _candidates.Items.Add(it);
                    n++;
                }

                if (_candidates.Items.Count > 0)
                {
                    if (prevIndex >= _candidates.Items.Count) prevIndex = _candidates.Items.Count - 1;
                    _candidates.Items[prevIndex].Selected = true;
                }
            }

            _candidates.EndUpdate();

            _syncing = true;
            _strict.Checked = g != null && g.Strict;
            _strict.Enabled = g != null;
            _syncing = false;
        }

        private void PopulateEvents()
        {
            var events = _store.SnapshotEvents(250);

            _events.BeginUpdate();
            _events.Items.Clear();
            foreach (var e in events)
            {
                var it = new ListViewItem(e.Time.ToString("yyyy-MM-dd HH:mm:ss"));
                it.SubItems.Add(e.Typed ?? "");
                it.SubItems.Add(e.Opened ?? "");
                it.SubItems.Add(e.Target ?? "");
                it.SubItems.Add(e.Source ?? "");
                _events.Items.Add(it);
            }
            _events.EndUpdate();
        }

        private GroupItem SelectedGroupItem()
        {
            if (_groups.SelectedItems.Count == 0) return null;
            return _groups.SelectedItems[0].Tag as GroupItem;
        }

        private HistoryGroup FindSelectedGroup()
        {
            if (string.IsNullOrEmpty(_selectedKey)) return null;
            foreach (var g in _store.SnapshotGroups())
                if (g.Key == _selectedKey) return g;
            return null;
        }

        private int IconFor(string target)
        {
            var key = target ?? "";
            int idx;
            if (_iconIndex.TryGetValue(key, out idx)) return idx;

            Icon ic = null;
            try
            {
                if (!string.IsNullOrEmpty(target) && File.Exists(target))
                    ic = Icon.ExtractAssociatedIcon(target);
            }
            catch
            {
                // fall back to the default icon
            }
            if (ic == null) ic = SystemIcons.Application;

            _icons.Images.Add(ic);
            idx = _icons.Images.Count - 1;
            _iconIndex[key] = idx;
            return idx;
        }

        // ---------------- actions ----------------

        private void AddApp()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Add app";
                dlg.Filter = "Programs (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddFiles(new string[] { dlg.FileName });
            }
        }

        private void AddFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;

            string key = _selectedKey;
            if (string.IsNullOrEmpty(key))
            {
                key = PromptForm.Ask(this, "Typed text", "When you type this in Start:");
                if (string.IsNullOrEmpty(key)) return;
                key = key.Trim().ToLowerInvariant();
            }

            foreach (var f in files)
            {
                var c = new Candidate();
                c.Name = Path.GetFileNameWithoutExtension(f);
                c.Target = f;
                c.Source = "manual";
                _store.AddCandidate(key, c);
            }

            _selectedKey = key;
            RefreshData();
        }

        private Candidate SelectedCandidate()
        {
            if (_candidates.SelectedItems.Count == 0) return null;
            return _candidates.SelectedItems[0].Tag as Candidate;
        }

        private void EditSelected()
        {
            var c = SelectedCandidate();
            if (c == null || string.IsNullOrEmpty(_selectedKey)) return;

            var edited = c.Clone();
            if (!CandidateForm.Edit(this, edited, "Edit app")) return;
            _store.UpdateCandidateAt(_selectedKey, _candidates.SelectedIndices[0], edited);
            RefreshData();
        }

        private void RemoveSelected()
        {
            var c = SelectedCandidate();
            if (c == null || string.IsNullOrEmpty(_selectedKey)) return;

            var answer = MessageBox.Show(this,
                "Remove \"" + (string.IsNullOrEmpty(c.Name) ? c.Target : c.Name) + "\" from \"" + _selectedKey + "\"?",
                "MenuPrio", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            _store.RemoveCandidateAt(_selectedKey, _candidates.SelectedIndices[0]);
            RefreshData();
        }

        private void MoveSelected(int delta)
        {
            if (_candidates.SelectedIndices.Count == 0 || string.IsNullOrEmpty(_selectedKey)) return;
            int idx = _candidates.SelectedIndices[0];
            if (_store.MoveCandidate(_selectedKey, idx, idx + delta)) RefreshData();
        }

        private void MoveSelectedGroup(int delta)
        {
            if (_groups.SelectedIndices.Count == 0) return;
            var gi = SelectedGroupItem();
            if (gi == null) return;
            if (_store.MoveGroup(gi.Key, _groups.SelectedIndices[0] + delta)) RefreshData();
        }

        private void DeleteGroup()
        {
            var gi = SelectedGroupItem();
            if (gi == null) return;

            var g = FindSelectedGroup();
            int count = g == null ? 0 : g.Candidates.Count;

            var answer = MessageBox.Show(this,
                "Delete \"" + gi.Key + "\" and its " + count + " app(s)?",
                "MenuPrio", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            _store.RemoveGroup(gi.Key);
            _selectedKey = null;
            RefreshData();
        }

        // ---------------- drag & drop: candidates ----------------

        private void CandidatesItemDrag(object sender, ItemDragEventArgs e)
        {
            var item = e.Item as ListViewItem;
            if (item == null) return;

            _dragging = true;
            try { _candidates.DoDragDrop(item, DragDropEffects.Move); }
            finally { _dragging = false; }
        }

        private void CandidatesDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else if (e.Data.GetDataPresent(typeof(ListViewItem))) e.Effect = DragDropEffects.Move;
            else e.Effect = DragDropEffects.None;
        }

        private void CandidatesDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                return;
            }

            e.Effect = DragDropEffects.Move;
            SetInsertionMark(_candidates, e);
        }

        private void CandidatesDragDrop(object sender, DragEventArgs e)
        {
            int mark = _candidates.InsertionMark.Index;
            bool after = _candidates.InsertionMark.AppearsAfterItem;
            _candidates.InsertionMark.Index = -1;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) AddFiles(files);
                return;
            }

            var dragged = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
            if (dragged == null || mark < 0 || string.IsNullOrEmpty(_selectedKey)) return;

            int from = dragged.Index;
            int to = after ? mark + 1 : mark;
            if (from < to) to--;

            if (_store.MoveCandidate(_selectedKey, from, to)) RefreshData();
        }

        // ---------------- drag & drop: groups ----------------

        private void GroupsItemDrag(object sender, ItemDragEventArgs e)
        {
            var item = e.Item as ListViewItem;
            if (item == null) return;

            _dragging = true;
            try { _groups.DoDragDrop(item, DragDropEffects.Move); }
            finally { _dragging = false; }
        }

        private void GroupsDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(ListViewItem)) ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void GroupsDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(ListViewItem)))
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Move;
            SetInsertionMark(_groups, e);
        }

        private void GroupsDragDrop(object sender, DragEventArgs e)
        {
            int mark = _groups.InsertionMark.Index;
            bool after = _groups.InsertionMark.AppearsAfterItem;
            _groups.InsertionMark.Index = -1;

            var dragged = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
            if (dragged == null || mark < 0) return;

            var gi = dragged.Tag as GroupItem;
            if (gi == null) return;

            int from = dragged.Index;
            int to = after ? mark + 1 : mark;
            if (from < to) to--;

            if (_store.MoveGroup(gi.Key, to))
            {
                _selectedKey = gi.Key;
                RefreshData();
            }
        }

        private static void SetInsertionMark(ListView list, DragEventArgs e)
        {
            var p = list.PointToClient(new Point(e.X, e.Y));
            var item = list.GetItemAt(p.X, p.Y);

            if (item == null)
            {
                list.InsertionMark.Index = list.Items.Count - 1;
                list.InsertionMark.AppearsAfterItem = true;
            }
            else
            {
                list.InsertionMark.Index = item.Index;
                list.InsertionMark.AppearsAfterItem = p.Y > item.Bounds.Top + item.Bounds.Height / 2;
            }
        }

        // ---------------- misc ----------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
                _icons.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class GroupItem
        {
            public readonly string Key;
            public readonly bool Strict;
            public readonly int Count;

            public GroupItem(HistoryGroup g)
            {
                Key = g.Key;
                Strict = g.Strict;
                Count = g.Candidates.Count;
            }

            public string Text
            {
                get { return Key + (Strict ? "   [exact]" : "") + "   (" + Count + ")"; }
            }
        }
    }

    /// <summary>Tiny one-field prompt.</summary>
    internal sealed class PromptForm : Form
    {
        private readonly TextBox _box = new TextBox();
        private readonly Label _label = new Label();

        public static string Ask(IWin32Window owner, string title, string label)
        {
            using (var f = new PromptForm())
            {
                f.Text = title;
                f._label.Text = label;
                return f.ShowDialog(owner) == DialogResult.OK ? f._box.Text.Trim() : null;
            }
        }

        private PromptForm()
        {
            ClientSize = new Size(360, 130);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;

            _label.SetBounds(12, 12, 336, 20);
            _box.SetBounds(12, 36, 336, 24);

            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(192, 76, 75, 26);

            var cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(273, 76, 75, 26);

            Controls.Add(_label);
            Controls.Add(_box);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    /// <summary>Edit name / target / args / working dir of one candidate.</summary>
    internal sealed class CandidateForm : Form
    {
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _target = new TextBox();
        private readonly TextBox _args = new TextBox();
        private readonly TextBox _workDir = new TextBox();

        public static bool Edit(IWin32Window owner, Candidate c, string title)
        {
            using (var f = new CandidateForm())
            {
                f.Text = title;
                f._name.Text = c.Name ?? "";
                f._target.Text = c.Target ?? "";
                f._args.Text = c.Args ?? "";
                f._workDir.Text = c.WorkDir ?? "";

                if (f.ShowDialog(owner) != DialogResult.OK) return false;

                if (f._target.Text.Trim().Length == 0)
                {
                    MessageBox.Show(owner, "Target cannot be empty.", "MenuPrio");
                    return false;
                }

                c.Name = f._name.Text.Trim();
                c.Target = f._target.Text.Trim();
                c.Args = f._args.Text;
                c.WorkDir = f._workDir.Text.Trim();
                return true;
            }
        }

        private CandidateForm()
        {
            ClientSize = new Size(520, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;

            AddRow("Name", _name, 12);
            AddRow("Target", _target, 48);
            AddRow("Arguments", _args, 84);
            AddRow("Working dir", _workDir, 120);

            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(352, 164, 75, 26);

            var cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(433, 164, 75, 26);

            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void AddRow(string label, TextBox box, int y)
        {
            var l = new Label();
            l.Text = label;
            l.SetBounds(12, y + 3, 100, 20);
            box.SetBounds(116, y, 392, 24);
            Controls.Add(l);
            Controls.Add(box);
        }
    }

    internal static class AppIcons
    {
        public static Icon Get()
        {
            try
            {
                var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) return ic;
            }
            catch
            {
                // fall through to the default icon
            }
            return SystemIcons.Application;
        }
    }
}
