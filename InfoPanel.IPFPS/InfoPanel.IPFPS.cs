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
 * Version: 1.2.0
 * Description: A plugin for InfoPanel to monitor and display FPS and frame times of fullscreen applications using PresentMon. Detects fullscreen processes, launches PresentMon to capture performance data, and updates sensors every second. Includes robust cleanup of PresentMon processes and ETW sessions on app exit or InfoPanel shutdown.
 * Changelog:
 *   - v1.2.0 (Mar 7, 2025): Fixed FPS hang with ReShade, prevented restarts, ensured full cleanup.
 *     - **Changes**: Enhanced `ProcessExists` with window check, added 10s cleanup timeout, ensured PresentMon terminates, added ReShade detection, prevented restarts by filtering InfoPanel PID and non-game apps, improved cleanup with retries and verification.
 *     - **Purpose**: Resolve hangs with ReShade, stop unwanted restarts, ensure all processes and ETW sessions terminate.
 *   - v1.1.0 (Mar 5, 2025): Finalized robust cleanup and fullscreen detection.
 *     - **Changes**: Consolidated cleanup to `StartCaptureAsync`, added `ProcessExists` with `HasExited` check, removed redundant monitoring loop cleanup.
 *     - **Purpose**: Ensures reliable shutdown of PresentMon and service, cleaner logs, stable FPS (~140-175 FPS in Arma Reforger).
 * Note: Requires PresentMon-2.3.0-x64.exe and PresentMonService.exe in 'PresentMon' subdirectory. Admin rights needed for service management.
 */

namespace InfoPanel.IPFPS
{
    public class IPFpsPlugin : BasePlugin, IDisposable
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _frameTimeSensor = new("frame time", "Frame Time", 0, "ms");

        private Process? _presentMonProcess;
        private CancellationTokenSource? _cts;
        private Task? _monitoringTask;
        private uint _currentPid;
        private volatile bool _isMonitoring;
        private float _frameTimeSum = 0;
        private int _frameCount = 0;
        private const int SmoothWindow = 5;
        private const string ServiceName = "InfoPanelPresentMonService";
        private string? _currentSessionName;
        private bool _serviceRunning = false;
        private readonly int _infoPanelPid; // Store InfoPanel's PID to exclude it

        private static readonly string PresentMonAppName = "PresentMon-2.3.0-x64";
        private static readonly string PresentMonServiceAppName = "PresentMonService";

