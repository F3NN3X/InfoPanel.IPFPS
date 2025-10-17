using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Presentmon.Interop;
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services
{
    /// <summary>
    /// Service for managing PresentMon sessions and processing frame data
    /// Handles P/Invoke calls, session lifecycle, and metric aggregation
    /// </summary>
    public class PresentMonService : IDisposable
    {
        private readonly FrameProcessingConfig _config;
        private readonly object _lockObject = new();
        private IntPtr _sessionHandle = IntPtr.Zero;
        private CancellationTokenSource? _updateCancellationTokenSource;
        private Task? _updateTask;
        
        // Rolling frame data collections
        private readonly List<float> _frameTimes = new();
        private readonly List<float> _gpuLatencies = new();
        private readonly List<float> _gpuTimes = new();
        private readonly List<float> _gpuBusyTimes = new();
        private readonly List<float> _gpuWaitTimes = new();
        private readonly List<float> _displayLatencies = new();
        private readonly List<float> _cpuBusyTimes = new();
        private readonly List<float> _cpuWaitTimes = new();

        private bool _disposed = false;

        public event EventHandler<FrameMetrics>? MetricsUpdated;

        public PresentMonService(FrameProcessingConfig? config = null)
        {
            _config = config ?? new FrameProcessingConfig();
        }

        /// <summary>
        /// Gets the current PresentMon API version
        /// </summary>
        public async Task<(uint major, uint minor, uint patch)?> GetVersionAsync()
        {
            return await Task.Run<(uint major, uint minor, uint patch)?>(() =>
            {
                try
                {
                    var result = PresentMonApi.pmGetVersion(out uint major, out uint minor, out uint patch);
                    if (result == PresentMonApi.PM_RESULT.PM_OK)
                    {
                        return (major, minor, patch);
                    }
                    Console.WriteLine($"Failed to get PresentMon version: {result}");
                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception getting PresentMon version: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Opens a PresentMon session for the specified process
        /// </summary>
        public async Task<bool> OpenSessionAsync(uint processId)
        {
            if (_sessionHandle != IntPtr.Zero)
            {
                await CloseSessionAsync();
            }

            return await Task.Run(() =>
            {
                try
                {
                    Console.WriteLine($"Opening PresentMon session for PID: {processId}");
                    var result = PresentMonApi.pmOpenSession(processId, out _sessionHandle);
                    
                    if (result == PresentMonApi.PM_RESULT.PM_OK)
                    {
                        Console.WriteLine($"Successfully opened PresentMon session: {_sessionHandle}");
                        StartUpdateLoop();
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"Failed to open PresentMon session: {result}");
                        _sessionHandle = IntPtr.Zero;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception opening PresentMon session: {ex.Message}");
                    _sessionHandle = IntPtr.Zero;
                    return false;
                }
            });
        }

        /// <summary>
        /// Closes the current PresentMon session
        /// </summary>
        public async Task CloseSessionAsync()
        {
            // Stop the update loop first
            if (_updateCancellationTokenSource != null)
            {
                _updateCancellationTokenSource.Cancel();
                
                if (_updateTask != null)
                {
                    try
                    {
                        await _updateTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancelling
                    }
                }
                
                _updateCancellationTokenSource.Dispose();
                _updateCancellationTokenSource = null;
                _updateTask = null;
            }

            // Close the session
            if (_sessionHandle != IntPtr.Zero)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        Console.WriteLine($"Closing PresentMon session: {_sessionHandle}");
                        var result = PresentMonApi.pmCloseSession(_sessionHandle);
                        if (result != PresentMonApi.PM_RESULT.PM_OK)
                        {
                            Console.WriteLine($"Warning: Failed to close session cleanly: {result}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception closing PresentMon session: {ex.Message}");
                    }
                    finally
                    {
                        _sessionHandle = IntPtr.Zero;
                    }
                });
            }

            // Clear frame data
            lock (_lockObject)
            {
                ClearFrameData();
            }
        }

        /// <summary>
        /// Gets the current frame metrics
        /// </summary>
        public FrameMetrics GetCurrentMetrics()
        {
            lock (_lockObject)
            {
                var metrics = new FrameMetrics();

                if (_frameTimes.Count < _config.MinFramesForMetrics)
                {
                    return metrics; // Return empty metrics if insufficient data
                }

                // Calculate FPS and frame time metrics
                float avgFrameTime = _frameTimes.Average();
                metrics.AverageFrameTime = avgFrameTime;
                metrics.AverageFps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0;

                // Calculate percentile lows (worst frame times = lowest FPS)
                var sortedFrameTimes = _frameTimes.OrderByDescending(ft => ft).ToArray();
                int onePercentIndex = (int)(_frameTimes.Count * 0.01);
                int zeroPointOnePercentIndex = (int)(_frameTimes.Count * 0.001);

                if (onePercentIndex < sortedFrameTimes.Length)
                {
                    float onePercentWorstFrameTime = sortedFrameTimes[onePercentIndex];
                    metrics.OnePercentLowFps = onePercentWorstFrameTime > 0 ? 1000f / onePercentWorstFrameTime : 0;
                }

                if (zeroPointOnePercentIndex < sortedFrameTimes.Length)
                {
                    float zeroPointOnePercentWorstFrameTime = sortedFrameTimes[zeroPointOnePercentIndex];
                    metrics.ZeroPointOnePercentLowFps = zeroPointOnePercentWorstFrameTime > 0 ? 1000f / zeroPointOnePercentWorstFrameTime : 0;
                }

                // Calculate other averages
                metrics.AverageGpuLatency = _gpuLatencies.Count > 0 ? _gpuLatencies.Average() : 0;
                metrics.AverageGpuTime = _gpuTimes.Count > 0 ? _gpuTimes.Average() : 0;
                metrics.AverageGpuBusy = _gpuBusyTimes.Count > 0 ? _gpuBusyTimes.Average() : 0;
                metrics.AverageGpuWait = _gpuWaitTimes.Count > 0 ? _gpuWaitTimes.Average() : 0;
                metrics.AverageDisplayLatency = _displayLatencies.Count > 0 ? _displayLatencies.Average() : 0;
                metrics.AverageCpuBusy = _cpuBusyTimes.Count > 0 ? _cpuBusyTimes.Average() : 0;
                metrics.AverageCpuWait = _cpuWaitTimes.Count > 0 ? _cpuWaitTimes.Average() : 0;

                // Calculate GPU utilization (simplified)
                if (metrics.AverageGpuTime > 0 && avgFrameTime > 0)
                {
                    metrics.GpuUtilization = Math.Min(100f, (metrics.AverageGpuTime / avgFrameTime) * 100f);
                }

                metrics.ValidFrameCount = _frameTimes.Count;
                metrics.TotalFrameCount = _frameTimes.Count; // Assuming no dropped frames in valid set

                return metrics;
            }
        }

        /// <summary>
        /// Starts the background update loop that polls for new frame data
        /// </summary>
        private void StartUpdateLoop()
        {
            _updateCancellationTokenSource = new CancellationTokenSource();
            _updateTask = Task.Run(async () =>
            {
                var cancellationToken = _updateCancellationTokenSource.Token;
                
                while (!cancellationToken.IsCancellationRequested && _sessionHandle != IntPtr.Zero)
                {
                    try
                    {
                        await UpdateSessionDataAsync();
                        await Task.Delay(_config.UpdateIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in PresentMon update loop: {ex.Message}");
                        await Task.Delay(1000, cancellationToken); // Wait longer on error
                    }
                }
            });
        }

        /// <summary>
        /// Updates the session with latest data and processes new frames
        /// </summary>
        private async Task UpdateSessionDataAsync()
        {
            if (_sessionHandle == IntPtr.Zero)
                return;

            await Task.Run(() =>
            {
                try
                {
                    // Update session to get latest ETW data
                    var updateResult = PresentMonApi.pmUpdateSession(_sessionHandle);
                    if (updateResult != PresentMonApi.PM_RESULT.PM_OK)
                    {
                        if (updateResult != PresentMonApi.PM_RESULT.PM_ERROR_NO_DATA)
                        {
                            Console.WriteLine($"Session update failed: {updateResult}");
                        }
                        return;
                    }

                    // Consume available frames
                    var consumeResult = PresentMonApi.pmConsumeFrames(_sessionHandle, out IntPtr framesPtr, out uint count);
                    if (consumeResult != PresentMonApi.PM_RESULT.PM_OK || count == 0)
                    {
                        return; // No new data available
                    }

                    // Marshal frame data
                    var frames = new PresentMonApi.PMFrame[count];
                    int frameSize = Marshal.SizeOf<PresentMonApi.PMFrame>();
                    
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr framePtr = IntPtr.Add(framesPtr, i * frameSize);
                        frames[i] = Marshal.PtrToStructure<PresentMonApi.PMFrame>(framePtr);
                    }

                    // Process frames
                    ProcessFrames(frames);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception updating session data: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Processes an array of frames and updates rolling metrics
        /// </summary>
        private void ProcessFrames(PresentMonApi.PMFrame[] frames)
        {
            lock (_lockObject)
            {
                foreach (var frame in frames)
                {
                    // Skip dropped frames
                    if (frame.Dropped != 0)
                        continue;

                    // Add frame data to rolling windows
                    AddToRollingList(_frameTimes, frame.MsBetweenPresents);
                    AddToRollingList(_gpuLatencies, frame.MsGpuLatency);
                    AddToRollingList(_gpuTimes, frame.MsGpuTime);
                    AddToRollingList(_gpuBusyTimes, frame.MsGpuBusy);
                    AddToRollingList(_gpuWaitTimes, frame.MsGpuWait);
                    AddToRollingList(_displayLatencies, frame.MsUntilDisplayed);
                    AddToRollingList(_cpuBusyTimes, frame.MsCpuBusy);
                    AddToRollingList(_cpuWaitTimes, frame.MsCpuWait);
                }
            }

            // Notify subscribers of updated metrics
            var metrics = GetCurrentMetrics();
            MetricsUpdated?.Invoke(this, metrics);
        }

        /// <summary>
        /// Adds a value to a rolling list, maintaining the maximum size
        /// </summary>
        private void AddToRollingList(List<float> list, float value)
        {
            list.Add(value);
            if (list.Count > _config.MaxFrameSamples)
            {
                list.RemoveAt(0);
            }
        }

        /// <summary>
        /// Clears all frame data collections
        /// </summary>
        private void ClearFrameData()
        {
            _frameTimes.Clear();
            _gpuLatencies.Clear();
            _gpuTimes.Clear();
            _gpuBusyTimes.Clear();
            _gpuWaitTimes.Clear();
            _displayLatencies.Clear();
            _cpuBusyTimes.Clear();
            _cpuWaitTimes.Clear();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CloseSessionAsync().GetAwaiter().GetResult();
                _disposed = true;
            }
        }
    }
}