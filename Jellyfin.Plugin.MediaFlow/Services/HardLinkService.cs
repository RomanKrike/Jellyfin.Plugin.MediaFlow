using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class HardLinkService
{
    public string Create(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Source media file does not exist.", source);
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination has no directory."));

        // Idempotent retry/reconciliation:
        // if the destination already exists and is the exact same filesystem object
        // (same device + inode on Linux, same volume + file ID on Windows),
        // treat the import as already completed instead of reporting a conflict.
        if (File.Exists(destination))
        {
            if (IsSameFile(source, destination))
            {
                return destination;
            }

            throw new IOException(
                $"Destination already exists and is a different file; refusing to overwrite: {destination}");
        }

        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(destination, source, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateHardLink failed.");
            }
        }
        else
        {
            if (LinkUnix(source, destination) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"link(2) failed. Source and destination must be on the same filesystem. {source} -> {destination}");
            }
        }

        return destination;
    }

    public bool IsSameFile(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);

        if (!File.Exists(source) || !File.Exists(destination))
        {
            return false;
        }

        return IsSameFileCore(source, destination);
    }

    private static bool IsSameFileCore(string source, string destination)
    {
        if (OperatingSystem.IsLinux())
        {
            return AreSameFileLinux(source, destination);
        }

        if (OperatingSystem.IsWindows())
        {
            return AreSameFileWindows(source, destination);
        }

        // Preserve the conservative behavior on unsupported platforms.
        return false;
    }

    private static bool AreSameFileLinux(string source, string destination)
    {
        if (StatLinux(source, out var sourceStat) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"stat(2) failed for source: {source}");
        }

        if (StatLinux(destination, out var destinationStat) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"stat(2) failed for destination: {destination}");
        }

        return sourceStat.Device == destinationStat.Device
            && sourceStat.Inode == destinationStat.Inode;
    }

    private static bool AreSameFileWindows(string source, string destination)
    {
        using var sourceHandle = OpenForFileIdentity(source);
        using var destinationHandle = OpenForFileIdentity(destination);

        if (!GetFileInformationByHandle(sourceHandle, out var sourceInfo))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"GetFileInformationByHandle failed for source: {source}");
        }

        if (!GetFileInformationByHandle(destinationHandle, out var destinationInfo))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"GetFileInformationByHandle failed for destination: {destination}");
        }

        return sourceInfo.VolumeSerialNumber == destinationInfo.VolumeSerialNumber
            && sourceInfo.FileIndexHigh == destinationInfo.FileIndexHigh
            && sourceInfo.FileIndexLow == destinationInfo.FileIndexLow;
    }

    private static SafeFileHandle OpenForFileIdentity(string path)
    {
        var handle = CreateFileWindows(
            path,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"CreateFile failed for identity check: {path}");
        }

        return handle;
    }

    // Linux x86_64/glibc struct stat layout. MediaFlow's current Jellyfin deployment
    // is Debian x86_64. Unsupported OSes remain conservative and report a conflict.
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong LinkCount;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding0;
        public ulong RDevice;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public Timespec AccessTime;
        public Timespec ModifyTime;
        public Timespec ChangeTime;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

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

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkUnix(string oldPath, string newPath);

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int StatLinux(string path, out LinuxStat stat);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);
}
