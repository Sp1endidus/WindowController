# AGENTS

## Purpose

`WindowController` is a WPF desktop utility that watches external application windows and forces their position, size, and optional topmost state according to rules stored in `windowrules.json`.

The project currently consists of one application project:

- `src/WindowController.App` - UI, tray integration, config editing, background window control loop.

## Runtime Agents

### 1. WPF Application Shell

Files:

- `src/WindowController.App/App.xaml`
- `src/WindowController.App/App.xaml.cs`

Responsibilities:

- Starts the WPF application.
- Uses `StartupUri="MainWindow.xaml"` to create the main window.

Notes:

- There is no composition root beyond `MainWindow`; the window creates the service directly.

### 2. Main Window Agent

Files:

- `src/WindowController.App/MainWindow.xaml`
- `src/WindowController.App/MainWindow.xaml.cs`

Responsibilities:

- Loads the config path from `AppContext.BaseDirectory + windowrules.json`.
- Constructs `WindowControlService`.
- Starts the background monitor with `_service.Start()`.
- Reads config flags and reflects them into UI:
  - `ApplyAllOnStartup`
  - `StartInTray`
- Applies startup rules immediately after service startup:
  - `ApplyAllRulesOnce()` when `ApplyAllOnStartup = true`
  - otherwise `ApplyStartupRulesOnce()`
- Maintains an in-memory editable `ObservableCollection<WindowRule>` for the grid.
- Saves edited rules back through `WindowControlService.Save(...)`.
- Reloads rules from disk through `WindowControlService.Reload()`.
- Triggers temporary re-application through `SimulateAppJustOpenedForAll()`.
- Handles tray icon creation, hide/show behavior, and application exit.

UI interactions:

- `Pick Window` minimizes the controller window, waits for the next left click, and fills the editor fields with the clicked window's process/title/geometry.
- Preset buttons fill editor fields based on `SystemParameters.WorkArea` or `PrimaryScreenHeight`.
- `Add / Update Rule` either updates an existing rule or appends a new one.
- `Save Rules` persists the current collection to `windowrules.json`.
- `Reload Rules` discards current UI state and reloads from disk.
- `Apply Now (5s)` clears service tracking state so existing windows are treated as newly opened again.
- Closing the main window does not exit the app; it hides to tray.

Internal rule identity:

- A rule is treated as the "same" rule by UI code when both match:
  - `ProcessName`
  - `MatchTitleContains`

This means:

- `ExcludeTitleContains`, geometry, startup flag, and `OnTop` do not participate in identity.
- Two rules with the same process and same include-title cannot coexist through the UI.

### 3. Window Monitoring Agent

File:

- `src/WindowController.App/Services/WindowControlService.cs`

Responsibilities:

- Owns the current `WindowRulesConfig`.
- Loads and saves JSON config.
- Runs a background polling loop over matching processes.
- Detects when windows appear, reappear, restore, become visible again, or change handles.
- Applies geometry and topmost rules using Win32 APIs.
- Maintains transient per-process tracking state.

Owned state:

- `_config` guarded by `_configLock`
- `_cts` for monitor loop lifetime
- `_firstSeenByPid`
- `_lastMinimizedByPid`
- `_lastVisibilityByPid`
- `_lastWindowHandleByPid`

Public control surface:

- `GetConfig()`
- `Reload()`
- `Save(WindowRulesConfig config)`
- `Start()`
- `Stop()`
- `ApplyStartupRulesOnce()`
- `ApplyAllRulesOnce()`
- `SimulateAppJustOpenedForAll()`

### 4. Window Picking Agent

File:

- `src/WindowController.App/Services/WindowPicker.cs`

Responsibilities:

- Reads the current cursor position and the window under it.
- Walks child controls up to their root top-level window.
- Resolves process id, process name, title, and bounding rect.
- Exposes:
  - synchronous `TryPickWindow(...)`
  - asynchronous `PickOnNextClickAsync(...)`

How it is used:

- Only `MainWindow.PickWindowButton_Click` uses it.
- The async picker ignores clicks on the controller's own process id.

### 5. Window Rule Applier Agent

File:

- `src/WindowController.App/Services/WindowRuleApplier.cs`

Responsibilities:

- Encapsulates the Win32 strategy for applying a rule to a target window.
- Restores the target window, updates placement, retries size/position changes, and applies topmost state.
- Exposes:
  - `Apply(...)`
  - `ApplyTopMostOnly(...)`

