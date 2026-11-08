using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowWatch
{
    internal sealed class ManageCaptures : Form
    {
        private readonly ListView clips = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill };
        private readonly Button open = ActionButton("Open capture"), recycle = ActionButton("Move to Recycle Bin"), refresh = ActionButton("Refresh");
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(800,0), Margin = new Padding(0,7,0,0) };
        private readonly string root;
        private readonly Writer writer;
        private bool busy, deleting;
        private static Button ActionButton(string text) { return new Button { Text = text, AutoSize = true, Height = 30 }; }

        internal ManageCaptures(string root, Writer writer)
        {
            this.root = Path.GetFullPath(root); this.writer = writer;
            Text = "Manage captures"; Font = new Font("Segoe UI", 9.5f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(860,420); MinimumSize = new Size(580,300); StartPosition = FormStartPosition.CenterParent;
            clips.Columns.Add("Captured",172); clips.Columns.Add("Window",225); clips.Columns.Add("Session",165);
            clips.Columns.Add("Trigger",150); clips.Columns.Add("Frames",75,HorizontalAlignment.Right);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0,8,0,0) };
            buttons.Controls.AddRange(new Control[] { open, recycle, refresh });
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(clips,0,0); layout.Controls.Add(buttons,0,1); layout.Controls.Add(status,0,2); Controls.Add(layout);
            clips.SelectedIndexChanged += delegate { Buttons(); };
            clips.DoubleClick += delegate { OpenSelected(); };
            clips.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.A)
                {
                    foreach (ListViewItem item in clips.Items) item.Selected = true;
                    e.SuppressKeyPress = true;
                }
            };
            open.Click += delegate { OpenSelected(); };
            refresh.Click += async delegate { await Reload("", true); };
            recycle.Click += async delegate { await RemoveSelected(); };
            Shown += async delegate { await Reload(""); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (deleting && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
            Buttons();
        }
        private void Buttons()
        {
            open.Enabled = !busy && clips.SelectedItems.Count == 1;
            recycle.Enabled = !busy && clips.SelectedItems.Count > 0;
            refresh.Enabled = !busy;
        }
        private void OpenSelected()
        {
            if (busy || clips.SelectedItems.Count != 1) return;
            string page = Path.Combine(((SavedCapture)clips.SelectedItems[0].Tag).Folder,"index.html");
            try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        private async Task Reload(string message, bool updateIndexes = false)
        {
            busy = true; Buttons();
            status.Text = "Reading saved captures…";
            try
            {
                var saved = await Task.Run(delegate
                {
                    var found = CaptureLibrary.Scan(root);
                    if (updateIndexes)
                        foreach (var session in found.Select(c => c.SessionFolder).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase))
                            Session.RefreshIndex(session);
                    return found;
                });
                if (IsDisposed || Disposing) return;
                clips.BeginUpdate();
                try
                {
                    clips.Items.Clear();
                    foreach (var c in saved)
                    {
                        string session = c.SessionFolder == null ? "—" : Path.GetFileName(c.SessionFolder).Substring(0,19).Replace('_',' ');
                        if (c.SessionFolder != null) session = session.Substring(0,11) + session.Substring(11).Replace('-',':');
                        clips.Items.Add(new ListViewItem(new[] {c.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"), c.Window, session, c.Kind, c.Frames.ToString()}) { Tag = c });
                    }
                }
                finally { clips.EndUpdate(); }
                writer.UpdateAfterRemoval(saved.Count == 0 ? null : saved[0].Folder);
                status.Text = message.Length > 0 ? message : saved.Count == 0 ? "No saved captures in this folder." :
                    saved.Count + (saved.Count == 1 ? " saved capture." : " saved captures.");
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = (message + " " + ex.Message).Trim(); }
            finally { busy = false; if (!IsDisposed) Buttons(); }
        }
        private async Task RemoveSelected()
        {
            if (busy || clips.SelectedItems.Count == 0) return;
            var chosen = clips.SelectedItems.Cast<ListViewItem>().Select(i => (SavedCapture)i.Tag).ToArray();
            busy = deleting = true; Buttons(); status.Text = "Moving selected captures to the Recycle Bin…";
            // The shell file operation uses an STA. Keep the main capture and its UI timer running.
            var done = new TaskCompletionSource<string>();
            var thread = new Thread(delegate()
            {
                try { done.SetResult(RecycleSelection(chosen)); }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            string result;
            try { result = await done.Task; }
            catch (Exception ex) { result = ex.Message; }
            finally { deleting = false; }
            if (!IsDisposed && !Disposing) await Reload(result);
        }
        private string RecycleSelection(SavedCapture[] chosen)
        {
            int count = 0;
            var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var c in chosen)
            {
                try { CaptureLibrary.Recycle(root, c); count++; }
                catch (Exception ex) { errors.Add(Path.GetFileName(c.Folder) + ": " + ex.Message); }
                if (c.SessionFolder != null && !Directory.Exists(c.Folder)) sessions.Add(c.SessionFolder);
            }
            foreach (string folder in sessions)
            {
                try { Session.RefreshIndex(folder); }
                catch (Exception ex) { errors.Add("Session index: " + ex.Message); }
            }
            string result = "Moved " + count + " of " + chosen.Length + " to the Recycle Bin.";
            if (errors.Count > 0) result += " " + errors[0];
            return result;
        }
    }
}
