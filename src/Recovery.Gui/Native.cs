using System.Runtime.InteropServices;

namespace Recovery.Gui;

internal static class Native
{
    /// <summary>
    /// Explorer visual style for ListView/TreeView: modern hover & selection
    /// highlight, chevrons instead of +/- boxes in trees.
    /// </summary>
    public static void UseExplorerStyle(Control control)
    {
        void Apply()
        {
            try { SetWindowTheme(control.Handle, "Explorer", null); }
            catch (EntryPointNotFoundException) { }
        }
        if (control.IsHandleCreated)
            Apply();
        control.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Show the native sort arrow on one ListView column header.</summary>
    public static void SetSortArrow(ListView list, int column, SortOrder order)
    {
        if (!list.IsHandleCreated)
            return;
        IntPtr header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero)
            return;
        for (int i = 0; i < list.Columns.Count; i++)
        {
            var item = new HDITEM { mask = HDI_FORMAT };
            SendMessage(header, HDM_GETITEM, (IntPtr)i, ref item);
            item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
            if (i == column && order != SortOrder.None)
                item.fmt |= order == SortOrder.Ascending ? HDF_SORTUP : HDF_SORTDOWN;
            SendMessage(header, HDM_SETITEM, (IntPtr)i, ref item);
        }
    }

    /// <summary>Sets the pixel spacing between icons in LargeIcon view.</summary>
    public static void SetIconSpacing(ListView list, int cx, int cy)
    {
        if (list.IsHandleCreated)
            SendMessage(list.Handle, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF)));
    }

    public enum RecycleResult { Moved, Cancelled, Failed }

    /// <summary>
    /// Moves a file or folder to the Recycle Bin. If Windows can't recycle it
    /// (bigger than the bin's limit, bin disabled…) it asks before deleting
    /// permanently instead of silently destroying it — FOF_WANTNUKEWARNING.
    /// </summary>
    public static RecycleResult MoveToRecycleBin(IWin32Window owner, string path, out string? error)
    {
        error = null;
        if (!Environment.Is64BitProcess)
        {
            // SHFILEOPSTRUCT packing differs on x86; let the shell show its own dialogs there.
            try
            {
                if (Directory.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (OperationCanceledException)
            {
                return RecycleResult.Cancelled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error = ex.Message;
                return RecycleResult.Failed;
            }
            return File.Exists(path) || Directory.Exists(path) ? RecycleResult.Cancelled : RecycleResult.Moved;
        }

        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner.Handle,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // double-NUL-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted)
            return RecycleResult.Cancelled;
        if (rc != 0)
        {
            error = $"Windows couldn't move it to the Recycle Bin (error 0x{rc:X}). It may be in use or protected.";
            return RecycleResult.Failed;
        }
        return File.Exists(path) || Directory.Exists(path) ? RecycleResult.Cancelled : RecycleResult.Moved;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETHEADER = LVM_FIRST + 31;
    private const int LVM_SETICONSPACING = LVM_FIRST + 53;
    private const int HDM_FIRST = 0x1200;
    private const int HDM_GETITEM = HDM_FIRST + 11;
    private const int HDM_SETITEM = HDM_FIRST + 12;
    private const int HDI_FORMAT = 0x0004;
    private const int HDF_SORTUP = 0x0400;
    private const int HDF_SORTDOWN = 0x0200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HDITEM
    {
        public int mask;
        public int cxy;
        public IntPtr pszText;
        public IntPtr hbm;
        public int cchTextMax;
        public int fmt;
        public IntPtr lParam;
        public int iImage;
        public int iOrder;
        public uint type;
        public IntPtr pvFilter;
        public uint state;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);
}
