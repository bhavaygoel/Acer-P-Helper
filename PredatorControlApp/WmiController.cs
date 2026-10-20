using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PredatorControlApp
{
    [SupportedOSPlatform("windows")]
    public class WmiController : IDisposable
    {
        private ManagementObject? _cachedObj;
        private readonly object _lock = new();

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(Guid scheme);

        private static readonly Guid OVERLAY_EFFICIENCY = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
        private static readonly Guid OVERLAY_BALANCED = new("00000000-0000-0000-0000-000000000000");
        private static readonly Guid OVERLAY_PERFORMANCE = new("ded574b5-45a0-4f42-8737-46345c09c238");

        private byte _lastR = 0, _lastG = 150, _lastB = 255;
        private byte _brightness = 100;
        private byte _speed = 5;       
        private byte _direction = 0;   
        private int _lastMode = 3;     

        private byte _customCpuFanSpeed = 50;
        private byte _customGpuFanSpeed = 50;
        private byte _lastFanMode = 0x01;
        private bool? _legacyTurboOn;

        /// <summary>
        /// When false, the NVML fallback for GPU temperature is skipped so the dGPU can stay asleep.
        /// </summary>
        public bool AllowGpuDriverQuery { get; set; } = true;

        private static readonly Lazy<string> _model = new(ReadModel);
        public static string Model => _model.Value;

        /// <summary>
        /// The 2020 Helios 300 (PH315-53) has no Quiet/Balanced/Performance modes in firmware;
        /// it only has Turbo, which is a fixed sequence of LED, fan and GPU overclock calls.
        /// </summary>
        public static bool UsesLegacyTurbo => Model.Contains("PH315-53", StringComparison.OrdinalIgnoreCase);

        private static string ReadModel()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Model FROM Win32_ComputerSystem");
                using var results = searcher.Get();
                return results.Cast<ManagementObject>().FirstOrDefault()?["Model"]?.ToString()?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        public byte LastR => _lastR;
        public byte LastG => _lastG;
        public byte LastB => _lastB;
        public byte Brightness => _brightness;
        public byte Speed => _speed;
        public byte Direction => _direction;
        public int LastRgbMode => _lastMode;
        public byte CustomCpuFanSpeed => _customCpuFanSpeed;
        public byte CustomGpuFanSpeed => _customGpuFanSpeed;

        private ManagementObject? GetWmiObject()
        {
            lock (_lock)
            {
                if (_cachedObj != null) return _cachedObj;
                try
                {
                    using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM AcerGamingFunction");
                    using var results = searcher.Get();
                    _cachedObj = results.Cast<ManagementObject>().FirstOrDefault();
                }
                catch { _cachedObj = null; }
                return _cachedObj;
            }
        }

        internal void ResetConnection() => InvalidateCache();

        private void InvalidateCache()
        {
            lock (_lock)
            {
                try { _cachedObj?.Dispose(); } catch { }
                _cachedObj = null;
            }
        }

        private (bool success, ulong output) SendCommand(string method, ulong input)
        {
            try
            {
                var obj = GetWmiObject();
                if (obj == null) return (false, 0);

                using var inParams = obj.GetMethodParameters(method);
                inParams["gmInput"] = input;
                using var outParams = obj.InvokeMethod(method, inParams, null);
                ulong result = Convert.ToUInt64(outParams["gmOutput"]);
                return ((result & 0xFF) == 0, result);
            }
            catch
            {
                InvalidateCache();
                return (false, 0);
            }
        }

        private bool SendLedCommand(byte[] payload)
        {
            try
            {
                var obj = GetWmiObject();
                if (obj == null) return false;

                using var inParams = obj.GetMethodParameters("SetGamingKBBacklight");
                inParams["gmInput"] = payload;
                using var outParams = obj.InvokeMethod("SetGamingKBBacklight", inParams, null);
                ulong result = Convert.ToUInt64(outParams["gmOutput"]);
                return (result & 0xFF) == 0;
            }
            catch
            {
                InvalidateCache();
                return false;
            }
        }

        // Status is in bits 0-7; temperatures use bits 8-15 and fan RPM bits 8-23.
        private int GetSensorReading(ulong sensorId, ulong mask = 0xFFFF)
        {
            try
            {
                var obj = GetWmiObject();
                if (obj == null) return 0;

                using var inParams = obj.GetMethodParameters("GetGamingSysInfo");
                inParams["gmInput"] = (ulong)(0x0001 | (sensorId << 8));
                using var outParams = obj.InvokeMethod("GetGamingSysInfo", inParams, null);
                ulong raw = Convert.ToUInt64(outParams["gmOutput"]);
                if ((raw & 0xFF) == 0) return (int)((raw >> 8) & mask);
            }
            catch (ManagementException) { InvalidateCache(); }
            catch (COMException) { InvalidateCache(); }
            catch { }
            return 0;
        }

        public void SetPowerMode(byte mode)
        {
            if (UsesLegacyTurbo)
                SetLegacyTurbo(mode == 0x05);
            else
                SendCommand("SetGamingMiscSetting", (ulong)0x0B | ((ulong)mode << 8));
            SyncWindowsPowerMode(mode);
        }

        // Same sequence the PH315-53 turbo key triggers (verified by rawPSense), minus PredatorSense's battery check.
        private void SetLegacyTurbo(bool on)
        {
            _legacyTurboOn ??= IsLegacyTurboOn();
            if (_legacyTurboOn == on) return;

            SendCommand("SetGamingLED", on ? 0x10001UL : 0x1UL);
            SendCommand("SetGamingFanBehavior", on ? 0x820009UL : 0x410009UL);
            // CPU/GPU overclock levels; these return the applied level rather than a status code
            SendCommand("SetGamingMiscSetting", on ? 0x205UL : 0x5UL);
            SendCommand("SetGamingMiscSetting", on ? 0x207UL : 0x7UL);
            _legacyTurboOn = on;

            // Leaving turbo resets both fans to auto; put back what the user had picked
            if (!on && _lastFanMode != 0x01)
                SetFanBehavior(_lastFanMode);
        }

        private bool IsLegacyTurboOn()
        {
            var (ok, output) = SendCommand("GetGamingLED", 0x1UL);
            return ok && ((output >> 8) & 0xFF) != 0;
        }

        /// <summary>Hands both fans back to the firmware's automatic control.</summary>
        public void RestoreAutoFans()
        {
            SendCommand("SetGamingFanBehavior", 0x09UL | (0x01UL << 16) | (0x01UL << 22));
        }

        public void SetFanBehavior(byte mode)
        {
            _lastFanMode = mode;
            SendCommand("SetGamingFanBehavior", (ulong)(0x09 | ((ulong)mode << 16) | ((ulong)mode << 22)));

            if (mode == 0x03)
                SetFanSpeed(_customCpuFanSpeed, _customGpuFanSpeed);
        }

        public bool SetFanSpeed(byte cpuSpeed, byte gpuSpeed)
        {
            _customCpuFanSpeed = cpuSpeed;
            _customGpuFanSpeed = gpuSpeed;

            var (cpuOk, _) = SendCommand("SetGamingFanSpeed", 0x01UL | ((ulong)cpuSpeed << 8));
            var (gpuOk, _) = SendCommand("SetGamingFanSpeed", 0x04UL | ((ulong)gpuSpeed << 8));
            return cpuOk && gpuOk;
        }

        public bool SetCpuFanSpeed(byte speed)
        {
            _customCpuFanSpeed = speed;
            var (ok, _) = SendCommand("SetGamingFanSpeed", 0x01UL | ((ulong)speed << 8));
            return ok;
        }

        public bool SetGpuFanSpeed(byte speed)
        {
            _customGpuFanSpeed = speed;
            var (ok, _) = SendCommand("SetGamingFanSpeed", 0x04UL | ((ulong)speed << 8));
            return ok;
        }

        public void SetRgbMode(int mode, byte r, byte g, byte b, byte brightness, byte speed, byte direction)
        {
            _lastR = r; _lastG = g; _lastB = b;
            _brightness = brightness;
            _speed = speed;
            _direction = direction;
            _lastMode = mode;
            ApplyLightingMode(mode);
        }

        public void SetBrightness(byte brightness)
        {
            _brightness = brightness;
            ApplyLightingMode(_lastMode);
        }

        public void SetSpeed(byte speed)
        {
            _speed = speed;
            ApplyLightingMode(_lastMode);
        }

        public void SetDirection(byte direction)
        {
            _direction = direction;
            ApplyLightingMode(_lastMode);
        }

        public void SetStaticColor(byte r, byte g, byte b, byte brightness)
        {
            _lastR = r; _lastG = g; _lastB = b;
            _brightness = brightness;
            _lastMode = 0;
            ApplyLightingMode(0);
        }

        private void ApplyLightingMode(int mode)
        {
            SendCommand("SetGamingLEDBehavior", 0x07ul);
            Thread.Sleep(50);

            if (mode != 2 && mode != 3)
            {
                ulong zonePayload = 0x06ul | (0x0Ful << 8)
                    | ((ulong)_lastR << 16) | ((ulong)_lastG << 24) | ((ulong)_lastB << 32);
                SendCommand("SetGamingLEDBehavior", zonePayload);
                Thread.Sleep(20);
            }

            byte[] payload = new byte[16];
            payload[0] = (byte)mode;     
            payload[1] = _speed;         
            payload[2] = _brightness;    
            payload[3] = _direction;     
            payload[5] = _lastR;
            payload[6] = _lastG;
            payload[7] = _lastB;
            payload[9] = 1;              
            SendLedCommand(payload);
        }

        public int CpuTemp => GetSensorReading(0x01, 0xFF);

        public int GpuTemp
        {
            get
            {
                int ec = GetSensorReading(0x0A, 0xFF);
                if (ec > 0 || !AllowGpuDriverQuery) return ec;
                int nv = Nvml.GpuTemperature();
                return nv > 0 ? nv : ec;
            }
        }

        public int CpuFanRpm => GetSensorReading(0x02);
        public int GpuFanRpm => GetSensorReading(0x06);

        private void SyncWindowsPowerMode(byte acerMode)
        {
            try
            {
                Guid overlay = acerMode switch
                {
                    0x00 or 0x06 => OVERLAY_EFFICIENCY,
                    0x04 or 0x05 => OVERLAY_PERFORMANCE,
                    _ => OVERLAY_BALANCED
                };
                PowerSetActiveOverlayScheme(overlay);
            }
            catch { }
        }

        private ManagementObject? GetBatteryControlObject()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryControl");
                using var results = searcher.Get();
                return results.Cast<ManagementObject>().FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public bool SetBatteryChargeLimit(bool enable)
        {
            try
            {
                using var obj = GetBatteryControlObject();
                if (obj == null) return false;

                using var inParams = obj.GetMethodParameters("SetBatteryHealthControl");
                inParams["uBatteryNo"] = (byte)1;
                inParams["uFunctionMask"] = (byte)1;
                inParams["uFunctionStatus"] = (byte)(enable ? 1 : 0);
                inParams["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };

                using var outParams = obj.InvokeMethod("SetBatteryHealthControl", inParams, null);
                ushort result = Convert.ToUInt16(outParams["uReturn"]);
                return result == 0;
            }
            catch
            {
                return false;
            }
        }
        public bool? GetBatteryChargeLimit()
        {
            try
            {
                using var obj = GetBatteryControlObject();
                if (obj == null) return null;

                using var inParams = obj.GetMethodParameters("GetBatteryHealthControlStatus");
                inParams["uBatteryNo"] = (byte)1;

                using var outParams = obj.InvokeMethod("GetBatteryHealthControlStatus", inParams, null);
                ushort status = Convert.ToUInt16(outParams["uFunctionStatus"]);
                return status == 1;
            }
            catch
            {
                return null;
            }
        }

        public bool IsBatteryControlSupported()
        {
            try
            {
                using var obj = GetBatteryControlObject();
                return obj != null;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _cachedObj?.Dispose();
                _cachedObj = null;
            }
        }
    }
}