        public IPFpsPlugin()
            : base("fps-plugin", "PresentMon FPS", "Retrieves FPS and frame times using PresentMon - v1.2.0")
        {
            _presentMonProcess = null;
            _cts = null;
            _monitoringTask = null;
            _infoPanelPid = Process.GetCurrentProcess().Id; // Capture InfoPanel's PID
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            Console.WriteLine("Initializing IPFpsPlugin...");
            TerminateExistingPresentMonProcesses();
            ClearETWSessions();
            _cts = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => StartMonitoringLoopAsync(_cts.Token));
            Console.WriteLine("Monitoring task started.");
        }

        private void StartPresentMonService()
        {
            if (_serviceRunning) return;

            string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir))
            {
                Console.WriteLine("Failed to determine plugin directory.");
                return;
            }

            string presentMonDir = Path.Combine(pluginDir, "PresentMon");
            string servicePath = Path.Combine(presentMonDir, $"{PresentMonServiceAppName}.exe");
            if (!File.Exists(servicePath))
            {
                Console.WriteLine($"PresentMonService not found at: {servicePath}");
                return;
            }

            try
            {
                ExecuteCommand("sc.exe", $"stop {ServiceName}", 5000, false);
                ExecuteCommand("sc.exe", $"delete {ServiceName}", 5000, false);

                Console.WriteLine($"Installing PresentMonService as {ServiceName}...");
                ExecuteCommand("sc.exe", $"create {ServiceName} binPath= \"{servicePath}\" start= demand", 5000, true);

                Console.WriteLine($"Starting {ServiceName}...");
                ExecuteCommand("sc.exe", $"start {ServiceName}", 5000, true);

                Thread.Sleep(1000); // To be async later
                _serviceRunning = true;
                Console.WriteLine($"{ServiceName} started successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service setup failed: {ex.Message}");
            }
        }

        private void StopPresentMonService()
        {
            if (!_serviceRunning) return;

            try
            {
                Console.WriteLine($"Stopping {ServiceName}...");
                ExecuteCommand("sc.exe", $"stop {ServiceName}", 15000, true);
                Console.WriteLine($"Waiting for {ServiceName} to stop...");
                Thread.Sleep(2000); // To be async later
                ExecuteCommand("sc.exe", $"delete {ServiceName}", 5000, true);
                _serviceRunning = false;
                Console.WriteLine($"{ServiceName} stopped and deleted.");
                TerminateExistingPresentMonProcesses(); // Ensure stragglers are killed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop service: {ex.Message}");
            }
        }

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
                            ExecuteCommand("logman.exe", $"stop {sessionName} -ets", 1000, true); // Log output
                        }
                    }
                }
                Console.WriteLine("ETW session cleanup completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear ETW sessions: {ex.Message}");
            }
        }

        private async Task StartMonitoringLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine("Starting monitoring loop...");
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint pid = GetActiveFullscreenProcessId();
                    Console.WriteLine($"Checked for fullscreen PID: {pid}");

                    if (pid != 0 && pid != _currentPid && !_isMonitoring)
                    {
                        _currentPid = pid;
                        StartPresentMonService();
                        await StartCaptureAsync(pid, cancellationToken);
                    }

                    await Task.Delay(1000, cancellationToken);
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
            finally
            {
                Console.WriteLine("Monitoring loop exited.");
            }
        }

        private async Task StartCaptureAsync(uint pid, CancellationToken cancellationToken)
        {
            if (_presentMonProcess != null) StopCapture();

            _currentSessionName = $"PresentMon_{Guid.NewGuid():N}";
            string arguments = $"--process_id {pid} --output_stdout --terminate_on_proc_exit --stop_existing_session --session_name {_currentSessionName}";
            ProcessStartInfo? startInfo = GetServiceStartInfo(arguments);
            if (startInfo == null)
            {
                Console.WriteLine("Failed to locate PresentMon executable.");
                return;
            }

            DateTime startTime = DateTime.Now;
            using (_presentMonProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                _presentMonProcess.Exited += (s, e) =>
                {
                    Console.WriteLine($"PresentMon exited with code: {_presentMonProcess?.ExitCode ?? -1}");
                    _isMonitoring = false;
                };

                try
                {
                    Console.WriteLine($"Starting PresentMon with args: {arguments}");
                    if (IsReShadeActive(pid))
                        Console.WriteLine("Warning: ReShade detected, potential interference with PresentMon.");
                    _presentMonProcess.Start();
                    _isMonitoring = true;
                    Console.WriteLine($"Started PresentMon for PID {pid}, PID: {_presentMonProcess.Id}");

                    using var outputReader = _presentMonProcess.StandardOutput;
                    using var errorReader = _presentMonProcess.StandardError;

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
                                ProcessOutputLine(line);
                            }
                            else
                            {
                                Console.WriteLine("Output stream ended.");
                                break;
                            }
                        }
                        Console.WriteLine("Output monitoring stopped.");
                    }, cancellationToken);

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
                        if (DateTime.Now - startTime > TimeSpan.FromSeconds(10) && _isMonitoring && !ProcessExists(pid))
                        {
                            Console.WriteLine($"Timeout: Forcing cleanup for PID {pid} after 10s...");
                            StopCapture();
                            StopPresentMonService();
                            _currentPid = 0;
                            break;
                        }
                    }

                    await Task.WhenAny(outputTask, errorTask);
                    Console.WriteLine("Capture tasks completed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to start or monitor PresentMon: {ex}");
                    StopCapture();
                    _isMonitoring = false;
                }
            }
        }

        private static ProcessStartInfo? GetServiceStartInfo(string arguments)
        {
            string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir)) return null;

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
                if (_frameCount >= SmoothWindow)
                {
                    float avgFrameTime = _frameTimeSum / SmoothWindow;
                    float fps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0;
                    _fpsSensor.Value = fps;
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
                _presentMonProcess.Kill(true);
                _presentMonProcess.WaitForExit(5000);
                if (!_presentMonProcess.HasExited)
                {
                    Console.WriteLine("PresentMon did not exit cleanly after kill, forcing termination.");
                    TerminateExistingPresentMonProcesses(); // Extra kill pass
                }
                else
                {
                    Console.WriteLine("PresentMon stopped successfully.");
                }
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
                Console.WriteLine("Capture cleanup completed.");
            }

            if (!string.IsNullOrEmpty(_currentSessionName))
            {
                Console.WriteLine($"Stopping ETW session: {_currentSessionName}");
                ExecuteCommand("logman.exe", $"stop {_currentSessionName} -ets", 1000, true);
                _currentSessionName = null;
            }
        }

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
            Console.WriteLine("Existing PresentMon processes terminated.");
        }

        private void ResetSensors()
        {
            _fpsSensor.Value = 0;
            _frameTimeSensor.Value = 0;
            _frameTimeSum = 0;
            _frameCount = 0;
            Console.WriteLine("Sensors reset.");
        }

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

        private bool ProcessExists(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                bool exists = proc != null && !proc.HasExited && proc.MainWindowHandle != IntPtr.Zero;
                if (!exists)
                    Console.WriteLine($"PID {pid} no longer exists or has no main window.");
                return exists;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"PID {pid} no longer exists.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking PID {pid}: {ex.Message}");
                return false;
            }
        }

        private bool IsReShadeActive(uint pid)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid);
                foreach (ProcessModule module in proc.Modules)
                {
                    if (module.ModuleName.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"ReShade (dxgi.dll) detected in PID {pid}.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking modules for PID {pid}: {ex.Message}");
            }
            return false;
        }

        public override void Update() { }

        public override void Close() => Dispose();

        public void Dispose()
        {
            Console.WriteLine("Cancellation requested.");
            try
            {
                _cts?.Cancel();
                if (_isMonitoring || _presentMonProcess != null)
                {
                    Console.WriteLine("Forcing cleanup in Dispose...");
                    StopCapture();
                }
                if (_serviceRunning)
                    StopPresentMonService();
                TerminateExistingPresentMonProcesses();
                ClearETWSessions();
                if (_monitoringTask != null)
                {
                    Console.WriteLine("Waiting for monitoring task to complete...");
                    _monitoringTask.Wait(5000);
                    if (!_monitoringTask.IsCompleted)
                        Console.WriteLine("Monitoring task did not complete within 5s.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dispose failed: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _monitoringTask = null;
                _currentPid = 0;
                GC.SuppressFinalize(this);
                Console.WriteLine("Dispose completed.");
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_frameTimeSensor);
            containers.Add(container);
            Console.WriteLine("Sensors loaded into UI.");
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

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

            bool isFullscreen = windowRect.Equals(monitorInfo.rcMonitor);
            if (!isFullscreen && DwmApi.DwmGetWindowAttribute(hWnd, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extendedFrameBounds).Succeeded)
                isFullscreen = extendedFrameBounds.Equals(monitorInfo.rcMonitor);

            User32.GetWindowThreadProcessId(hWnd, out uint pid);
            Console.WriteLine($"Foreground PID: {pid}, Window rect: {windowRect}, Monitor rect: {monitorInfo.rcMonitor}, Fullscreen: {isFullscreen}");

            if (isFullscreen && pid > 4 && pid != _infoPanelPid) // Exclude system PIDs and InfoPanel
            {
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        string exeName = proc.ProcessName.ToLower();
                        // Filter for plausible game executables (add more as needed)
                        if (exeName.Contains("sons") || exeName.Contains("game") || exeName.Contains("arma") || exeName.Contains("forest"))
                        {
                            Console.WriteLine($"Detected game process: {exeName}");
                            return pid;
                        }
                        Console.WriteLine($"PID {pid} ({exeName}) is fullscreen but not a recognized game.");
                    }
                    else
                    {
                        Console.WriteLine($"PID {pid} has no main window.");
                    }
                    return 0;
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"PID {pid} is no longer valid.");
                    return 0;
                }
            }

            return 0;
        }
    }
}