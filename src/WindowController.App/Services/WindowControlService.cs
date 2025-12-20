using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowController.App.Services;

public sealed class WindowControlService : IDisposable
{
    private const int SW_RESTORE = 9;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint WM_SIZE = 0x0005;
    private const uint WM_MOVE = 0x0003;
    private const uint SIZE_RESTORED = 0;
    private const int SW_SHOWNORMAL = 1;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    // Window style flags
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SIZEBOX = 0x00040000; // Same as WS_THICKFRAME
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_OVERLAPPED = 0x00000000;

    private const int WS_OVERLAPPEDWINDOW =
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    private const int WS_CAPTION = 0x00C00000;
    private const int WS_BORDER = 0x00800000;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_CHILD = 0x40000000;

    private const uint GW_OWNER = 4;
    private static readonly nint HWND_TOPMOST = new nint(-1);
    private static readonly nint HWND_NOTOPMOST = new nint(-2);
    private readonly object _configLock = new();
    private readonly Dictionary<int, DateTime> _firstSeenByPid = new();
    private readonly Dictionary<int, bool> _lastMinimizedByPid = new();
    private readonly Dictionary<int, bool> _lastVisibilityByPid = new();
    private readonly Dictionary<int, nint> _lastWindowHandleByPid = new();
    private WindowRulesConfig _config;
    private CancellationTokenSource? _cts;

    public WindowControlService(string configPath)
    {
        ConfigPath = configPath;
        _config = LoadConfigInternal(configPath);
    }

    public string ConfigPath { get; }

    public void Dispose()
    {
        Stop();
    }

    public WindowRulesConfig GetConfig()
    {
        lock (_configLock)
        {
            return _config;
        }
    }

    public void Reload()
    {
        var cfg = LoadConfigInternal(ConfigPath);
        lock (_configLock)
        {
            _config = cfg;
        }
    }

    public void Save(WindowRulesConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, json);
        lock (_configLock)
        {
            _config = config;
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => MonitorLoopAsync(token), token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void ApplyStartupRulesOnce()
    {
        WindowRulesConfig snapshot;
        lock (_configLock)
        {
            snapshot = _config;
        }

        foreach (var rule in snapshot.Rules.Where(r => r.Enabled && r.ApplyOnStartup))
        {
            try
            {
                var processes = Process.GetProcessesByName(rule.ProcessName);
                foreach (var process in processes)
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    var title = process.MainWindowTitle ?? string.Empty;

                    // Check exclusion filter
                    if (!string.IsNullOrWhiteSpace(rule.ExcludeTitleContains))
                    {
                        if (title.Contains(rule.ExcludeTitleContains, StringComparison.InvariantCultureIgnoreCase))
                            continue;
                    }

                    // Check inclusion filter
                    if (!string.IsNullOrWhiteSpace(rule.MatchTitleContains))
                    {
                        if (!title.Contains(rule.MatchTitleContains, StringComparison.InvariantCultureIgnoreCase))
                            continue;
                    }

                    ApplyRuleToWindow(process.MainWindowHandle, rule);
                }
            }
            catch
            {
            }
        }
    }

