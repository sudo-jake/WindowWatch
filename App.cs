using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WindowWatch
{
    internal sealed class Target
    {
        internal IntPtr Handle;
        internal uint ProcessId;
        internal string Title, Process;
        public override string ToString() { return Title + "   (" + Process + ", " + ProcessId + ")"; }
    }
    internal static class WindowsList
    {
        private delegate bool WindowCallback(IntPtr handle, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int length);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        internal static List<Target> Targets()
        {
            var result = new List<Target>();
            int own = System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr h, IntPtr state)
            {
                uint pid;
                int cloaked;
                GetWindowThreadProcessId(h, out pid);
                if (!IsWindowVisible(h) || pid == own || (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0)) return true;
                var name = new StringBuilder(1024);
                if (GetWindowText(h, name, name.Capacity) == 0 || string.IsNullOrWhiteSpace(name.ToString())) return true;
                string process = "PID " + pid;
                try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) process = p.ProcessName; }
                catch (ArgumentException) { return true; }
                catch (System.ComponentModel.Win32Exception) { }
                result.Add(new Target { Handle = h, Title = name.ToString(), Process = process, ProcessId = pid });
                return true;
            }, IntPtr.Zero);
            result.Sort((a,b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }
    }
    internal sealed class WatchForm : Form
    {
        private sealed class SettingNumber : NumericUpDown
        {
            // Closing from the tray can skip the usual focus change that commits typed text.
            internal void CommitPendingEdit() { if (UserEdit) ValidateEditText(); }
        }
        private readonly ComboBox windows = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 450 };
        private readonly Button refresh = ButtonFor("Refresh list"), choose = ButtonFor("Browse…"), start = ButtonFor("Start watching");
        private readonly Button openFolder = ButtonFor("Open folder"), latest = ButtonFor("View last capture"), viewSession = ButtonFor("View session");
        private readonly Button manage = ButtonFor("Manage captures");
        private readonly CheckBox pauses = Check("Pause during motion"), flashes = Check("Brief flash"), redraws = Check("Large redraw");
        private readonly NumericUpDown before = Numeric(100, 3000, 700, 100), after = Numeric(100, 3000, 700, 100), pause = Numeric(50, 1000, 120, 10);
        private readonly NumericUpDown flashArea = Area(Options.DefaultFlashPercent), redrawArea = Area(Options.DefaultRedrawPercent);
        private readonly Label path = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Width = 450, Height = 30 };
        private readonly Label state = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        private readonly ListBox history = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly Timer timer = new Timer { Interval = 100 };
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolTip help = new ToolTip { ShowAlways = true, InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 15000 };
        private readonly List<Control> settings = new List<Control>();
        private readonly Preferences preferences;
        private WatchedWindow lastWatched;
        private bool reportedMissingWindow;
        private readonly Writer writer;
        private Capture capture;
        private Recorder recorder;
        private Target target;
        private Session session;
        private bool closing;
        private string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowWatch Captures");
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 30), UseVisualStyleBackColor = true }; }
        private static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, Checked = true, Margin = new Padding(2, 6, 14, 3) }; }
        private static NumericUpDown Numeric(int min, int max, int value, int step) { return new SettingNumber { Minimum = min, Maximum = max, Value = value, Increment = step, Width = 68, TextAlign = HorizontalAlignment.Right }; }
        private static NumericUpDown Area(decimal value) { return new SettingNumber { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 100, Value = value, Increment = 0.5m, Width = 68, TextAlign = HorizontalAlignment.Right }; }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 8, 4) }; }
        private static FlowLayoutPanel Row(params Control[] items)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 7) };
            row.Controls.AddRange(items); return row;
        }
        private void Describe(Control control, string description)
        {
            help.SetToolTip(control, description);
            control.AccessibleDescription = description;
            foreach (Control child in control.Controls) Describe(child, description);
        }
        private Control SettingHelp(string description, params Control[] controls)
        {
            // Windows sends mouse input to the parent when a setting is disabled during capture.
            var group = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = Padding.Empty };
            group.Controls.AddRange(controls);
            Describe(group, description);
            // The native tooltip skips disabled children even when their parent receives the hover.
            var setting = controls[controls.Length - 1];
            group.MouseHover += delegate
            {
                if (setting.Enabled) return;
                Point cursor = group.PointToClient(Cursor.Position);
                help.Show(description, group, cursor.X, group.ClientSize.Height, help.AutoPopDelay);
            };
            group.MouseLeave += delegate { if (!setting.Enabled) help.Hide(group); };
            return group;
        }
        private Control NumberSetting(string label, NumericUpDown field, string description)
        {
            field.AccessibleName = label;
            return SettingHelp(description, LabelFor(label), field);
        }
        internal WatchForm() : this(Preferences.DefaultKey) { }
        internal WatchForm(string preferencesKey)
        {
            preferences = new Preferences(preferencesKey, Log);
            preferences.Bind("DetectPauses", pauses);
            preferences.Bind("DetectFlashes", flashes);
            preferences.Bind("DetectRedraws", redraws);
            preferences.Bind("BeforeMs", before);
            preferences.Bind("AfterMs", after);
            preferences.Bind("MinPauseMs", pause);
            preferences.Bind("MinFlashPercent", flashArea);
            preferences.Bind("MinRedrawPercent", redrawArea);
            string folder = preferences.Read("CaptureFolder") as string;
            Uri location;
            if (Uri.TryCreate(folder, UriKind.Absolute, out location) && location.IsFile
                && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(folder)) directory = folder;
            lastWatched = WatchedWindow.Load(preferences.Read("LastWatched"));
            Icon = new Icon(typeof(WatchForm), "WindowWatch.ico");
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open WindowWatch", null, delegate { RestoreFromTray(); });
            trayMenu.Items.Add("Exit", null, delegate
            {
                if (closing) return;
                closing = true;
                Close();
            });
            tray.ContextMenuStrip = trayMenu;
            tray.Icon = Icon;
            tray.Text = "WindowWatch";
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            Disposed += delegate { help.Dispose(); tray.Dispose(); trayMenu.Dispose(); };
            Text = "WindowWatch"; Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(690, 500); MinimumSize = new Size(675, 500);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, RowCount = 10 };
            for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = LabelFor("Watch a window"); heading.Font = new Font(Font.FontFamily, 15, FontStyle.Bold);
            layout.Controls.Add(heading,0,0);
            layout.Controls.Add(Row(
                SettingHelp("Choose the window whose changes you want to capture.", windows),
                SettingHelp("Look for windows that opened after WindowWatch started.", refresh)),0,1);
            layout.Controls.Add(Row(
                SettingHelp("Saves a clip when steady movement briefly freezes, then resumes.\r\nFor example, a scrolling chat sticks for a moment and then catches up.", pauses),
                SettingHelp("Saves a clip when part of the window changes and returns to its earlier\r\nappearance within 350 ms, like a panel that briefly goes blank.", flashes),
                SettingHelp("Saves a clip when a large enough area changes in one frame.\r\nA chat jumping from the bottom to the top can trigger this.", redraws)),0,2);
            layout.Controls.Add(Row(
                NumberSetting("Before (ms)", before, "How much time to include before a detected event.\r\nAt 700 ms, you can look back about 0.7 seconds."),
                NumberSetting("After (ms)", after, "How much time to include after the event ends.\r\nIncrease this to see more of what happens next."),
                NumberSetting("Min. pause (ms)", pause, "The shortest pause to look for during steady movement.\r\n120 ms is 0.12 seconds. Lower it to catch shorter stutters.")),0,3);
            layout.Controls.Add(Row(
                NumberSetting("Min. flash area (%)", flashArea, "How much of the window must change for a brief flash to count.\r\nLower this percentage to catch a flicker in a smaller area."),
                NumberSetting("Min. redraw area (%)", redrawArea, "How much of the window must change at once to count as a redraw.\r\nRaise this percentage if routine updates save too many clips.")),0,4);
            const string folderHelp = "Captures are grouped by window and session inside this folder.";
            var folderLabel = LabelFor("Save events to");
            Describe(folderLabel, folderHelp);
            layout.Controls.Add(folderLabel,0,5);
            path.Text = directory;
            layout.Controls.Add(Row(SettingHelp(folderHelp, path),
                SettingHelp("Choose the folder where future captures will be saved.", choose)),0,6);
            layout.Controls.Add(Row(start, openFolder, viewSession, latest, manage),0,7);
            layout.Controls.Add(state,0,8);
            layout.Controls.Add(history,0,9); Controls.Add(layout);
            settings.AddRange(new Control[]{windows,refresh,choose,pauses,flashes,redraws,before,after,pause,flashArea,redrawArea});
            writer = new Writer(message => Post(delegate { Log(message); }));
            refresh.Click += delegate { LoadWindows(); };
            choose.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "Choose the folder for WindowWatch events", SelectedPath = Directory.Exists(directory) ? directory : "" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) SetCaptureFolder(dialog.SelectedPath);
            };
            start.Click += delegate { if (capture == null) StartWatching(); else StopWatching("Monitoring stopped."); };
            openFolder.Click += delegate { Open(session == null ? directory : session.Folder, true); };
            viewSession.Click += delegate
            {
                if (session == null || !session.HasIndex) return;
                try { Session.RefreshIndex(session.Folder); Open(Path.Combine(session.Folder, "index.html"), false); }
                catch (Exception ex) { Log("Could not open the session: " + ex.Message); }
            };
            manage.Click += delegate { using (var library = new ManageCaptures(directory,writer) { Icon = Icon }) library.ShowDialog(this); };
            latest.Click += delegate { if (writer.LastSaved != null) Open(Path.Combine(writer.LastSaved, "index.html"), false); };
            Shown += delegate { tray.Visible = true; LoadWindows(); timer.Start(); };
            timer.Tick += delegate { UpdateState(); };
            FormClosing += ClosingForm;
            FormClosed += delegate { timer.Stop(); writer.Close(); };
            state.Text = "Ready"; latest.Enabled = false; viewSession.Enabled = false;
        }
        internal void SetCaptureFolder(string folder)
        {
            directory = folder;
            path.Text = folder;
            session = null;
            preferences.Save("CaptureFolder", folder);
        }
        private void RestoreFromTray()
        {
            if (closing) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }
        private void LoadWindows()
        {
            var selected = windows.SelectedItem as Target;
            var targets = WindowsList.Targets();
            var match = selected == null ? null : targets.Find(t => t.Handle == selected.Handle && t.ProcessId == selected.ProcessId);
            if (match == null && lastWatched != null) match = lastWatched.Find(targets);
            if (match == null && lastWatched == null && targets.Count > 0) match = targets[0];
            windows.BeginUpdate();
            try
            {
                windows.Items.Clear();
                windows.Items.AddRange(targets.ToArray());
                windows.SelectedItem = match;
            }
            finally { windows.EndUpdate(); }
            if (match == null && lastWatched != null && !reportedMissingWindow)
            {
                Log("Last watched: " + lastWatched.Title + ". Choose a window, or open it and click Refresh list.");
                reportedMissingWindow = true;
            }
            if (match != null) reportedMissingWindow = false;
        }
        private void StartWatching()
        {
            target = windows.SelectedItem as Target;
            if (target == null) { Log("Select a window to watch."); return; }
            if (!pauses.Checked && !flashes.Checked && !redraws.Checked) { Log("Enable at least one trigger."); return; }
            uint pid;
            WindowsList.GetWindowThreadProcessId(target.Handle, out pid);
            if (pid != target.ProcessId) { Log("The selected window closed. Refresh the list and choose it again."); return; }
            if (WindowsList.IsIconic(target.Handle)) { Log("Restore the selected window before starting."); return; }
            try
            {
                Directory.CreateDirectory(directory);
                var options = new Options { Before = (double)before.Value, After = (double)after.Value, Pause = (double)pause.Value,
                    FlashArea = (double)flashArea.Value / 100, RedrawArea = (double)redrawArea.Value / 100,
                    Pauses = pauses.Checked, Flashes = flashes.Checked, Redraws = redraws.Checked };
                var nextSession = new Session(directory, target.Title, target.ToString());
                var live = new Recorder(options, nextSession, writer.Submit);
                capture = new Capture(target.Handle, live.Observe, message => Post(delegate { if (recorder == live) StopWatching(message); }));
                session = nextSession;
                recorder = live;
                foreach (var control in settings) control.Enabled = false;
                start.Text = "Stop watching";
                tray.Text = "WindowWatch · Watching";
                Log("Watching " + target.Title);
                lastWatched = WatchedWindow.From(target);
                preferences.Save("LastWatched", lastWatched.Serialize());
            }
            catch (Exception e) { Log("Could not start capture: " + e.Message); }
            UpdateState();
        }
        private void StopWatching(string message)
        {
            if (capture == null) return;
            var closingCapture = capture;
            capture = null;
            closingCapture.Dispose();
            var final = recorder.Stop();
            recorder = null;
            foreach (var control in settings) control.Enabled = true;
            start.Text = "Start watching";
            tray.Text = "WindowWatch";
            if (final == null) Log(message + " No frames were available to save.");
            else
            {
                writer.SubmitFinal(final);
                Log(message + " Saving " + final.Frames.Count + (final.Frames.Count == 1 ? " buffered frame." : " buffered frames."));
            }
            start.Enabled = writer.Waiting == 0;
        }
        private void UpdateState()
        {
            if (closing && writer.Waiting == 0) { Close(); return; }
            bool minimized = capture != null && WindowsList.IsIconic(target.Handle);
            if (recorder != null) recorder.Tick(Recorder.Now, minimized);
            state.Text = recorder == null ? "Stopped" : minimized ? "Paused while the window is minimized" : "Watching   ·   " + recorder.Counts();
            if (writer.Waiting > 0) state.Text += "   ·   Saving " + writer.Waiting + " event(s)";
            latest.Enabled = writer.LastSaved != null;
            viewSession.Enabled = session != null && session.HasIndex;
            start.Enabled = !closing && (capture != null || writer.Waiting == 0);
        }
        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            foreach (var field in settings.OfType<SettingNumber>()) field.CommitPendingEdit();
            if (e.CloseReason == CloseReason.UserClosing && !closing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            closing = true;
            StopWatching("Monitoring stopped.");
            // System shutdown can close immediately; FormClosed also drains the writer.
            if (writer.Waiting == 0 || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true; start.Enabled = false;
            Log("Finishing the saved frames before closing.");
        }
        private void Post(Action action)
        {
            if (!IsDisposed && IsHandleCreated)
            {
                try { BeginInvoke(action); } catch (InvalidOperationException) { }
            }
        }
        private void Log(string message)
        {
            history.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + message);
            if (history.Items.Count > 150) history.Items.RemoveAt(0);
            history.TopIndex = history.Items.Count - 1;
        }
        private void Open(string location, bool create)
        {
            try
            {
                if (create) Directory.CreateDirectory(location);
                Process.Start(new ProcessStartInfo(location) { UseShellExecute = true });
            }
            catch (Exception e) { Log("Could not open the location: " + e.Message); }
        }
    }
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            WindowsList.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WatchForm());
        }
    }
}
