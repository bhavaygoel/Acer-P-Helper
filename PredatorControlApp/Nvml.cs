using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    // Minimal binding to NVML (nvml.dll ships with the NVIDIA driver in System32).
    // Used when the EC reports 0 for the dGPU temperature, which happens on older
    // models like the PH315-53 because newer NVIDIA drivers stopped pushing it over ACPI.
    [SupportedOSPlatform("windows")]
    internal static class Nvml
    {
        private const string Lib = "nvml.dll";
        private const int NVML_TEMPERATURE_GPU = 0;

        [DllImport(Lib, EntryPoint = "nvmlInit_v2")]
        private static extern int Init();

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int GetHandleByIndex(uint index, out IntPtr device);

        [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")]
        private static extern int GetTemperature(IntPtr device, int sensor, out uint temp);

        private static readonly object _lock = new();
        private static bool _tried;
        private static IntPtr _device;

        /// <summary>GPU core temperature in °C, or -1 if NVML is unavailable.</summary>
        public static int GpuTemperature()
        {
            lock (_lock)
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        if (Init() == 0 && GetHandleByIndex(0, out var dev) == 0)
                            _device = dev;
                    }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                }

                if (_device == IntPtr.Zero) return -1;

                try
                {
                    return GetTemperature(_device, NVML_TEMPERATURE_GPU, out uint t) == 0 ? (int)t : -1;
                }
                catch
                {
                    return -1;
                }
            }
        }
    }
}
