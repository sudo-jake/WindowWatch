using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WindowWatch
{
    internal static class RecycleBin
    {
        // Native IFileOperation vtable; unused methods are declared to retain slot order.
        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOperationFlags(uint flags);
            void SetProgressMessage(IntPtr message);
            void SetProgressDialog(IntPtr dialog);
            void SetProperties(IntPtr properties);
            void SetOwnerWindow(IntPtr window);
            void ApplyPropertiesToItem(IntPtr item);
            void ApplyPropertiesToItems(IntPtr items);
            void RenameItem(IntPtr item, IntPtr name, IntPtr sink);
            void RenameItems(IntPtr items, IntPtr name);
            void MoveItem(IntPtr item, IntPtr parent, IntPtr name, IntPtr sink);
            void MoveItems(IntPtr items, IntPtr parent);
            void CopyItem(IntPtr item, IntPtr parent, IntPtr name, IntPtr sink);
            void CopyItems(IntPtr items, IntPtr parent);
            void DeleteItem(IntPtr item, IProgressSink sink);
            void DeleteItems(IntPtr items);
            void NewItem(IntPtr parent, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
            [PreserveSig] int PerformOperations();
            void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IProgressSink
        {
            void StartOperations(); void FinishOperations(int result);
            void PreRenameItem(uint flags, IntPtr item, IntPtr name);
            void PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr target);
            void PreMoveItem(uint flags, IntPtr item, IntPtr parent, IntPtr name);
            void PostMoveItem(uint flags, IntPtr item, IntPtr parent, IntPtr name, int result, IntPtr target);
            void PreCopyItem(uint flags, IntPtr item, IntPtr parent, IntPtr name);
            void PostCopyItem(uint flags, IntPtr item, IntPtr parent, IntPtr name, int result, IntPtr target);
            void PreDeleteItem(uint flags, IntPtr item);
            void PostDeleteItem(uint flags, IntPtr item, int result, IntPtr target);
            void PreNewItem(uint flags, IntPtr parent, IntPtr name);
            void PostNewItem(uint flags, IntPtr parent, IntPtr name, IntPtr template, uint attributes, int result, IntPtr item);
            void UpdateProgress(uint total, uint done); void ResetTimer(); void PauseTimer(); void ResumeTimer();
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class RecycleOnly : IProgressSink
        {
            internal bool Recycled;
            public void PreDeleteItem(uint flags, IntPtr item)
            {
                const uint recycleIfPossible = 0x80;
                if ((flags & recycleIfPossible) == 0) throw new COMException("Windows cannot recycle this capture.", unchecked((int)0x80004004));
            }
            public void PostDeleteItem(uint flags, IntPtr item, int result, IntPtr target) { Recycled = result >= 0 && target != IntPtr.Zero; }
            public void StartOperations() {} public void FinishOperations(int result) {}
            public void PreRenameItem(uint f, IntPtr i, IntPtr n) {}
            public void PostRenameItem(uint f, IntPtr i, IntPtr n, int r, IntPtr t) {}
            public void PreMoveItem(uint f, IntPtr i, IntPtr p, IntPtr n) {}
            public void PostMoveItem(uint f, IntPtr i, IntPtr p, IntPtr n, int r, IntPtr t) {}
            public void PreCopyItem(uint f, IntPtr i, IntPtr p, IntPtr n) {}
            public void PostCopyItem(uint f, IntPtr i, IntPtr p, IntPtr n, int r, IntPtr t) {}
            public void PreNewItem(uint f, IntPtr p, IntPtr n) {}
            public void PostNewItem(uint f, IntPtr p, IntPtr n, IntPtr t, uint a, int r, IntPtr i) {}
            public void UpdateProgress(uint total, uint done) {} public void ResetTimer() {} public void PauseTimer() {} public void ResumeTimer() {}
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);

        internal static void Move(string folder)
        {
            var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
            IntPtr item = IntPtr.Zero;
            try
            {
                const uint recycleOnDelete = 0x80000, addUndoRecord = 0x20000000, quiet = 0x414;
                operation.SetOperationFlags(recycleOnDelete | addUndoRecord | quiet);
                var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out item);
                var sink = new RecycleOnly();
                operation.DeleteItem(item, sink);
                int result = operation.PerformOperations();
                bool aborted;
                operation.GetAnyOperationsAborted(out aborted);
                Marshal.ThrowExceptionForHR(result);
                if (aborted || !sink.Recycled || Directory.Exists(folder)) throw new IOException("Windows did not confirm that the capture reached the Recycle Bin.");
            }
            finally
            {
                if (item != IntPtr.Zero) Marshal.Release(item);
                Marshal.ReleaseComObject(operation);
            }
        }
    }
}
