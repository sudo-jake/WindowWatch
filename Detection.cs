using System;
using System.Collections.Generic;

namespace WindowWatch
{
    internal sealed class Options
    {
        internal const decimal DefaultFlashPercent = 0.5m, DefaultRedrawPercent = 1m;
        internal double Before = 700, After = 700, Pause = 120;
        internal double FlashArea = (double)(DefaultFlashPercent / 100), RedrawArea = (double)(DefaultRedrawPercent / 100);
        internal bool Pauses = true, Flashes = true, Redraws = true;
    }
    internal sealed class Finding
    {
        internal readonly string Kind;
        internal readonly double Start, End;
        internal Finding(string kind, double start, double end) { Kind = kind; Start = start; End = end; }
    }
    internal sealed class Detector
    {
        private readonly Options options;
        private int[] previous, baseline;
        private Finding pendingPause;
        private double candidateAt, candidateFraction, lastMotion, lastFraction, lastChange;
        private readonly List<double> cadence = new List<double>();
        internal Detector(Options options) { this.options = options; }

        private static double Changed(int[] a, int[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int x = a[i], y = b[i];
                if (Math.Abs((x & 255) - (y & 255)) > 15 || Math.Abs(((x >> 8) & 255) - ((y >> 8) & 255)) > 15 ||
                    Math.Abs((x >> 16) - (y >> 16)) > 15) count++;
            }
            return count / (double)a.Length;
        }
        internal void Observe(double t, int[] current, Action<Finding> report)
        {
            if (previous == null) { previous = current; return; }
            double delta = Changed(previous, current);
            bool returned = false;
            if (options.Flashes)
            {
                if (baseline != null && t - candidateAt > 350) baseline = null;
                if (baseline != null && Changed(baseline, current) <= Math.Max(1.0 / current.Length, 0.20 * candidateFraction))
                {
                    report(new Finding("Brief flash", candidateAt, t));
                    baseline = null;
                    returned = true;
                }
                if (!returned && delta >= options.FlashArea && (baseline == null || delta > 1.3 * candidateFraction))
                {
                    baseline = previous;
                    candidateAt = t;
                    candidateFraction = delta;
                }
            }
            if (options.Redraws && !returned && delta >= options.RedrawArea && (lastFraction < 0.6 * options.RedrawArea || t - lastChange > 350))
                report(new Finding("Large redraw", t, t));
            if (options.Pauses && delta >= 0.0008)
            {
                double gap = t - lastMotion;
                if (pendingPause != null)
                {
                    if (gap > 0 && gap <= Math.Min(100, options.Pause * 0.8)) report(pendingPause);
                    pendingPause = null;
                }
                if (cadence.Count >= 5 && gap >= options.Pause && gap <= 2500)
                {
                    var sorted = cadence.ToArray();
                    Array.Sort(sorted);
                    if (gap >= 2.8 * sorted[sorted.Length / 2]) pendingPause = new Finding("Pause during motion", lastMotion, t);
                }
                if (lastMotion > 0 && gap > 0 && gap <= Math.Min(100, options.Pause * 0.8))
                {
                    cadence.Add(gap);
                    if (cadence.Count > 12) cadence.RemoveAt(0);
                }
                else cadence.Clear();
                lastMotion = t;
            }
            lastFraction = delta;
            lastChange = t;
            previous = current;
        }
    }
}
