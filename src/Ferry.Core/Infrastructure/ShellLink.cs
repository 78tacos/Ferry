using System.Runtime.InteropServices;
using System.Text;

namespace Ferry.Core.Infrastructure;

/// <summary>Reads and rewrites .lnk shortcuts, so a desktop shortcut to C:\Users\old\… points at C:\Users\new\… after moving.</summary>
public static class ShellLink
{
    private const uint RawPath = 0x4;
    private const int MaxPath = 32768;

    public sealed record LinkInfo(string Target, string Arguments, string WorkingDirectory, string IconPath);

    public static LinkInfo? Read(string path)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkObject();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                return new LinkInfo(GetPath(link), GetArguments(link), GetWorkingDirectory(link), GetIcon(link, out _));
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read shortcut {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Rewrites old paths in the shortcut's target, arguments, start-in folder and icon. Returns true if it changed.</summary>
    public static bool Retarget(string path, PathRemapper remap)
    {
        if (remap.IsEmpty)
            return false;
        try
        {
            var link = (IShellLinkW)new ShellLinkObject();
            try
            {
                var persist = (IPersistFile)link;
                persist.Load(path, 2 /* STGM_READWRITE */);
                var changed = false;

                var target = GetPath(link);
                var newTarget = remap.Remap(target);
                if (newTarget != target)
                {
                    link.SetPath(newTarget);
                    changed = true;
                }
                var args = GetArguments(link);
                var newArgs = remap.Remap(args);
                if (newArgs != args)
                {
                    link.SetArguments(newArgs);
                    changed = true;
                }
                var dir = GetWorkingDirectory(link);
                var newDir = remap.Remap(dir);
                if (newDir != dir)
                {
                    link.SetWorkingDirectory(newDir);
                    changed = true;
                }
                var icon = GetIcon(link, out var index);
                var newIcon = remap.Remap(icon);
                if (newIcon != icon)
                {
                    link.SetIconLocation(newIcon, index);
                    changed = true;
                }
                if (changed)
                    persist.Save(path, true);
                return changed;
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not update shortcut {path}: {ex.Message}");
            return false;
        }
    }

    private static string GetPath(IShellLinkW link)
    {
        var sb = new StringBuilder(MaxPath);
        link.GetPath(sb, sb.Capacity, IntPtr.Zero, RawPath);
        return sb.ToString();
    }

    private static string GetArguments(IShellLinkW link)
    {
        var sb = new StringBuilder(MaxPath);
        link.GetArguments(sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWorkingDirectory(IShellLinkW link)
    {
        var sb = new StringBuilder(MaxPath);
        link.GetWorkingDirectory(sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetIcon(IShellLinkW link, out int index)
    {
        var sb = new StringBuilder(MaxPath);
        link.GetIconLocation(sb, sb.Capacity, out index);
        return sb.ToString();
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
