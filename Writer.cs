using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace WindowWatch
{
    internal sealed class Writer
    {
        // A stop can queue its last capture even while an automatic event is waiting.
        private readonly BlockingCollection<Clip> jobs = new BlockingCollection<Clip>(2);
        private readonly Thread thread;
        private readonly Action<string> status;
        private int waiting;
        private string lastSaved;
        internal string LastSaved { get { return Volatile.Read(ref lastSaved); } }
        internal void UpdateAfterRemoval(string replacement)
        {
            string last = LastSaved;
            if (last != null && !Directory.Exists(last)) Interlocked.CompareExchange(ref lastSaved, replacement, last);
        }
        internal int Waiting { get { return System.Threading.Volatile.Read(ref waiting); } }
        internal Writer(Action<string> status)
        {
            this.status = status;
            thread = new Thread(Run) { IsBackground = true, Name = "WindowWatch PNG writer" };
            thread.Start();
        }
        internal void Submit(Clip clip)
        {
            Interlocked.Increment(ref waiting);
            if (jobs.Count < 1 && jobs.TryAdd(clip)) return;
            Interlocked.Decrement(ref waiting);
            status("Missed an event while the PNG writer was busy. Try shorter capture intervals or a smaller window.");
        }
        internal void SubmitFinal(Clip clip)
        {
            Interlocked.Increment(ref waiting);
            jobs.Add(clip);
        }
        private void Run()
        {
            foreach (var clip in jobs.GetConsumingEnumerable())
            {
                try
                {
                    var directory = Save(clip);
                    Volatile.Write(ref lastSaved, directory);
                    status("Saved " + clip.Frames.Count + (clip.Frames.Count == 1 ? " frame in " : " frames in ") + Path.GetFileName(directory));
                    try { clip.Session.AddCapture(directory, Utc(clip, clip.Start), clip.Findings[0].Kind, clip.Frames.Count); }
                    catch (Exception e) { status("Capture saved. Could not update the session index: " + e.Message); }
                }
                catch (Exception e) { status("Could not finish saving the event: " + e.Message); }
                finally { Interlocked.Decrement(ref waiting); }
            }
        }
        internal void Close() { jobs.CompleteAdding(); thread.Join(); jobs.Dispose(); }
        private static string Number(double value) { return value.ToString("0.000", CultureInfo.InvariantCulture); }
        private static DateTime Utc(Clip clip, double t) { return clip.UtcReference.AddMilliseconds(t - clip.ClockReference); }
        private static string Save(Clip clip)
        {
            DateTime utc = Utc(clip, clip.Start);
            string name = utc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture) + "_" +
                clip.Findings[0].Kind.Replace(' ', '-') + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string final = Path.Combine(clip.Session.Folder, name), path = final + ".partial";
            Directory.CreateDirectory(path);
            var timeline = new StringBuilder("frame,file,relative_ms,utc,capture_clock_ms\r\n");
            var times = new StringBuilder();
            var frameFiles = new Dictionary<double,string>();
            int firstTriggerFrame = 0;
            for (int i = 0; i < clip.Frames.Count; i++)
            {
                var frame = clip.Frames[i];
                string file = (i + 1).ToString("0000") + ".png";
                frame.Save(Path.Combine(path, file));
                string ms = Number(frame.Time - clip.Start);
                timeline.AppendLine((i + 1) + "," + file + "," + ms + "," + Utc(clip, frame.Time).ToString("O") + "," + Number(frame.Time));
                frameFiles.Add(frame.Time, file);
                if (i != 0) times.Append(',');
                times.Append(ms);
                if (frame.Time <= clip.Start) firstTriggerFrame = i;
            }
            var marks = new StringBuilder();
            var events = new StringBuilder("event,start_ms,end_ms,duration_ms\r\n");
            foreach (var f in clip.Findings)
            {
                string start = Number(f.Start - clip.Start), end = Number(f.End - clip.Start);
                events.AppendLine(f.Kind + "," + start + "," + end + "," + Number(f.End - f.Start));
                marks.Append("<li>").Append(f.Kind).Append(" &nbsp; ").Append(start).Append(" ms");
                if (f.End > f.Start) marks.Append(" to ").Append(end).Append(" ms");
                marks.Append("</li>");
            }
            File.WriteAllText(Path.Combine(path, "timeline.csv"), timeline.ToString());
            File.WriteAllText(Path.Combine(path, "events.csv"), events.ToString());
            var samples = new StringBuilder("relative_ms,image_file\r\n");
            foreach (var sample in clip.Samples)
            {
                string file;
                if (frameFiles.TryGetValue(sample.ImageTime, out file)) samples.AppendLine(Number(sample.Time - clip.Start) + "," + file);
            }
            File.WriteAllText(Path.Combine(path, "samples.csv"), samples.ToString());
            string template;
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("viewer.html"))) template = reader.ReadToEnd();
            string html = template.Replace("@@TIMES@@", times.ToString()).Replace("@@INDEX@@", firstTriggerFrame.ToString())
                .Replace("@@ORIGIN@@", clip.Findings[0].Kind == "Stopped watching" ? "from stop" : "from event start")
                .Replace("@@EVENTS@@", marks.ToString()).Replace("@@DATE@@", utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Replace("@@NOTE@@", WebUtility.HtmlEncode(clip.Note ?? "")).Replace("@@WINDOW@@", WebUtility.HtmlEncode(clip.Session.Window));
            File.WriteAllText(Path.Combine(path, "index.html"), html);
            Directory.Move(path, final);
            return final;
        }
    }
}
