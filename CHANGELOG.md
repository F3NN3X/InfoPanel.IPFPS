# Changelog

All notable changes to the **InfoPanel Presentmon Plugin** are documented here.

## [2.0.0] - 2025-10-17

### 🎉 Major Release: Complete Architecture Overhaul

This release represents a complete rewrite of the plugin with a modern, maintainable architecture and significantly improved performance.

### Added
- **Service-Based Architecture**: Introduced proper separation of concerns with dedicated service classes
  - `PresentMonService`: Handles P/Invoke calls, session management, and frame processing
  - `FullscreenDetectionService`: Win32 API integration for detecting fullscreen applications
- **Data Models**: Created structured models for better data management
  - `FrameMetrics`: Processed performance data with all FPS metrics
  - `MonitoringState`: Current monitoring status and process information
  - `FrameProcessingConfig`: Configurable processing parameters
- **P/Invoke Integration**: Direct native DLL integration with `PresentMonAPI2.dll`
  - `PresentMonApi`: Centralized P/Invoke declarations with proper error handling
  - Native `PMFrame` struct processing for optimal performance
- **Event-Driven Updates**: Real-time sensor updates via service events instead of polling
- **Background Processing**: Non-blocking frame data processing with async patterns throughout

### Changed
- **BREAKING**: Project renamed from `InfoPanel.IPFPS` to `InfoPanel.Presentmon` for clarity
- **BREAKING**: Completely replaced EXE-based PresentMon integration with direct DLL calls
- **Performance**: Eliminated process spawning, CSV parsing, and inter-process communication overhead
- **Architecture**: Migrated from single 1100+ line monolith to clean service-based architecture
- **Data Flow**: Switched from polling-based updates to event-driven real-time updates
- **Resource Management**: Improved memory efficiency with direct native frame data access
- **Error Handling**: Enhanced error handling with proper P/Invoke result codes (`PM_RESULT`)

### Removed
- **EXE Dependencies**: No longer requires `PresentMon-2.3.0-x64.exe` or `PresentMonService.exe`
- **Windows Service Management**: Eliminated complex service creation/management code
- **CSV Processing**: Removed string parsing overhead in favor of native struct processing
- **Process Management**: Removed complex process orchestration and cleanup logic

### Fixed
- **Reliability**: Eliminated process-related crashes and hanging issues
- **Performance**: Reduced CPU overhead and memory usage significantly
- **Maintainability**: Clear separation of concerns makes debugging and testing easier
- **Thread Safety**: Proper async/await patterns throughout with cancellation support

### Technical Details
- **Framework**: Targets .NET 8.0-windows with x64 architecture
- **Dependencies**: Only requires `PresentMonAPI2.dll` (bundled) and `Vanara.PInvoke.*` packages
- **Compatibility**: Maintains identical sensor interface for seamless InfoPanel integration
- **Build Output**: Simplified to `InfoPanel.Presentmon-v2.0.0.zip` with clean folder structure

### Migration Notes
- Users upgrading from v1.x will need to replace the entire plugin folder
- All existing functionality is preserved but with significantly improved performance
- Configuration and sensor names remain unchanged for compatibility

---

## [1.3.2] - 2025-03-22

### Added
- New `PluginText` sensor (`windowtitle`) to display the currently captured window title (e.g., "Arma Reforger") or "Nothing to capture" when idle.

## [1.3.1] - 2025-03-09
### Fixed
- Corrected 1% and 0.1% low FPS calculations in `ProcessOutputLine` to use the worst frames (highest frame times) by sorting frame times in descending order and selecting the appropriate percentiles (99th and 99.9th). Previously, the best frames were incorrectly used, leading to inflated low FPS values (e.g., 176 FPS instead of < average).
### Changed
- Increased `MaxFrameSamples` from default (assumed 100) to 1000 for finer granularity in frame time sampling, providing a rolling window of ~8-10 seconds at typical FPS rates (100-120 FPS). This improves the accuracy of average, 1%, and 0.1% low FPS metrics over a longer period.
### Notes
- Verified fix with logs, confirming 1% low (e.g., 86.74 FPS → 10.09 FPS) and 0.1% low (e.g., 82.60 FPS → 8.36 FPS) now correctly reflect performance dips below the average FPS (~128 FPS → 122 FPS).

