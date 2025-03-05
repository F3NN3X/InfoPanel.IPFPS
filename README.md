# PresentMon FPS Plugin for InfoPanel

A plugin for InfoPanel to monitor and display real-time frames per second (FPS) and frame times for fullscreen applications using PresentMon.

## Features
- Displays FPS and frame times for fullscreen apps (e.g., games).
- Detects active fullscreen applications automatically via Windows API.
- Uses PresentMon for high-performance DirectX metric capture, averaging over 5 frames for smooth output.
- Manages PresentMonService for Event Tracing for Windows (ETW) sessions.
- Ensures robust cleanup of processes and ETW sessions on app exit or InfoPanel shutdown.

## Installation and Setup
Follow these steps to get the PresentMon FPS Plugin working with InfoPanel:

1. **Download the Plugin**:
   - Download the latest release ZIP file (`InfoPanel.IPFPS-vX.X.X.zip`) from the [GitHub Releases page](https://github.com/F3NN3X/InfoPanel.IPFPS/releases).

2. **Import the Plugin into InfoPanel**:
   - Open the InfoPanel app.
   - Navigate to the **Plugins** page.
   - Click **Import Plugin Archive**, then select the downloaded ZIP file.
   - InfoPanel will extract and install the plugin.

3. **Configure the Plugin**:
   - On the Plugins page, click **Open Plugins Folder** to locate the plugin files (e.g., `C:\ProgramData\InfoPanel\plugins\InfoPanel.IPFPS\`).
   - Ensure `PresentMon-2.3.0-x64.exe` and `PresentMonService.exe` are in this folder:
    - Download from [PresentMon releases](https://github.com/GameTechDev/PresentMon/releases) if missing.
    - Place `PresentMon-2.3.0-x64.exe` and `PresentMonService.exe` in the `PresentMon` subdirectory (e.g., `C:\ProgramData\InfoPanel\plugins\InfoPanel.IPFPS\PresentMon\`).
    
4. **Run InfoPanel**:
   - Launch InfoPanel and activate plugin.
   - The plugin will auto-start and monitor fullscreen apps.

5. **Enjoy**:
   - Start a fullscreen application (e.g., game).
   - Check the "FPS" section in InfoPanel for real-time FPS and frame time data.

## Obtaining PresentMon Binaries
1. Visit the [PresentMon GitHub releases page](https://github.com/GameTechDev/PresentMon/releases).
2. Download the latest release (e.g., `PresentMon-v2.3.0.zip`).
3. Extract `PresentMon-2.3.0-x64.exe` and `PresentMonService.exe` from the ZIP.
4. Copy these files to the plugin folder as described in step 3 above.

## Troubleshooting Steps & Error Messages

If the plugin isn’t working as expected, check the InfoPanel logs or UI for clues. Since this plugin doesn’t display error messages directly in the UI like SpotifyPlugin, look at the console output in InfoPanel (or logs if enabled). Here are common issues and fixes:

### **No FPS Data Showing**
- **What It Means**: The plugin isn’t detecting a fullscreen app or PresentMon isn’t running.
- **How to Fix**:
  - Ensure your app is in fullscreen mode (not windowed).
  - Check logs for `Checked for fullscreen PID: X`—if it’s always `0`, switch to fullscreen (e.g., Alt+Enter).
  - Verify `PresentMon-2.3.0-x64.exe` and `PresentMonService.exe` are in the plugin folder.

### **Logs Show "Failed to locate PresentMon executable"**
- **What It Means**: The plugin can’t find `PresentMon-2.3.0-x64.exe`.
- **How to Fix**:
  - Confirm `PresentMon-2.3.0-x64.exe` is in `C:\ProgramData\InfoPanel\plugins\InfoPanel.IPFPS\`.
  - Download from [PresentMon releases](https://github.com/GameTechDev/PresentMon/releases) if missing.

### **Logs Show "Service setup failed"**
- **What It Means**: The plugin couldn’t start `InfoPanelPresentMonService`—likely a permissions issue.
- **How to Fix**:
  - Run InfoPanel as administrator (right-click, **Run as administrator**).
  - Check if `PresentMonService.exe` is in the plugin folder.

### **Logs Show "PresentMon did not exit cleanly after kill"**
- **What It Means**: PresentMon didn’t stop within 5 seconds after being killed.
- **How to Fix**:
  - Open Task Manager, end any `PresentMon-2.3.0-x64.exe` processes manually.
  - Restart InfoPanel—shouldn’t persist with v1.1.0.

### **ETW Sessions Linger (e.g., `logman query -ets` shows `PresentMon_*`)**
- **What It Means**: Cleanup didn’t remove an ETW session—rare with v1.1.0.
- **How to Fix**:
  - Run `logman stop PresentMon_<session_name> -ets` in an admin Command Prompt.
  - Restart InfoPanel to ensure full cleanup.

### **General Tips**
- **Restart**: Fixes most glitches—close and reopen InfoPanel.

### **Still Stuck?**
If issues persist, check Task Manager for lingering `PresentMon-2.3.0-x64.exe` or `PresentMonService.exe` processes—kill them manually. Open a GitHub Issue with your logs (e.g., last 20 lines after closing the app) and steps tried.

## Contributing

Found a bug or have a feature idea? Open an [issue](https://github.com/F3NN3X/InfoPanel.IPFPS/issues) or submit a [pull request](https://github.com/F3NN3X/InfoPanel.IPFPS/pulls) on the repository!

## Requirements for Compile
- .NET 8.0
- InfoPanel application
- Dependencies: `Vanara.PInvoke` (bundled in release)
- PresentMon binaries (`PresentMon-2.3.0-x64.exe`, `PresentMonService.exe`) in the plugin directory (bundled in release)