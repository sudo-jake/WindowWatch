using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Runtime.InteropServices;
class SampleWindow : Form
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int mode);
    readonly string marker;
    readonly Timer timer = new Timer { Interval = 30 };
    readonly Stopwatch watch = new Stopwatch();
    int x = 30; bool flash, redraw;
    public SampleWindow(string marker)
    {
        this.marker = marker; Text = "WindowWatch generated test pattern"; ClientSize = new Size(650,380);
        DoubleBuffered = true; StartPosition = FormStartPosition.Manual; Location = new Point(20,40);
        Shown += delegate { ShowWindow(Handle,8); timer.Start(); };
        timer.Tick += delegate {
            if (!watch.IsRunning) { if (File.Exists(this.marker)) watch.Start(); return; }
            long t = watch.ElapsedMilliseconds;
            if (t > 11500) { Close(); return; }
            if (t < 2000 && t > 600 || t > 2440 && t < 3450) { x = (x+9)%530; Invalidate(); }
            bool f = t >= 6800 && t < 6890, r = t > 9100;
            if (f != flash || r != redraw) { flash=f; redraw=r; Invalidate(); }
        };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(flash ? Color.White : redraw ? Color.SeaGreen : Color.FromArgb(30,38,60));
        e.Graphics.FillRectangle(Brushes.Orange,x,95,72,88);
    }
    [STAThread] static void Main(string[] args) { Application.EnableVisualStyles(); Application.Run(new SampleWindow(args[0])); }
}
