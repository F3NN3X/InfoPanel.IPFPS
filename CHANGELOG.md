# Changelog

All notable changes to the **PresentMon FPS Plugin** (`IPFpsPlugin`) are documented here.

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