How it is used:

- `WindowControlService` decides when a rule should be applied.
- `WindowRuleApplier` owns how that application is performed.

## Main Runtime Flow

### Startup flow

1. WPF loads `MainWindow`.
2. `MainWindow` computes config path in the output directory.
3. `MainWindow` creates `WindowControlService(configPath)`.
4. Service loads `windowrules.json` or constructs defaults.
5. `MainWindow` calls `_service.Start()`.
6. `MainWindow` reads config flags and applies startup rules once.
7. `MainWindow` binds config rules into `_rules` for editing.
8. Tray icon is created.
9. If `StartInTray` is `true`, the window is hidden immediately.

### Save flow

1. User edits fields and grid state.
2. `SaveButton_Click` copies `_rules` into the config snapshot from `_service.GetConfig()`.
3. UI-level flags `ApplyAllOnStartup` and `StartInTray` are copied into config.
4. `WindowControlService.Save(...)` serializes config with indented JSON.
5. File is written to `ConfigPath`.
6. Service replaces its in-memory `_config` with a cloned snapshot and resets monitor tracking so new rules are reapplied immediately.

### Reload flow

1. User clicks `Reload Rules`.
2. `MainWindow` calls `_service.Reload()`.
3. Service re-reads the JSON file, swaps `_config`, and resets monitor tracking state.
4. UI rebuilds `_rules` from the service snapshot.

### Monitor loop flow

For every poll interval:

1. Service takes a snapshot of `_config` under lock.
2. For each enabled rule, it calls `Process.GetProcessesByName(rule.ProcessName)`.
3. For each process:
   - skips missing `MainWindowHandle`
   - skips popup/context-menu style windows
   - reads title
   - applies include/exclude title filtering
   - checks visibility and minimized state
   - detects restore/reopen/rehandle transitions
   - resets first-seen timing when needed
   - verifies actual window size
   - reapplies rule when inside control window or when size drift is detected
   - reapplies `OnTop` even outside the timed enforcement window
4. Service delays `max(250, PollIntervalMs)` and repeats.

### Manual "Apply Now (5s)" flow

1. UI calls `SimulateAppJustOpenedForAll()`.
2. UI also calls `_service.Reload()`.
3. Service clears all per-process tracking dictionaries.
4. Next monitor iterations treat current windows as newly detected.

## Config Contract

File:

- `src/WindowController.App/windowrules.json`

Deployment behavior:

- The file is marked `CopyToOutputDirectory=PreserveNewest`.
- Runtime reads and writes the copy located in `AppContext.BaseDirectory`, not the source file in the repo.

Top-level fields:

- `rules`
- `pollIntervalMs`
- `applyOnLaunchOnly`
- `controlDurationMs`
- `applyAllOnStartup`
- `startInTray`

Rule fields:

- `processName`
- `x`
- `y`
- `width`
- `height`
- `matchTitleContains`
- `excludeTitleContains`
- `enabled`
- `applyOnStartup`
- `onTop`

Derived rule behavior:

- `ExcludeTitleContains` is split by `;` into trimmed exclusion patterns.
- `OnTop` is tri-state:
  - `null` = do not modify z-order
  - `true` = set topmost
  - `false` = remove topmost

## Native/External Interactions

### .NET / WPF

- UI framework: WPF on `net9.0-windows`
- Collection binding via `ObservableCollection<WindowRule>`
- Screen/work area presets via `SystemParameters`

### Tray integration

Dependency:

- `Hardcodet.NotifyIcon.Wpf`

Used for:

- tray icon creation
- context menu
- restore/show action
- exit action

### Win32 APIs used by the service

`WindowControlService` calls into `user32.dll` for:

- `MoveWindow`
- `ShowWindow`
- `SetWindowPos`
- `SendMessage`
- `GetWindowPlacement`
- `SetWindowPlacement`
- `IsWindowVisible`
- `IsIconic`
- `GetWindowLong`
- `SetWindowLong`
- `GetWindowRect`
- `GetWindow`
- `GetClassName`

Purpose:

- restore minimized windows
- set normal placement before restore
- force position and size
- toggle topmost state
- inspect popup-like windows
- inspect and alter resize-related style flags

### Win32 APIs used by the picker

`WindowPicker` uses:

