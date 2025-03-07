using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using InfoPanel.Plugins;
using Vanara.PInvoke;
using System.ComponentModel;

/*
 * Plugin: PresentMon FPS - IPFpsPlugin
 * Version: 1.2.0
 * Description: A plugin for InfoPanel to monitor and display FPS and frame times of fullscreen/borderless applications using PresentMon.
 * Changelog:
 *   - v1.2.0 (Mar 7, 2025): Stable release with universal fullscreen detection and robust cleanup.
 *     - Added comprehensive fullscreen/borderless window detection using window styles and client area matching.
 *     - Implemented robust PresentMon service management with start/stop and ETW session cleanup.
 *     - Enhanced anti-cheat safety by avoiding unnecessary module enumeration in most cases.
 *     - Silenced most debugger exception noise:
 *       - Replaced Process.GetProcessById with Process.GetProcesses in ProcessExists to avoid ArgumentException.
 *       - Simplified IsReShadeActive to check process state first, reducing Win32Exception occurrences.
 *       - Suppressed ArgumentException logging in ProcessExists for expected game exits.
 *     - Fixed type mismatch warnings (CS1503) by using Vanara.PInvoke.RECT and POINT structs.
 *     - Improved cleanup with timeout checks and forced termination of lingering processes.
 *     - Added ReShade detection with fallback assumption on access denial for safety.
 *     - Optimized FPS and frame time averaging over a 5-frame window.
 *   - v1.1.0 (Pre-release): Initial PresentMon integration with basic FPS monitoring.
 *   - v1.0.0 (Pre-release): Basic plugin structure for InfoPanel.
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
        private readonly uint _selfPid;
        private volatile bool _isMonitoring;
        private float _frameTimeSum = 0;
        private int _frameCount = 0;
        private const int SmoothWindow = 5;
        private const string ServiceName = "InfoPanelPresentMonService";
        private string? _currentSessionName;
        private bool _serviceRunning = false;

        private static readonly string PresentMonAppName = "PresentMon-2.3.0-x64";
        private static readonly string PresentMonServiceAppName = "PresentMonService";

        private const int GWL_STYLE = -16;
        private const uint WS_CAPTION = 0x00C00000; // Title bar
        private const uint WS_THICKFRAME = 0x00040000; // Resizable border

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowLong(HWND hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(HWND hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(HWND hWnd, out Vanara.PInvoke.RECT lpRect);

        public IPFpsPlugin()
            : base("fps-plugin", "PresentMon FPS", "Retrieves FPS and frame times using PresentMon - v1.2.0")
        {
            _selfPid = (uint)Process.GetCurrentProcess().Id;
            _presentMonProcess = null;
            _cts = null;
            _monitoringTask = null;
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

                Thread.Sleep(1000);
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
                Thread.Sleep(2000);

                using (var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = $"query {ServiceName}",
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
                    if (!output.Contains("STOPPED"))
                    {
                        Console.WriteLine($"{ServiceName} did not stop cleanly, forcing termination...");
                        TerminateExistingPresentMonProcesses();
                    }
                }

                ExecuteCommand("sc.exe", $"delete {ServiceName}", 5000, true);
                _serviceRunning = false;
                Console.WriteLine($"{ServiceName} stopped and deleted.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop service: {ex.Message}");
                TerminateExistingPresentMonProcesses();
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
                            ExecuteCommand("logman.exe", $"stop {sessionName} -ets", 1000, true);
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
                        Console.WriteLine("Warning: ReShade detected or assumed, potential interference with PresentMon.");
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
                    Console.WriteLine($"Averaged: FrameTime={avgFrameTime:F2}ms, FPS={fps:F2}", System.Globalization.CultureInfo.InvariantCulture);
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
                    TerminateExistingPresentMonProcesses();
                }
                else
                {
                    Console.WriteLine("PresentMon stopped successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping PresentMon: {ex.Message}");
                TerminateExistingPresentMonProcesses();
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
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if ((uint)proc.Id == pid)
                    {
                        bool exists = !proc.HasExited && proc.MainWindowHandle != IntPtr.Zero;
                        if (!exists)
                            Console.WriteLine($"PID {pid} no longer exists or has no main window.");
                        return exists;
                    }
                }
                finally
                {
                    proc.Dispose();
                }
            }
            return false;
        }

        private bool IsReShadeActive(uint pid)
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    if ((uint)proc.Id != pid) continue;

                    if (proc.HasExited)
                    {
                        Console.WriteLine($"PID {pid} has exited, skipping ReShade check.");
                        return false;
                    }

                    if (!CanAccessModules(proc))
                    {
                        Console.WriteLine($"Unable to check modules for PID {pid} due to access denial; assuming potential ReShade presence for safety.");
                        return true;
                    }

                    foreach (ProcessModule module in proc.Modules)
                    {
                        if (module.ModuleName.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"ReShade (dxgi.dll) detected in PID {pid}.");
                            return true;
                        }
                    }
                    return false;
                }
                finally
                {
                    proc.Dispose();
                }
            }
            Console.WriteLine($"PID {pid} not found for ReShade check.");
            return false;
        }

        private bool CanAccessModules(Process proc)
        {
            try
            {
                var modules = proc.Modules; // Test access
                return true;
            }
            catch (Win32Exception ex) when (ex.Message.Contains("Access is denied"))
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override void Update() { }

        public override void Close() => Dispose();

        private void Cleanup()
        {
            if (_isMonitoring || _presentMonProcess != null)
            {
                Console.WriteLine("Forcing capture cleanup in Dispose...");
                StopCapture();
            }
            if (_serviceRunning)
            {
                Console.WriteLine("Ensuring service shutdown...");
                StopPresentMonService();
            }
            TerminateExistingPresentMonProcesses();
            ClearETWSessions();
        }

        public void Dispose()
        {
            Console.WriteLine("Cancellation requested.");
            try
            {
                _cts?.Cancel();
                if (_monitoringTask != null)
                {
                    Console.WriteLine("Waiting for monitoring task to complete...");
                    try
                    {
                        Task.WaitAll(new[] { _monitoringTask }, 5000);
                        if (!_monitoringTask.IsCompleted)
                            Console.WriteLine("Monitoring task did not complete within 5s, forcing cleanup.");
                    }
                    catch (Exception ex) when (ex is AggregateException || ex is TaskCanceledException)
                    {
                        Console.WriteLine($"Monitoring task cancelled or failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dispose encountered an error: {ex.Message}");
            }
            finally
            {
                Cleanup();
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

        private delegate bool EnumWindowsProc(HWND hWnd, IntPtr lParam);

        private uint GetActiveFullscreenProcessId()
        {
            uint fullscreenPid = 0;
            HWND foregroundWindow = User32.GetForegroundWindow();

            EnumWindows((hWnd, lParam) =>
            {
                if (!User32.IsWindowVisible(hWnd))
                    return true;

                if (!User32.GetWindowRect(hWnd, out Vanara.PInvoke.RECT windowRect))
                {
                    Console.WriteLine($"Failed to get window rect for HWND {hWnd}.");
                    return true;
                }

                HMONITOR hMonitor = User32.MonitorFromWindow(hWnd, User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero)
                {
                    Console.WriteLine($"No monitor found for HWND {hWnd}.");
                    return true;
                }

                var monitorInfo = new User32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFO>() };
                if (!User32.GetMonitorInfo(hMonitor, ref monitorInfo))
                {
                    Console.WriteLine($"Failed to get monitor info for HWND {hWnd}.");
                    return true;
                }

                if (!GetClientRect(hWnd, out Vanara.PInvoke.RECT clientRect))
                {
                    Console.WriteLine($"Failed to get client rect for HWND {hWnd}.");
                    return true;
                }

                Vanara.PInvoke.POINT topLeft = new Vanara.PInvoke.POINT { X = clientRect.Left, Y = clientRect.Top };
                Vanara.PInvoke.POINT bottomRight = new Vanara.PInvoke.POINT { X = clientRect.Right, Y = clientRect.Bottom };
                User32.ClientToScreen(hWnd, ref topLeft);
                User32.ClientToScreen(hWnd, ref bottomRight);
                Vanara.PInvoke.RECT clientScreenRect = new Vanara.PInvoke.RECT
                {
                    Left = topLeft.X,
                    Top = topLeft.Y,
                    Right = bottomRight.X,
                    Bottom = bottomRight.Y
                };

                int clientArea = (clientScreenRect.Right - clientScreenRect.Left) * (clientScreenRect.Bottom - clientScreenRect.Top);
                int monitorArea = (monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left) * (monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top);
                float areaMatch = (float)clientArea / monitorArea;

                uint style = GetWindowLong(hWnd, GWL_STYLE);
                bool hasCaptionOrBorders = (style & WS_CAPTION) != 0 || (style & WS_THICKFRAME) != 0;

                bool isFullscreen = clientScreenRect.Equals(monitorInfo.rcMonitor);
                bool isBorderlessCandidate = areaMatch >= 0.98f && hWnd == foregroundWindow && !hasCaptionOrBorders;

                if (clientArea < 1000 || clientScreenRect.Left < monitorInfo.rcMonitor.Left - 100 || clientScreenRect.Top < monitorInfo.rcMonitor.Top - 100)
                    return true;

                StringBuilder className = new StringBuilder(256);
                int classLength = GetClassName(hWnd, className, className.Capacity);
                string windowClass = classLength > 0 ? className.ToString().ToLower() : "unknown";

                User32.GetWindowThreadProcessId(hWnd, out uint pid);
                string processName = "unknown";
                try
                {
                    var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName.ToLower();

                    if (IsSystemUIWindow(processName, windowClass))
                    {
                        Console.WriteLine($"Skipping system UI window: {processName} ({windowClass})");
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"PID {pid} no longer valid.");
                    return true;
                }

                Console.WriteLine($"Window HWND: {hWnd}, PID: {pid}, Class: {windowClass}, Process: {processName}, ClientRect: {clientScreenRect}, Monitor: {monitorInfo.rcMonitor}, Fullscreen: {isFullscreen}, AreaMatch: {areaMatch:F2}, HasCaptionOrBorders: {hasCaptionOrBorders}");

                if ((isFullscreen || isBorderlessCandidate) && pid > 4 && pid != _selfPid)
                {
                    fullscreenPid = pid;
                    Console.WriteLine($"Detected potential game: {processName} ({windowClass}), Fullscreen: {isFullscreen}, Borderless: {isBorderlessCandidate}");
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return fullscreenPid;
        }

        private bool IsSystemUIWindow(string processName, string windowClass)
        {
            string[] systemProcesses = { "textinputhost", "explorer", "dwm", "sihost", "shellhost" };
            string[] systemClasses = { "windows.ui.core.corewindow", "applicationframewindow" };

            bool isSystem = systemProcesses.Contains(processName.ToLower()) || systemClasses.Contains(windowClass.ToLower());
            if (isSystem)
            {
                Console.WriteLine($"Identified system UI: {processName} ({windowClass})");
            }
            return isSystem;
        }
    }
}