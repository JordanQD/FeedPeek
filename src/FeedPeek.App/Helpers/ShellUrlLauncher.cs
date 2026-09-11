using System;
using System.Runtime.InteropServices;

namespace FeedPeek.Helpers;

/// <summary>
/// Opens a URL through the shell while asking Windows not to activate the target window
/// (<c>SW_SHOWNOACTIVATE</c>), i.e. a best-effort "open in the background". Whether the
/// browser honours it is up to the browser: an already running browser commonly activates
/// itself anyway when it opens a new tab.
/// </summary>
internal static class ShellUrlLauncher
{
    private const int SwShowNoActivate = 4;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;

    /// <summary>Returns <see langword="false"/> when the shell could not start the URL.</summary>
    public static bool TryOpenInBackground(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskNoAsync | SeeMaskFlagNoUi,
            Verb = "open",
            File = url,
            Show = SwShowNoActivate,
        };

        return ShellExecuteEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo execInfo);
}