## [1.3.0] - 2025-03-09
- **Improved**: fullscreen detection reliability across multi-monitor setups
- **Enhanced**: Enhanced PID transition handling for seamless game restarts
- **Optimized**: performance metric averaging for smoother output
- **Enhanced**: Added robust cleanup on game exit (stops PresentMon, resets sensors)
- **Fixed**: minor logging truncation issues
- **Enhanced**: Added new sensors: GpuTime, GpuBusy, GpuWait, and GpuUtilization
- **Enhanced**: Improved PresentMon integration for real-time data capture
- **Enhanced**: Refined CPU metrics with CpuBusy and CpuWait sensors

## [1.2.5] - 2025-03-09
- **Improved**: Replaced synchronous `process.WaitForExit(timeout)` with asynchronous `WaitForExitAsync(cancellationToken)` and `Task.WhenAny` for timeouts in `ExecuteCommandAsync` and `StopCaptureAsync`.
- **Optimized**: Updated `ProcessExists` and `GetProcessName` to use `Process.GetProcessById` with exception handling instead of iterating all processes, improving performance.
- **Refactored**: Consolidated cleanup logic in `Dispose` to call a unified `CleanupAsync` method, streamlining capture stop, service shutdown, and ETW session clearing.
- **Enhanced**: Added try-catch blocks to output and error reading tasks in `StartCaptureAsync` for better exception handling during asynchronous stream reads.
- **Tweaked**: Simplified fullscreen detection in `GetActiveFullscreenProcessId` with early exits and additional logging to reduce unnecessary API calls.
- **Fixed**: Resolved `CS8625` warnings in `WaitForExitAsync` by using `TaskCompletionSource<bool>` instead of passing `null` to `TaskCompletionSource<object>`.

## [1.2.4] - 2025-03-08
- **Fixed**: Removed hardcoded game check, making the plugin agnostic to specific apps.
  - Replaced with generic handling of access-denied cases in `IsReShadeActive`, assuming no ReShade interference unless `dxgi.dll` is confirmed.
- **Fixed**: Resolved `CS8600` warning by declaring `processName` as `string?` in `StartCaptureAsync`.

## [1.2.3] - 2025-03-08
- **Fixed**: Bypassed ReShade check for anti-cheat protected games to avoid `Win32Exception` due to access denial. Added `GetProcessName` to safely identify processes and skip unnecessary checks.
- **Restored**: Functionality from v1.2.1 with stability fixes.
  - Fixed `StartAsync` to prevent task completion races and added PID logging for debugging.
  - Simplified `StartCaptureAsync` to ensure reliable output capture and added error logging for PresentMon diagnostics.
  - Retained robust fullscreen detection and service management from v1.2.1.
  - Avoided LINQ in `ProcessExists` to prevent disposal-related crashes.

## [1.2.1] - 2025-03-08
- **Improved**: Asynchronous optimization phase 1, fixed race condition in process monitoring.

## [1.2.0] - 2025-03-07

### Added
- **Fullscreen/Borderless Detection**: Implemented comprehensive window enumeration using window styles (`WS_CAPTION`, `WS_THICKFRAME`) and client area matching (98%+ monitor coverage) to detect fullscreen and borderless applications universally.
- **PresentMon Integration**: Added robust service management (`InfoPanelPresentMonService`) with start/stop functionality and ETW session cleanup via `logman.exe`.
- **FPS and Frame Time Monitoring**: Added 5-frame window averaging for smooth FPS and frame time output using PresentMon’s CSV data.
- **ReShade Detection**: Included detection of `dxgi.dll` in process modules, with a fallback assumption of ReShade presence on access denial for safety with anti-cheat systems.
- **Anti-Cheat Safety**: Minimized module enumeration to reduce interference, with checks limited to `IsReShadeActive`.

### Changed
- **Exception Noise Reduction**:
  - Replaced `Process.GetProcessById` with `Process.GetProcesses` in `ProcessExists` to eliminate `System.ArgumentException` in the debugger when games exit.
  - Simplified `IsReShadeActive` to check `HasExited` first, reducing unnecessary `System.ComponentModel.Win32Exception` occurrences from `proc.Modules`.
  - Suppressed `ArgumentException` logging in `ProcessExists` for expected game exits, improving log cleanliness.
- **Type Safety**: Fixed type mismatch warnings (CS1503) by using `Vanara.PInvoke.RECT` and `Vanara.PInvoke.POINT` structs for window geometry calculations.
- **Cleanup Logic**: Enhanced cleanup with a 10-second timeout check in `StartCaptureAsync` and forced termination of lingering PresentMon processes in `StopCapture` and `Dispose`.

