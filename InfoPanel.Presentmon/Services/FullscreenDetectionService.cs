using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using InfoPanel.Presentmon.Models;
using Vanara.PInvoke;

namespace InfoPanel.Presentmon.Services
{
    /// <summary>
    /// Service for detecting fullscreen applications
    /// Uses Win32 APIs to enumerate windows and identify fullscreen processes
    /// </summary>
    public class FullscreenDetectionService
    {
        private const int GWL_STYLE = -16;
        private const uint WS_CAPTION = 0x00C00000; // Title bar
        private const uint WS_THICKFRAME = 0x00040000; // Resizable border
        private readonly uint _selfPid;

        public delegate bool EnumWindowsProc(HWND hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowLong(HWND hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(HWND hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(HWND hWnd, out Vanara.PInvoke.RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(HWND hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(HWND hWnd, out uint lpdwProcessId);

        public FullscreenDetectionService()
        {
            _selfPid = (uint)Process.GetCurrentProcess().Id;
        }

        /// <summary>
        /// Detects the currently active fullscreen process
        /// </summary>
        /// <returns>MonitoringState with detected process information, or null if none found</returns>
        public async Task<MonitoringState?> DetectFullscreenProcessAsync()
        {
            return await Task.Run(() =>
            {
                MonitoringState? detectedState = null;

                bool EnumWindowCallback(HWND hWnd, IntPtr lParam)
                {
                    try
                    {
                        // Get window style
                        uint style = GetWindowLong(hWnd, GWL_STYLE);
                        
                        // Check if window is fullscreen (no caption, no thick frame)
                        bool isFullscreen = (style & WS_CAPTION) == 0 && (style & WS_THICKFRAME) == 0;
                        
                        if (!isFullscreen)
                            return true; // Continue enumeration

                        // Get window rectangle
                        if (!GetClientRect(hWnd, out var rect))
                            return true;

                        // Check if window covers significant screen area (basic fullscreen detection)
                        int width = rect.right - rect.left;
                        int height = rect.bottom - rect.top;
                        
                        if (width < 800 || height < 600) // Minimum reasonable fullscreen size
                            return true;

                        // Get process ID
                        GetWindowThreadProcessId(hWnd, out uint processId);
                        
                        if (processId == 0 || processId == _selfPid)
                            return true; // Skip invalid or self PID

                        // Get process information
                        try
                        {
                            using var process = Process.GetProcessById((int)processId);
                            string processName = process.ProcessName;
                            
                            // Get window title
                            var titleBuilder = new StringBuilder(256);
                            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                            string windowTitle = titleBuilder.ToString();
                            
                            if (string.IsNullOrWhiteSpace(windowTitle))
                                windowTitle = processName;

                            Console.WriteLine($"Detected fullscreen process: {processName} (PID: {processId}) - {windowTitle}");
                            
                            detectedState = new MonitoringState
                            {
                                ProcessId = processId,
                                ProcessName = processName,
                                WindowTitle = windowTitle,
                                IsMonitoring = false,
                                SessionStarted = DateTime.UtcNow
                            };
                            
                            return false; // Stop enumeration - found our target
                        }
                        catch (ArgumentException)
                        {
                            // Process no longer exists
                            Console.WriteLine($"Process {processId} no longer exists");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error getting process info for PID {processId}: {ex.Message}");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in window enumeration callback: {ex.Message}");
                        return true; // Continue enumeration
                    }
                }

                try
                {
                    EnumWindows(EnumWindowCallback, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error enumerating windows: {ex.Message}");
                }

                if (detectedState == null)
                {
                    Console.WriteLine("No fullscreen process detected");
                }

                return detectedState;
            });
        }

        /// <summary>
        /// Checks if a specific process is still valid and accessible
        /// </summary>
        /// <param name="processId">Process ID to check</param>
        /// <returns>True if process exists and is accessible</returns>
        public async Task<bool> IsProcessValidAsync(uint processId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    return !process.HasExited;
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error checking process {processId}: {ex.Message}");
                    return false;
                }
            });
        }

        /// <summary>
        /// Gets the window title for a specific process
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <returns>Window title or process name if title unavailable</returns>
        public async Task<string> GetProcessWindowTitleAsync(uint processId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    
                    // Try to get main window title
                    if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    {
                        return process.MainWindowTitle;
                    }
                    
                    // Fallback to process name
                    return process.ProcessName;
                }
                catch (ArgumentException)
                {
                    return "Process not found";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error getting window title for PID {processId}: {ex.Message}");
                    return "Unknown";
                }
            });
        }
    }
}