using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
namespace WindowWatch
{
    internal static class Diagnostics
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        static void ClickWindowClose(Form form) { SendMessage(form.Handle, 0x0112, new IntPtr(0xF060), IntPtr.Zero); }
        static void Check(bool valid, string description) { if (!valid) throw new Exception(description); }
        static IEnumerable<Control> Controls(Control parent)
        {
            foreach (Control control in parent.Controls) { yield return control; foreach (var child in Controls(control)) yield return child; }
        }
        static unsafe Session Core(string dir)
        {
            var notes = new List<string>();
            var writer = new Writer(s => notes.Add(s));
            var session = new Session(dir, "Synthetic <test>", "Synthetic <test>");
            var recorder = new Recorder(new Options { Before = 300, After = 300, RedrawArea = 0.35 }, session, writer.Submit);
            var pixels = new int[160*120];
            double start = Recorder.Now;
            Action<double,int> frame = (offset, pos) => {
                for (int i=0;i<pixels.Length;i++) pixels[i] = pos == -2 ? 0xffffff : 0x102030;
                if (pos >= 0) for (int y=25;y<75;y++) for(int x=0;x<24;x++) pixels[y*160 + x + pos] = 0x0088ff;
                fixed (int* p = pixels) recorder.Observe(start+offset,(IntPtr)p,160*4,160,120);
            };
            frame(0,-1); frame(50,-1);
            recorder.Tick(start+4800,false);
            Check(writer.Waiting == 0 && !Directory.Exists(dir),"Static window produced an event");
            for(int i=0;i<12;i++) frame(5000+i*20,i*3);
            frame(5560,36); frame(5580,39); frame(5600,42);
            recorder.Tick(start+6100,false);
            while (writer.Waiting != 0) Thread.Sleep(5);
            string gap = writer.LastSaved;
            Check(gap != null && File.ReadAllText(Path.Combine(gap,"events.csv")).Contains("Pause during motion,0.000,340.000,340.000"),"Expected 340 ms pause");
            frame(10000,-2); frame(10016,42); frame(10032,42);
            recorder.Tick(start+10500,false);
            while(writer.Waiting != 0) Thread.Sleep(5);
            string flash = writer.LastSaved;
            Check(File.ReadAllText(Path.Combine(flash,"events.csv")).Contains("Brief flash,0.000,16.000,16.000"),"Missed one-frame flash");
            var images = Directory.GetFiles(flash,"*.png").OrderBy(s=>s).ToArray();
            Check(images.Length==3,"Expected pre, flash and restored frames");
            Check(File.ReadAllText(Path.Combine(flash,"samples.csv")).Contains("32.000,0003.png"),"Duplicate frame timestamp was not preserved");
            Color[] colors = images.Select(file=>{ using(var b = new Bitmap(file)) return b.GetPixel(0,0); }).ToArray();
            Check(colors[0].ToArgb()==colors[2].ToArgb() && colors[1].ToArgb()==Color.White.ToArgb(),"Pixel round trip mismatch");
            Check(File.ReadAllText(Path.Combine(flash,"index.html")).Contains("Synthetic &lt;test&gt;"),"Unsafe viewer window title");
            recorder.Tick(start+11000,true); frame(11200,-2); recorder.Tick(start+11300,false); frame(11400,-1);
            var final = recorder.Stop();
            Check(final != null && final.Frames.Count == 1 && final.Findings.Single().Kind == "Stopped watching", "Expected the restored window in the stop capture");
            Check(recorder.Stop() == null, "Repeated stop duplicated the capture");
            writer.SubmitFinal(final);
            writer.Close();
            Check(Directory.GetDirectories(session.Folder).Length==3,"Unexpected event during idle or minimize");
            Check(session.HasIndex && File.ReadAllText(Path.Combine(session.Folder,"index.html")).Contains("Synthetic &lt;test&gt;"), "Missing session index or unescaped title");
            Check(Session.WindowFolder("..") != ".." && Session.WindowFolder("CON").StartsWith("_") && Session.WindowFolder("a/b") != Session.WindowFolder("a:b"), "Unsafe or colliding window folder");
            return session;
        }
        static unsafe void StopChecks()
        {
            var unexpected = new Action<Clip>(c => { throw new Exception("Unexpected automatic export"); });
            var options = new Options();
            Check(options.FlashArea == 0.005 && options.RedrawArea == 0.01, "Detector defaults");
            var session = new Session("test", "test", "test");
            var empty = new Recorder(options, session, unexpected);
            Check(empty.Stop() == null, "Stop before the first frame should be empty");
            var pixels = new int[128*80];
            double t = Recorder.Now;
            fixed (int* p = pixels)
            {
                var quiet = new Recorder(options, session, unexpected);
                for (int i = 0; i < 27; i++)
                {
                    pixels[i] = 0xffffff;
                    quiet.Observe(t+i*16, (IntPtr)p, 512, 128, 80);
                }
                quiet.Tick(t+500, true);
                var stopped = quiet.Stop();
                Check(stopped.Frames.Count == 27 && stopped.Samples.Count == 27 && stopped.Findings.Single().Kind == "Stopped watching", "Stop lost sub-threshold changes while minimized");
                var active = new Recorder(options, session, unexpected);
                active.Observe(t,(IntPtr)p,512,128,80);
                for (int i = 0; i < 220; i++) pixels[i] = 0x0088ff;
                active.Observe(t+16,(IntPtr)p,512,128,80);
                var triggered = active.Stop();
                Check(triggered.Frames.Count == 2 && triggered.Findings.Single().Kind == "Large redraw", "Stop lost the pending automatic redraw");
            }
        }
        static void CheckManager(string generatedRoot, Session recording)
        {
            var existing = CaptureLibrary.Scan(generatedRoot);
            Check(existing.Count == 3 && existing.All(c => c.Window.StartsWith("Synthetic")), "Expected only generated fixtures");
            var writer = new Writer(s => {});
            try
            {
                using (var manager = new ManageCaptures(generatedRoot,writer))
                using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
                {
                    var controls = Controls(manager).ToArray();
                    var list = controls.OfType<ListView>().Single();
                    var recycle = controls.OfType<Button>().Single(b => b.Text == "Move to Recycle Bin");
                    ListViewItem[] chosen = null;
                    Exception failure = null;
                    var clock = Stopwatch.StartNew();
                    timer.Tick += delegate {
                        try {
                            if (clock.ElapsedMilliseconds > 8000) throw new TimeoutException("Capture manager did not finish.");
                            if (chosen == null && list.Items.Count == 3) {
                                chosen = list.Items.Cast<ListViewItem>().Take(2).ToArray();
                                foreach (var item in chosen) item.Selected = true;
                                Check(recycle.Enabled, "Multi-select recycle is disabled");
                                recycle.PerformClick();
                            }
                            else if (chosen != null && list.Items.Count == 1) { timer.Stop(); manager.Close(); }
                        }
                        catch (Exception ex) { failure = ex; timer.Stop(); manager.Close(); }
                    };
                    manager.Shown += delegate { timer.Start(); }; manager.ShowDialog();
                    if (failure != null) throw failure;
                    var remaining = CaptureLibrary.Scan(generatedRoot).Single();
                    recording.AddCapture(remaining.Folder,remaining.Time.ToUniversalTime(),remaining.Kind,remaining.Frames);
                    string index = File.ReadAllText(Path.Combine(existing[0].SessionFolder,"index.html"));
                    foreach (var item in chosen)
                    {
                        var c = (SavedCapture)item.Tag;
                        Check(!Directory.Exists(c.Folder) && !index.Contains(Path.GetFileName(c.Folder)), "Deleted capture is still listed");
                    }
                    Check(index.Contains(Path.GetFileName(remaining.Folder)) && File.Exists(Path.Combine(remaining.Folder,"0001.png")), "The unselected capture was affected");
                }
            }
            finally { writer.Close(); }
        }
        static NumericUpDown Number(Control form, string name)
        {
            return Controls(form).OfType<NumericUpDown>().Single(n => n.AccessibleName == name);
        }
        static void CheckRestart(string dir, string key)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new WatchForm(key))
            {
                form.Show(); Application.DoEvents();
                var controls = Controls(form).ToArray();
                var picker = controls.OfType<ComboBox>().Single();
                Check(picker.SelectedIndex == -1, "Missing last window silently selected a different target");
                Check(controls.OfType<Label>().Any(l => l.Text == Path.Combine(dir, "live")), "Capture folder was lost");
                Check(controls.OfType<CheckBox>().All(b => !b.Checked), "Disabled triggers were lost");
                Check(Number(form, "Before (ms)").Value == 1400 && Number(form, "After (ms)").Value == 1800
                    && Number(form, "Min. pause (ms)").Value == 240 && Number(form, "Min. flash area (%)").Value == 2.3m
                    && Number(form, "Min. redraw area (%)").Value == 18.7m, "Numeric settings were lost");
                Check(controls.OfType<Button>().Any(b => b.Text == "Start watching"), "Capture started on launch");
                var startup = new ProcessStartInfo(Path.Combine(dir, "test-window.exe"), "\"" + Path.Combine(dir, "unused-marker") + "\"")
                    { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
                using (var reopened = Process.Start(startup))
                {
                    try
                    {
                        var refresh = controls.OfType<Button>().Single(b => b.Text == "Refresh list");
                        var clock = Stopwatch.StartNew();
                        while (picker.SelectedIndex == -1 && clock.ElapsedMilliseconds < 5000)
                        { refresh.PerformClick(); Application.DoEvents(); Thread.Sleep(50); }
                        var target = picker.SelectedItem as Target;
                        Check(target != null && target.ProcessId == reopened.Id, "Refresh did not restore the relaunched target");
                        var saved = WatchedWindow.Load(new Preferences(key, message => { throw new Exception(message); }).Read("LastWatched"));
                        Check(saved.Find(new List<Target> { target, new Target { Title = target.Title, Process = target.Process } }) == null,
                            "Ambiguous titles silently chose a window");
                        Check(saved.Find(new List<Target> { new Target { Title = "Launcher", Process = target.Process } }) == null,
                            "Another window from the same process was chosen");
                        using (var again = new WatchForm(key))
                        {
                            again.Show(); Application.DoEvents();
                            var restored = Controls(again).OfType<ComboBox>().Single().SelectedItem as Target;
                            Check(restored != null && restored.ProcessId == reopened.Id, "Startup did not restore the relaunched target");
                        }
                    }
                    finally { if (!reopened.HasExited) reopened.Kill(); }
                }
            }
        }
        [STAThread] private static void Main(string[] args)
        {
            string dir = args[0]; Directory.CreateDirectory(dir);
            bool restart = args.Length > 2 && args[2] == "restart";
            string log = Path.Combine(dir, restart ? "restart.log" : "result.log");
            try {
                if (restart)
                {
                    CheckRestart(dir, args[1]);
                    File.WriteAllText(log, "PASS settings survived a process restart, missing window stayed unselected, and the relaunched target matched on refresh and startup.");
                    return;
                }
                var coreSession = Core(Path.Combine(dir,"core"));
                StopChecks();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Control.CheckForIllegalCrossThreadCalls = true;
                CheckManager(Path.Combine(dir,"core"), coreSession);
                var form = new WatchForm(args[1]);
                string liveDir = Path.Combine(dir,"live");
                form.SetCaptureFolder(liveDir);
                var timer = new System.Windows.Forms.Timer { Interval=100 };
                var clock=Stopwatch.StartNew();
                bool watching=false, testedStop=false, restoredFromTray=false, testedExit=false;
                var tray=(NotifyIcon)typeof(WatchForm).GetField("tray", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form);
                var all=Controls(form).ToArray();
                var picker=all.OfType<ComboBox>().Single();
                var start=all.OfType<Button>().Single(b=>b.Text=="Start watching");
                var areas=all.OfType<NumericUpDown>().Where(n=>n.DecimalPlaces==1).Select(n=>n.Value).OrderBy(n=>n).ToArray();
                Check(areas.SequenceEqual(new decimal[]{0.5m,1m}),"Unexpected sensitivity in the window");
                var history=all.OfType<ListBox>().Single();
                timer.Tick+=delegate {
                    if (clock.ElapsedMilliseconds>25000) throw new TimeoutException("GUI validation timed out");
                    if (!watching) {
                        if (!start.Enabled) return;
                        all.OfType<Button>().Single(b=>b.Text=="Refresh list").PerformClick();
                        var sample=picker.Items.Cast<Target>().FirstOrDefault(t=>t.Title=="WindowWatch generated test pattern");
                        if (sample==null) return;
                        picker.SelectedItem=sample;
                        if (testedStop) {
                            string stoppedEvent = Directory.GetFiles(liveDir,"events.csv",SearchOption.AllDirectories).Single();
                            string savedDir = Path.GetDirectoryName(stoppedEvent);
                            Check(File.ReadAllText(stoppedEvent).Contains("Stopped watching") && File.Exists(Path.Combine(savedDir,"0001.png")) && File.Exists(Path.Combine(savedDir,"index.html")), "Quiet window did not save when Stop watching was clicked");
                        }
                        start.PerformClick();
                        Check(start.Text=="Stop watching","Capture failed to start: "+string.Join(" | ",history.Items.Cast<string>()));
                        if (testedStop) {
                            ClickWindowClose(form);
                            Check(!form.Visible && !form.IsDisposed && tray.Visible && start.Text=="Stop watching", "X should hide the window and preserve the capture");
                            File.WriteAllText(Path.Combine(dir,"go"),"go");
                        }
                        watching=true;
                    }
                    else if (!testedStop) {
                        if (!all.OfType<Label>().Any(l => l.Text.Contains("frames received") && !l.Text.Contains("·   0 frames received"))) return;
                        start.PerformClick();
                        Check(start.Text == "Start watching", "Stop watching button did not stop");
                        testedStop=true; watching=false;
                    }
                    else if (!restoredFromTray && Directory.GetFiles(liveDir,"events.csv",SearchOption.AllDirectories).Length > 1) {
                        Check(!form.Visible, "Automatic save reopened the hidden window");
                        tray.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item=>item.Text=="Open WindowWatch").PerformClick();
                        Check(form.Visible && start.Text=="Stop watching","Tray restore lost the active capture");
                        ClickWindowClose(form);
                        restoredFromTray=true;
                    }
                    else if (history.Items.Cast<string>().Any(s=>s.Contains("selected window closed"))) {
                        Check(restoredFromTray && !form.Visible, "Expected a second close to tray");
                        timer.Stop(); File.WriteAllLines(Path.Combine(dir,"ui.log"),history.Items.Cast<string>());
                        foreach (var box in all.OfType<CheckBox>()) box.Checked = false;
                        Number(form, "Before (ms)").Value = 1400;
                        Number(form, "After (ms)").Controls.OfType<TextBox>().Single().Text = "1800";
                        Number(form, "Min. pause (ms)").Value = 240;
                        Number(form, "Min. flash area (%)").Value = 2.3m;
                        Number(form, "Min. redraw area (%)").Value = 18.7m;
                        picker.SelectedIndex = -1;
                        testedExit = true;
                        tray.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item=>item.Text=="Exit").PerformClick();
                    }
                };
                form.Shown+=delegate { timer.Start(); }; Application.Run(form);
                Check(testedExit, "Test window exited before the tray exit and settings checks completed");
                string[] files=Directory.GetFiles(liveDir,"events.csv",SearchOption.AllDirectories);
                var results=files.Select(File.ReadAllText).ToArray();
                string manual=files.FirstOrDefault(f=>File.ReadAllText(f).Contains("Stopped watching") && Directory.GetFiles(Path.GetDirectoryName(f),"*.png").Length>0);
                Check(manual!=null && File.Exists(Path.Combine(Path.GetDirectoryName(manual),"index.html")), "Stop watching did not export a viewable capture");
                Check(results.Any(s=>s.Contains("Brief flash")) && results.Any(s=>s.Contains("Pause during motion")) && results.Any(s=>s.Contains("Large redraw")),"Missing live trigger: "+string.Join(" | ",results));
                var windowDir = Directory.GetDirectories(liveDir).Single();
                Check(Path.GetFileName(windowDir) == "WindowWatch generated test pattern", "Expected a stable window folder without PID");
                var sessions = Directory.GetDirectories(windowDir);
                Check(sessions.Length == 2, "Two runs should have separate session folders");
                foreach (string folder in sessions) {
                    string index = File.ReadAllText(Path.Combine(folder, "index.html"));
                    foreach (string sub in Directory.GetDirectories(folder))
                        Check(index.Contains(Uri.EscapeDataString(Path.GetFileName(sub)) + "/index.html"), "Session index missing capture");
                }
                Check(Directory.GetDirectories(liveDir,"*.partial",SearchOption.AllDirectories).Length==0,"Partial event left over");
                File.WriteAllText(log,"PASS "+files.Length+" live events; core idle, exact 340 ms pause, single-frame flash PNGs, visibility reset and viewer verified.\r\n"+string.Join("\r\n",results));
            }
            catch(Exception e) { File.WriteAllText(log,e.ToString()); Environment.ExitCode=1; }
        }
    }
}
