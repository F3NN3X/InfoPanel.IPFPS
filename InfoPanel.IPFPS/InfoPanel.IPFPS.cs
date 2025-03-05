using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using InfoPanel.Plugins;
using Vanara.PInvoke;

/*
 * Plugin: PresentMon FPS - IPFpsPlugin
 * Version: 1.1.0
 * Description: A plugin for InfoPanel to monitor and display FPS and frame times of fullscreen applications using PresentMon. Detects fullscreen processes, launches PresentMon to capture performance data, and updates sensors every second. Includes robust cleanup of PresentMon processes and ETW sessions on app exit or InfoPanel shutdown.
 * Changelog:
 *   - v1.1.0 (Mar 5, 2025): Finalized robust cleanup and fullscreen detection.
 *     - **Changes**: Consolidated cleanup to `StartCaptureAsync`, added `ProcessExists` with `HasExited` check, removed redundant monitoring loop cleanup. Left `ArgumentException` in logs as debug artifact.
 *     - **Purpose**: Ensures reliable shutdown of PresentMon and service, cleaner logs, and stable FPS tracking (~140-175 FPS in Arma Reforger).
 *   - v1.0.9 (Mar 5, 2025): Improved process detection and exception handling.
 *     - **Changes**: Added `proc != null && !proc.HasExited` to `ProcessExists`, moved cleanup logic to `StartCaptureAsync`, enhanced logging in `GetActiveFullscreenProcessId`.
 *     - **Purpose**: Fix stalled cleanup when Arma exits, reduce redundant checks, improve debug visibility.
 *   - v1.0.8 (Mar 4, 2025): Initial stable release with service management.
 *     - **Changes**: Introduced `StartPresentMonService` and `StopPresentMonService` for ETW session handling, fixed PresentMon termination with `--terminate_on_proc_exit`.
 *     - **Purpose**: Enable FPS capture for DXGI apps, ensure ETW sessions don’t linger.
 *   - Earlier versions: Prototypes with basic fullscreen detection and FPS parsing.
 *     - **Purpose**: Proof of concept for PresentMon integration.
 * Note: Requires PresentMon-2.3.0-x64.exe and PresentMonService.exe in the plugin subdirectory 'PresentMon'. Admin rights needed for service management.
 */

namespace InfoPanel.IPFPS
{
    public class IPFpsPlugin : BasePlugin, IDisposable
    {
        // Sensors for displaying FPS and frame time in the UI
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _frameTimeSensor = new("frame time", "Frame Time", 0, "ms");

        // PresentMon process instance and state tracking
        private Process? _presentMonProcess;
        private CancellationTokenSource? _cts; // For cancelling async tasks
        private uint _currentPid; // Tracks the current fullscreen app PID
        private volatile bool _isMonitoring; // Indicates if PresentMon is active
        private float _frameTimeSum = 0; // Sums frame times for averaging
        private int _frameCount = 0; // Counts frames for smoothing
        private const int SmoothWindow = 5; // Number of frames to average over
        private const string ServiceName = "InfoPanelPresentMonService"; // Service name for ETW
        private string? _currentSessionName; // Unique ETW session name
        private bool _serviceRunning = false; // Tracks service state

        // Executable names for PresentMon and its service
        private static readonly string PresentMonAppName = "PresentMon-2.3.0-x64";
        private static readonly string PresentMonServiceAppName = "PresentMonService";

        // Constructor with plugin metadata
        public IPFpsPlugin()
            : base("fps-plugin", "PresentMon FPS", "Retrieves FPS and frame times using PresentMon - v1.1.0")
        {
            _presentMonProcess = null;
            _cts = null;
        }

        public override string? ConfigFilePath => null; // No config file needed
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1); // Update every second

        // Initialize the plugin by cleaning up leftovers and starting the monitoring loop
        public override void Initialize()
        {
            TerminateExistingPresentMonProcesses(); // Kill any stray PresentMon instances
            ClearETWSessions(); // Remove lingering ETW sessions
            _cts = new CancellationTokenSource(); // Create cancellation token
            _ = Task.Run(() => StartMonitoringLoopAsync(_cts.Token)); // Start async monitoring
        }

