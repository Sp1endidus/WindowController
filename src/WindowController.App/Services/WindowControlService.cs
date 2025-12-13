using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowController.App.Services;

public sealed class WindowControlService : IDisposable
{
    private readonly string _configPath;
    private readonly object _configLock = new();
    private WindowRulesConfig _config;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<int, DateTime> _firstSeenByPid = new();
    private readonly Dictionary<int, bool> _lastVisibilityByPid = new();
    private readonly Dictionary<int, bool> _lastMinimizedByPid = new();
    private readonly Dictionary<int, nint> _lastWindowHandleByPid = new();

    public WindowControlService(string configPath)
    {
        _configPath = configPath;
        _config = LoadConfigInternal(configPath);
    }

    public string ConfigPath => _configPath;

    public WindowRulesConfig GetConfig()
    {
        lock (_configLock)
        {
            return _config;
        }
    }

    public void Reload()
    {
        var cfg = LoadConfigInternal(_configPath);
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
        File.WriteAllText(_configPath, json);
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
            } catch { }
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
            } catch { }
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
                            System.Diagnostics.Debug.WriteLine($"Skipping popup window for {process.ProcessName} (PID {process.Id})");
                            // Don't update tracking for popup windows - keep the last valid main window handle
                            continue;
                        }

                        // Check if window handle reappeared (was zero, now non-zero) or changed (different handle for same PID)
                        var lastHandle = _lastWindowHandleByPid.TryGetValue(process.Id, out var h) ? h : IntPtr.Zero;
                        var handleReappeared = lastHandle == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero;
                        // Only treat as handle change if the new handle is not a popup (already checked above)
                        var handleChanged = lastHandle != IntPtr.Zero && lastHandle != process.MainWindowHandle && process.MainWindowHandle != IntPtr.Zero;

                        // Check if process reappeared (wasn't in previous iteration, now is)
                        var processReappeared = !previousIterationPids.Contains(process.Id);

                        // Get window title for matching and validation
                        var title = process.MainWindowTitle ?? string.Empty;

                        // Skip windows with empty or very short titles (likely popups/context menus)
                        // Main application windows typically have meaningful titles
                        if (string.IsNullOrWhiteSpace(title) || title.Length < 3)
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipping window with empty/short title for {process.ProcessName} (PID {process.Id}): '{title}'");
                            continue;
                        }

                        // Check exclusion filter first - if title matches exclusion, skip this window
                        if (!string.IsNullOrWhiteSpace(rule.ExcludeTitleContains))
                        {
                            if (title.Contains(rule.ExcludeTitleContains, StringComparison.InvariantCultureIgnoreCase))
                            {
                                System.Diagnostics.Debug.WriteLine($"Skipping window matching exclusion pattern for {process.ProcessName} (PID {process.Id}): '{title}' matches '{rule.ExcludeTitleContains}'");
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
                        var wasMinimized = _lastMinimizedByPid.TryGetValue(process.Id, out var lastMin) ? lastMin : false;
                        var wasRestored = wasMinimized && !isMinimized;

                        // Check if visibility changed from hidden to visible
                        // Default to false for first-time detection (assume window wasn't visible before we started tracking)
                        var wasVisible = _lastVisibilityByPid.TryGetValue(process.Id, out var lastVis) ? lastVis : false;
                        var becameVisible = !wasVisible && isVisible;

                        // Window became visible if process reappeared, handle reappeared, handle changed, was restored from minimized state, OR if visibility changed
                        // Note: Only treat processReappeared as restore if we had previously tracked this PID (to avoid treating first-time detection as restore)
                        var wasPreviouslyTracked = _lastWindowHandleByPid.ContainsKey(process.Id);
                        var shouldApply = (processReappeared && wasPreviouslyTracked) || handleReappeared || handleChanged || becameVisible || wasRestored;

                        System.Diagnostics.Debug.WriteLine($"Process {process.ProcessName}: processReappeared={processReappeared}, wasPreviouslyTracked={wasPreviouslyTracked}, handleReappeared={handleReappeared}, handleChanged={handleChanged}, isVisible={isVisible}, isMinimized={isMinimized}, wasVisible={wasVisible}, wasMinimized={wasMinimized}, becameVisible={becameVisible}, wasRestored={wasRestored}, shouldApply={shouldApply}");

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
                            System.Diagnostics.Debug.WriteLine($"Reset timer for {process.ProcessName} - first time, became visible, or was restored");
                        }

                        // Update visibility, minimized state, and window handle tracking
                        _lastVisibilityByPid[process.Id] = isVisible;
                        _lastMinimizedByPid[process.Id] = isMinimized;
                        _lastWindowHandleByPid[process.Id] = process.MainWindowHandle;

                        var elapsed = DateTime.UtcNow - first;

                        // Apply rule if within the control duration window OR if window just became visible again or was restored
                        if (elapsed.TotalMilliseconds <= snapshot.ControlDurationMs || shouldApply)
                        {
                            System.Diagnostics.Debug.WriteLine($"Applying rule to {process.ProcessName} (PID {process.Id}): elapsed={elapsed.TotalMilliseconds}ms, shouldApply={shouldApply}, ControlDuration={snapshot.ControlDurationMs}ms");
                            ApplyRuleToWindow(process.MainWindowHandle, rule);
                        } else if (rule.OnTop.HasValue)
                        {
                            // Always apply OnTop rules, even outside the 5-second window
                            // This ensures OnTop settings persist when windows are shown again
                            System.Diagnostics.Debug.WriteLine($"Applying OnTop outside timer for {process.ProcessName}: {rule.OnTop.Value}");
                            var result = SetWindowPos(process.MainWindowHandle, rule.OnTop.Value ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                            System.Diagnostics.Debug.WriteLine($"SetWindowPos OnTop for {process.ProcessName}: Result={result}");
                        }
                        appliedForPid.Add(process.Id);
                    }
                } catch
                {
                    // ignore per-process errors
                }
            }

            // Update previous iteration PIDs for next iteration
            previousIterationPids = currentIterationPids;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(250, snapshot.PollIntervalMs)), token);
            } catch (TaskCanceledException)
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
        // Restore if minimized or maximized
        ShowWindow(hWnd, SW_RESTORE);
        // Move and size
        MoveWindow(hWnd, rule.X, rule.Y, rule.Width, rule.Height, true);
        // Control OnTop property
        if (rule.OnTop.HasValue)
        {
            var result = SetWindowPos(hWnd, rule.OnTop.Value ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            System.Diagnostics.Debug.WriteLine($"SetWindowPos for {rule.ProcessName}: OnTop={rule.OnTop.Value}, Result={result}");
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
        } catch
        {
            return new WindowRulesConfig { Rules = new List<WindowRule>(), PollIntervalMs = 1500, ControlDurationMs = 5000 };
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private const int SW_RESTORE = 9;
    private static readonly nint HWND_TOPMOST = new nint(-1);
    private static readonly nint HWND_NOTOPMOST = new nint(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    private const int GWL_STYLE = -16;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_CHILD = 0x40000000;

    private static bool IsPopupWindow(nint hWnd)
    {
        // Get window class name first - this is the most reliable way to identify context menus
        var className = GetWindowClassName(hWnd);
        // Common context menu class names in Windows
        if (className == "#32768" || className == "ContextMenu" || className == "Menu" ||
            className.StartsWith("Menu", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"Detected popup by class name: {className}");
            return true;
        }

        // Check if window has an owner (owned windows are often popups like context menus)
        var owner = GetWindow(hWnd, GW_OWNER);
        if (owner != IntPtr.Zero)
        {
            // This window is owned by another window - likely a popup/context menu
            System.Diagnostics.Debug.WriteLine($"Detected popup by owner window");
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
                    System.Diagnostics.Debug.WriteLine($"Detected popup by size: {width}x{height}");
                    return true;
                }
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private static string GetWindowClassName(nint hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    public sealed class WindowRulesConfig
    {
        public List<WindowRule> Rules { get; set; } = new();
        public int PollIntervalMs { get; set; } = 1500;
        public bool ApplyOnLaunchOnly { get; set; } = false;
        public int ControlDurationMs { get; set; } = 5000;
        public bool ApplyAllOnStartup { get; set; } = false;
        public bool StartInTray { get; set; } = false;
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
        public bool Enabled { get; set; } = true;
        public bool ApplyOnStartup { get; set; } = true;
        public bool? OnTop { get; set; } = null; // null = don't change, true = set on top, false = remove from top
    }
}


