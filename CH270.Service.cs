using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CH270.Service
{
    class Program
    {
        [DllImport("hid.dll", SetLastError = true)]
        static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid interfaceClassGuid;
            public uint flags;
            public IntPtr reserved;
        }

        const uint DIGCF_PRESENT = 0x02;
        const uint DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x01;
        const uint FILE_SHARE_WRITE = 0x02;
        const uint OPEN_EXISTING = 3;

        // Config variables
        static volatile bool enableDisplay = true;
        static volatile string currentModeSetting = "CPU"; // "CPU", "GPU", "CYCLE"
        static volatile int intervalSeconds = 3;
        static volatile string fanInterface = "CPUFANIN0";
        static volatile string tempUnit = "C";

        // Sensor variables
        static volatile float cpuTemp = 50f;
        static volatile ushort cpuPower = 35;
        static volatile byte cpuUsage = 10;
        static volatile ushort cpuFreq = 4800;
        static volatile ushort cpuFan = 900;

        static volatile float gpuTemp = 50f;
        static volatile ushort gpuPower = 30;
        static volatile byte gpuUsage = 5;
        static volatile ushort gpuFreq = 1000;

        static volatile bool isRunning = true;
        static SafeFileHandle hidHandle = null;
        static string hidPath = null;
        static DateTime lastConfigFileTime = DateTime.MinValue;

        static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "CH270_Service_Singleton_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    // Already running
                    return;
                }

                LoadConfig();

                Thread sensorThread = new Thread(SensorWorker);
                sensorThread.IsBackground = true;
                sensorThread.Start();

                // Main display update loop
                int tickCounter = 0;
                int cycleStep = 0;
                bool cpuShowFan = false;
                bool lastStateWasEnabled = true;

                while (isRunning)
                {
                    try
                    {
                        // Check for config updates on every tick
                        CheckConfigUpdate();

                        if (enableDisplay)
                        {
                            lastStateWasEnabled = true;
                            byte displayMode = 2; // Default CPU Freq: 2

                            string modeUpper = (currentModeSetting ?? "CPU").ToUpper();
                            if (modeUpper == "GPU")
                            {
                                displayMode = 4; // GPU mode
                            }
                            else if (modeUpper == "CYCLE" || modeUpper == "轮播")
                            {
                                tickCounter++;
                                if (tickCounter >= Math.Max(1, intervalSeconds))
                                {
                                    tickCounter = 0;
                                    cycleStep = (cycleStep + 1) % 3;
                                }
                                if (cycleStep == 0) displayMode = 2; // CPU Frequency
                                else if (cycleStep == 1) displayMode = 3; // CPU Fan
                                else displayMode = 4; // GPU
                            }
                            else
                            {
                                // CPU mode: carousel between Frequency (2) and Fan (3) every intervalSeconds
                                tickCounter++;
                                if (tickCounter >= Math.Max(1, intervalSeconds))
                                {
                                    tickCounter = 0;
                                    cpuShowFan = !cpuShowFan;
                                }
                                displayMode = cpuShowFan ? (byte)3 : (byte)2;
                            }

                            SendReport(displayMode);
                        }
                        else
                        {
                            if (lastStateWasEnabled)
                            {
                                SendOffReport();
                                lastStateWasEnabled = false;
                            }
                        }
                    }
                    catch { }

                    Thread.Sleep(1000);
                }
            }
        }

        static void CheckConfigUpdate()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(configPath))
                {
                    DateTime curWrite = File.GetLastWriteTimeUtc(configPath);
                    if (curWrite != lastConfigFileTime)
                    {
                        lastConfigFileTime = curWrite;
                        LoadConfig();
                    }
                }
            }
            catch { }
        }

        static void LoadConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath, Encoding.UTF8);
                    enableDisplay = GetJsonBool(json, "enable_display", true);
                    currentModeSetting = GetJsonString(json, "mode", "CPU");
                    intervalSeconds = GetJsonInt(json, "interval_seconds", 3);
                    fanInterface = GetJsonString(json, "fan_interface", "CPUFANIN0");
                    tempUnit = GetJsonString(json, "temp_unit", "C");
                }
            }
            catch { }
        }

        static string GetJsonString(string json, string key, string def)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return def;

                int colon = json.IndexOf(":", idx + marker.Length);
                if (colon == -1) return def;

                int firstQuote = json.IndexOf("\"", colon + 1);
                if (firstQuote == -1) return def;

                int secondQuote = json.IndexOf("\"", firstQuote + 1);
                if (secondQuote == -1) return def;

                return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }
            catch { return def; }
        }

        static int GetJsonInt(string json, string key, int def)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return def;

                int colon = json.IndexOf(":", idx + marker.Length);
                if (colon == -1) return def;

                int start = colon + 1;
                while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
                int end = start;
                while (end < json.Length && char.IsDigit(json[end])) end++;
                if (end > start)
                {
                    int val;
                    if (int.TryParse(json.Substring(start, end - start), out val)) return val;
                }
            }
            catch { }
            return def;
        }

        static bool GetJsonBool(string json, string key, bool def)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return def;

                int colon = json.IndexOf(":", idx + marker.Length);
                if (colon == -1) return def;

                string sub = json.Substring(colon + 1).Trim().ToLower();
                if (sub.StartsWith("true")) return true;
                if (sub.StartsWith("false")) return false;
            }
            catch { }
            return def;
        }

        static SafeFileHandle GetHidDevice()
        {
            if (hidHandle != null && !hidHandle.IsInvalid && !hidHandle.IsClosed)
            {
                return hidHandle;
            }

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hDevInfo == IntPtr.Zero) return null;

            SP_DEVICE_INTERFACE_DATA ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = (uint)Marshal.SizeOf(ifData);

            string targetPath = null;
            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index, ref ifData))
            {
                uint requiredSize = 0;
                SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                if (requiredSize > 0)
                {
                    IntPtr detailData = Marshal.AllocHGlobal((int)requiredSize);
                    Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 5);
                    if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, detailData, requiredSize, out requiredSize, IntPtr.Zero))
                    {
                        IntPtr pDevicePath = new IntPtr(detailData.ToInt64() + 4);
                        string path = Marshal.PtrToStringAuto(pDevicePath);
                        if (path.ToLower().Contains("vid_3633") && path.ToLower().Contains("pid_0016"))
                        {
                            targetPath = path;
                            Marshal.FreeHGlobal(detailData);
                            break;
                        }
                    }
                    Marshal.FreeHGlobal(detailData);
                }
                index++;
            }
            SetupDiDestroyDeviceInfoList(hDevInfo);

            if (targetPath != null)
            {
                hidPath = targetPath;
                hidHandle = CreateFile(targetPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                return hidHandle;
            }

            return null;
        }

        static void SendReport(byte mode)
        {
            SafeFileHandle handle = GetHidDevice();
            if (handle == null || handle.IsInvalid) return;

            byte[] report = new byte[65];
            report[0] = 16; // Report ID 0x10
            report[1] = 104; // 0x68
            report[2] = 1;
            report[3] = 6;
            report[4] = 35;
            report[5] = 1;
            report[6] = mode; // 2 = CPU Freq, 3 = CPU Fan, 4 = GPU

            // Temp Unit (0 = C, 1 = F)
            bool isF = (tempUnit ?? "C").ToUpper() == "F";
            report[9] = (byte)(isF ? 1 : 0);

            float sendCpuTemp = isF ? (cpuTemp * 1.8f + 32f) : cpuTemp;
            float sendGpuTemp = isF ? (gpuTemp * 1.8f + 32f) : gpuTemp;

            if (mode == 4)
            {
                // GPU Mode: write GPU metrics to primary display slots (D7..D18)
                // as well as dedicated GPU slots (D19..D27) for full hardware compatibility!
                report[7] = (byte)(gpuPower >> 8);
                report[8] = (byte)(gpuPower & 0xFF);

                byte[] tBytes = BitConverter.GetBytes(sendGpuTemp);
                if (BitConverter.IsLittleEndian) Array.Reverse(tBytes);
                Array.Copy(tBytes, 0, report, 10, 4);

                report[14] = gpuUsage;

                report[15] = (byte)(gpuFreq >> 8);
                report[16] = (byte)(gpuFreq & 0xFF);

                report[17] = (byte)(cpuFan >> 8);
                report[18] = (byte)(cpuFan & 0xFF);
            }
            else if (mode == 3)
            {
                // CPU Fan mode
                report[7] = (byte)(cpuPower >> 8);
                report[8] = (byte)(cpuPower & 0xFF);

                byte[] tBytes = BitConverter.GetBytes(sendCpuTemp);
                if (BitConverter.IsLittleEndian) Array.Reverse(tBytes);
                Array.Copy(tBytes, 0, report, 10, 4);

                report[14] = cpuUsage;

                report[15] = (byte)(cpuFan >> 8);
                report[16] = (byte)(cpuFan & 0xFF);

                report[17] = (byte)(cpuFan >> 8);
                report[18] = (byte)(cpuFan & 0xFF);
            }
            else
            {
                // CPU Frequency mode (mode == 2)
                report[7] = (byte)(cpuPower >> 8);
                report[8] = (byte)(cpuPower & 0xFF);

                byte[] tBytes = BitConverter.GetBytes(sendCpuTemp);
                if (BitConverter.IsLittleEndian) Array.Reverse(tBytes);
                Array.Copy(tBytes, 0, report, 10, 4);

                report[14] = cpuUsage;

                report[15] = (byte)(cpuFreq >> 8);
                report[16] = (byte)(cpuFreq & 0xFF);

                report[17] = (byte)(cpuFan >> 8);
                report[18] = (byte)(cpuFan & 0xFF);
            }

            // D19..D27: Dedicated GPU slots per CH Gen2 spec
            report[19] = (byte)(gpuPower >> 8);
            report[20] = (byte)(gpuPower & 0xFF);

            byte[] gBytes = BitConverter.GetBytes(sendGpuTemp);
            if (BitConverter.IsLittleEndian) Array.Reverse(gBytes);
            Array.Copy(gBytes, 0, report, 21, 4);

            report[25] = gpuUsage;

            report[26] = (byte)(gpuFreq >> 8);
            report[27] = (byte)(gpuFreq & 0xFF);

            // D40: Checksum sum(D1..D39) % 256
            int sum = 0;
            for (int i = 1; i <= 39; i++) sum += report[i];
            report[40] = (byte)(sum % 256);
            report[41] = 22; // Termination byte

            uint written;
            bool ok = WriteFile(handle, report, (uint)report.Length, out written, IntPtr.Zero);
            if (!ok)
            {
                try { handle.Close(); } catch { }
                hidHandle = null;
            }

            try
            {
                string modeStr = mode == 4 ? "GPU" : (mode == 3 ? "CPU-Fan" : "CPU-Freq");
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "status.log"),
                    string.Format("Time: {0:yyyy-MM-dd HH:mm:ss} | Mode: {1} | Ok: {2} | CPU: {3:0.0}{4}, {5}W, {6}%, {7}MHz | Fan: {8} ({9}RPM) | GPU: {10:0.0}{4}, {11}W, {12}%, {13}MHz",
                    DateTime.Now, modeStr, ok, sendCpuTemp, isF ? "F" : "C", cpuPower, cpuUsage, cpuFreq, fanInterface, cpuFan, sendGpuTemp, gpuPower, gpuUsage, gpuFreq));
            }
            catch { }
        }

        static void SendOffReport()
        {
            SafeFileHandle handle = GetHidDevice();
            if (handle == null || handle.IsInvalid) return;

            byte[] report = new byte[65];
            report[0] = 16;
            report[1] = 104;
            report[2] = 1;
            report[3] = 6;
            report[4] = 35;
            report[5] = 1;
            report[6] = 0; // Mode 0 = Screen Off
            int sum = 0;
            for (int i = 1; i <= 39; i++) sum += report[i];
            report[40] = (byte)(sum % 256);
            report[41] = 22;

            uint written;
            WriteFile(handle, report, (uint)report.Length, out written, IntPtr.Zero);
        }

        static void SensorWorker()
        {
            while (isRunning)
            {
                try
                {
                    using (NamedPipeClientStream controlPipe = new NamedPipeClientStream(".", "deepcool_sensor_data", PipeDirection.InOut))
                    {
                        controlPipe.Connect(4000);
                        byte[] hello = Encoding.UTF8.GetBytes("{\"type\":\"SERVER_HELLO\",\"clientVersion\":\"1.2.12\",\"seq\":1,\"payload\":{}}");
                        controlPipe.Write(hello, 0, hello.Length);
                        controlPipe.Flush();

                        byte[] buffer = new byte[4096];
                        int read = controlPipe.Read(buffer, 0, buffer.Length);
                        if (read <= 0) continue;

                        byte[] setup = Encoding.UTF8.GetBytes("{\"type\":\"SETUP_DATA_CHANNEL\",\"seq\":2,\"payload\":{}}");
                        controlPipe.Write(setup, 0, setup.Length);
                        controlPipe.Flush();

                        read = controlPipe.Read(buffer, 0, buffer.Length);
                        string readyResp = Encoding.UTF8.GetString(buffer, 0, read);

                        string marker = "\"DataPipeName\":\"";
                        int idx = readyResp.IndexOf(marker);
                        if (idx == -1) continue;

                        int endIdx = readyResp.IndexOf("\"", idx + marker.Length);
                        string fullPipe = readyResp.Substring(idx + marker.Length, endIdx - (idx + marker.Length));
                        int lastSlash = fullPipe.LastIndexOf('\\');
                        string pipeName = lastSlash >= 0 ? fullPipe.Substring(lastSlash + 1) : fullPipe;

                        using (NamedPipeClientStream dataPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In))
                        {
                            dataPipe.Connect(4000);
                            byte[] chunk = new byte[16384];

                            while (isRunning && dataPipe.IsConnected)
                            {
                                int dataRead = dataPipe.Read(chunk, 0, chunk.Length);
                                if (dataRead < 6) break;

                                ushort jsonLen = BitConverter.ToUInt16(chunk, 4);
                                if (jsonLen > 0 && jsonLen <= dataRead - 6)
                                {
                                    string text = Encoding.UTF8.GetString(chunk, 6, jsonLen);
                                    ParseSensorJson(text);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // SensorBridgeServer not ready or disconnected, retry in 2s
                    Thread.Sleep(2000);
                }
            }
        }

        static void ParseSensorJson(string text)
        {
            try
            {
                int pIdx = text.IndexOf("\"payload\":\"");
                if (pIdx == -1) return;
                int start = pIdx + "\"payload\":\"".Length;
                string unescaped = text.Substring(start).Replace("\\\"", "\"").Replace("\\\\", "\\");

                // Extract CPU metrics
                float temp = ExtractJsonFloat(unescaped, "CPU Temperature");
                if (temp > 0) cpuTemp = temp;

                float power = ExtractJsonFloat(unescaped, "CPU Power");
                if (power > 0) cpuPower = (ushort)Math.Round(power);

                float usage = ExtractJsonFloat(unescaped, "CPU Usage");
                if (usage >= 0) cpuUsage = (byte)Math.Min(100, Math.Max(0, (int)Math.Round(usage)));

                float freq = ExtractJsonFloat(unescaped, "CPU Clock");
                if (freq > 0) cpuFreq = (ushort)Math.Round(freq);

                // Fan extraction from "Fan list": { "CPUFANIN0": 539, ... }
                string targetFan = fanInterface ?? "CPUFANIN0";
                int fanIdx = unescaped.IndexOf("\"" + targetFan + "\"", StringComparison.OrdinalIgnoreCase);
                if (fanIdx != -1)
                {
                    int colon = unescaped.IndexOf(":", fanIdx + targetFan.Length);
                    if (colon != -1)
                    {
                        int fStart = colon + 1;
                        while (fStart < unescaped.Length && (unescaped[fStart] == ' ' || unescaped[fStart] == '\t')) fStart++;
                        int fEnd = fStart;
                        while (fEnd < unescaped.Length && (char.IsDigit(unescaped[fEnd]) || unescaped[fEnd] == '.')) fEnd++;
                        if (fEnd > fStart)
                        {
                            float val;
                            if (float.TryParse(unescaped.Substring(fStart, fEnd - fStart), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val))
                            {
                                cpuFan = (ushort)Math.Round(val);
                            }
                        }
                    }
                }

                // GPU metrics (case-insensitive)
                float gTemp = ExtractJsonFloat(unescaped, "Gpu temperature");
                if (gTemp > 0) gpuTemp = gTemp;

                float gPower = ExtractJsonFloat(unescaped, "Gpu power");
                if (gPower > 0) gpuPower = (ushort)Math.Round(gPower);

                float gUsage = ExtractJsonFloat(unescaped, "Gpu usage");
                if (gUsage >= 0) gpuUsage = (byte)Math.Min(100, Math.Max(0, (int)Math.Round(gUsage)));

                float gFreq = ExtractJsonFloat(unescaped, "Gpu clock");
                if (gFreq > 0) gpuFreq = (ushort)Math.Round(gFreq);
            }
            catch { }
        }

        static float ExtractJsonFloat(string text, string key)
        {
            try
            {
                string marker = "\"" + key + "\"";
                int idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx == -1) return -1f;

                string valMarker = "\"Value\":";
                int vIdx = text.IndexOf(valMarker, idx, StringComparison.OrdinalIgnoreCase);
                if (vIdx == -1) return -1f;

                int start = vIdx + valMarker.Length;
                while (start < text.Length && (text[start] == ' ' || text[start] == '\t')) start++;
                int end = start;
                while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.' || text[end] == '-')) end++;
                if (end > start)
                {
                    float val;
                    if (float.TryParse(text.Substring(start, end - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val))
                    {
                        return val;
                    }
                }
            }
            catch { }
            return -1f;
        }
    }
}
