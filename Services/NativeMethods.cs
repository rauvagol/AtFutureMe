using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AtFutureMe.Services;

public static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public static void ApplyOverlayStyles(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        // auto model: overlays default to no-activate; only transparent when locked
        ApplyExStyle(hwnd, clickThrough: false, allowActivate: false);
    }

    public static void SetClickThrough(Window window, bool clickThrough, bool allowActivate = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        ApplyExStyle(hwnd, clickThrough, allowActivate);
    }

    // auto model: keep WS_EX_NOACTIVATE unless a text field / menu needs keyboard focus
    static void ApplyExStyle(IntPtr hwnd, bool clickThrough, bool allowActivate)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var next = style | WsExToolWindow | WsExLayered;
        if (allowActivate)
            next &= ~(long)WsExNoActivate;
        else
            next |= WsExNoActivate;
        if (clickThrough)
            next |= WsExTransparent;
        else
            next &= ~(long)WsExTransparent;
        if (next == style)
            return;

        SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)next);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public static bool TryGetCursorInWindow(Window window, out Point local)
    {
        local = default;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var pt) || !ScreenToClient(hwnd, ref pt))
            return false;
        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        local = new Point(pt.X / scaleX, pt.Y / scaleY);
        return true;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static bool WindowLooksOnScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd) || !IsWindowVisible(hwnd))
            return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        if (!GetWindowRect(hwnd, out var rect))
            return false;
        return rect.Right - rect.Left > 2 && rect.Bottom - rect.Top > 2;
    }

    const uint SwpNoMove = 0x0002;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;
    const uint SwpFrameChanged = 0x0020;
    static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>Styles for the hover-tip HWND (once). Click-through, no taskbar/Alt-Tab, no activate.</summary>
    public static void ConfigureTipWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        ApplyExStyle(hwnd, clickThrough: true, allowActivate: false);
    }

    /// <summary>
    /// Put tip at the top of the topmost band without touching exstyles.
    /// (Re-applying styles / FRAMECHANGED was flashing the busy cursor; skipping
    /// this entirely left the tip under the clock overlay.)
    /// </summary>
    public static void RaiseTipZOrder(Window tip) => RaiseTopmost(tip);

    /// <summary>Move a window to the top of the topmost band without activating it.</summary>
    public static void RaiseTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, System.Text.StringBuilder exeName, ref int size);

    public static string? GetProcessImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var size = 1024;
            var buffer = new System.Text.StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    const int SwRestore = 9;
    const uint GwOwner = 4;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public static bool ClassLooksLikeSplash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        var buffer = new System.Text.StringBuilder(256);
        var n = GetClassName(hwnd, buffer, buffer.Capacity);
        if (n <= 0)
            return false;
        var name = buffer.ToString(0, n);
        return name.Contains("splash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("anticheat", StringComparison.OrdinalIgnoreCase)
            || name.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase);
    }

    public readonly record struct AppWindow(IntPtr Handle, int ProcessId, long Area, string Title);

    public static bool TryActivateProcessWindow(IReadOnlySet<int> processIds)
    {
        var windows = ListAppWindows(processIds);
        if (windows.Count == 0)
            return false;
        var best = windows[0];
        foreach (var window in windows)
        {
            if (window.Area > best.Area)
                best = window;
        }
        ForceForeground(best.Handle);
        return true;
    }

    public static List<AppWindow> ListAppWindows(IReadOnlySet<int> processIds) =>
        ListWindows(processIds);

    public static List<AppWindow> ListOpenWindows() => ListWindows(null);

    static List<AppWindow> ListWindows(IReadOnlySet<int>? processIds)
    {
        var found = new List<AppWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (TryMeasureWindow(hwnd, processIds, out var window))
                found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool TryMeasureWindow(IntPtr hwnd, IReadOnlySet<int>? processIds, out AppWindow window)
    {
        window = default;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;
        GetWindowThreadProcessId(hwnd, out var rawPid);
        var pid = (int)rawPid;
        if (processIds != null && !processIds.Contains(pid))
            return false;
        var iconic = IsIconic(hwnd);
        if (!iconic && !IsWindowVisible(hwnd))
            return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((style & WsExToolWindow) != 0)
            return false;
        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            return false;
        long area = 1;
        if (GetWindowRect(hwnd, out var rect))
        {
            var w = Math.Max(0, rect.Right - rect.Left);
            var h = Math.Max(0, rect.Bottom - rect.Top);
            area = Math.Max(1, (long)w * h);
        }
        if (iconic)
            area = Math.Max(area, 8_000_000);
        var title = ReadWindowTitle(hwnd);
        if (title.Length > 0)
            area += 2_000_000;
        window = new AppWindow(hwnd, pid, area, title);
        return true;
    }

    static string ReadWindowTitle(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(512);
        var n = GetWindowText(hwnd, buffer, buffer.Capacity);
        return n <= 0 ? "" : buffer.ToString(0, n);
    }

    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return;
        // SW_SHOW on an already-open game also reveals owned splash windows
        // (anti-cheat) for a frame. Restore only when the target is minimized.
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        var fore = GetForegroundWindow();
        var foreThread = GetWindowThreadProcessId(fore, out _);
        var appThread = GetCurrentThreadId();
        var attached = false;
        try
        {
            if (foreThread != 0 && foreThread != appThread)
                attached = AttachThreadInput(appThread, foreThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(appThread, foreThread, false);
        }
    }
}
