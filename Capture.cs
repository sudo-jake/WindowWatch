using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace WindowWatch
{
    internal sealed class Capture : IDisposable
    {
        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICaptureInterop
        {
            IntPtr CreateForWindow(IntPtr window, ref Guid iid);
            IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
        }
        [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IByteAccess { void GetBuffer(out IntPtr data, out uint capacity); }
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count, uint version, out IntPtr device, out uint featureLevel, out IntPtr context);
        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);

        private static IDirect3DDevice CreateDevice()
        {
            IntPtr d3d = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, winrt = IntPtr.Zero;
            try
            {
                uint level;
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 32, IntPtr.Zero, 0, 7, out d3d, out level, out context));
                var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref iid, out dxgi));
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out winrt));
                return (IDirect3DDevice)Marshal.GetObjectForIUnknown(winrt);
            }
            finally
            {
                foreach (IntPtr pointer in new[] { winrt, dxgi, context, d3d }) if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            }
        }

        private readonly object gate = new object();
        private IDirect3DDevice device;
        private GraphicsCaptureItem item;
        private Direct3D11CaptureFramePool pool;
        private GraphicsCaptureSession session;
        private Windows.Graphics.SizeInt32 size;
        private bool stopped;
        private readonly Action<double, IntPtr, int, int, int> receive;
        private readonly Action<string> failed;

        internal Capture(IntPtr hwnd, Action<double, IntPtr, int, int, int> receive, Action<string> failed)
        {
            this.receive = receive;
            this.failed = failed;
            try
            {
                if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable in this session.");
                var factory = (ICaptureInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
                var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                IntPtr pointer = factory.CreateForWindow(hwnd, ref iid);
                try { item = (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(pointer); }
                finally { Marshal.Release(pointer); }
                device = CreateDevice();
                size = item.Size;
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                pool.FrameArrived += OnFrame;
                item.Closed += OnClosed;
                session.StartCapture();
            }
            catch { Dispose(); throw; }
        }
        private void OnClosed(GraphicsCaptureItem sender, object args) { failed("The selected window closed."); }
        private void OnFrame(Direct3D11CaptureFramePool sender, object args)
        {
            lock (gate)
            {
                if (stopped) return;
                try
                {
                    using (var frame = sender.TryGetNextFrame())
                    {
                        if (frame == null) return;
                        var next = frame.ContentSize;
                        if (next.Width < 1 || next.Height < 1) return;
                        if (next.Width != size.Width || next.Height != size.Height) size = next;
                        else
                        {
                            using (var bmp = CopySurface(frame.Surface))
                            using (var buffer = bmp.LockBuffer(BitmapBufferAccessMode.Read))
                            using (var reference = buffer.CreateReference())
                            {
                                IntPtr data;
                                uint capacity;
                                ((IByteAccess)reference).GetBuffer(out data, out capacity);
                                var plane = buffer.GetPlaneDescription(0);
                                if (plane.Stride < size.Width * 4 || (long)plane.StartIndex + (long)(size.Height - 1) * plane.Stride + size.Width * 4 > capacity)
                                    throw new InvalidOperationException("Invalid pixel buffer reported by Windows.");
                                receive(frame.SystemRelativeTime.TotalMilliseconds, IntPtr.Add(data, plane.StartIndex), plane.Stride, size.Width, size.Height);
                            }
                            return;
                        }
                    }
                    pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                }
                catch (Exception ex) { stopped = true; failed("Capture stopped: " + ex.Message); }
            }
        }

        private static SoftwareBitmap CopySurface(IDirect3DSurface surface)
        {
            var operation = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface);
            var done = new System.Threading.Tasks.TaskCompletionSource<SoftwareBitmap>();
            operation.Completed = delegate(IAsyncOperation<SoftwareBitmap> result, AsyncStatus status)
            {
                try { done.TrySetResult(result.GetResults()); }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            try { return done.Task.GetAwaiter().GetResult(); }
            finally { operation.Close(); }
        }

        public void Dispose()
        {
            lock (gate) { stopped = true; }
            if (item != null) item.Closed -= OnClosed;
            if (pool != null) pool.FrameArrived -= OnFrame;
            if (session != null) session.Dispose();
            if (pool != null) pool.Dispose();
            if (device != null) device.Dispose();
        }
    }
}
