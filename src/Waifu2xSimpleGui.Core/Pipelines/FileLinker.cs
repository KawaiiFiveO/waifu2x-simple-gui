using System.Runtime.InteropServices;

namespace Waifu2xSimpleGui.Core.Pipelines;

/// <summary>Stages a file by hard link (zero-copy, same volume) and falls back to a copy.</summary>
internal static partial class FileLinker
{
    public static void LinkOrCopy(string source, string destination)
    {
        var linked = OperatingSystem.IsWindows()
            ? CreateHardLinkW(destination, source, IntPtr.Zero)
            : Link(source, destination) == 0;
        if (!linked)
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existing, string newPath);
}
