using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using InfoPanel.Presentmon.Models;
using InfoPanel.Presentmon.Services;

namespace InfoPanel.Presentmon
{
    /// <summary>
    /// InfoPanel plugin for monitoring FPS and performance metrics using PresentMon DLL
    /// Version 2.0.0 - Refactored to use direct DLL integration with service architecture
    /// </summary>
    public class IPFpsPlugin : BasePlugin, IDisposable
    {
        #region Sensors
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _frameTimeSensor = new("frame time", "Frame Time", 0, "ms");
        private readonly PluginSensor _gpuLatencySensor = new("gpu latency", "GPU Latency", 0, "ms");
        private readonly PluginSensor _gpuTimeSensor = new("gpu time", "GPU Time", 0, "ms");
        private readonly PluginSensor _gpuBusySensor = new("gpu busy", "GPU Busy", 0, "ms");
        private readonly PluginSensor _gpuWaitSensor = new("gpu wait", "GPU Wait", 0, "ms");
        private readonly PluginSensor _displayLatencySensor = new("display latency", "Display Latency", 0, "ms");
        private readonly PluginSensor _cpuBusySensor = new("cpu busy", "CPU Busy", 0, "ms");
        private readonly PluginSensor _cpuWaitSensor = new("cpu wait", "CPU Wait", 0, "ms");
        private readonly PluginSensor _gpuUtilizationSensor = new("gpu utilization", "GPU Utilization", 0, "%");
        private readonly PluginSensor _onePercentLowSensor = new("1% low", "1% Low FPS", 0, "FPS");
        private readonly PluginSensor _zeroPointOnePercentLowSensor = new("0.1% low", "0.1% Low FPS", 0, "FPS");
        private readonly PluginText _windowTitle = new("windowtitle", "Currently Capturing", "Nothing to capture");
        #endregion

        #region Services
        private readonly PresentMonService _presentMonService;
        private readonly FullscreenDetectionService _fullscreenDetectionService;
        private readonly FrameProcessingConfig _config;
        #endregion

        #region State
        private CancellationTokenSource? _monitoringCancellationTokenSource;
        private Task? _monitoringTask;
        private MonitoringState _currentState = new();
        private bool _disposed = false;
        #endregion

        public IPFpsPlugin()
            : base("fps-plugin", "PresentMon FPS", "Real-time FPS and performance monitoring using PresentMon DLL - v2.0.0")
        {
            _config = new FrameProcessingConfig();
            _presentMonService = new PresentMonService(_config);
            _fullscreenDetectionService = new FullscreenDetectionService();
            
            // Subscribe to metrics updates
            _presentMonService.MetricsUpdated += OnMetricsUpdated;
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            Console.WriteLine("Initializing IPFpsPlugin v2.0.0...");
            
            // Start monitoring task
            _monitoringCancellationTokenSource = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => StartMonitoringLoopAsync(_monitoringCancellationTokenSource.Token));
            
            Console.WriteLine("Plugin initialized successfully");
        }

        public override void Update()
        {
            // Sensor updates are handled by the PresentMonService via events
            // This method intentionally left empty
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            // All updates are handled asynchronously by the monitoring loop
            return Task.CompletedTask;
        }

