using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowController.App.Services;

public sealed class WindowControlService : IDisposable
{
    private const int GWL_STYLE = -16;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_CHILD = 0x40000000;

    private const uint GW_OWNER = 4;
    private readonly object _configLock = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<int, DateTime> _firstSeenByPid = new();
    private readonly Dictionary<int, bool> _lastMinimizedByPid = new();
    private readonly Dictionary<int, bool> _lastVisibilityByPid = new();
    private readonly Dictionary<int, nint> _lastWindowHandleByPid = new();
    private readonly HashSet<int> _suppressedWhileMinimizedByPid = new();
    private readonly WindowRuleApplier _windowRuleApplier;
    private WindowRulesConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    public WindowControlService(string configPath, WindowRuleApplier? windowRuleApplier = null)
    {
        ConfigPath = configPath;
        _windowRuleApplier = windowRuleApplier ?? new WindowRuleApplier();
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
            return _config.Clone();
        }
    }

    public void Reload()
    {
        var cfg = LoadConfigInternal(ConfigPath);
        lock (_configLock)
        {
            _config = cfg;
        }

        SimulateAppJustOpenedForAll();
    }

    public void Save(WindowRulesConfig config)
    {
        var snapshot = config.Clone();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, json);
        lock (_configLock)
        {
            _config = snapshot;
        }

        SimulateAppJustOpenedForAll();
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _monitorTask = Task.Run(() => MonitorLoopAsync(token), token);
    }

    public void Stop()
    {
        var cts = _cts;
        var monitorTask = _monitorTask;
        cts?.Cancel();
        _cts = null;
        _monitorTask = null;

        if (monitorTask is { IsCompleted: false })
        {
            try
            {
                monitorTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is TaskCanceledException))
            {
            }
        }

        cts?.Dispose();
    }

    public void ApplyStartupRulesOnce()
    {
        WindowRulesConfig snapshot;
        lock (_configLock)
        {
            snapshot = _config.Clone();
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

                    if (!ShouldApplyToTitle(rule, title))
                        continue;

                    _windowRuleApplier.Apply(process.MainWindowHandle, rule);
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
            snapshot = _config.Clone();
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

                    if (!ShouldApplyToTitle(rule, title))
                        continue;

                    _windowRuleApplier.Apply(process.MainWindowHandle, rule);
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
                snapshot = _config.Clone();
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
                            lock (_stateLock)
                            {
                                _lastWindowHandleByPid[process.Id] = IntPtr.Zero;
                            }
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
                        nint lastHandle;
                        bool wasMinimized;
                        bool wasVisible;
                        bool wasPreviouslyTracked;
                        bool isSuppressedWhileMinimized;
                        lock (_stateLock)
                        {
                            lastHandle = _lastWindowHandleByPid.TryGetValue(process.Id, out var h) ? h : IntPtr.Zero;
                            wasMinimized = _lastMinimizedByPid.GetValueOrDefault(process.Id, false);
                            wasVisible = _lastVisibilityByPid.GetValueOrDefault(process.Id, false);
                            wasPreviouslyTracked = _lastWindowHandleByPid.ContainsKey(process.Id);
                            isSuppressedWhileMinimized = _suppressedWhileMinimizedByPid.Contains(process.Id);
                        }
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

                        if (!ShouldApplyToTitle(rule, title))
                            continue;

                        // Check if window is visible (not minimized/hidden)
                        var isMinimized = IsIconic(process.MainWindowHandle);
                        var isVisible = IsWindowVisible(process.MainWindowHandle) && !isMinimized;

                        // Check if window was minimized before and is now restored
                        var wasRestored = wasMinimized && !isMinimized;

                        // Check if window is being minimized (transitioning from visible to minimized)
                        var isBeingMinimized = wasVisible && !isVisible && isMinimized;

                        // Check if visibility changed from hidden to visible
                        // Default to false for first-time detection (assume window wasn't visible before we started tracking)
                        var becameVisible = !wasVisible && isVisible;

                        if (isBeingMinimized)
                        {
                            lock (_stateLock)
                            {
                                _suppressedWhileMinimizedByPid.Add(process.Id);
                                _lastVisibilityByPid[process.Id] = isVisible;
                                _lastMinimizedByPid[process.Id] = isMinimized;
                                _lastWindowHandleByPid[process.Id] = process.MainWindowHandle;
                            }

                            Debug.WriteLine(
                                $"Suppressing auto-restore after manual minimize for {process.ProcessName} (PID {process.Id})");
                            appliedForPid.Remove(process.Id);
                            continue;
                        }

                        if (isSuppressedWhileMinimized)
                        {
                            if (isMinimized)
                            {
                                lock (_stateLock)
                                {
                                    _lastVisibilityByPid[process.Id] = isVisible;
                                    _lastMinimizedByPid[process.Id] = isMinimized;
                                    _lastWindowHandleByPid[process.Id] = process.MainWindowHandle;
                                }

                                continue;
                            }

                            if (wasRestored || becameVisible)
                            {
                                lock (_stateLock)
                                {
                                    _suppressedWhileMinimizedByPid.Remove(process.Id);
                                }
                            }
                        }

                        // Window became visible if process reappeared, handle reappeared, handle changed, was restored from minimized state, OR if visibility changed
                        // Note: Only treat processReappeared as restore if we had previously tracked this PID (to avoid treating first-time detection as restore)
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
                        DateTime first;
                        lock (_stateLock)
                        {
                            if (!_firstSeenByPid.TryGetValue(process.Id, out first) || shouldApply)
                            {
                                first = DateTime.UtcNow;
                                _firstSeenByPid[process.Id] = first;
                                Debug.WriteLine(
                                    $"Reset timer for {process.ProcessName} - first time, became visible, or was restored");
                            }
                        }

                        lock (_stateLock)
                        {
                            _lastVisibilityByPid[process.Id] = isVisible;
                            _lastMinimizedByPid[process.Id] = isMinimized;
                            _lastWindowHandleByPid[process.Id] = process.MainWindowHandle;
                        }

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
                                    _windowRuleApplier.Apply(process.MainWindowHandle, rule);
                                    // Apply again after a short delay to ensure it sticks
                                    Thread.Sleep(200);
                                    _windowRuleApplier.Apply(process.MainWindowHandle, rule);
                                    // Apply one more time after a longer delay to catch any late restoration
                                    Thread.Sleep(500);
                                    _windowRuleApplier.Apply(process.MainWindowHandle, rule);
                                }, token);
                            }
                            else
                            {
                                _windowRuleApplier.Apply(process.MainWindowHandle, rule);
                            }
                        }
                        else if (rule.OnTop.HasValue && isVisible)
                        {
                            // Always apply OnTop rules, even outside the 5-second window
                            // This ensures OnTop settings persist when windows are shown again
                            Debug.WriteLine(
                                $"Applying OnTop outside timer for {process.ProcessName}: {rule.OnTop.Value}");
                            _windowRuleApplier.ApplyTopMostOnly(process.MainWindowHandle, rule);
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
        lock (_stateLock)
        {
            _firstSeenByPid.Clear();
            _lastVisibilityByPid.Clear();
            _lastMinimizedByPid.Clear();
            _lastWindowHandleByPid.Clear();
            _suppressedWhileMinimizedByPid.Clear();
        }
    }

    private static bool ShouldApplyToTitle(WindowRule rule, string? title)
    {
        var effectiveTitle = title ?? string.Empty;

        if (rule.ExcludeTitlePatterns.Any(pattern =>
                effectiveTitle.Contains(pattern, StringComparison.InvariantCultureIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rule.MatchTitleContains))
        {
            return true;
        }

        return effectiveTitle.Contains(rule.MatchTitleContains, StringComparison.InvariantCultureIgnoreCase);
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
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

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

        public WindowRulesConfig Clone()
        {
            return new WindowRulesConfig
            {
                Rules = Rules.Select(static rule => rule.Clone()).ToList(),
                PollIntervalMs = PollIntervalMs,
                ApplyOnLaunchOnly = ApplyOnLaunchOnly,
                ControlDurationMs = ControlDurationMs,
                ApplyAllOnStartup = ApplyAllOnStartup,
                StartInTray = StartInTray
            };
        }
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
        private string[]? _excludeTitlePatterns;
        [JsonIgnore]
        public IReadOnlyList<string> ExcludeTitlePatterns
        {
            get
            {
                if (_excludeTitlePatterns == null)
                {
                    _excludeTitlePatterns = string.IsNullOrWhiteSpace(ExcludeTitleContains)
                        ? Array.Empty<string>()
                        : ExcludeTitleContains
                            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                return _excludeTitlePatterns;
            }
        }
        public bool Enabled { get; set; } = true;
        public bool ApplyOnStartup { get; set; } = true;
        public bool? OnTop { get; set; } = null; // null = don't change, true = set on top, false = remove from top

        public WindowRule Clone()
        {
            return new WindowRule
            {
                ProcessName = ProcessName,
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                MatchTitleContains = MatchTitleContains,
                ExcludeTitleContains = ExcludeTitleContains,
                Enabled = Enabled,
                ApplyOnStartup = ApplyOnStartup,
                OnTop = OnTop
            };
        }
    }
}
