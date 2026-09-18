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

    const uint SwpNoMove = 0x0002;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;
    const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

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
}
