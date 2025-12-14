using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowController.App.Services;

public static class WindowPicker
{
    private const int VK_LBUTTON = 0x01;

    private const uint GA_ROOT = 2;

    public static bool TryPickWindow(out PickedWindow? picked)
    {
        picked = null;
        // Capture the current cursor position and window under cursor
        if (!GetCursorPos(out var pt)) return false;
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        // For child controls, get the top-level window
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        // Get window rect
        if (!GetWindowRect(hwnd, out var rect)) return false;

        // Get process name and title
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        string processName = string.Empty;
        try
        {
            var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch
        {
        }

        var title = GetWindowText(hwnd);

        picked = new PickedWindow
        {
            Handle = hwnd,
            ProcessName = processName,
            Title = title,
            X = rect.Left,
            Y = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top
        };
        return true;
    }

    public static async Task<PickedWindow?> PickOnNextClickAsync(TimeSpan timeout, int ownProcessId)
    {
        var start = DateTime.UtcNow;
        var wasDown = false;
        while ((DateTime.UtcNow - start) < timeout)
        {
            var state = GetAsyncKeyState(VK_LBUTTON);
            var isDown = (state & 0x8000) != 0;
            if (isDown && !wasDown)
            {
                // on edge from up->down, capture
                if (GetCursorPos(out var pt))
                {
                    var hwnd = WindowFromPoint(pt);
                    if (hwnd != IntPtr.Zero)
                    {
                        var root = GetAncestor(hwnd, GA_ROOT);
                        if (root != IntPtr.Zero) hwnd = root;
                        _ = GetWindowThreadProcessId(hwnd, out var pid);
                        if (pid != (uint)ownProcessId)
                        {
                            if (GetWindowRect(hwnd, out var rect))
                            {
                                var processName = string.Empty;
                                try
                                {
                                    processName = Process.GetProcessById((int)pid).ProcessName;
                                }
                                catch
                                {
                                }

                                var title = GetWindowText(hwnd);
                                return new PickedWindow
                                {
                                    Handle = hwnd,
                                    ProcessName = processName,
                                    Title = title,
                                    X = rect.Left,
                                    Y = rect.Top,
                                    Width = rect.Right - rect.Left,
                                    Height = rect.Bottom - rect.Top
                                };
                            }
                        }
                    }
                }
            }

            wasDown = isDown;
            await Task.Delay(25).ConfigureAwait(false);
        }

        return null;
    }

    private static string GetWindowText(nint hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var buffer = new StringBuilder(len + 1);
        _ = GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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
}

public sealed class PickedWindow
{
    public nint Handle { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}