# Changelog

All notable changes to the **PresentMon FPS Plugin** (`IPFpsPlugin`) are documented here.

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
- Established a working plugin for DXGI apps (e.g., Arma Reforger), capturing ~140-175 FPS with clean startup/shutdown.

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
- Proof of concept to integrate PresentMon with InfoPanel, focusing on Arma Reforger as a test case.