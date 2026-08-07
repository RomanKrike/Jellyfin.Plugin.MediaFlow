using System.ComponentModel;
using System.Runtime.InteropServices;

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

        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Destination has no directory."));

        if (File.Exists(destination))
        {
            throw new IOException($"Destination already exists; refusing to overwrite or assume it is the same hardlink: {destination}");
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
                throw new Win32Exception(error, $"link(2) failed. Source and destination must be on the same filesystem. {source} -> {destination}");
            }
        }

        return destination;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkUnix(string oldPath, string newPath);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);
}
