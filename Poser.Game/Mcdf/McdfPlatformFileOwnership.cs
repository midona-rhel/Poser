using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Poser.Game.Mcdf;

/// <summary>Windows ownership primitives whose managed counterparts are
/// path-based and therefore cannot prove exclusive directory creation or
/// rename the exact open temporary file.</summary>
internal static class McdfPlatformFileOwnership
{
    private const uint GenericWrite = 0x40000000;
    private const uint GenericRead = 0x80000000;
    private const uint ReadAttributes = 0x00000080;
    private const uint Delete = 0x00010000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;

    internal static bool TryCreateDirectoryExclusive(string path)
        => CreateDirectory(path, IntPtr.Zero);

    internal static FileStream CreateExclusiveTemporary(string path)
    {
        var handle = CreateFile(
            path, GenericWrite | Delete, ShareRead | ShareDelete,
            IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new FileStream(handle, FileAccess.Write);
    }

    internal static FileStream CreateExclusiveOwnedMarker(string path)
    {
        var handle = CreateFile(
            path, GenericWrite | Delete, ShareRead | ShareDelete,
            IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new FileStream(handle, FileAccess.Write);
    }

    internal static FileStream OpenOwnedMarker(string path)
    {
        var handle = CreateFile(
            path, GenericRead | Delete, ShareRead | ShareDelete,
            IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new FileStream(handle, FileAccess.Read);
    }

    internal static string GetRequiredFinalPath(SafeFileHandle handle)
    {
        uint capacity = 260;
        while (true)
        {
            var buffer = new StringBuilder(checked((int)capacity));
            uint length = GetFinalPathNameByHandle(handle, buffer, capacity, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < capacity)
            {
                string path = buffer.ToString(0, checked((int)length));
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    path = @"\\" + path[8..];
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    path = path[4..];
                return Path.GetFullPath(path);
            }
            capacity = checked(length + 1);
        }
    }

    internal static string? TryGetIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            return null;
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    internal static SafeFileHandle OpenFencedDirectory(string path)
    {
        var handle = CreateFile(
            path, Delete | ReadAttributes, ShareRead | ShareWrite, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return handle;
    }

    internal static SafeFileHandle OpenDirectoryForInspection(string path)
    {
        var handle = CreateFile(
            path, ReadAttributes, ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return handle;
    }

    internal static SafeFileHandle OpenDestinationForCommit(string path)
    {
        var handle = CreateFile(
            path, GenericRead | ReadAttributes,
            ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
            OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return handle;
    }

    internal static void CommitExactHandle(
        SafeFileHandle handle, string destination, bool replaceExisting)
    {
        byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        int bufferSize = checked(nameOffset + name.Length + sizeof(char));
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int i = 0; i < bufferSize; i++)
                Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteByte(buffer, replaceExisting ? (byte)1 : (byte)0);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(
                    handle, FileRenameInfo, buffer, (uint)bufferSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void MarkDeleteOnClose(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(
                    handle, FileDispositionInfo, buffer, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int informationClass, IntPtr information, uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
