using System;

namespace InfoPanel.Presentmon.Models
{
    /// <summary>
    /// Represents processed frame metrics after aggregation
    /// </summary>
    public class FrameMetrics
    {
        public float AverageFps { get; set; }
        public float AverageFrameTime { get; set; }
        public float OnePercentLowFps { get; set; }
        public float ZeroPointOnePercentLowFps { get; set; }
        public float AverageGpuLatency { get; set; }
        public float AverageGpuTime { get; set; }
        public float AverageGpuBusy { get; set; }
        public float AverageGpuWait { get; set; }
        public float AverageDisplayLatency { get; set; }
        public float AverageCpuBusy { get; set; }
        public float AverageCpuWait { get; set; }
        public float GpuUtilization { get; set; }
        public int ValidFrameCount { get; set; }
        public int TotalFrameCount { get; set; }
        public DateTime LastUpdated { get; set; }

        public FrameMetrics()
        {
            LastUpdated = DateTime.UtcNow;
        }

        /// <summary>
        /// Resets all metrics to zero
        /// </summary>
        public void Reset()
        {
            AverageFps = 0;
            AverageFrameTime = 0;
            OnePercentLowFps = 0;
            ZeroPointOnePercentLowFps = 0;
            AverageGpuLatency = 0;
            AverageGpuTime = 0;
            AverageGpuBusy = 0;
            AverageGpuWait = 0;
            AverageDisplayLatency = 0;
            AverageCpuBusy = 0;
            AverageCpuWait = 0;
            GpuUtilization = 0;
            ValidFrameCount = 0;
            TotalFrameCount = 0;
            LastUpdated = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Configuration for frame data processing
    /// </summary>
    public class FrameProcessingConfig
    {
        /// <summary>
        /// Maximum number of frame samples to keep in rolling window
        /// </summary>
        public int MaxFrameSamples { get; set; } = 1000;

        /// <summary>
        /// Minimum number of frames required for stable metrics
        /// </summary>
        public int MinFramesForMetrics { get; set; } = 30;

        /// <summary>
        /// Update interval for session polling in milliseconds
        /// </summary>
        public int UpdateIntervalMs { get; set; } = 16;

        /// <summary>
        /// Timeout for session operations in milliseconds
        /// </summary>
        public int SessionTimeoutMs { get; set; } = 5000;
    }

    /// <summary>
    /// Represents the current monitoring state
    /// </summary>
    public class MonitoringState
    {
        public uint ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = "Nothing to capture";
        public bool IsMonitoring { get; set; }
        public IntPtr SessionHandle { get; set; } = IntPtr.Zero;
        public DateTime SessionStarted { get; set; }
        public DateTime LastDataReceived { get; set; }

        /// <summary>
        /// Resets the monitoring state
        /// </summary>
        public void Reset()
        {
            ProcessId = 0;
            ProcessName = string.Empty;
            WindowTitle = "Nothing to capture";
            IsMonitoring = false;
            SessionHandle = IntPtr.Zero;
            SessionStarted = DateTime.UtcNow;
            LastDataReceived = DateTime.MinValue;
        }
    }
}