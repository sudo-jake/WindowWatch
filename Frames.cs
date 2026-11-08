using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WindowWatch
{
    internal sealed class Shot
    {
        internal double Time;
        internal int Width, Height;
        internal bool Compressed;
        internal byte[] Bytes;

        [DllImport("ntdll.dll")]
        private static extern int RtlDecompressBuffer(ushort format, byte[] destination, int size, byte[] source, int length, out int written);

        internal void Save(string path)
        {
            byte[] pixels = Bytes;
            if (Compressed)
            {
                pixels = new byte[checked(Width * Height * 4)];
                int written;
                if (RtlDecompressBuffer(3, pixels, pixels.Length, Bytes, Bytes.Length, out written) != 0 || written != pixels.Length)
                    throw new InvalidOperationException("Unable to unpack a buffered frame.");
            }
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using (var image = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppRgb, pin.AddrOfPinnedObject()))
                    image.Save(path, ImageFormat.Png);
            }
            finally { pin.Free(); }
        }
    }

    // The capture callback owns these reusable scratch buffers. Saved shots own their compressed data.
    internal sealed class FramePacker
    {
        [DllImport("ntdll.dll")]
        private static extern int RtlGetCompressionWorkSpaceSize(ushort format, out int workspace, out int fragment);
        [DllImport("ntdll.dll")]
        private static extern int RtlCompressBuffer(ushort format, byte[] source, int length, byte[] dest, int capacity, int chunk, out int written, byte[] workspace);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int memcmp(byte* a, byte* b, UIntPtr count);
        private readonly byte[] workspace;
        private byte[] packed, last;
        private int width, height;

        internal FramePacker()
        {
            int size, dummy;
            if (RtlGetCompressionWorkSpaceSize(3, out size, out dummy) == 0) workspace = new byte[size];
        }
        internal void Reset() { width = 0; }

        internal unsafe Shot Read(double time, IntPtr data, int stride, int w, int h)
        {
            int row = checked(w * 4), length = checked(row * h);
            if (length > 192 * 1024 * 1024) throw new InvalidOperationException("Window exceeds the capture memory limit. Reduce its size and try again.");
            bool differentSize = w != width || h != height;
            if (last == null || last.Length != length) { last = new byte[length]; packed = new byte[length]; }
            fixed (byte* previous = last)
            {
                var src = (byte*)data;
                if (stride == row)
                {
                    if (!differentSize && memcmp(src, previous, (UIntPtr)length) == 0) return null;
                    Marshal.Copy(data, last, 0, length);
                }
                else
                {
                    bool different = differentSize;
                    for (int y = 0; y < h; y++)
                    {
                        byte* sourceRow = src + y * stride;
                        if (!differentSize && memcmp(sourceRow, previous + y * row, (UIntPtr)row) == 0) continue;
                        different = true;
                        Marshal.Copy((IntPtr)sourceRow, last, y * row, row);
                    }
                    if (!different) return null;
                }
            }
            width = w;
            height = h;
            int written = 0;
            bool compressed = workspace != null && RtlCompressBuffer(3, last, length, packed, length, 4096, out written, workspace) == 0 && written < length;
            var bytes = new byte[compressed ? written : length];
            Buffer.BlockCopy(compressed ? packed : last, 0, bytes, 0, bytes.Length);
            return new Shot { Time = time, Width = w, Height = h, Bytes = bytes, Compressed = compressed };
        }
        internal int[] Signature()
        {
            int columns = Math.Min(128, width), rows = Math.Min(80, height);
            var result = new int[columns * rows];
            int i = 0;
            for (int y = 0; y < rows; y++)
            {
                int offset = ((2 * y + 1) * height / (2 * rows)) * width;
                for (int x = 0; x < columns; x++)
                {
                    int p = 4 * (offset + (2 * x + 1) * width / (2 * columns));
                    result[i++] = last[p] | last[p + 1] << 8 | last[p + 2] << 16;
                }
            }
            return result;
        }
    }
}