- `GetCursorPos`
- `WindowFromPoint`
- `GetAncestor`
- `GetWindowRect`
- `GetWindowText`
- `GetWindowTextLength`
- `GetWindowThreadProcessId`
- `GetAsyncKeyState`

Purpose:

- capture the clicked top-level window and its metadata

## Key Behavioral Rules

### Rule matching

A process/window is eligible only if:

- rule is enabled
- process name matches `Process.GetProcessesByName`
- main window handle is non-zero
- title passes include/exclude filters
- window is not classified as popup/context-menu

### Popup suppression

The service tries to avoid resizing transient UI such as menus by checking:

- window class names like `#32768`, `ContextMenu`, `Menu`
- owned-window status via `GetWindow(..., GW_OWNER)`
- popup style plus very small size

### Timed enforcement

Geometry is strongly enforced during `ControlDurationMs` after first detection or rediscovery.

Rediscovery events include:

- process reappeared after being tracked before
- window handle reappeared
- main window handle changed
- window became visible again
- window was restored from minimized state

### Continuous correction

Even outside the timed window, size drift can trigger re-application if the actual size differs from rule width/height by more than one pixel.

### OnTop persistence

`OnTop` is enforced even when geometry is no longer inside the timed control window.

## Important Implementation Nuances

### Config snapshots are cloned

`GetConfig()` returns a cloned snapshot. `Save(...)` also clones before storing. This prevents UI-side mutation of the live service config and keeps monitor-loop reads isolated from editor state.

### Runtime config file location

The app edits the config in the build output directory. Changing `src/WindowController.App/windowrules.json` while the built app is running will not affect the active runtime copy until rebuilt or recopied.

### Window close behavior

Main window closing is canceled and redirected to tray hide unless `_isExitRequested` has been set by the tray menu `Exit`. On actual exit, the service is disposed and the tray icon is explicitly cleaned up.

### Startup apply modes

- `ApplyStartupRulesOnce()` applies only rules where `ApplyOnStartup = true`.
- `ApplyAllRulesOnce()` ignores per-rule `ApplyOnStartup` and applies every enabled rule.

### Exclusion matching is unified

All rule application paths now use the same title filter:

- `ExcludeTitleContains` is split by `;`
- each token is trimmed
- exclusion uses case-insensitive substring matching
- inclusion uses case-insensitive substring matching via `MatchTitleContains`

Startup apply, manual apply, and the monitor loop now make the same decision for the same rule.

### UI does not expose every config field

The main window edits:

- rules
- `ApplyAllOnStartup`
- `StartInTray`

It does not expose:

- `PollIntervalMs`
- `ApplyOnLaunchOnly`
- `ControlDurationMs`
- per-rule `ApplyOnStartup`

Those fields can exist in JSON and are preserved if not overwritten indirectly.

### DataGrid editing vs save path

The grid binds directly to `_rules`. Persisted output is produced only when the user clicks `Save Rules`.

## File-to-File Interaction Map

- `App.xaml` -> starts `MainWindow.xaml`
- `MainWindow.xaml` -> defines controls and event handlers implemented in `MainWindow.xaml.cs`
- `MainWindow.xaml.cs` -> creates and controls `WindowControlService`
- `MainWindow.xaml.cs` -> calls `WindowPicker`
- `MainWindow.xaml.cs` -> creates tray icon via `Hardcodet.NotifyIcon.Wpf`
- `WindowControlService.cs` -> reads/writes `windowrules.json`
- `WindowControlService.cs` -> queries running processes via `System.Diagnostics.Process`
- `WindowControlService.cs` -> manipulates external windows via Win32
- `WindowControlService.cs` -> delegates actual rule application to `WindowRuleApplier`
- `WindowRuleApplier.cs` -> performs Win32 placement/restore/resize/topmost operations
- `WindowPicker.cs` -> inspects clicked windows via Win32

## Practical Change Guidance

If you modify this project, keep these boundaries intact:

- UI concerns belong in `MainWindow`.
- Process/window monitoring belongs in `WindowControlService`.
- Click-to-select external window behavior belongs in `WindowPicker`.
- JSON schema changes must be reflected in both `WindowRulesConfig` and the UI if the field should be user-editable.

High-risk areas:

- `ApplyRuleToWindow(...)` because it mixes placement, style mutation, restore, and repeated retries.
- popup detection, because false positives skip real windows and false negatives resize menus/tooltips.
- config loading/saving, because runtime uses the output-directory copy of `windowrules.json`.