        // Start the PresentMon service for ETW session management from the PresentMon subdirectory
        private void StartPresentMonService()
        {
            if (_serviceRunning) return; // Skip if already running

            string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir))
            {
                Console.WriteLine("Failed to determine plugin directory.");
                return;
            }

            // Look for PresentMonService.exe in the PresentMon subdirectory
            string presentMonDir = Path.Combine(pluginDir, "PresentMon");
            string servicePath = Path.Combine(presentMonDir, $"{PresentMonServiceAppName}.exe");
            if (!File.Exists(servicePath))
            {
                Console.WriteLine($"PresentMonService not found at: {servicePath}");
                return;
            }

            try
            {
                // Stop and delete any existing service instance
                ExecuteCommand("sc.exe", $"stop {ServiceName}", 5000, false);
                ExecuteCommand("sc.exe", $"delete {ServiceName}", 5000, false);

                Console.WriteLine($"Installing PresentMonService as {ServiceName}...");
                ExecuteCommand("sc.exe", $"create {ServiceName} binPath= \"{servicePath}\" start= demand", 5000, true);

                Console.WriteLine($"Starting {ServiceName}...");
                ExecuteCommand("sc.exe", $"start {ServiceName}", 5000, true);

                Thread.Sleep(1000); // Give service time to start
                _serviceRunning = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service setup failed: {ex.Message}");
            }
        }

        // Stop and remove the PresentMon service
        private void StopPresentMonService()
        {
            if (!_serviceRunning) return; // Skip if not running

            try
            {
                Console.WriteLine($"Stopping {ServiceName}...");
                ExecuteCommand("sc.exe", $"stop {ServiceName}", 15000, true); // Long timeout for graceful stop
                Console.WriteLine($"Waiting for {ServiceName} to stop...");
                Thread.Sleep(2000); // Wait for stop to complete
                ExecuteCommand("sc.exe", $"delete {ServiceName}", 5000, true);
                _serviceRunning = false;
                Console.WriteLine($"{ServiceName} stopped and deleted.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop service: {ex.Message}");
            }
        }

        // Clear any existing PresentMon ETW sessions
        private void ClearETWSessions()
        {
            try
            {
                using (var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "logman.exe",
                        Arguments = "query -ets",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                })
                {
                    proc.Start();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
                    string[] lines = output.Split('\n');
                    foreach (string line in lines)
                    {
                        if (line.Contains("PresentMon"))
                        {
                            string sessionName = line.Trim().Split(' ')[0];
                            Console.WriteLine($"Found ETW session: {sessionName}, stopping...");
                            ExecuteCommand("logman.exe", $"stop {sessionName} -ets", 1000, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear ETW sessions: {ex.Message}");
            }
        }

        // Main monitoring loop to detect fullscreen apps and start capture
        private async Task StartMonitoringLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint pid = GetActiveFullscreenProcessId(); // Check for fullscreen app
                    Console.WriteLine($"Checked for fullscreen PID: {pid}");

                    if (pid != 0 && pid != _currentPid && !_isMonitoring) // New fullscreen app detected
                    {
                        _currentPid = pid;
                        StartPresentMonService(); // Start ETW service
                        await StartCaptureAsync(pid, cancellationToken); // Launch PresentMon
                    }

                    await Task.Delay(1000, cancellationToken); // Check every second
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Monitoring loop canceled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Monitoring loop error: {ex}");
            }
        }

        // Launch PresentMon to capture FPS data for the given PID
        private async Task StartCaptureAsync(uint pid, CancellationToken cancellationToken)
        {
            if (_presentMonProcess != null) StopCapture(); // Stop any existing instance

            _currentSessionName = $"PresentMon_{Guid.NewGuid():N}"; // Unique session name
            string arguments = $"--process_id {pid} --output_stdout --terminate_on_proc_exit --stop_existing_session --session_name {_currentSessionName}";
            ProcessStartInfo? startInfo = GetServiceStartInfo(arguments);
            if (startInfo == null)
            {
                Console.WriteLine("Failed to locate PresentMon executable.");
                return;
            }

            using (_presentMonProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                _presentMonProcess.Exited += (s, e) => // Handle PresentMon exit
                {
                    Console.WriteLine($"PresentMon exited with code: {_presentMonProcess?.ExitCode ?? -1}");
                    _isMonitoring = false;
                };

                try
                {
                    Console.WriteLine($"Starting PresentMon with args: {arguments}");
                    _presentMonProcess.Start();
                    _isMonitoring = true;
                    Console.WriteLine($"Started PresentMon for PID {pid}, PID: {_presentMonProcess.Id}");

                    using var outputReader = _presentMonProcess.StandardOutput; // Read FPS data
                    using var errorReader = _presentMonProcess.StandardError; // Capture errors

                    // Task to process PresentMon output
                    Task outputTask = Task.Run(async () =>
                    {
                        bool headerSkipped = false;
                        while (!cancellationToken.IsCancellationRequested && !_presentMonProcess.HasExited)
                        {
                            string? line = await outputReader.ReadLineAsync();
                            if (line != null)
                            {
                                if (!headerSkipped)
                                {
                                    if (line.StartsWith("Application", StringComparison.OrdinalIgnoreCase))
                                    {
                                        headerSkipped = true;
                                        Console.WriteLine("Skipped PresentMon CSV header.");
                                    }
                                    continue;
                                }
                                ProcessOutputLine(line); // Parse FPS data
                            }
                            else
                            {
                                Console.WriteLine("Output stream ended.");
                                break;
                            }
                        }
                        Console.WriteLine("Output monitoring stopped.");
                    }, cancellationToken);

                    // Task to log PresentMon errors
                    Task errorTask = Task.Run(async () =>
                    {
                        while (!cancellationToken.IsCancellationRequested && !_presentMonProcess.HasExited)
                        {
                            string? line = await errorReader.ReadLineAsync();
                            if (line != null)
                                Console.WriteLine($"Error: {line}");
                            else
                                break;
                        }
                    }, cancellationToken);

                    // Monitor the target PID and stop if it exits
                    while (!cancellationToken.IsCancellationRequested && _isMonitoring)
                    {
                        if (!ProcessExists(pid))
                        {
                            Console.WriteLine($"Target PID {pid} gone, initiating cleanup...");
                            StopCapture();
                            StopPresentMonService();
                            _currentPid = 0;
                            break;
                        }
                        await Task.Delay(1000, cancellationToken);
                    }

                    await Task.WhenAny(outputTask, errorTask); // Wait for output or error task
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to start or monitor PresentMon: {ex}");
                    StopCapture();
                }
            }
        }

        // Get the start info for PresentMon executable from the PresentMon subdirectory
        private static ProcessStartInfo? GetServiceStartInfo(string arguments)
        {
            string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir)) return null;

            // Look for PresentMon-2.3.0-x64.exe in the PresentMon subdirectory
            string presentMonDir = Path.Combine(pluginDir, "PresentMon");
            string path = Path.Combine(presentMonDir, $"{PresentMonAppName}.exe");
            if (!File.Exists(path))
            {
                Console.WriteLine($"PresentMon not found at: {path}");
                return null;
            }

            Console.WriteLine($"Found PresentMon at: {path}");
            return new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        // Parse PresentMon output line to update FPS and frame time sensors
        private void ProcessOutputLine(string line)
        {
            string[] parts = line.Split(',');
            if (parts.Length < 10)
            {
                Console.WriteLine($"Invalid CSV line: {line}");
                return;
            }

            Console.WriteLine($"Raw frame data: {line}");

            if (float.TryParse(parts[9], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float msBetweenPresents))
            {
                _frameTimeSum += msBetweenPresents;
                _frameCount++;
                if (_frameCount >= SmoothWindow) // Average over 5 frames
                {
                    float avgFrameTime = _frameTimeSum / SmoothWindow;
                    float fps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0;
                    _fpsSensor.Value = fps; // Update UI sensors
                    _frameTimeSensor.Value = avgFrameTime;
                    Console.WriteLine($"Averaged: FrameTime={avgFrameTime:F2}ms, FPS={fps:F2}");
                    _frameTimeSum = 0;
                    _frameCount = 0;
                }
            }
            else
            {
                Console.WriteLine($"Failed to parse MsBetweenPresents: {line}");
            }
        }

        // Stop PresentMon and clean up resources
        private void StopCapture()
        {
            if (_presentMonProcess == null || _presentMonProcess.HasExited)
            {
                Console.WriteLine("PresentMon already stopped or not started.");
                _isMonitoring = false;
                _presentMonProcess = null;
                ResetSensors();
                return;
            }

            try
            {
                Console.WriteLine("Stopping PresentMon...");
                _presentMonProcess.Kill(true); // Force terminate
                _presentMonProcess.WaitForExit(5000); // Wait up to 5s
                if (!_presentMonProcess.HasExited)
                    Console.WriteLine("PresentMon did not exit cleanly after kill.");
                else
                    Console.WriteLine("PresentMon stopped successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping PresentMon: {ex.Message}");
            }
            finally
            {
                _presentMonProcess.Dispose();
                _presentMonProcess = null;
                _isMonitoring = false;
                ResetSensors();
            }

            if (!string.IsNullOrEmpty(_currentSessionName))
            {
                Console.WriteLine($"Stopping ETW session: {_currentSessionName}");
                ExecuteCommand("logman.exe", $"stop {_currentSessionName} -ets", 1000, false);
                _currentSessionName = null;
            }
        }

        // Terminate any existing PresentMon processes on startup
        private void TerminateExistingPresentMonProcesses()
        {
            foreach (var process in Process.GetProcessesByName(PresentMonAppName))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(1000);
                    Console.WriteLine($"Terminated PresentMon PID: {process.Id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to terminate PresentMon PID {process.Id}: {ex.Message}");
                }
            }

            foreach (var process in Process.GetProcessesByName(PresentMonServiceAppName))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(1000);
                    Console.WriteLine($"Terminated PresentMonService PID: {process.Id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to terminate PresentMonService PID {process.Id}: {ex.Message}");
                }
            }
        }

        // Reset sensor values to 0
        private void ResetSensors()
        {
            _fpsSensor.Value = 0;
            _frameTimeSensor.Value = 0;
            _frameTimeSum = 0;
            _frameCount = 0;
        }

        // Execute a command (e.g., sc.exe) with optional output logging
        private void ExecuteCommand(string fileName, string arguments, int timeoutMs, bool logOutput)
        {
            try
            {
                using (var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = logOutput,
                        RedirectStandardError = logOutput
                    }
                })
                {
                    proc.Start();
                    if (logOutput)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        string error = proc.StandardError.ReadToEnd();
                        proc.WaitForExit(timeoutMs);
                        if (proc.ExitCode == 0)
                            Console.WriteLine($"{fileName} {arguments}: {output}");
                        else
                            Console.WriteLine($"{fileName} {arguments} failed: {error}");
                    }
                    else
                    {
                        proc.WaitForExit(timeoutMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to execute {fileName} {arguments}: {ex.Message}");
            }
        }

        // Check if a process exists and is still running
        private bool ProcessExists(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                return proc != null && !proc.HasExited; // Verify process is alive
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"PID {pid} no longer exists."); // Logs when process is gone
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking PID {pid}: {ex.Message}");
                return false;
            }
        }

        public override void Update() { } // No synchronous updates needed

        public override void Close() => Dispose(); // Cleanup on plugin close

        // Dispose resources and ensure clean shutdown
        public void Dispose()
        {
            Console.WriteLine("Cancellation requested.");
            try
            {
                _cts?.Cancel(); // Stop monitoring loop
                if (_isMonitoring || _presentMonProcess != null)
                {
                    Console.WriteLine("Forcing cleanup in Dispose...");
                    StopCapture(); // Stop PresentMon if running
                }
                if (_serviceRunning)
                    StopPresentMonService(); // Stop service if running
                TerminateExistingPresentMonProcesses(); // Kill any leftovers
                ClearETWSessions(); // Clear ETW sessions
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dispose failed: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _currentPid = 0;
                GC.SuppressFinalize(this);
                Console.WriteLine("Dispose completed.");
            }
        }

        // Load sensors into InfoPanel UI
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_frameTimeSensor);
            containers.Add(container);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask; // No async updates needed
        }

        // Detect the active fullscreen process
        private uint GetActiveFullscreenProcessId()
        {
            HWND hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                Console.WriteLine("No foreground window found.");
                return 0;
            }

            if (!User32.GetWindowRect(hWnd, out RECT windowRect))
            {
                Console.WriteLine("Failed to get window rect.");
                return 0;
            }

            HMONITOR hMonitor = User32.MonitorFromWindow(hWnd, User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero)
            {
                Console.WriteLine("No monitor found for window.");
                return 0;
            }

            var monitorInfo = new User32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFO>() };
            if (!User32.GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                Console.WriteLine("Failed to get monitor info.");
                return 0;
            }

            bool isFullscreen = windowRect.Equals(monitorInfo.rcMonitor); // Check if window matches monitor size
            if (!isFullscreen && DwmApi.DwmGetWindowAttribute(hWnd, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extendedFrameBounds).Succeeded)
                isFullscreen = extendedFrameBounds.Equals(monitorInfo.rcMonitor); // Fallback for extended bounds

            User32.GetWindowThreadProcessId(hWnd, out uint pid);
            Console.WriteLine($"Foreground PID: {pid}, Window rect: {windowRect}, Monitor rect: {monitorInfo.rcMonitor}, Fullscreen: {isFullscreen}");

            if (isFullscreen && pid > 4) // Exclude system PIDs (0-4)
            {
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    if (proc.MainWindowHandle != IntPtr.Zero) // Ensure it has a main window
                        return pid;
                    Console.WriteLine($"PID {pid} has no main window.");
                    return 0;
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"PID {pid} is no longer valid.");
                    return 0;
                }
            }

            return 0; // No fullscreen app found
        }
    }
}