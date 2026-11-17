using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace HerdrBubble;

public sealed record TerminalWindowInfo(
    IntPtr Handle,
    string Title,
    string ProcessName,
    bool IsMinimized,
    bool IsHerdr);

public static class Win32WindowHelper
{
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const int SW_SHOW = 5;
    private const int SW_SHOWNORMAL = 1;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;
    private const int SW_SHOWMAXIMIZED = 3;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONULL = 0x00000000;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public const int WM_HOTKEY = 0x0312;
    public const int ToggleHotkeyId = 0x4842;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48;

    private static readonly string[] TerminalProcessNames =
    {
        "WindowsTerminal",
        "cmd",
        "pwsh",
        "powershell",
        "conhost",
        "alacritty",
        "wezterm-gui",
        "mintty",
        "Hyper",
        "herdr"
    };

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// Returns the working areas of all active monitors in physical screen pixels.
    /// </summary>
    public static List<Rect> GetAllMonitorWorkAreasPixels()
    {
        var areas = new List<Rect>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                RECT w = info.rcWork;
                if (w.Right > w.Left && w.Bottom > w.Top)
                {
                    areas.Add(new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top));
                }
            }
            return true;
        }, IntPtr.Zero);

        return areas;
    }

    public static bool IsWindowTopmost(IntPtr hWnd) =>
        IsHandleValid(hWnd) && (GetWindowExStyle(hWnd) & WS_EX_TOPMOST) != 0;

    public static void EnsureTopmostNoActivate(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd))
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static Point GetCursorPosPixels()
    {
        return GetCursorPos(out POINT pt) ? new Point(pt.X, pt.Y) : new Point(0, 0);
    }

    public static Rect GetWindowRectPixels(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd) && GetWindowRect(hWnd, out RECT r))
        {
            return new Rect(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        }

        return Rect.Empty;
    }

    /// <summary>
    /// Returns the visually visible frame bounds (excluding the invisible resize border),
    /// falling back to the raw window rect when DWM does not report bounds.
    /// </summary>
    public static Rect GetVisibleWindowRectPixels(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return Rect.Empty;
        }

        try
        {
            int hr = DwmGetWindowAttributeRect(
                hWnd,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT r,
                Marshal.SizeOf<RECT>());

            if (hr == 0 && r.Right > r.Left && r.Bottom > r.Top)
            {
                return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        catch
        {
            // Fall through to the legacy window rect below.
        }

        return GetWindowRectPixels(hWnd);
    }

    /// <summary>
    /// Compares the raw window rect against the DWM visible frame and returns the
    /// invisible frame padding encoded as (insetLeft, insetTop, widthPad, heightPad).
    /// </summary>
    public static Rect GetWindowFrameInsetsPixels(IntPtr hWnd)
    {
        Rect raw = GetWindowRectPixels(hWnd);
        Rect visible = GetVisibleWindowRectPixels(hWnd);

        if (raw.IsEmpty || visible.IsEmpty || raw.Width <= 0 || raw.Height <= 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        double insetLeft = Math.Max(0, visible.Left - raw.Left);
        double insetTop = Math.Max(0, visible.Top - raw.Top);
        double widthPad = Math.Max(0, raw.Width - visible.Width);
        double heightPad = Math.Max(0, raw.Height - visible.Height);

        return new Rect(insetLeft, insetTop, widthPad, heightPad);
    }

    /// <summary>
    /// Expands a visible-frame rect back into a raw window rect using frame insets.
    /// </summary>
    public static Rect ApplyFrameInsets(Rect visibleRectPx, Rect frameInsets) =>
        new(
            visibleRectPx.Left - frameInsets.Left,
            visibleRectPx.Top - frameInsets.Top,
            visibleRectPx.Width + frameInsets.Width,
            visibleRectPx.Height + frameInsets.Height);

    /// <summary>
    /// Sets the normal (restored) position of a minimized window so it restores at the
    /// requested raw rect. Window placement coordinates are workspace-relative.
    /// </summary>
    public static void SetMinimizedNormalRect(IntPtr hWnd, Rect rawTargetRectPx)
    {
        if (!IsHandleValid(hWnd) || rawTargetRectPx.IsEmpty)
        {
            return;
        }

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref wp))
        {
            return;
        }

        (Rect primaryBounds, Rect primaryWork) = GetPrimaryMonitorBoundsAndWorkArea();
        double offsetX = primaryWork.Left - primaryBounds.Left;
        double offsetY = primaryWork.Top - primaryBounds.Top;

        wp.rcNormalPosition = new RECT
        {
            Left = (int)Math.Round(rawTargetRectPx.Left - offsetX),
            Top = (int)Math.Round(rawTargetRectPx.Top - offsetY),
            Right = (int)Math.Round(rawTargetRectPx.Right - offsetX),
            Bottom = (int)Math.Round(rawTargetRectPx.Bottom - offsetY)
        };

        SetWindowPlacement(hWnd, ref wp);
    }

    /// <summary>
    /// Ensures a maximized window ends up maximized on the requested monitor work area.
    /// </summary>
    public static void MoveMaximizedWindowToMonitor(IntPtr hWnd, Rect targetMonitorWorkAreaPx)
    {
        if (!IsHandleValid(hWnd) || targetMonitorWorkAreaPx.IsEmpty)
        {
            return;
        }

        Rect visible = GetVisibleWindowRectPixels(hWnd);
        if (!visible.IsEmpty)
        {
            Rect currentWorkArea = GetMonitorWorkAreaForPointPixels(
                visible.Left + visible.Width * 0.5,
                visible.Top + visible.Height * 0.5);

            if (RectsMatchWithin(currentWorkArea, targetMonitorWorkAreaPx, 2.0))
            {
                return;
            }
        }

        ShowWindow(hWnd, SW_SHOWNORMAL);
        MoveAndResizeWindowPixels(
            hWnd,
            (int)Math.Round(targetMonitorWorkAreaPx.Left),
            (int)Math.Round(targetMonitorWorkAreaPx.Top),
            (int)Math.Round(targetMonitorWorkAreaPx.Width),
            (int)Math.Round(targetMonitorWorkAreaPx.Height),
            topmost: false);
        ShowWindow(hWnd, SW_SHOWMAXIMIZED);
    }

    private static (Rect bounds, Rect work) GetPrimaryMonitorBoundsAndWorkArea()
    {
        Rect primaryWork = GetPrimaryMonitorWorkAreaPixels();
        Rect primaryBounds = primaryWork;

        IntPtr primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (primaryMonitor != IntPtr.Zero && GetMonitorInfo(primaryMonitor, ref info))
        {
            RECT m = info.rcMonitor;
            primaryBounds = new Rect(m.Left, m.Top, Math.Max(1, m.Right - m.Left), Math.Max(1, m.Bottom - m.Top));

            RECT w = info.rcWork;
            primaryWork = new Rect(w.Left, w.Top, Math.Max(1, w.Right - w.Left), Math.Max(1, w.Bottom - w.Top));
        }

        return (primaryBounds, primaryWork);
    }

    private static bool RectsMatchWithin(Rect a, Rect b, double tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance
        && Math.Abs(a.Top - b.Top) <= tolerance
        && Math.Abs(a.Width - b.Width) <= tolerance
        && Math.Abs(a.Height - b.Height) <= tolerance;

    public static bool IsRestoredAsMaximized(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return false;
        }

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref wp))
        {
            return false;
        }

        return wp.showCmd == SW_SHOWMAXIMIZED
            || (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;
    }

    /// <summary>
    /// Predicts where a minimized window will land when restored, in physical screen pixels.
    /// </summary>
    public static Rect GetPredictedWindowRectPixels(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return Rect.Empty;
        }

        if (!IsIconic(hWnd))
        {
            return GetVisibleWindowRectPixels(hWnd);
        }

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hWnd, ref wp))
        {
            return Rect.Empty;
        }

        Rect primaryWork = GetPrimaryMonitorWorkAreaPixels();
        Rect primaryBounds = primaryWork;

        IntPtr primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var primaryInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (primaryMonitor != IntPtr.Zero && GetMonitorInfo(primaryMonitor, ref primaryInfo))
        {
            RECT m = primaryInfo.rcMonitor;
            primaryBounds = new Rect(m.Left, m.Top, Math.Max(1, m.Right - m.Left), Math.Max(1, m.Bottom - m.Top));

            RECT w = primaryInfo.rcWork;
            primaryWork = new Rect(w.Left, w.Top, Math.Max(1, w.Right - w.Left), Math.Max(1, w.Bottom - w.Top));
        }

        double offsetX = primaryWork.Left - primaryBounds.Left;
        double offsetY = primaryWork.Top - primaryBounds.Top;

        var roughRect = new RECT
        {
            Left = (int)Math.Round(wp.rcNormalPosition.Left + offsetX),
            Top = (int)Math.Round(wp.rcNormalPosition.Top + offsetY),
            Right = (int)Math.Round(wp.rcNormalPosition.Right + offsetX),
            Bottom = (int)Math.Round(wp.rcNormalPosition.Bottom + offsetY)
        };

        Rect targetWorkArea = primaryWork;
        IntPtr targetMonitor = MonitorFromRect(ref roughRect, MONITOR_DEFAULTTONEAREST);
        var targetInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (targetMonitor != IntPtr.Zero && GetMonitorInfo(targetMonitor, ref targetInfo))
        {
            RECT w = targetInfo.rcWork;
            targetWorkArea = new Rect(w.Left, w.Top, Math.Max(1, w.Right - w.Left), Math.Max(1, w.Bottom - w.Top));
        }

        var normalPos = new Rect(
            wp.rcNormalPosition.Left,
            wp.rcNormalPosition.Top,
            wp.rcNormalPosition.Right - wp.rcNormalPosition.Left,
            wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top);

        bool restoreToMaximized = wp.showCmd == SW_SHOWMAXIMIZED
            || (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;

        return WindowTransitionGeometry.ComputePredictedRestoreRect(
            normalPos,
            restoreToMaximized,
            primaryBounds,
            primaryWork,
            targetWorkArea);
    }

    public static void PlaceWindowDirectlyBelow(IntPtr hWnd, IntPtr insertAfterHandle)
    {
        if (IsHandleValid(hWnd) && IsHandleValid(insertAfterHandle))
        {
            SetWindowPos(
                hWnd,
                insertAfterHandle,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static void MoveAndResizeWindowPixels(IntPtr hWnd, int x, int y, int width, int height, bool topmost = true)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        SetWindowPos(
            hWnd,
            topmost ? HWND_TOPMOST : IntPtr.Zero,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            SWP_NOACTIVATE | (topmost ? 0u : SWP_NOZORDER));
    }

    public static void ApplyOverlayWindowStyles(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        long exStyle = GetWindowExStyle(hWnd);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_LAYERED;

        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }
        else
        {
            SetWindowLong32(hWnd, GWL_EXSTYLE, unchecked((int)exStyle));
        }
    }

    public static void SetWindowAlpha(IntPtr hWnd, double opacity)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        double clamped = Math.Clamp(opacity, 0.0, 1.0);
        byte alpha = (byte)Math.Round(clamped * 255.0);
        SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);
    }

    public static void TrySetRoundedCorners(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // DWM may be unavailable or the attribute unsupported; ignore.
        }
    }

    public static void TrySetTransitionsDisabled(IntPtr hWnd, bool disabled)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        try
        {
            int value = disabled ? 1 : 0;
            DwmSetWindowAttribute(hWnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref value, sizeof(int));
        }
        catch
        {
            // DWM may be unavailable or the attribute unsupported; ignore.
        }
    }

    public static void MoveWindowPixels(IntPtr hWnd, int x, int y)
    {
        if (IsHandleValid(hWnd))
        {
            SetWindowPos(hWnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public static Rect GetPrimaryMonitorWorkAreaPixels()
    {
        IntPtr hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info))
        {
            RECT w = info.rcWork;
            return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        return SystemParameters.WorkArea;
    }

    public static Rect GetMonitorWorkAreaForPointPixels(double x, double y)
    {
        var pt = new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info))
        {
            RECT w = info.rcWork;
            return new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }

        return GetPrimaryMonitorWorkAreaPixels();
    }

    public static bool IsPointOnAnyMonitor(double x, double y)
    {
        var pt = new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    public static bool RegisterGlobalToggleHotkey(IntPtr hWnd)
    {
        return IsHandleValid(hWnd) && RegisterHotKey(hWnd, ToggleHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_H);
    }

    public static void UnregisterGlobalToggleHotkey(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd))
        {
            UnregisterHotKey(hWnd, ToggleHotkeyId);
        }
    }

    public static List<TerminalWindowInfo> FindTerminalWindows(string? targetKeyword = null)
    {
        var results = new List<TerminalWindowInfo>();
        int selfPid = Environment.ProcessId;
        string keyword = string.IsNullOrWhiteSpace(targetKeyword) ? "herdr" : targetKeyword.Trim();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsWindowCloaked(hWnd) || IsToolWindow(hWnd))
            {
                return true;
            }

            if (!IsIconic(hWnd) && GetWindowRect(hWnd, out RECT rect))
            {
                if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
                {
                    return true;
                }
            }

            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return true;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == selfPid)
            {
                return true;
            }

            string procName = TryGetProcessName(pid);

            bool matchesProcess = TerminalProcessNames.Any(p =>
                string.Equals(p, procName, StringComparison.OrdinalIgnoreCase));
            bool matchesKeywordProcess = string.Equals(procName, keyword, StringComparison.OrdinalIgnoreCase);

            if (!matchesProcess && !matchesKeywordProcess)
            {
                return true;
            }

            bool isTargetMatch = matchesKeywordProcess
                || title.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            results.Add(new TerminalWindowInfo(hWnd, title, procName, IsIconic(hWnd), isTargetMatch));

            return true;
        }, IntPtr.Zero);

        return results
            .OrderByDescending(w => w.IsHerdr)
            .ThenByDescending(w => string.Equals(w.ProcessName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => string.Equals(w.ProcessName, "cmd", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static bool IsHandleValid(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd);

    public static bool IsMinimized(IntPtr hWnd) => IsHandleValid(hWnd) && IsIconic(hWnd);

    public static bool IsForeground(IntPtr hWnd) => IsHandleValid(hWnd) && GetForegroundWindow() == hWnd;

    public static IntPtr GetForegroundHandle() => GetForegroundWindow();

    public static void MinimizeWindow(IntPtr hWnd)
    {
        if (IsHandleValid(hWnd))
        {
            ShowWindow(hWnd, SW_MINIMIZE);
        }
    }

    public static void BringToFront(IntPtr hWnd)
    {
        if (!IsHandleValid(hWnd))
        {
            return;
        }

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hWnd, SW_SHOW);
        }

        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;

        bool attachedForeground = false;
        bool attachedTarget = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            }
            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            }

            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
            if (attachedForeground)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    public static void LaunchHerdrTerminal(string? launchCommand = null, string? windowTitle = null)
    {
        string cmd = string.IsNullOrWhiteSpace(launchCommand) ? "herdr" : launchCommand.Trim();
        string title = string.IsNullOrWhiteSpace(windowTitle) ? "Herdr" : windowTitle.Trim();

        if (ExistsOnPath("wt.exe"))
        {
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", $"--title \"{title}\" cmd /k {cmd}") { UseShellExecute = true });
                return;
            }
            catch
            {
                // Fall back to cmd.exe when Windows Terminal alias fails to start.
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k title {title} & {cmd}") { UseShellExecute = true });
        }
        catch
        {
            // Avoid crashing the floating bubble if terminal launch fails.
        }
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try
        {
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsToolWindow(IntPtr hWnd) =>
        (GetWindowExStyle(hWnd) & WS_EX_TOOLWINDOW) != 0;

    private static long GetWindowExStyle(IntPtr hWnd) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_EXSTYLE);

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ExistsOnPath(string fileName)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return false;
        }

        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            string full = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(full))
            {
                return true;
            }
        }

        return false;
    }
}