        #region Plugin Interface Implementation
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_frameTimeSensor);
            container.Entries.Add(_gpuLatencySensor);
            container.Entries.Add(_gpuTimeSensor);
            container.Entries.Add(_gpuBusySensor);
            container.Entries.Add(_gpuWaitSensor);
            container.Entries.Add(_displayLatencySensor);
            container.Entries.Add(_cpuBusySensor);
            container.Entries.Add(_cpuWaitSensor);
            container.Entries.Add(_gpuUtilizationSensor);
            container.Entries.Add(_onePercentLowSensor);
            container.Entries.Add(_zeroPointOnePercentLowSensor);
            container.Entries.Add(_windowTitle);
            containers.Add(container);
            Console.WriteLine("Sensors loaded into UI.");
        }

        public override void Close()
        {
            Dispose();
        }
        #endregion

        #region Monitoring Loop
        private async Task StartMonitoringLoopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Starting monitoring loop...");
            
            // Display PresentMon version info
            var version = await _presentMonService.GetVersionAsync();
            if (version.HasValue)
            {
                Console.WriteLine($"PresentMon API Version: {version.Value.major}.{version.Value.minor}.{version.Value.patch}");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await MonitoringCycleAsync(cancellationToken);
                    await Task.Delay(1000, cancellationToken); // Check for new processes every second
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in monitoring loop: {ex.Message}");
                    await Task.Delay(5000, cancellationToken); // Wait longer on error
                }
            }
            
            Console.WriteLine("Monitoring loop stopped");
        }

        private async Task MonitoringCycleAsync(CancellationToken cancellationToken)
        {
            // Check if current process is still valid
            if (_currentState.IsMonitoring && _currentState.ProcessId != 0)
            {
                bool processValid = await _fullscreenDetectionService.IsProcessValidAsync(_currentState.ProcessId);
                if (!processValid)
                {
                    Console.WriteLine($"Process {_currentState.ProcessId} ({_currentState.ProcessName}) is no longer valid");
                    await StopMonitoringCurrentProcessAsync();
                }
                else
                {
                    // Update window title periodically
                    string newTitle = await _fullscreenDetectionService.GetProcessWindowTitleAsync(_currentState.ProcessId);
                    if (!string.IsNullOrWhiteSpace(newTitle) && newTitle != _currentState.WindowTitle)
                    {
                        _currentState.WindowTitle = newTitle;
                        _windowTitle.Value = newTitle;
                    }
                    return; // Still monitoring current process
                }
            }

            // Look for new fullscreen process
            var detectedState = await _fullscreenDetectionService.DetectFullscreenProcessAsync();
            if (detectedState != null && detectedState.ProcessId != _currentState.ProcessId)
            {
                Console.WriteLine($"Switching to new process: {detectedState.ProcessName} (PID: {detectedState.ProcessId})");
                await StartMonitoringProcessAsync(detectedState);
            }
            else if (detectedState == null && _currentState.IsMonitoring)
            {
                Console.WriteLine("No fullscreen process detected, stopping monitoring");
                await StopMonitoringCurrentProcessAsync();
            }
        }

        private async Task StartMonitoringProcessAsync(MonitoringState newState)
        {
            // Stop current monitoring if active
            if (_currentState.IsMonitoring)
            {
                await StopMonitoringCurrentProcessAsync();
            }

            // Start new session
            bool sessionOpened = await _presentMonService.OpenSessionAsync(newState.ProcessId);
            if (sessionOpened)
            {
                _currentState = newState;
                _currentState.IsMonitoring = true;
                _currentState.SessionStarted = DateTime.UtcNow;
                
                _windowTitle.Value = _currentState.WindowTitle;
                
                Console.WriteLine($"Successfully started monitoring {_currentState.ProcessName} (PID: {_currentState.ProcessId})");
            }
            else
            {
                Console.WriteLine($"Failed to start monitoring {newState.ProcessName} (PID: {newState.ProcessId})");
                ResetSensorsToDefault();
            }
        }

        private async Task StopMonitoringCurrentProcessAsync()
        {
            if (_currentState.IsMonitoring)
            {
                Console.WriteLine($"Stopping monitoring of {_currentState.ProcessName} (PID: {_currentState.ProcessId})");
                await _presentMonService.CloseSessionAsync();
            }

            _currentState.Reset();
            _windowTitle.Value = _currentState.WindowTitle;
            ResetSensorsToDefault();
        }
        #endregion

        #region Event Handlers
        private void OnMetricsUpdated(object? sender, FrameMetrics metrics)
        {
            try
            {
                // Update all sensors with new metrics
                _fpsSensor.Value = metrics.AverageFps;
                _frameTimeSensor.Value = metrics.AverageFrameTime;
                _gpuLatencySensor.Value = metrics.AverageGpuLatency;
                _gpuTimeSensor.Value = metrics.AverageGpuTime;
                _gpuBusySensor.Value = metrics.AverageGpuBusy;
                _gpuWaitSensor.Value = metrics.AverageGpuWait;
                _displayLatencySensor.Value = metrics.AverageDisplayLatency;
                _cpuBusySensor.Value = metrics.AverageCpuBusy;
                _cpuWaitSensor.Value = metrics.AverageCpuWait;
                _gpuUtilizationSensor.Value = metrics.GpuUtilization;
                _onePercentLowSensor.Value = metrics.OnePercentLowFps;
                _zeroPointOnePercentLowSensor.Value = metrics.ZeroPointOnePercentLowFps;

                _currentState.LastDataReceived = DateTime.UtcNow;
                
                Console.WriteLine($"Updated metrics: FPS={metrics.AverageFps:F1}, FrameTime={metrics.AverageFrameTime:F2}ms, " +
                                $"1%Low={metrics.OnePercentLowFps:F1}, Frames={metrics.ValidFrameCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating sensors: {ex.Message}");
            }
        }
        #endregion

        #region Helper Methods
        private void ResetSensorsToDefault()
        {
            _fpsSensor.Value = 0;
            _frameTimeSensor.Value = 0;
            _gpuLatencySensor.Value = 0;
            _gpuTimeSensor.Value = 0;
            _gpuBusySensor.Value = 0;
            _gpuWaitSensor.Value = 0;
            _displayLatencySensor.Value = 0;
            _cpuBusySensor.Value = 0;
            _cpuWaitSensor.Value = 0;
            _gpuUtilizationSensor.Value = 0;
            _onePercentLowSensor.Value = 0;
            _zeroPointOnePercentLowSensor.Value = 0;
        }
        #endregion

        #region Disposal
        public void Dispose()
        {
            if (!_disposed)
            {
                Console.WriteLine("Disposing IPFpsPlugin...");

                // Cancel monitoring
                if (_monitoringCancellationTokenSource != null)
                {
                    _monitoringCancellationTokenSource.Cancel();
                    
                    if (_monitoringTask != null)
                    {
                        try
                        {
                            _monitoringTask.Wait(TimeSpan.FromSeconds(5));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error waiting for monitoring task to complete: {ex.Message}");
                        }
                    }
                    
                    _monitoringCancellationTokenSource.Dispose();
                }

                // Dispose services
                _presentMonService?.Dispose();

                // Reset sensors
                ResetSensorsToDefault();
                _windowTitle.Value = "Nothing to capture";

                _disposed = true;
                Console.WriteLine("IPFpsPlugin disposed successfully");
            }
        }
        #endregion
    }
}