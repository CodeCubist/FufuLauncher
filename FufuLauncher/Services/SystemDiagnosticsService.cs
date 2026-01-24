using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using FufuLauncher.Models;

namespace FufuLauncher.Services;

public class SystemDiagnosticsService
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    public async Task<SystemDiagnosticsInfo> GetSystemInfoAsync()
    {
        return await Task.Run(() =>
        {
            var info = new SystemDiagnosticsInfo();

            try
            {
                info.OsVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                using (var searcher = new ManagementObjectSearcher("select Name from Win32_Processor"))
                {
                    foreach (var item in searcher.Get())
                    {
                        info.CpuName = item["Name"]?.ToString() ?? "未知处理器";
                        break;
                    }
                }

                using (var searcher = new ManagementObjectSearcher("select Capacity from Win32_PhysicalMemory"))
                {
                    long totalCapacity = 0;
                    foreach (var item in searcher.Get())
                    {
                        if (long.TryParse(item["Capacity"]?.ToString(), out long capacity))
                        {
                            totalCapacity += capacity;
                        }
                    }
                    info.TotalMemory = $"{totalCapacity / (1024 * 1024 * 1024)} GB";
                }

                using (var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController"))
                {
                    foreach (var item in searcher.Get())
                    {
                        info.GpuName = item["Name"]?.ToString() ?? "未知显卡";
                        if (info.GpuName.Contains("NVIDIA") || info.GpuName.Contains("AMD")) break;
                    }
                }

                DEVMODE dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                if (EnumDisplaySettings(null, -1, ref dm))
                {
                    info.ScreenResolution = $"{dm.dmPelsWidth} x {dm.dmPelsHeight}";
                    info.CurrentRefreshRate = $"{dm.dmDisplayFrequency} Hz";
                }

                int maxHz = 0;
                int i = 0;
                while (EnumDisplaySettings(null, i, ref dm))
                {
                    if (dm.dmDisplayFrequency > maxHz)
                    {
                        maxHz = dm.dmDisplayFrequency;
                    }
                    i++;
                }
                info.MaxRefreshRate = maxHz > 0 ? $"{maxHz} Hz" : "无法检测";

                info.Suggestion = GenerateSuggestion(info);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Diagnostics] Error: {ex.Message}");
                info.Suggestion = "诊断过程中发生错误，部分信息可能不准确。";
            }

            return info;
        });
    }

    private string GenerateSuggestion(SystemDiagnosticsInfo info)
    {
        var suggestions = new List<string>();

        if (int.TryParse(info.CurrentRefreshRate.Replace(" Hz", ""), out int currentHz) &&
            int.TryParse(info.MaxRefreshRate.Replace(" Hz", ""), out int maxHz))
        {
            if (currentHz < maxHz)
            {
                suggestions.Add($"您的显示器支持 {maxHz}Hz，但当前仅运行在 {currentHz}Hz");
            }
            else if (currentHz >= 120)
            {
                suggestions.Add("正常");
            }
        }

        if (suggestions.Count == 0) return "正常";

        return string.Join("\n", suggestions);
    }
}