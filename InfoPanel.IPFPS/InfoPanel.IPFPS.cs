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
 * Version: 1.2.5
 * Description: A plugin for InfoPanel to monitor and display FPS and frame times of fullscreen/borderless applications using PresentMon.
 * Changelog:
 *   [1.2.5] - 2025-03-09
 *      - Improved: Replaced synchronous `process.WaitForExit(timeout)` with asynchronous `WaitForExitAsync(cancellationToken)` and `Task.WhenAny` for timeouts in `ExecuteCommandAsync` and `StopCaptureAsync`.
 *      - Optimized: Updated `ProcessExists` and `GetProcessName` to use `Process.GetProcessById` with exception handling instead of iterating all processes, improving performance.
 *      - Refactored: Consolidated cleanup logic in `Dispose` to call a unified `CleanupAsync` method, streamlining capture stop, service shutdown, and ETW session clearing.
 *      - Enhanced: Added try-catch blocks to output and error reading tasks in `StartCaptureAsync` for better exception handling during asynchronous stream reads.
 *      - Tweaked: Simplified fullscreen detection in `GetActiveFullscreenProcessId` with early exits and additional logging to reduce unnecessary API calls.
 */

namespace InfoPanel.IPFPS
{
    public static class ProcessExtensions
    {
        public static Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => tcs.TrySetResult(true);
            if (cancellationToken != default)
                cancellationToken.Register(() => tcs.TrySetCanceled());
            if (process.HasExited)
                tcs.TrySetResult(true);
            return tcs.Task;
        }
    }

    public class IPFpsPlugin : BasePlugin, IDisposable
    {
        // Sensors
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
            : base("fps-plugin", "PresentMon FPS", "Retrieves FPS and frame times using PresentMon - v1.2.5")
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
            TerminateExistingPresentMonProcessesAsync(CancellationToken.None).GetAwaiter().GetResult();
            ClearETWSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
            _cts = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => StartMonitoringLoopAsync(_cts.Token));
            Console.WriteLine("Monitoring task started.");
        }

        private static async Task<bool> StartAsync(Process process)
        {
            var tcs = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) =>
            {
                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(process.ExitCode == 0);
            };
            try
            {
                if (!process.Start())
                {
                    Console.WriteLine($"Process failed to start: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    tcs.TrySetResult(false);
                    return await tcs.Task;
                }
                Console.WriteLine($"Process started: {process.StartInfo.FileName} {process.StartInfo.Arguments}, PID: {process.Id}");
                tcs.TrySetResult(true);
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"Failed to start process {process.StartInfo.FileName}: {ex} (Error Code: {ex.NativeErrorCode})");
                tcs.TrySetResult(false);
            }
            return await tcs.Task;
        }

        private async Task ExecuteCommandAsync(string fileName, string arguments, int timeoutMs, bool logOutput, CancellationToken cancellationToken)
        {
            try
            {
                using var proc = new Process
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
                };

                bool started = await StartAsync(proc).ConfigureAwait(false);
                if (!started)
                {
                    Console.WriteLine($"Command {fileName} {arguments} failed to start.");
                    return;
                }

                if (logOutput)
                {
                    var exitTask = proc.WaitForExitAsync(cancellationToken);
                    var delayTask = Task.Delay(timeoutMs, cancellationToken);
                    if (await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false) == exitTask)
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                        string error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        if (proc.ExitCode == 0)
                            Console.WriteLine($"{fileName} {arguments}: {output}");
                        else
                            Console.WriteLine($"{fileName} {arguments} failed: {error}");
                    }
                    else
                    {
                        Console.WriteLine($"{fileName} {arguments} timed out after {timeoutMs}ms.");
                        try
                        {
                            proc.Kill(true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to kill process {fileName}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    var exitTask = proc.WaitForExitAsync(cancellationToken);
                    var delayTask = Task.Delay(timeoutMs, cancellationToken);
                    if (await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false) != exitTask)
                    {
                        Console.WriteLine($"{fileName} {arguments} timed out after {timeoutMs}ms.");
                        try
                        {
                            proc.Kill(true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to kill process {fileName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to execute {fileName} {arguments}: {ex.Message}");
            }
        }

        private async Task StartPresentMonServiceAsync(CancellationToken cancellationToken)
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
                await ExecuteCommandAsync("sc.exe", $"stop {ServiceName}", 5000, false, cancellationToken).ConfigureAwait(false);
                await ExecuteCommandAsync("sc.exe", $"delete {ServiceName}", 5000, false, cancellationToken).ConfigureAwait(false);

                Console.WriteLine($"Installing PresentMonService as {ServiceName}...");
                await ExecuteCommandAsync("sc.exe", $"create {ServiceName} binPath= \"{servicePath}\" start= demand", 5000, true, cancellationToken).ConfigureAwait(false);

                Console.WriteLine($"Starting {ServiceName}...");
                await ExecuteCommandAsync("sc.exe", $"start {ServiceName}", 5000, true, cancellationToken).ConfigureAwait(false);

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                _serviceRunning = true;
                Console.WriteLine($"{ServiceName} started successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Service setup failed: {ex.Message}");
            }
        }

        private async Task StopPresentMonServiceAsync(CancellationToken cancellationToken)
        {
            if (!_serviceRunning) return;

            try
            {
                Console.WriteLine($"Stopping {ServiceName}...");
                await ExecuteCommandAsync("sc.exe", $"stop {ServiceName}", 15000, true, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Waiting for {ServiceName} to stop...");
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

                using var proc = new Process
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
                };
                bool started = await StartAsync(proc).ConfigureAwait(false);
                if (started)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    if (!output.Contains("STOPPED"))
                    {
                        Console.WriteLine($"{ServiceName} did not stop cleanly, forcing termination...");
                        await TerminateExistingPresentMonProcessesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await ExecuteCommandAsync("sc.exe", $"delete {ServiceName}", 5000, true, cancellationToken).ConfigureAwait(false);
                _serviceRunning = false;
                Console.WriteLine($"{ServiceName} stopped and deleted.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to stop service: {ex.Message}");
                await TerminateExistingPresentMonProcessesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ClearETWSessionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var proc = new Process
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
                };
                bool started = await StartAsync(proc).ConfigureAwait(false);
                if (started)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    string[] lines = output.Split('\n');
                    foreach (string line in lines)
                    {
                        if (line.Contains("PresentMon"))
                        {
                            string sessionName = line.Trim().Split(' ')[0];
                            Console.WriteLine($"Found ETW session: {sessionName}, stopping...");
                            await ExecuteCommandAsync("logman.exe", $"stop {sessionName} -ets", 1000, true, cancellationToken).ConfigureAwait(false);
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

        private async Task TerminateExistingPresentMonProcessesAsync(CancellationToken cancellationToken)
        {
            // Terminate PresentMon processes
            foreach (var process in Process.GetProcessesByName(PresentMonAppName))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"Terminated PresentMon PID: {process.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to terminate PresentMon PID {process.Id}: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Terminate PresentMonService processes
            foreach (var process in Process.GetProcessesByName(PresentMonServiceAppName))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"Terminated PresentMonService PID: {process.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to terminate PresentMonService PID {process.Id}: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
            Console.WriteLine("Existing PresentMon processes terminated.");
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
                        await StartPresentMonServiceAsync(cancellationToken).ConfigureAwait(false);
                        await StartCaptureAsync(pid, cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
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
            if (_presentMonProcess != null)
            {
                await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
            }

            _currentSessionName = $"PresentMon_{Guid.NewGuid():N}";
            string arguments = $"--process_id {pid} --output_stdout --terminate_on_proc_exit --stop_existing_session --session_name {_currentSessionName}";
            ProcessStartInfo? startInfo = GetServiceStartInfo(arguments);
            if (startInfo == null)
            {
                Console.WriteLine("Failed to locate PresentMon executable.");
                return;
            }

            DateTime startTime = DateTime.Now;
            _presentMonProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _presentMonProcess.Exited += (s, e) =>
            {
                Console.WriteLine($"PresentMon exited with code: {_presentMonProcess?.ExitCode ?? -1}");
                _isMonitoring = false;
            };

            try
            {
                Console.WriteLine($"Starting PresentMon with args: {arguments}");
                string? processName = GetProcessName(pid);
                if (processName != null && IsReShadeActive(pid))
                {
                    Console.WriteLine($"Warning: ReShade detected in process {processName} (PID {pid}), potential interference with PresentMon.");
                }

                bool started = await StartAsync(_presentMonProcess).ConfigureAwait(false);
                if (!started)
                {
                    Console.WriteLine("PresentMon failed to start.");
                    return;
                }
                _isMonitoring = true;

                // Asynchronously monitor output and error streams
                var outputReader = _presentMonProcess.StandardOutput;
                var errorReader = _presentMonProcess.StandardError;

                var outputTask = Task.Run(async () =>
                {
                    bool headerSkipped = false;
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested && _presentMonProcess != null)
                        {
                            if (_presentMonProcess.HasExited) // Check before reading
                            {
                                Console.WriteLine("PresentMon has exited, stopping output read.");
                                break;
                            }
                            string? line = await outputReader.ReadLineAsync().ConfigureAwait(false);
                            if (line != null)
                            {
                                Console.WriteLine($"PresentMon output: {line}");
                                if (!headerSkipped && line.StartsWith("Application", StringComparison.OrdinalIgnoreCase))
                                {
                                    headerSkipped = true;
                                    Console.WriteLine("Skipped PresentMon CSV header.");
                                    continue;
                                }
                                if (headerSkipped)
                                    ProcessOutputLine(line);
                            }
                            else
                            {
                                Console.WriteLine("Output stream ended.");
                                break;
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("has exited"))
                    {
                        Console.WriteLine($"Output read stopped: PresentMon process has exited - {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading output: {ex.Message}");
                    }
                    Console.WriteLine("Output monitoring stopped.");
                }, cancellationToken);

                var errorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!cancellationToken.IsCancellationRequested && _presentMonProcess != null && !_presentMonProcess.HasExited)
                        {
                            string? line = await errorReader.ReadLineAsync().ConfigureAwait(false);
                            if (line != null)
                                Console.WriteLine($"PresentMon error: {line}");
                            else
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading error stream: {ex.Message}");
                    }
                }, cancellationToken);

                // Poll for target process existence and timeout
                while (!cancellationToken.IsCancellationRequested && _isMonitoring)
                {
                    bool processStillExists = ProcessExists(pid);
                    if (!processStillExists)
                    {
                        Console.WriteLine($"Target PID {pid} gone, initiating cleanup...");
                        await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
                        await StopPresentMonServiceAsync(cancellationToken).ConfigureAwait(false);
                        _currentPid = 0;
                        break;
                    }
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    if (DateTime.Now - startTime > TimeSpan.FromSeconds(10) && _isMonitoring && !processStillExists)
                    {
                        Console.WriteLine($"Timeout: Forcing cleanup for PID {pid} after 10s...");
                        await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
                        await StopPresentMonServiceAsync(cancellationToken).ConfigureAwait(false);
                        _currentPid = 0;
                        break;
                    }
                }

                await Task.WhenAny(outputTask, errorTask).ConfigureAwait(false);
                Console.WriteLine("Capture tasks completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start or monitor PresentMon: {ex}");
                await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
                _isMonitoring = false;
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

        private async Task StopCaptureAsync(CancellationToken cancellationToken)
        {
            if (_presentMonProcess == null || _presentMonProcess.HasExited)
            {
                Console.WriteLine("PresentMon already stopped or not started.");
                _isMonitoring = false;
                _presentMonProcess?.Dispose();
                _presentMonProcess = null;
                ResetSensors();
                return;
            }

            try
            {
                Console.WriteLine("Stopping PresentMon...");
                _presentMonProcess.Kill(true);
                await _presentMonProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (!_presentMonProcess.HasExited)
                {
                    Console.WriteLine("PresentMon did not exit cleanly after kill, forcing termination.");
                    await TerminateExistingPresentMonProcessesAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine("PresentMon stopped successfully.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping PresentMon: {ex.Message}");
                await TerminateExistingPresentMonProcessesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _presentMonProcess?.Dispose();
                _presentMonProcess = null;
                _isMonitoring = false;
                ResetSensors();
                Console.WriteLine("Capture cleanup completed.");
            }

            if (!string.IsNullOrEmpty(_currentSessionName))
            {
                Console.WriteLine($"Stopping ETW session: {_currentSessionName}");
                await ExecuteCommandAsync("logman.exe", $"stop {_currentSessionName} -ets", 1000, true, cancellationToken).ConfigureAwait(false);
                _currentSessionName = null;
            }
        }

        private void ResetSensors()
        {
            _fpsSensor.Value = 0;
            _frameTimeSensor.Value = 0;
            _frameTimeSum = 0;
            _frameCount = 0;
            Console.WriteLine("Sensors reset.");
        }

        private bool ProcessExists(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                bool exists = !proc.HasExited && proc.MainWindowHandle != IntPtr.Zero;
                if (!exists)
                    Console.WriteLine($"PID {pid} no longer exists or has no main window.");
                return exists;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"PID {pid} is no longer valid.");
                return false;
            }
        }

        private string? GetProcessName(uint pid)
        {
            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"PID {pid} no longer valid for process name check.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get process name for PID {pid}: {ex.Message}");
                return null;
            }
        }

        private bool IsReShadeActive(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.HasExited)
                {
                    Console.WriteLine($"PID {pid} has exited, skipping ReShade check.");
                    return false;
                }

                if (!CanAccessModules(proc))
                {
                    Console.WriteLine($"Unable to check modules for PID {pid} due to access denial; assuming no ReShade interference.");
                    return false;
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
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to perform ReShade check for PID {pid}: {ex.Message}");
                return false;
            }
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

        private async Task CleanupAsync(CancellationToken cancellationToken)
        {
            if (_isMonitoring || _presentMonProcess != null)
            {
                Console.WriteLine("Forcing capture cleanup in Dispose...");
                await StopCaptureAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_serviceRunning)
            {
                Console.WriteLine("Ensuring service shutdown...");
                await StopPresentMonServiceAsync(cancellationToken).ConfigureAwait(false);
            }
            await TerminateExistingPresentMonProcessesAsync(cancellationToken).ConfigureAwait(false);
            await ClearETWSessionsAsync(cancellationToken).ConfigureAwait(false);
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
                    catch (AggregateException ex)
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
                CleanupAsync(CancellationToken.None).GetAwaiter().GetResult();
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

                Vanara.PInvoke.POINT topLeft = new() { X = clientRect.Left, Y = clientRect.Top };
                Vanara.PInvoke.POINT bottomRight = new() { X = clientRect.Right, Y = clientRect.Bottom };
                User32.ClientToScreen(hWnd, ref topLeft);
                User32.ClientToScreen(hWnd, ref bottomRight);
                Vanara.PInvoke.RECT clientScreenRect = new()
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
                bool isMaximized = User32.IsZoomed(hWnd);

                bool isFullscreen = clientScreenRect.Equals(monitorInfo.rcMonitor) && !hasCaptionOrBorders;
                bool isBorderlessCandidate = areaMatch >= 0.98f && hWnd == foregroundWindow && !hasCaptionOrBorders && !isMaximized;

                if (clientArea < 1000 || clientScreenRect.Left < monitorInfo.rcMonitor.Left - 100 || clientScreenRect.Top < monitorInfo.rcMonitor.Top - 100)
                    return true;

                StringBuilder className = new(256);
                int classLength = GetClassName(hWnd, className, className.Capacity);
                string windowClass = classLength > 0 ? className.ToString().ToLower() : "unknown";

                User32.GetWindowThreadProcessId(hWnd, out uint pid);
                string processName = "unknown";
                try
                {
                    using Process proc = Process.GetProcessById((int)pid);
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

                Console.WriteLine($"Window HWND: {hWnd}, PID: {pid}, Class: {windowClass}, Process: {processName}, ClientRect: {clientScreenRect}, Monitor: {monitorInfo.rcMonitor}, Fullscreen: {isFullscreen}, AreaMatch: {areaMatch:F2}, HasCaptionOrBorders: {hasCaptionOrBorders}, IsMaximized: {isMaximized}");

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