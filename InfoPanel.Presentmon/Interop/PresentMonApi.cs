using System;
using System.Runtime.InteropServices;

namespace InfoPanel.Presentmon.Interop
{
    /// <summary>
    /// Native interop definitions for PresentMonAPI2.dll
    /// Based on PresentMonAPI2.h from PresentMon v2.3.1+
    /// </summary>
    public static class PresentMonApi
    {
        private const string DllName = "PresentMonAPI2.dll";

        /// <summary>
        /// Result codes returned by PresentMon API functions
        /// </summary>
        public enum PM_RESULT : uint
        {
            PM_OK = 0,
            PM_ERROR_FAIL = 1,
            PM_ERROR_NO_DATA = 2,
            PM_ERROR_INVALID_PARAMETER = 3,
            PM_ERROR_ACCESS_DENIED = 4,
            PM_ERROR_SESSION_NOT_FOUND = 5
        }

        /// <summary>
        /// Frame data structure returned by PresentMon
        /// Contains timing and performance metrics for a single frame
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PMFrame
        {
            public ulong PresentId;           // Unique frame identifier
            public ulong QpcTime;             // Query Performance Counter timestamp
            public ulong Runtime;             // Runtime identifier
            public ulong SwapChain;           // SwapChain identifier
            public float MsBetweenPresents;   // Frame time in milliseconds (key for FPS calculation)
            public float MsInPresentApi;      // Time spent in present API
            public float MsUntilRenderComplete; // GPU render completion time
            public float MsUntilDisplayed;    // Display latency
            public float MsGpuLatency;        // GPU latency
            public float MsGpuTime;           // GPU execution time
            public float MsVideoCpuTime;      // Video processing CPU time
            public float MsGpuVideoTime;      // Video processing GPU time
            public float MsCpuBusy;           // CPU busy time
            public float MsCpuWait;           // CPU wait time
            public float MsGpuBusy;           // GPU busy time
            public float MsGpuWait;           // GPU wait time
            public float Dropped;             // 1.0 if frame was dropped, 0.0 otherwise
        }

        /// <summary>
        /// Opens a PresentMon session for the specified process
        /// </summary>
        /// <param name="processId">Target process ID to monitor</param>
        /// <param name="session">Output session handle</param>
        /// <returns>PM_RESULT indicating success or failure</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PM_RESULT pmOpenSession(uint processId, out IntPtr session);

        /// <summary>
        /// Updates the session with latest frame data from ETW
        /// Should be called regularly (every 16-33ms) to refresh data
        /// </summary>
        /// <param name="session">Session handle from pmOpenSession</param>
        /// <returns>PM_RESULT indicating success or failure</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PM_RESULT pmUpdateSession(IntPtr session);

        /// <summary>
        /// Consumes available frame data from the session
        /// </summary>
        /// <param name="session">Session handle from pmOpenSession</param>
        /// <param name="framesPtr">Pointer to frame data array</param>
        /// <param name="count">Number of frames available</param>
        /// <returns>PM_RESULT indicating success or failure</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PM_RESULT pmConsumeFrames(IntPtr session, out IntPtr framesPtr, out uint count);

        /// <summary>
        /// Closes a PresentMon session and releases resources
        /// Must be called for every opened session
        /// </summary>
        /// <param name="session">Session handle from pmOpenSession</param>
        /// <returns>PM_RESULT indicating success or failure</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PM_RESULT pmCloseSession(IntPtr session);

        /// <summary>
        /// Gets the version of the PresentMon API
        /// </summary>
        /// <param name="major">Major version number</param>
        /// <param name="minor">Minor version number</param>
        /// <param name="patch">Patch version number</param>
        /// <returns>PM_RESULT indicating success or failure</returns>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PM_RESULT pmGetVersion(out uint major, out uint minor, out uint patch);
    }
}