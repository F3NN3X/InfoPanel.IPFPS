// Copyright (C) 2022 Intel Corporation
// SPDX-License-Identifier: MIT
#pragma once
#ifdef PRESENTMON_API_EXPORTS
#define PRESENTMON_API_EXPORT __declspec(dllexport)
#else
#define PRESENTMON_API_EXPORT __declspec(dllimport)
#endif
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

  // PresentMon Library Error Codes
  enum PM_STATUS
  {
     PM_STATUS_SUCCESS,
     PM_STATUS_CREATE_SESSION_FAILED,
     PM_STATUS_NO_DATA,
     PM_STATUS_DATA_LOSS,
     PM_STATUS_INSUFFICIENT_BUFFER,
     PM_STATUS_INVALID_SESSION,
     PM_STATUS_SESSION_ALREADY_EXISTS,
     PM_STATUS_SERVICE_NOT_INITIALIZED,
     PM_STATUS_SERVICE_NOT_FOUND,
     PM_STATUS_SERVICE_SESSIONS_FULL,
     PM_STATUS_SERVICE_ERROR,
     PM_STATUS_SERVICE_NOT_SUPPORTED,
     PM_STATUS_OUT_OF_RANGE,
     PM_STATUS_INVALID_PID,
     PM_STATUS_INVALID_ADAPTER_ID,
     PM_STATUS_INVALID_ETL_FILE,
     PM_STATUS_PROCESS_NOT_EXIST,
     PM_STATUS_STREAM_ALREADY_EXISTS,
     PM_STATUS_UNABLE_TO_CREATE_NSM,
     PM_STATUS_ERROR
   };

  enum PM_PRESENT_MODE {
    PM_PRESENT_MODE_HARDWARE_LEGACY_FLIP,
    PM_PRESENT_MODE_HARDWARE_LEGACY_COPY_TO_FRONT_BUFFER,
    PM_PRESENT_MODE_HARDWARE_INDEPENDENT_FLIP,
    PM_PRESENT_MODE_COMPOSED_FLIP,
    PM_PRESENT_MODE_HARDWARE_COMPOSED_INDEPENDENT_FLIP,
    PM_PRESENT_MODE_COMPOSED_COPY_WITH_GPU_GDI,
    PM_PRESENT_MODE_COMPOSED_COPY_WITH_CPU_GDI,
    PM_PRESENT_MODE_UNKNOWN
  };

  enum PM_PSU_TYPE {
    PM_PSU_TYPE_NONE,
    PM_PSU_TYPE_PCIE,
    PM_PSU_TYPE_6PIN,
    PM_PSU_TYPE_8PIN
  };

  enum PM_GPU_VENDOR {
      PM_GPU_VENDOR_INTEL,
      PM_GPU_VENDOR_NVIDIA,
      PM_GPU_VENDOR_AMD,
      PM_GPU_VENDOR_UNKNOWN
  };

  #define MAX_RUNTIME_LENGTH 7
  #define MAX_PM_PATH 260
  #define MAX_PM_FAN_COUNT 5
  #define MAX_PM_PSU_COUNT 5
  #define MAX_PM_ADAPTER_NAME 64
  #define MAX_PM_CPU_NAME 256
  #define MIN_PM_TELEMETRY_PERIOD 1
  #define MAX_PM_TELEMETRY_PERIOD 1000

  struct PM_FRAME_DATA_OPT_DOUBLE {
    double data;
    bool valid;
  };

  struct PM_FRAME_DATA_OPT_UINT64 {
    uint64_t data;
    bool valid;
  };

  struct PM_FRAME_DATA_OPT_INT {
    int data;
    bool valid;
  };

  struct PM_FRAME_DATA_OPT_PSU_TYPE {
    PM_PSU_TYPE data;
    bool valid;
  };
  ////////////////////////////////////////////////////////////////////////////
  /// @brief Structure holding PresentMon Frame data
  struct PM_FRAME_DATA
  {
    // @brief The name of the process that called Present().
    char application[MAX_PM_PATH];

    // @brief The process ID of the process that called Present().
    uint32_t process_id;
    // @brief The address of the swap chain that was presented into.
    uint64_t swap_chain_address;
    // @brief The runtime used to present (e.g., D3D9 or DXGI).
    char runtime[MAX_RUNTIME_LENGTH];
    // @brief The sync interval provided by the application in the Present()
    // call. This value may be modified later by the driver, e.g., based on
    // control panel overrides.
    int32_t sync_interval;
    // @brief Flags used in the Present() call.
    uint32_t present_flags;
    // @brief Whether the frame was dropped (1) or displayed (0).  Note, if
    // dropped, msUntilDisplayed will be 0.
    uint32_t dropped;
    // @brief The time of the Present() call, in seconds, relative to when the
    // PresentMon started recording.
    double time_in_seconds;
    // @brief The time spent inside the Present() call, in milliseconds.
    double ms_in_present_api;
    // @brief The time between this Present() call and the previous one, in
    // milliseconds.
    double ms_between_presents;
    // @brief Whether tearing is possible (1) or not (0).
    uint32_t allows_tearing;
    // @brief The presentation mode used by the system for this Present().
    PM_PRESENT_MODE present_mode;
    // @brief The time between the Present() call and when the GPU work
    // completed, in milliseconds.
    double ms_until_render_complete;
    // @brief The time between the Present() call and when the frame was
    // displayed, in milliseconds.
    double ms_until_displayed;
    // @brief How long the previous frame was displayed before this Present()
    // was displayed, in milliseconds.
    double ms_between_display_change;
    // @brief The time between the Present() call and when the GPU work
    // started, in milliseconds.
    double ms_until_render_start;
    // @brief The time of the Present() call, as a performance counter value.
    uint64_t qpc_time;

    // @brief The time between the Present() call and the earliest keyboard or
    // mouse interaction that contributed to this frame.
    double ms_since_input;
    // @brief The time that any GPU engine was active working on this frame,
    // in milliseconds. Not supported on Win7
    double ms_gpu_active;
    // @brief The time video encode/decode was active separate from the other
    // engines in milliseconds. Not supported on Win7
    double ms_gpu_video_active;

    // Power telemetry
    PM_FRAME_DATA_OPT_DOUBLE gpu_power_w;
    PM_FRAME_DATA_OPT_DOUBLE gpu_sustained_power_limit_w;
    PM_FRAME_DATA_OPT_DOUBLE gpu_voltage_v;
    PM_FRAME_DATA_OPT_DOUBLE gpu_frequency_mhz;
    PM_FRAME_DATA_OPT_DOUBLE gpu_temperature_c;
    PM_FRAME_DATA_OPT_DOUBLE gpu_utilization;
    PM_FRAME_DATA_OPT_DOUBLE gpu_render_compute_utilization;
    PM_FRAME_DATA_OPT_DOUBLE gpu_media_utilization;

    PM_FRAME_DATA_OPT_DOUBLE vram_power_w;
    PM_FRAME_DATA_OPT_DOUBLE vram_voltage_v;
    PM_FRAME_DATA_OPT_DOUBLE vram_frequency_mhz;
    PM_FRAME_DATA_OPT_DOUBLE vram_effective_frequency_gbs;
    PM_FRAME_DATA_OPT_DOUBLE vram_temperature_c;

    PM_FRAME_DATA_OPT_DOUBLE fan_speed_rpm[MAX_PM_FAN_COUNT];

    PM_FRAME_DATA_OPT_PSU_TYPE psu_type[MAX_PM_PSU_COUNT];
    PM_FRAME_DATA_OPT_DOUBLE psu_power[MAX_PM_PSU_COUNT];
    PM_FRAME_DATA_OPT_DOUBLE psu_voltage[MAX_PM_PSU_COUNT];

    // Gpu memory telemetry
    PM_FRAME_DATA_OPT_UINT64 gpu_mem_total_size_b;
    PM_FRAME_DATA_OPT_UINT64 gpu_mem_used_b;
    PM_FRAME_DATA_OPT_UINT64 gpu_mem_max_bandwidth_bps;
    PM_FRAME_DATA_OPT_DOUBLE gpu_mem_read_bandwidth_bps;
    PM_FRAME_DATA_OPT_DOUBLE gpu_mem_write_bandwidth_bps;
    
    // Throttling flags
    PM_FRAME_DATA_OPT_INT gpu_power_limited;
    PM_FRAME_DATA_OPT_INT gpu_temperature_limited;
    PM_FRAME_DATA_OPT_INT gpu_current_limited;
    PM_FRAME_DATA_OPT_INT gpu_voltage_limited;
    PM_FRAME_DATA_OPT_INT gpu_utilization_limited;

    PM_FRAME_DATA_OPT_INT vram_power_limited;
    PM_FRAME_DATA_OPT_INT vram_temperature_limited;
    PM_FRAME_DATA_OPT_INT vram_current_limited;
    PM_FRAME_DATA_OPT_INT vram_voltage_limited;
    PM_FRAME_DATA_OPT_INT vram_utilization_limited;

    // Cpu Telemetry
    PM_FRAME_DATA_OPT_DOUBLE cpu_utilization;
    PM_FRAME_DATA_OPT_DOUBLE cpu_power_w;
    PM_FRAME_DATA_OPT_DOUBLE cpu_power_limit_w;
    PM_FRAME_DATA_OPT_DOUBLE cpu_temperature_c;
    PM_FRAME_DATA_OPT_DOUBLE cpu_frequency;
  };

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Returns PresentMon frame data.
  /// @detail Make successive calls to pmGetStreamData from an active steaming
  ///         session providing an array of PM_FRAME_DATA structures and the
  ///         number of frame data structures provided in numFrames. Note: Data
  ///         can be lost if the time between successive calls is too great and
  ///         older streaming data has been written by newer data. In this case
  ///         PM_STATUS_LOST_DATA is returned.
  ///
  ///         A PresentMon stream session gathers ALL available raw ETW
  ///         information. This includes Microsoft Windows D3D9, DWM, DXGI,
  ///         DXG Kernel and Win32K events. In addition this can include
  ///         Intel specific events. See the structure definition PM_FRAME_DATA
  ///         metric for complete details.
  /// @param  [In] Process Id of a currently active stream session
  ///         [In/Out] Array of PM_FRAME_DATA structures to hold returned
  ///         streaming frame data
  ///         [In/Out] The number of PM_FRAME_DATA structures passed in. On
  ///         return contains the number of PM_FRAME_DATA structures that were
  ///         written and are valid.
  /// @return Returns PM_STATUS_SUCCESS if successful.
  ///         Returns PM_STATUS_INVALID_PID if the passed in process id does
  ///         not match any currently running stream seassions
  ///         Returns PM_STATUS_LOST_DATA if frame data was not returned because
  ///         newer data overwrote the older data.
  PRESENTMON_API_EXPORT PM_STATUS pmGetFrameData(uint32_t processId,
                                                 uint32_t* in_out_num_frames,
                                                 PM_FRAME_DATA* out_data);
  PRESENTMON_API_EXPORT PM_STATUS pmGetEtlFrameData(uint32_t* in_out_num_frames,
                                                    PM_FRAME_DATA* out_data);
  PRESENTMON_API_EXPORT PM_STATUS
  pmGetStreamAllFrameData(uint32_t* in_out_num_frames, PM_FRAME_DATA* out_data);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Loads the PresentMon library.
  /// @return Returns PM_STATUS_SUCCESS if successful.
  ///         Returns PM_STATUS_LIBRARY_NOT_FOUND if unable to load PresentMon
  ///         library.
  PRESENTMON_API_EXPORT PM_STATUS pmInitialize();

  ///////////////////////////////////////////////////////////////////////////
  /// @brief Unloads the PresentMon library.
  /// @return Returns PM_STATUS_SUCCESS if successful.
  ///         Returns PM_STATUS_LIBRARY_NOT_FOUND if unable to load
  ///         PresentMon library.
  PRESENTMON_API_EXPORT PM_STATUS pmShutdown();

  struct PM_METRIC_DOUBLE_DATA {
    double avg;
    double percentile_99;
    double percentile_95;
    double percentile_90;
    double high;
    double low;
    double raw;
    bool valid;
  };

  ///////////////////////////////////////////////////////////////////////////
  /// @brief Structure holding FPS data.
  struct PM_FPS_DATA
  {
    uint64_t swap_chain;

    PM_METRIC_DOUBLE_DATA displayed_fps;
    PM_METRIC_DOUBLE_DATA presented_fps;
    PM_METRIC_DOUBLE_DATA frame_time_ms;

    PM_METRIC_DOUBLE_DATA gpu_busy;
    PM_METRIC_DOUBLE_DATA percent_dropped_frames;

    uint32_t num_presents;
    
    int32_t sync_interval;
    PM_PRESENT_MODE present_mode;
    int32_t allows_tearing;
  };
  PRESENTMON_API_EXPORT PM_STATUS
  pmGetFramesPerSecondData(uint32_t processId, PM_FPS_DATA* pmFpsData,
                           double windowSizeinMs, uint32_t* numSwapChains);


  struct PM_GFX_LATENCY_DATA
  {
    uint64_t swap_chain;
    PM_METRIC_DOUBLE_DATA render_latency_ms;
    PM_METRIC_DOUBLE_DATA display_latency_ms;
  };
  PRESENTMON_API_EXPORT PM_STATUS
  pmGetGfxLatencyData(uint32_t processId, PM_GFX_LATENCY_DATA* pmGfxLatencyData,
                      double windowSizeinMs, uint32_t* numSwapChains);

  struct PM_PSU_DATA {
    // PSU type
    PM_PSU_TYPE psu_type;
    // PSU total energy consumed by power source
    PM_METRIC_DOUBLE_DATA psu_power;
    // PSU voltage of the power source
    PM_METRIC_DOUBLE_DATA psu_voltage;
    bool valid;
  };

  struct PM_GPU_DATA
  {
    // Total energy consumed by the GPU chip
    PM_METRIC_DOUBLE_DATA gpu_power_w;
    // Sustained power limit in W
    PM_METRIC_DOUBLE_DATA gpu_sustained_power_limit_w;
    // Voltage feeding the GPU chip, measured at the power supply output -
    // chip input will be lower
    PM_METRIC_DOUBLE_DATA gpu_voltage_v;
    // GPU chip frequency
    PM_METRIC_DOUBLE_DATA gpu_frequency_mhz;
    // GPU chip temperature
    PM_METRIC_DOUBLE_DATA gpu_temperature_c;
    // Fan speed
    PM_METRIC_DOUBLE_DATA gpu_fan_speed_rpm[MAX_PM_FAN_COUNT];
    // Percentage utilization of the GPU
    PM_METRIC_DOUBLE_DATA gpu_utilization;
    // Percentage utilization of 3D/Compute blocks in GPU
    PM_METRIC_DOUBLE_DATA gpu_render_compute_utilization;
    // Percentage utilization of media blocks in the GPU
    PM_METRIC_DOUBLE_DATA gpu_media_utilization;
    // Total energy consumed by the memory modules
    PM_METRIC_DOUBLE_DATA vram_power_w;
    // Voltage feeding the memory modules
    PM_METRIC_DOUBLE_DATA vram_voltage_v;
    // Raw frequency driving the memory modules
    PM_METRIC_DOUBLE_DATA vram_frequency_mhz;
    // Effective data transfer rate the memory modules can sustain based on
    // current clock frequency
    PM_METRIC_DOUBLE_DATA vram_effective_frequency_gbps;
    // Read bandwidth
    PM_METRIC_DOUBLE_DATA vram_read_bandwidth_bps;
    // Write bandwidth
    PM_METRIC_DOUBLE_DATA vram_write_bandwidth_bps;
    // Memory modules temperature
    PM_METRIC_DOUBLE_DATA vram_temperature_c;
    // Total GPU memory in bytes
    PM_METRIC_DOUBLE_DATA gpu_mem_total_size_b;
    // Total GPU memory used in bytes
    PM_METRIC_DOUBLE_DATA gpu_mem_used_b;
    // Percent utilization of GPU memory used.
    PM_METRIC_DOUBLE_DATA gpu_mem_utilization;
    // Max GPU bandwidth in bytes per second
    PM_METRIC_DOUBLE_DATA gpu_mem_max_bandwidth_bps;
    // GPU read bandwidth in bytes per second
    PM_METRIC_DOUBLE_DATA gpu_mem_read_bandwidth_bps;
    // GPU write bandwidth in bytes per second
    PM_METRIC_DOUBLE_DATA gpu_mem_write_bandwidth_bps;

    // PSU power data
    PM_PSU_DATA psu_data[MAX_PM_PSU_COUNT];

    // Throttling flags
    // GPU is being throttled because the GPU chip exceeding the maximum power
    // limits
    PM_METRIC_DOUBLE_DATA gpu_power_limited;
    // GPU frequency is being throttled because the GPU chip is exceeding the
    // temperature limits
    PM_METRIC_DOUBLE_DATA gpu_temperature_limited;
    // The desired GPU frequency is being throttled because the GPU chip has
    // exceeded the power supply current limits
    PM_METRIC_DOUBLE_DATA gpu_current_limited;
    // GPU frequency cannot be increased because the voltage limits have been
    // reached
    PM_METRIC_DOUBLE_DATA gpu_voltage_limited;
    // Due to lower GPU utilization, the hardware has lowered the GPU
    // frequency
    PM_METRIC_DOUBLE_DATA gpu_utilization_limited;
    // Memory frequency is being throttled because the memory modules are
    // exceeding the maximum power limits
    PM_METRIC_DOUBLE_DATA vram_power_limited;
    // Memory frequency is being throttled because the memory modules are
    // exceeding the maximum temperature limits
    PM_METRIC_DOUBLE_DATA vram_temperature_limited;
    // Memory frequency is being throttled because the memory modules have
    // exceeded the power supply current limits
    PM_METRIC_DOUBLE_DATA vram_current_limited;
    // Memory frequency cannot be increased because the voltage limits have
    // been reached
    PM_METRIC_DOUBLE_DATA vram_voltage_limited;
    // Due to lower memory traffic, the hardware has lowered the memory
    // frequency
    PM_METRIC_DOUBLE_DATA vram_utilization_limited;
  };
  PRESENTMON_API_EXPORT PM_STATUS pmGetGPUData(uint32_t process_id,
                                               PM_GPU_DATA* gpu_data,
                                               double window_size_in_ms);

  struct PM_CPU_DATA {
    // CPU utilization
    PM_METRIC_DOUBLE_DATA cpu_utilization;
    // Total energy consumed by CPU
    PM_METRIC_DOUBLE_DATA cpu_power_w;
    // Power limit of CPU
    PM_METRIC_DOUBLE_DATA cpu_power_limit_w;
    // Temperature of CPU
    PM_METRIC_DOUBLE_DATA cpu_temperature_c;
    // Clock frequency of CPU
    PM_METRIC_DOUBLE_DATA cpu_frequency;
  };
  PRESENTMON_API_EXPORT PM_STATUS pmGetCPUData(uint32_t process_id,
                                               PM_CPU_DATA* gpu_data,
                                               double window_size_in_ms);

  struct PM_ADAPTER_INFO
  {
      uint32_t id;
      PM_GPU_VENDOR vendor;
      char name[MAX_PM_ADAPTER_NAME];
  };

  /////////////////////////////////////////////////////////////////////////////
  /// @brief  Enumerate graphics adapters
  /// @details Gets number of adapters and fills an array of adapter info
  /// @param  [out] the pointer to buffer to copy adapter info into
  ///               pass in nullptr to just query adapter count
  ///         [in/out] pointer to size of buffer in, available count out
  /// @return Returns PM_STATUS_SUCCESS if successful.
  PRESENTMON_API_EXPORT PM_STATUS pmEnumerateAdapters(PM_ADAPTER_INFO* adapter_info_buffer,
                                                      uint32_t* adapter_count);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief  Get the CPU name
  /// @details Returns a string with the name of the CPU
  /// @param  [out] pointer to a char array that holds the name of the CPU
  ///         [in/out] pointer to size of buffer in, characters written out
  /// @return Returns PM_STATUS_SUCCESS if successful.
  PRESENTMON_API_EXPORT PM_STATUS pmGetCpuName(
      char* cpu_name_buffer, uint32_t* buffer_size);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief  Select active adapter
  /// @details Selects which adapter is used to source GPU telemetry into frame data
  /// @param  id of the adapter to set as active
  /// @return Returns PM_STATUS_SUCCESS if successful.
  PRESENTMON_API_EXPORT PM_STATUS pmSetActiveAdapter(uint32_t adapter_id);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief  Set the sampling period for gpu telemetry
  /// @param  sampling period in milliseconds between MIN_PM_TELEMETRY_PERIOD
  ///         and MAX_PM_TELEMETRY_PERIOD
  /// @return Returns PM_STATUS_SUCCESS if successful.
  PRESENTMON_API_EXPORT PM_STATUS pmSetGPUTelemetryPeriod(uint32_t period_ms);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Starts a PresentMon stream session
  /// @details Notifies PresentMon to start recording ETW events
  /// @param The target processId to capture ETW events
  /// @return Returns PM_STATUS_SUCCESS if successful.
  ///         Returns PM_STATUS_PROCESS_NOT_EXIST if the passed in processId
  ///         does not match any currently running process
  PRESENTMON_API_EXPORT PM_STATUS pmStartStream(uint32_t process_id);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Start processing an ETW log file.
  /// @param [In] Name of the ETW log file
  ///        [In] Size of the ETW log file name
  /// @return Returns PM_STATUS_SUCCESS if successful
  ///         Returns PM_STATUS_INVALID_ETL_FILE is unable to open or process
  ///         the passed in ETW log file
  PRESENTMON_API_EXPORT PM_STATUS pmStartStreamEtl(char const* etl_file_name);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Stops a PresentMon stream session
  /// @param Process Id of a currently active stream session
  /// @return Returns PM_STATUS_SUCCESS always. No effect if process do not
  /// exists.
  PRESENTMON_API_EXPORT PM_STATUS pmStopStream(uint32_t process_id);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Sets an offset in time from the most recent metrics data. This call
  /// affects pmGetFramesPerSecondData, pmGetGfxLatencyData, and pmGetGPUData.
  /// @param The offset in milliseconds
  /// @return Returns PM_STATUS_SUCCESS always.
  PRESENTMON_API_EXPORT PM_STATUS pmSetMetricsOffset(double offset_in_ms);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Sets an offset in time from the most recent metrics data. This call
  /// affects pmGetFramesPerSecondData, pmGetGfxLatencyData, and pmGetGPUData.
  /// @param The offset in milliseconds
  /// @return Returns PM_STATUS_SUCCESS always.
  PRESENTMON_API_EXPORT PM_STATUS pmSetMetricsOffset(double offset_in_ms);

  /////////////////////////////////////////////////////////////////////////////
  /// @brief Initializes glog for the middleware. Don't call if glog is used
  /// by the application already.
  /// @param directory to log to
  /// @param base name of the log file
  /// @param file extension of the log file
  /// @param severity level to log at
  PRESENTMON_API_EXPORT void pmInitializeLogging(const char* location, const char* basename, const char* extension, int level);

#ifdef __cplusplus
} // extern "C"
#endif