using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WindowWatch
{
    internal struct Observation
    {
        internal double Time, ImageTime;
    }
    internal sealed class Clip
    {
        internal readonly List<Shot> Frames = new List<Shot>();
        internal readonly List<Finding> Findings = new List<Finding>();
        internal readonly List<Observation> Samples = new List<Observation>();
        internal Session Session;
        internal string Note;
        internal double Start, Until, FirstDetection, ClockReference;
        internal DateTime UtcReference;
        internal long Bytes;
        internal void Add(Shot frame) { Frames.Add(frame); Bytes += frame.Bytes.Length; }
    }
    internal sealed class Recorder
    {
        internal static double Now { get { return Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency); } }
        private const long HistoryBytes = 160L << 20, ClipBytes = 256L << 20;
        private readonly object gate = new object();
        private readonly LinkedList<Shot> history = new LinkedList<Shot>();
        private readonly Queue<Observation> samples = new Queue<Observation>();
        private readonly FramePacker packer = new FramePacker();
        private readonly Action<Clip> submit;
        private readonly Options options;
        private readonly Session session;
        private readonly double clockReference = Now;
        private readonly DateTime utcReference = DateTime.UtcNow;
        private Detector detector;
        private long bytes;
        private int width, height;
        private bool suspended, stopped, resetOnNextFrame;
        private Clip clip;
        private double lastReceived = double.NegativeInfinity, lastEvicted = double.NegativeInfinity;
        private int received, distinct;
        internal Recorder(Options options, Session session, Action<Clip> submit)
        {
            this.options = options; this.session = session; this.submit = submit;
            detector = new Detector(options);
        }
        internal string Counts()
        {
            lock (gate) return received + " frames received  ·  " + distinct + " changed";
        }
        internal void Observe(double t, IntPtr p, int stride, int w, int h)
        {
            lock (gate)
            {
                if (suspended || stopped || t <= lastReceived) return;
                if (resetOnNextFrame || w != width || h != height)
                {
                    Reset("Window size changed.");
                    resetOnNextFrame = false;
                    width = w; height = h;
                }
                if (clip != null && t > clip.Until) Finish(null);
                lastReceived = t;
                received++;
                var frame = packer.Read(t, p, stride, w, h);
                var sample = new Observation { Time = t, ImageTime = frame == null ? history.Last.Value.Time : frame.Time };
                samples.Enqueue(sample);
                double oldest = t - options.Before - 2750;
                while (samples.Count > 0 && samples.Peek().Time < oldest) samples.Dequeue();
                if (clip != null) clip.Samples.Add(sample);
                if (frame == null) return;
                distinct++;
                history.AddLast(frame);
                bytes += frame.Bytes.Length;
                // Keep the last image from before the time horizon as a visual baseline.
                while (history.Count > 1 && (history.First.Next.Value.Time <= oldest || bytes > HistoryBytes))
                {
                    if (bytes > HistoryBytes) lastEvicted = history.First.Value.Time;
                    bytes -= history.First.Value.Bytes.Length;
                    history.RemoveFirst();
                }
                if (clip != null)
                {
                    if (clip.Bytes + frame.Bytes.Length <= ClipBytes) clip.Add(frame);
                    else Finish("Event reached the memory limit. The sequence ends early.");
                }
                detector.Observe(t, packer.Signature(), Report);
            }
        }
        private Clip FromHistory(double origin, double from)
        {
            var result = new Clip { Session = session, Start = origin, FirstDetection = lastReceived,
                UtcReference = utcReference, ClockReference = clockReference };
            var node = history.First;
            while (node.Next != null && node.Next.Value.Time <= from) node = node.Next;
            if (node.Value.Time > from && !double.IsNegativeInfinity(lastEvicted))
                result.Note = "Earlier frames were evicted by the memory limit.";
            for (; node != null; node = node.Next) result.Add(node.Value);
            foreach (var sample in samples)
                if (sample.Time >= from && sample.ImageTime >= result.Frames[0].Time) result.Samples.Add(sample);
            return result;
        }
        private void Report(Finding finding)
        {
            if (clip == null) clip = FromHistory(finding.Start, finding.Start - options.Before);
            clip.Findings.Add(finding);
            clip.Until = Math.Min(clip.FirstDetection + 5000 + options.After, Math.Max(clip.Until, finding.End + options.After));
        }
        private void Finish(string reason)
        {
            if (clip == null) return;
            if (reason != null) clip.Note = (clip.Note == null ? "" : clip.Note + " ") + reason;
            submit(clip);
            clip = null;
        }
        private void Reset(string reason)
        {
            Finish(reason);
            history.Clear();
            samples.Clear();
            bytes = 0;
            lastEvicted = double.NegativeInfinity;
            packer.Reset();
            detector = new Detector(options);
        }
        internal void Tick(double now, bool minimized)
        {
            lock (gate)
            {
                if (minimized != suspended)
                {
                    if (minimized) { Finish("Window was minimized."); resetOnNextFrame = true; }
                    suspended = minimized;
                }
                if (clip != null && now >= clip.Until + 150) Finish(null);
            }
        }
        internal Clip Stop()
        {
            lock (gate)
            {
                if (stopped) return null;
                stopped = true;
                var final = clip;
                if (final != null)
                {
                    if (Now < final.Until) final.Note = (final.Note + " Monitoring stopped before the post-event interval finished.").Trim();
                }
                else if (history.Count > 0)
                {
                    double end = Math.Max(Now, lastReceived);
                    final = FromHistory(end, double.NegativeInfinity);
                    final.Findings.Add(new Finding("Stopped watching", end, end));
                }
                clip = null;
                history.Clear();
                samples.Clear();
                return final;
            }
        }
    }
}
