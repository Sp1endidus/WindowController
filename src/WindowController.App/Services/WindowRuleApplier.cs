using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowController.App.Services;

public sealed class WindowRuleApplier
{
    private const int SW_RESTORE = 9;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint WM_SIZE = 0x0005;
    private const uint SIZE_RESTORED = 0;
    private const int SW_SHOWNORMAL = 1;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SIZEBOX = 0x00040000;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;

    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);

    public void Apply(nint hWnd, WindowControlService.WindowRule rule)
    {
        Debug.WriteLine(
            $"ApplyRuleToWindow called for {rule.ProcessName}: X={rule.X}, Y={rule.Y}, Width={rule.Width}, Height={rule.Height}");

        if (GetWindowRect(hWnd, out var rectBefore))
        {
            Debug.WriteLine(
                $"Window rect BEFORE: Left={rectBefore.Left}, Top={rectBefore.Top}, Right={rectBefore.Right}, Bottom={rectBefore.Bottom}, Width={rectBefore.Right - rectBefore.Left}, Height={rectBefore.Bottom - rectBefore.Top}");
        }

        var currentPlacement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hWnd, ref currentPlacement))
        {
            var normalWidth = currentPlacement.rcNormalPosition.Right - currentPlacement.rcNormalPosition.Left;
            var normalHeight = currentPlacement.rcNormalPosition.Bottom - currentPlacement.rcNormalPosition.Top;
            Debug.WriteLine(
                $"Current window placement: showCmd={currentPlacement.showCmd}, NormalSize={normalWidth}x{normalHeight}, NormalPos=({currentPlacement.rcNormalPosition.Left},{currentPlacement.rcNormalPosition.Top})");
        }

        var styleBefore = GetWindowLong(hWnd, GWL_STYLE);
        var exStyleBefore = GetWindowLong(hWnd, GWL_EXSTYLE);
        Debug.WriteLine($"Window style BEFORE: Style=0x{styleBefore:X8}, ExStyle=0x{exStyleBefore:X8}");
        Debug.WriteLine(
            $"  WS_THICKFRAME: {(styleBefore & WS_THICKFRAME) != 0}, WS_SIZEBOX: {(styleBefore & WS_SIZEBOX) != 0}, WS_MAXIMIZEBOX: {(styleBefore & WS_MAXIMIZEBOX) != 0}, WS_MINIMIZEBOX: {(styleBefore & WS_MINIMIZEBOX) != 0}");

        var needsStyleUpdate = false;
        var newStyle = styleBefore;

        if ((styleBefore & WS_THICKFRAME) == 0)
        {
            Debug.WriteLine("Window missing WS_THICKFRAME! Adding it...");
            newStyle |= WS_THICKFRAME;
            needsStyleUpdate = true;
        }

        if ((styleBefore & WS_MAXIMIZEBOX) == 0)
        {
            Debug.WriteLine("Window missing WS_MAXIMIZEBOX! Adding it (may help with size constraints)...");
            newStyle |= WS_MAXIMIZEBOX;
            needsStyleUpdate = true;
        }

        if (needsStyleUpdate)
        {
            Debug.WriteLine($"Applying style changes BEFORE resize: 0x{styleBefore:X8} -> 0x{newStyle:X8}");
            SetWindowLong(hWnd, GWL_STYLE, newStyle);
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            Thread.Sleep(100);
        }

        var targetPlacement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            flags = 0,
            showCmd = SW_SHOWNORMAL,
            rcNormalPosition = new RECT
            {
                Left = rule.X,
                Top = rule.Y,
                Right = rule.X + rule.Width,
                Bottom = rule.Y + rule.Height
            }
        };

        var placementResult = SetWindowPlacement(hWnd, ref targetPlacement);
        Debug.WriteLine(
            $"SetWindowPlacement (BEFORE restore) result: {placementResult}, Applied: X={rule.X}, Y={rule.Y}, Width={rule.Width}, Height={rule.Height}");
        Thread.Sleep(100);

        var restoreResult = ShowWindow(hWnd, SW_RESTORE);
        Debug.WriteLine($"ShowWindow(SW_RESTORE) result: {restoreResult}");
        Thread.Sleep(100);

        var styleAfterRestore = GetWindowLong(hWnd, GWL_STYLE);
        Debug.WriteLine($"Window style AFTER RESTORE: Style=0x{styleAfterRestore:X8}");
        Debug.WriteLine(
            $"  WS_THICKFRAME: {(styleAfterRestore & WS_THICKFRAME) != 0}, WS_SIZEBOX: {(styleAfterRestore & WS_SIZEBOX) != 0}, WS_MAXIMIZEBOX: {(styleAfterRestore & WS_MAXIMIZEBOX) != 0}");

        if ((styleAfterRestore & WS_THICKFRAME) == 0 || (styleAfterRestore & WS_MAXIMIZEBOX) == 0)
        {
            Debug.WriteLine("Window flags were removed after restore! Re-adding them...");
            var restoredStyle = styleAfterRestore;
            if ((styleAfterRestore & WS_THICKFRAME) == 0) restoredStyle |= WS_THICKFRAME;
            if ((styleAfterRestore & WS_MAXIMIZEBOX) == 0) restoredStyle |= WS_MAXIMIZEBOX;
            SetWindowLong(hWnd, GWL_STYLE, restoredStyle);
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            Thread.Sleep(100);
        }

        var placementAfter = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hWnd, ref placementAfter))
        {
            var afterWidth = placementAfter.rcNormalPosition.Right - placementAfter.rcNormalPosition.Left;
            var afterHeight = placementAfter.rcNormalPosition.Bottom - placementAfter.rcNormalPosition.Top;
            Debug.WriteLine($"Window placement AFTER RESTORE: NormalSize={afterWidth}x{afterHeight}");
            if (afterWidth != rule.Width || afterHeight != rule.Height)
            {
                Debug.WriteLine(
                    $"WARNING: App changed placement! Expected {rule.Width}x{rule.Height}, got {afterWidth}x{afterHeight}");
                SetWindowPlacement(hWnd, ref targetPlacement);
                Thread.Sleep(100);
            }
        }

        var setPosResult = SetWindowPos(hWnd, IntPtr.Zero, rule.X, rule.Y, rule.Width, rule.Height,
            SWP_SHOWWINDOW | SWP_NOZORDER);
        Debug.WriteLine($"SetWindowPos (1st attempt) result: {setPosResult}");

        Thread.Sleep(50);

        var moveResult = MoveWindow(hWnd, rule.X, rule.Y, rule.Width, rule.Height, true);
        Debug.WriteLine($"MoveWindow (1st attempt) result: {moveResult}");

        Thread.Sleep(100);

        var retryCount = 0;
        const int maxRetries = 5;
        while (retryCount < maxRetries)
        {
            if (!GetWindowRect(hWnd, out var rectCurrent))
                break;

            var actualWidth = rectCurrent.Right - rectCurrent.Left;
            var actualHeight = rectCurrent.Bottom - rectCurrent.Top;

            if (retryCount == 0)
            {
                Debug.WriteLine($"Window rect AFTER initial attempt: Width={actualWidth}, Height={actualHeight}");
            }

            if (Math.Abs(actualWidth - rule.Width) <= 1 && Math.Abs(actualHeight - rule.Height) <= 1)
            {
                Debug.WriteLine($"Size matches! Width={actualWidth}, Height={actualHeight}");
                break;
            }

            retryCount++;
            Debug.WriteLine(
                $"Size mismatch (attempt {retryCount}/{maxRetries})! Expected: {rule.Width}x{rule.Height}, Actual: {actualWidth}x{actualHeight}. Retrying...");

            SetWindowPos(hWnd, IntPtr.Zero, rule.X, rule.Y, rule.Width, rule.Height,
                SWP_SHOWWINDOW | SWP_NOZORDER);

            Thread.Sleep(50);

            MoveWindow(hWnd, rule.X, rule.Y, rule.Width, rule.Height, true);

            var lParam = (nint)((rule.Height << 16) | (rule.Width & 0xFFFF));
            SendMessage(hWnd, WM_SIZE, (nint)SIZE_RESTORED, lParam);

            Thread.Sleep(100 + (retryCount * 50));
        }

        if (GetWindowRect(hWnd, out var rectFinal))
        {
            var finalWidth = rectFinal.Right - rectFinal.Left;
            var finalHeight = rectFinal.Bottom - rectFinal.Top;
            Debug.WriteLine($"Window rect FINAL: Width={finalWidth}, Height={finalHeight}");

            if (Math.Abs(finalWidth - rule.Width) > 1 || Math.Abs(finalHeight - rule.Height) > 1)
            {
                Debug.WriteLine(
                    $"WARNING: Final size still doesn't match! Expected: {rule.Width}x{rule.Height}, Got: {finalWidth}x{finalHeight}");
            }
        }

        ApplyTopMostOnly(hWnd, rule);
    }

    public void ApplyTopMostOnly(nint hWnd, WindowControlService.WindowRule rule)
    {
        if (!rule.OnTop.HasValue)
            return;

        var result = SetWindowPos(hWnd, rule.OnTop.Value ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Debug.WriteLine($"SetWindowPos for {rule.ProcessName}: OnTop={rule.OnTop.Value}, Result={result}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

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
