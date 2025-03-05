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

namespace InfoPanel.IPFPS
{
    public class IPFpsPlugin : BasePlugin, IDisposable
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _frameTimeSensor = new("frame time", "Frame Time", 0, "ms");

        private Process? _presentMonProcess;
        private CancellationTokenSource? _cts;
        private uint _currentPid;
        private volatile bool _isMonitoring;
        private float _frameTimeSum = 0;
        private int _frameCount = 0;
        private const int SmoothWindow = 5;

        private static readonly string PresentMonAppName = "PresentMon-2.2.0-x64";

        public IPFpsPlugin()
            : base("fps-plugin", "PresentMon FPS", "Retrieves FPS and frame times using PresentMon - v1.0.8")
        {
            _presentMonProcess = null;
            _cts = null;
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            TerminateExistingPresentMonProcesses(); // Clear any leftovers
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => StartMonitoringLoopAsync(_cts.Token));
        }

        private async Task StartMonitoringLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint pid = GetActiveFullscreenProcessId();
                    Console.WriteLine($"Checked for fullscreen PID: {pid}");
                    if (pid != 0 && pid != _currentPid && !_isMonitoring)
                    {
                        _currentPid = pid;
                        await StartCaptureAsync(pid, cancellationToken);
                    }
                    else if (pid == 0 && _currentPid != 0)
                    {
                        StopCapture();
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
                Console.WriteLine($"Unexpected error in monitoring loop: {ex.Message}");
            }
        }

        private async Task StartCaptureAsync(uint pid, CancellationToken cancellationToken)
        {
            if (_presentMonProcess != null)
                StopCapture();

            string sessionName = $"PresentMon_{Guid.NewGuid():N}";
            string arguments = $"--process_id {pid} --output_stdout --terminate_on_proc_exit --stop_existing_session --session_name {sessionName}";
            ProcessStartInfo? startInfo = GetServiceStartInfo(arguments);
            if (startInfo == null)
            {
                Console.WriteLine("Failed to locate PresentMon executable; capture aborted.");
                return;
            }

            _presentMonProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            _presentMonProcess.Exited += (s, e) =>
            {
                Console.WriteLine($"PresentMon process exited with code: {_presentMonProcess?.ExitCode ?? -1}");
                if (_isMonitoring && !cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("PresentMon stopped unexpectedly. Attempting to restart...");
                    Task.Run(() => StartCaptureAsync(pid, cancellationToken)); // Restart if unintended exit
                }
                else
                {
                    StopCapture();
                }
            };

            try
            {
                Console.WriteLine($"Attempting to start PresentMon with args: {arguments}");
                _presentMonProcess.Start();
                _isMonitoring = true;
                Console.WriteLine($"Started PresentMon for PID {pid} with stdout redirection, PresentMon PID: {_presentMonProcess.Id}");

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
                        else if (_presentMonProcess.HasExited)
                        {
                            Console.WriteLine("Output stream ended due to process exit.");
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
                        {
                            Console.WriteLine($"Error: {line}");
                            if (line.Contains("error code 1450"))
                            {
                                Console.WriteLine("Error 1450: Insufficient system resources. Try closing other monitoring tools or restarting your system.");
                            }
                        }
                        else if (_presentMonProcess.HasExited)
                        {
                            Console.WriteLine("Error monitoring stopped.");
                            break;
                        }
                    }
                }, cancellationToken);

                await Task.WhenAny(outputTask, errorTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start PresentMon: {ex.Message}");
                StopCapture();
            }
        }

        private static ProcessStartInfo? GetServiceStartInfo(string arguments)
        {
            string? pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir))
            {
                Console.WriteLine("Failed to determine plugin directory from assembly location.");
                return null;
            }

            string path = Path.Combine(pluginDir, $"{PresentMonAppName}.exe");
            if (!File.Exists(path))
            {
                Console.WriteLine($"PresentMon executable not found at: {path}");
                return null;
            }

            Console.WriteLine($"Found PresentMon executable at: {path}");
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
                Console.WriteLine($"Invalid CSV line (too few columns): {line}");
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
                Console.WriteLine($"Failed to parse MsBetweenPresents from line: {line}");
            }
        }

        private void StopCapture()
        {
            if (_presentMonProcess == null || _presentMonProcess.HasExited)
                return;

            try
            {
                _presentMonProcess.Kill();
                _presentMonProcess.WaitForExit(5000);
                if (!_presentMonProcess.HasExited)
                    Console.WriteLine("PresentMon did not exit cleanly after timeout.");
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
                _currentPid = 0;
                _isMonitoring = false;
                ResetSensors();
            }
        }

        private void TerminateExistingPresentMonProcesses()
        {
            foreach (var process in Process.GetProcessesByName(PresentMonAppName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(1000); // Wait up to 1 second
                    Console.WriteLine($"Terminated existing PresentMon process with PID: {process.Id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to terminate PresentMon PID {process.Id}: {ex.Message}");
                }
            }
        }

        private void ResetSensors()
        {
            _fpsSensor.Value = 0;
            _frameTimeSensor.Value = 0;
            _frameTimeSum = 0;
            _frameCount = 0;
        }

        public override void Update() { }

        public override void Close() => Dispose();

        public void Dispose()
        {
            Console.WriteLine("Cancellation requested.");
            try
            {
                _cts?.Cancel();
                StopCapture();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dispose failed: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                GC.SuppressFinalize(this);
                Console.WriteLine("Dispose completed.");
            }
            Console.WriteLine("Plugin shutdown finalized.");
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_frameTimeSensor);
            containers.Add(container);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private uint GetActiveFullscreenProcessId()
        {
            HWND hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return 0;

            if (!User32.GetWindowRect(hWnd, out RECT windowRect)) return 0;

            HMONITOR hMonitor = User32.MonitorFromWindow(hWnd, User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return 0;

            var monitorInfo = new User32.MONITORINFO { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFO>() };
            if (!User32.GetMonitorInfo(hMonitor, ref monitorInfo)) return 0;

            bool isFullscreen = windowRect.Equals(monitorInfo.rcMonitor);
            if (!isFullscreen && DwmApi.DwmGetWindowAttribute(hWnd, DwmApi.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extendedFrameBounds).Succeeded)
                isFullscreen = extendedFrameBounds.Equals(monitorInfo.rcMonitor);

            if (isFullscreen)
            {
                User32.GetWindowThreadProcessId(hWnd, out uint pid);
                try
                {
                    return pid > 4 && Process.GetProcessById((int)pid).MainWindowHandle != IntPtr.Zero ? pid : 0;
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"PID is no longer valid.");
                    return 0;
                }
            }

            Console.WriteLine($"Window rect {windowRect} does not match monitor rect {monitorInfo.rcMonitor}");
            return 0;
        }
    }
}