### Fixed
- Resolved false positives in fullscreen detection by filtering out system UI windows (e.g., `explorer`, `textinputhost`) and small/invalid windows (client area < 1000 pixels or off-monitor).
- Fixed "Access is denied" errors in module checking by gracefully handling exceptions in `CanAccessModules`.
- Corrected cleanup issues ensuring PresentMon and its service stop reliably, including ETW session termination.

### Known Issues
- A `System.ComponentModel.Win32Exception` ("Access is denied") may still appear in the debugger when `IsReShadeActive` checks `proc.Modules` for games with anti-cheat protection. This is caught and handled, affecting only debug output, not functionality.

### Notes
- This release consolidates all prior development efforts into a stable version, reverting from an unstable v1.2.6 attempt that introduced `OpenProcess` for module access checking, which caused cascading exceptions (`Win32Exception`, `InvalidOperationException`) and broke PresentMon startup.

## [1.1.0] - 2025-03-05

### Final Release with Robust Cleanup

- **Added**:
  - Detailed comments throughout `IPFpsPlugin.cs` for code clarity.

- **Changed**:
  - Consolidated cleanup logic into `StartCaptureAsync`’s PID monitoring loop.
    - Removed redundant cleanup from `StartMonitoringLoopAsync`.
    - Ensures single-point shutdown when the target PID (e.g., `20332`) exits.
  - Updated `ProcessExists` to include `proc != null && !proc.HasExited` for extra reliability.
  - Bumped version to `1.1.0` in constructor and documentation.

- **Fixed**:
  - Resolved issue where PresentMon lingered after the target app closed.
    - Now reliably stops PresentMon and service.

- **Notes**:
- Left `System.ArgumentException` in logs as a debug artifact (e.g., before `"PID 20332 no longer exists."`).
- Harmless and only visible in debug mode; no functional impact.

- **Purpose**:
- Finalized a production-ready plugin with stable FPS tracking and no resource leaks.

## [1.0.9] - 2025-03-05

### Improved Process Detection and Logging

- **Added**:
- `proc != null && !proc.HasExited` check in `ProcessExists` for robustness.
- Enhanced logging in `GetActiveFullscreenProcessId` with window/monitor rects and fullscreen state.

- **Changed**:
- Moved cleanup trigger to `StartCaptureAsync`’s PID monitoring loop.
- Checks `ProcessExists` every second, stops PresentMon if the target PID is gone.
- Simplified `StartMonitoringLoopAsync` to only detect new fullscreen apps.

- **Fixed**:
- Stalled cleanup when Arma exited (previously no `"Fullscreen app exited"` log).
- Now detects via `ProcessExists` and triggers full shutdown.

- **Purpose**:
- Addressed intermittent cleanup failures, improved debug visibility (e.g., `Foreground PID: 20332, Window rect: {0,0,2560,1440}, Fullscreen: True`).

## [1.0.8] - 2025-03-04

### Initial Stable Release with Service Management

- **Added**:
- `StartPresentMonService` and `StopPresentMonService` for ETW session management.
- Installs and starts `InfoPanelPresentMonService` with `sc.exe`.
- `--terminate_on_proc_exit` flag to PresentMon arguments.
- `ClearETWSessions` to remove lingering PresentMon ETW sessions on startup.

- **Changed**:
- Updated PresentMon launch to use stdout redirection for FPS data.
- Implemented 5-frame averaging in `ProcessOutputLine` for smoother FPS output.

- **Fixed**:
- PresentMon not terminating when the target app closed.
- Added explicit `Kill(true)` in `StopCapture` with 5s timeout.
- ETW session leaks (e.g., `PresentMon_15a132264c0649a59270077c6dd9a2bb`) on shutdown.

- **Purpose**:
- Established a working plugin for DXGI apps (e.g., game), capturing ~140-175 FPS with clean startup/shutdown.

## [1.0.7 and Earlier] - Pre-2025-03-04

### Prototypes and Early Development

- **Added**:
- Basic fullscreen detection with `GetActiveFullscreenProcessId` using `User32.GetForegroundWindow`.
- Initial PresentMon integration with hardcoded PID testing.
- FPS parsing from PresentMon CSV output (`MsBetweenPresents` column).

- **Changed**:
- Iterated on cleanup logic, initially using `Dispose` only.
- Experimented with monitoring loops and service-less ETW handling.

- **Fixed**:
- Early issues with PresentMon not starting (missing executable path).
- Incorrect FPS calculations (no averaging).

- **Purpose**:
- Proof of concept to integrate PresentMon with InfoPanel.