    public void ApplyAllRulesOnce()
    {
        WindowRulesConfig snapshot;
        lock (_configLock)
        {
            snapshot = _config;
        }

        foreach (var rule in snapshot.Rules.Where(r => r.Enabled))
        {
            try
            {
                var processes = Process.GetProcessesByName(rule.ProcessName);
                foreach (var process in processes)
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    var title = process.MainWindowTitle;

                    // Check exclusion filter
                    if (!string.IsNullOrWhiteSpace(rule.ExcludeTitleContains))
                    {
                        if (rule.ExcludeTitles.Contains(title))
                            continue;
                    }

                    // Check inclusion filter
                    if (!string.IsNullOrWhiteSpace(rule.MatchTitleContains))
                    {
                        if (!title.Contains(rule.MatchTitleContains, StringComparison.InvariantCultureIgnoreCase))
                            continue;
                    }

                    ApplyRuleToWindow(process.MainWindowHandle, rule);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine($"rule {rule?.ProcessName} error: {e}");
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        var appliedForPid = new HashSet<int>();
        var previousIterationPids = new HashSet<int>();
        while (!token.IsCancellationRequested)
        {
            WindowRulesConfig snapshot;
            lock (_configLock)
            {
                snapshot = _config;
            }

            var currentIterationPids = new HashSet<int>();

            foreach (var rule in snapshot.Rules.Where(r => r.Enabled))
            {
                try
                {
                    var processes = Process.GetProcessesByName(rule.ProcessName);
                    foreach (var process in processes)
                    {
                        currentIterationPids.Add(process.Id);

                        if (process.MainWindowHandle == IntPtr.Zero)
                        {
                            // Window handle is zero - track this state
                            _lastWindowHandleByPid[process.Id] = IntPtr.Zero;
                            continue;
                        }

                        // Skip popup windows and context menus - check this BEFORE any other processing
                        // This prevents context menus from being resized
                        if (IsPopupWindow(process.MainWindowHandle))
                        {
                            Debug.WriteLine($"Skipping popup window for {process.ProcessName} (PID {process.Id})");
                            // Don't update tracking for popup windows - keep the last valid main window handle
                            continue;
                        }

                        // Check if window handle reappeared (was zero, now non-zero) or changed (different handle for same PID)
                        var lastHandle = _lastWindowHandleByPid.TryGetValue(process.Id, out var h) ? h : IntPtr.Zero;
                        var handleReappeared = lastHandle == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero;
                        // Only treat as handle change if the new handle is not a popup (already checked above)
                        var handleChanged = lastHandle != IntPtr.Zero && lastHandle != process.MainWindowHandle &&
                                            process.MainWindowHandle != IntPtr.Zero;

                        // Check if process reappeared (wasn't in previous iteration, now is)
                        var processReappeared = !previousIterationPids.Contains(process.Id);

                        // Get window title for matching and validation
                        var title = process.MainWindowTitle;

                        // Skip windows with empty or very short titles (likely popups/context menus)
                        // Main application windows typically have meaningful titles
                        if (string.IsNullOrWhiteSpace(title) || title.Length < 3)
                        {
                            Debug.WriteLine(
                                $"Skipping window with empty/short title for {process.ProcessName} (PID {process.Id}): '{title}'");
                            continue;
                        }

                        // Check exclusion filter first - if title matches exclusion, skip this window
                        if (!string.IsNullOrWhiteSpace(rule.ExcludeTitleContains))
                        {
                            if (rule.ExcludeTitles.Contains(title))
                            {
                                Debug.WriteLine(
                                    $"Skipping window matching exclusion pattern for {process.ProcessName} (PID {process.Id}): '{title}' matches '{rule.ExcludeTitleContains}'");
                                continue;
                            }
                        }

                        // Check inclusion filter - if specified, only process windows that match
                        if (!string.IsNullOrWhiteSpace(rule.MatchTitleContains))
                        {
                            if (!title.Contains(rule.MatchTitleContains, StringComparison.InvariantCultureIgnoreCase))
                                continue;
                        }

                        // Check if window is visible (not minimized/hidden)
                        var isMinimized = IsIconic(process.MainWindowHandle);
                        var isVisible = IsWindowVisible(process.MainWindowHandle) && !isMinimized;

                        // Check if window was minimized before and is now restored
                        var wasMinimized = _lastMinimizedByPid.GetValueOrDefault(process.Id, false);
                        var wasRestored = wasMinimized && !isMinimized;

                        // Check if window is being minimized (transitioning from visible to minimized)
                        var wasVisible = _lastVisibilityByPid.GetValueOrDefault(process.Id, false);
                        var isBeingMinimized = wasVisible && !isVisible && isMinimized;

                        // Check if visibility changed from hidden to visible
                        // Default to false for first-time detection (assume window wasn't visible before we started tracking)
                        var becameVisible = !wasVisible && isVisible;

                        // Window became visible if process reappeared, handle reappeared, handle changed, was restored from minimized state, OR if visibility changed
                        // Note: Only treat processReappeared as restore if we had previously tracked this PID (to avoid treating first-time detection as restore)
                        var wasPreviouslyTracked = _lastWindowHandleByPid.ContainsKey(process.Id);
                        var shouldApply = (processReappeared && wasPreviouslyTracked) || handleReappeared ||
                                          handleChanged || becameVisible || wasRestored;

                        Debug.WriteLine(
                            $"Process {process.ProcessName}: processReappeared={processReappeared}, wasPreviouslyTracked={wasPreviouslyTracked}, handleReappeared={handleReappeared}, handleChanged={handleChanged}, isVisible={isVisible}, isMinimized={isMinimized}, wasVisible={wasVisible}, wasMinimized={wasMinimized}, becameVisible={becameVisible}, wasRestored={wasRestored}, isBeingMinimized={isBeingMinimized}, shouldApply={shouldApply}");

                        // If window became visible again or was restored, remove from appliedForPid so we can reapply rules
                        if (shouldApply)
                        {
                            appliedForPid.Remove(process.Id);
                        }

                        // Skip if ApplyOnLaunchOnly is enabled and we've already applied to this PID (unless it just became visible or was restored)
                        if (snapshot.ApplyOnLaunchOnly && appliedForPid.Contains(process.Id) && !shouldApply)
                            continue;

                        // Enforce only for the first ControlDurationMs since first detection OR when simulating launch
                        // Also reset timer if window becomes visible after being hidden or is restored
                        if (!_firstSeenByPid.TryGetValue(process.Id, out var first) || shouldApply)
                        {
                            first = DateTime.UtcNow;
                            _firstSeenByPid[process.Id] = first;
                            Debug.WriteLine(
                                $"Reset timer for {process.ProcessName} - first time, became visible, or was restored");
                        }

                        // Update visibility, minimized state, and window handle tracking
                        _lastVisibilityByPid[process.Id] = isVisible;
                        _lastMinimizedByPid[process.Id] = isMinimized;
                        _lastWindowHandleByPid[process.Id] = process.MainWindowHandle;

                        var elapsed = DateTime.UtcNow - first;

                        // Check if window size is wrong and needs correction
                        var sizeNeedsCorrection = false;
                        if (isVisible && GetWindowRect(process.MainWindowHandle, out var currentRect))
                        {
                            var currentWidth = currentRect.Right - currentRect.Left;
                            var currentHeight = currentRect.Bottom - currentRect.Top;
                            // Check if size doesn't match (allow 1 pixel tolerance)
                            if (Math.Abs(currentWidth - rule.Width) > 1 || Math.Abs(currentHeight - rule.Height) > 1)
                            {
                                sizeNeedsCorrection = true;
                                Debug.WriteLine(
                                    $"Window size mismatch detected for {process.ProcessName}: Expected {rule.Width}x{rule.Height}, Actual {currentWidth}x{currentHeight}");
                            }
                        }

                        // Apply rule if:
                        // 1. Within the control duration window OR window just became visible/restored, OR
                        // 2. Window size is wrong and needs correction (continuous enforcement)
                        // BUT: Don't apply if window is currently minimized (unless it was just restored)
                        // AND: Don't apply if window is being minimized (user is actively minimizing it)
                        var shouldApplyNow =
                            ((elapsed.TotalMilliseconds <= snapshot.ControlDurationMs || shouldApply) ||
                             sizeNeedsCorrection) &&
                            (isVisible || wasRestored || handleReappeared || handleChanged) &&
                            !isBeingMinimized;

                        if (shouldApplyNow)
                        {
                            Debug.WriteLine(
                                $"Applying rule to {process.ProcessName} (PID {process.Id}): elapsed={elapsed.TotalMilliseconds}ms, shouldApply={shouldApply}, sizeNeedsCorrection={sizeNeedsCorrection}, isVisible={isVisible}, wasRestored={wasRestored}, handleReappeared={handleReappeared}, handleChanged={handleChanged}, isBeingMinimized={isBeingMinimized}, ControlDuration={snapshot.ControlDurationMs}ms");
                            // If handle just reappeared or changed, apply immediately with minimal delay to beat the app's restore
                            if (handleReappeared || handleChanged)
                            {
                                Debug.WriteLine(
                                    "Handle reappeared/changed - applying rule IMMEDIATELY to prevent app from restoring its own size");
                                // Apply on a separate thread with minimal delay to be as fast as possible
                                _ = Task.Run(() =>
                                {
                                    Thread.Sleep(10); // Minimal delay
                                    ApplyRuleToWindow(process.MainWindowHandle, rule);
                                    // Apply again after a short delay to ensure it sticks
                                    Thread.Sleep(200);
                                    ApplyRuleToWindow(process.MainWindowHandle, rule);
                                    // Apply one more time after a longer delay to catch any late restoration
                                    Thread.Sleep(500);
                                    ApplyRuleToWindow(process.MainWindowHandle, rule);
                                }, token);
                            }
                            else
                            {
                                ApplyRuleToWindow(process.MainWindowHandle, rule);
                            }
                        }
                        else if (rule.OnTop.HasValue && isVisible)
                        {
                            // Always apply OnTop rules, even outside the 5-second window
                            // This ensures OnTop settings persist when windows are shown again
                            Debug.WriteLine(
                                $"Applying OnTop outside timer for {process.ProcessName}: {rule.OnTop.Value}");
                            var result = SetWindowPos(process.MainWindowHandle,
                                rule.OnTop.Value ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                            Debug.WriteLine($"SetWindowPos OnTop for {process.ProcessName}: Result={result}");
                        }

                        appliedForPid.Add(process.Id);
                    }
                }
                catch
                {
                    // ignore per-process errors
                }
            }

            // Update previous iteration PIDs for next iteration
            previousIterationPids = currentIterationPids;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(250, snapshot.PollIntervalMs)), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public void SimulateAppJustOpenedForAll()
    {
        // Reset first-seen timestamps so next monitor cycles treat all as newly opened
        _firstSeenByPid.Clear();
        _lastVisibilityByPid.Clear();
        _lastMinimizedByPid.Clear();
        _lastWindowHandleByPid.Clear();
    }

    private static void ApplyRuleToWindow(nint hWnd, WindowRule rule)
    {
        Debug.WriteLine(
            $"ApplyRuleToWindow called for {rule.ProcessName}: X={rule.X}, Y={rule.Y}, Width={rule.Width}, Height={rule.Height}");

        // Get current window rect before applying
        if (GetWindowRect(hWnd, out var rectBefore))
        {
            Debug.WriteLine(
                $"Window rect BEFORE: Left={rectBefore.Left}, Top={rectBefore.Top}, Right={rectBefore.Right}, Bottom={rectBefore.Bottom}, Width={rectBefore.Right - rectBefore.Left}, Height={rectBefore.Bottom - rectBefore.Top}");
        }

        // Get and log current window placement to see what the app thinks the normal size should be
        var currentPlacement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hWnd, ref currentPlacement))
        {
            var normalWidth = currentPlacement.rcNormalPosition.Right - currentPlacement.rcNormalPosition.Left;
            var normalHeight = currentPlacement.rcNormalPosition.Bottom - currentPlacement.rcNormalPosition.Top;
            Debug.WriteLine(
                $"Current window placement: showCmd={currentPlacement.showCmd}, NormalSize={normalWidth}x{normalHeight}, NormalPos=({currentPlacement.rcNormalPosition.Left},{currentPlacement.rcNormalPosition.Top})");
        }

        // Get and log window style flags
        var styleBefore = GetWindowLong(hWnd, GWL_STYLE);
        var exStyleBefore = GetWindowLong(hWnd, GWL_EXSTYLE);
        Debug.WriteLine($"Window style BEFORE: Style=0x{styleBefore:X8}, ExStyle=0x{exStyleBefore:X8}");
        Debug.WriteLine(
            $"  WS_THICKFRAME: {(styleBefore & WS_THICKFRAME) != 0}, WS_SIZEBOX: {(styleBefore & WS_SIZEBOX) != 0}, WS_MAXIMIZEBOX: {(styleBefore & WS_MAXIMIZEBOX) != 0}, WS_MINIMIZEBOX: {(styleBefore & WS_MINIMIZEBOX) != 0}");

        // CRITICAL: Set window flags BEFORE trying to resize
        // The app may be enforcing size constraints based on window style flags
        // We need to ensure the window has all necessary flags for unrestricted resizing
        var needsStyleUpdate = false;
        var newStyle = styleBefore;

        // Ensure WS_THICKFRAME (required for resizing)
        if ((styleBefore & WS_THICKFRAME) == 0)
        {
            Debug.WriteLine("Window missing WS_THICKFRAME! Adding it...");
            newStyle |= WS_THICKFRAME;
            needsStyleUpdate = true;
        }

        // Ensure WS_MAXIMIZEBOX (may be related to size constraints)
        if ((styleBefore & WS_MAXIMIZEBOX) == 0)
        {
            Debug.WriteLine("Window missing WS_MAXIMIZEBOX! Adding it (may help with size constraints)...");
            newStyle |= WS_MAXIMIZEBOX;
            needsStyleUpdate = true;
        }

        // Apply style changes BEFORE any window operations
        if (needsStyleUpdate)
        {
            Debug.WriteLine($"Applying style changes BEFORE resize: 0x{styleBefore:X8} -> 0x{newStyle:X8}");
            SetWindowLong(hWnd, GWL_STYLE, newStyle);
            // Force window to recalculate its non-client area immediately
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            Thread.Sleep(100); // Give window time to process style change
        }

        // Set the placement FIRST, before restoring the window
        // This way the app will restore to our desired size instead of its stored size
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

        // Set placement BEFORE restoring - this is critical!
        var placementResult = SetWindowPlacement(hWnd, ref targetPlacement);
        Debug.WriteLine(
            $"SetWindowPlacement (BEFORE restore) result: {placementResult}, Applied: X={rule.X}, Y={rule.Y}, Width={rule.Width}, Height={rule.Height}");
        Thread.Sleep(100);

        // Now restore the window - it should use our placement
        var restoreResult = ShowWindow(hWnd, SW_RESTORE);
        Debug.WriteLine($"ShowWindow(SW_RESTORE) result: {restoreResult}");
        Thread.Sleep(100);

        // Verify style after restore
        var styleAfterRestore = GetWindowLong(hWnd, GWL_STYLE);
        Debug.WriteLine($"Window style AFTER RESTORE: Style=0x{styleAfterRestore:X8}");
        Debug.WriteLine(
            $"  WS_THICKFRAME: {(styleAfterRestore & WS_THICKFRAME) != 0}, WS_SIZEBOX: {(styleAfterRestore & WS_SIZEBOX) != 0}, WS_MAXIMIZEBOX: {(styleAfterRestore & WS_MAXIMIZEBOX) != 0}");

        // If flags were removed, restore them immediately
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

        // Check placement after restore
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
                // Try setting placement one more time after restore
                SetWindowPlacement(hWnd, ref targetPlacement);
                Thread.Sleep(100);
            }
        }

        // Try multiple approaches to ensure the size is set correctly
        // Some applications resist resizing, so we need to be persistent

        // Approach 1: SetWindowPos
        var setPosResult = SetWindowPos(hWnd, IntPtr.Zero, rule.X, rule.Y, rule.Width, rule.Height,
            SWP_SHOWWINDOW | SWP_NOZORDER);
        Debug.WriteLine($"SetWindowPos (1st attempt) result: {setPosResult}");

        Thread.Sleep(50);

        // Approach 2: MoveWindow
        var moveResult = MoveWindow(hWnd, rule.X, rule.Y, rule.Width, rule.Height, true);
        Debug.WriteLine($"MoveWindow (1st attempt) result: {moveResult}");

        Thread.Sleep(100);

        // Verify and retry if needed
        var retryCount = 0;
        const int maxRetries = 5;
        while (retryCount < maxRetries)
        {
            if (GetWindowRect(hWnd, out var rectCurrent))
            {
                var actualWidth = rectCurrent.Right - rectCurrent.Left;
                var actualHeight = rectCurrent.Bottom - rectCurrent.Top;

                if (retryCount == 0)
                {
                    Debug.WriteLine($"Window rect AFTER initial attempt: Width={actualWidth}, Height={actualHeight}");
                }

                // Check if size matches (allow 1 pixel tolerance for rounding)
                if (Math.Abs(actualWidth - rule.Width) <= 1 && Math.Abs(actualHeight - rule.Height) <= 1)
                {
                    Debug.WriteLine($"Size matches! Width={actualWidth}, Height={actualHeight}");
                    break;
                }

                retryCount++;
                Debug.WriteLine(
                    $"Size mismatch (attempt {retryCount}/{maxRetries})! Expected: {rule.Width}x{rule.Height}, Actual: {actualWidth}x{actualHeight}. Retrying...");

                // Use SetWindowPos with different flags for more forceful resize
                SetWindowPos(hWnd, IntPtr.Zero, rule.X, rule.Y, rule.Width, rule.Height,
                    SWP_SHOWWINDOW | SWP_NOZORDER);

                Thread.Sleep(50);

                // Also try MoveWindow
                MoveWindow(hWnd, rule.X, rule.Y, rule.Width, rule.Height, true);

                // Send WM_SIZE message to force the window to process the size change
                var lParam = (nint)((rule.Height << 16) | (rule.Width & 0xFFFF));
                SendMessage(hWnd, WM_SIZE, (nint)SIZE_RESTORED, lParam);

                // Longer delay for later retries
                Thread.Sleep(100 + (retryCount * 50));
            }
            else
            {
                break;
            }
        }

        // Final verification
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

        // Control OnTop property
        if (rule.OnTop.HasValue)
        {
            var result = SetWindowPos(hWnd, rule.OnTop.Value ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            Debug.WriteLine($"SetWindowPos for {rule.ProcessName}: OnTop={rule.OnTop.Value}, Result={result}");
        }
    }

    private static WindowRulesConfig LoadConfigInternal(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new WindowRulesConfig
                {
                    Rules = new List<WindowRule>(),
                    PollIntervalMs = 1500,
                    ApplyOnLaunchOnly = false,
                    ControlDurationMs = 5000
                };
            }

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<WindowRulesConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var result = cfg ?? new WindowRulesConfig { Rules = new List<WindowRule>(), PollIntervalMs = 1500 };
            if (result.ControlDurationMs <= 0) result.ControlDurationMs = 5000;
            return result;
        }
        catch
        {
            return new WindowRulesConfig
                { Rules = new List<WindowRule>(), PollIntervalMs = 1500, ControlDurationMs = 5000 };
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongPtr(nint hWnd, int nIndex, int dwNewLong);

    private static bool IsPopupWindow(nint hWnd)
    {
        // Get window class name first - this is the most reliable way to identify context menus
        var className = GetWindowClassName(hWnd);
        // Common context menu class names in Windows
        if (className == "#32768" || className == "ContextMenu" || className == "Menu" ||
            className.StartsWith("Menu", StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"Detected popup by class name: {className}");
            return true;
        }

        // Check if window has an owner (owned windows are often popups like context menus)
        var owner = GetWindow(hWnd, GW_OWNER);
        if (owner != IntPtr.Zero)
        {
            // This window is owned by another window - likely a popup/context menu
            Debug.WriteLine("Detected popup by owner window");
            return true;
        }

        var style = GetWindowLong(hWnd, GWL_STYLE);
        var isPopup = (style & WS_POPUP) != 0;
        var isChild = (style & WS_CHILD) != 0;

        // Skip pure popup windows (context menus, tooltips) but allow popup dialogs
        // Pure popups typically don't have a parent and are temporary
        if (isPopup && !isChild)
        {
            // If it's a small popup window (likely tooltip or context menu), skip it
            if (GetWindowRect(hWnd, out var rect))
            {
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                // Very small windows are likely tooltips or context menus
                // Context menus are typically narrow and tall, tooltips are small
                if (width < 200 || height < 80)
                {
                    Debug.WriteLine($"Detected popup by size: {width}x{height}");
                    return true;
                }
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    private static string GetWindowClassName(nint hWnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

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

    public sealed class WindowRulesConfig
    {
        public List<WindowRule> Rules { get; set; } = new();
        public int PollIntervalMs { get; set; } = 1500;
        public bool ApplyOnLaunchOnly { get; set; }
        public int ControlDurationMs { get; set; } = 5000;
        public bool ApplyAllOnStartup { get; set; }
        public bool StartInTray { get; set; }
    }

    public sealed class WindowRule
    {
        public string ProcessName { get; set; } = string.Empty; // without .exe
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? MatchTitleContains { get; set; } // Include only windows with title containing this
        public string? ExcludeTitleContains { get; set; } // Exclude windows with title containing this
        [JsonIgnore]
        private string[] _excludeTitles;
        [JsonIgnore]
        public string[] ExcludeTitles
        {
            get
            {
                if (_excludeTitles == null)
                {
                    _excludeTitles = ExcludeTitleContains?.Split(';');
                }

                return _excludeTitles;
            }
        }
        public bool Enabled { get; set; } = true;
        public bool ApplyOnStartup { get; set; } = true;
        public bool? OnTop { get; set; } = null; // null = don't change, true = set on top, false = remove from top
    }
}