/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using FufuLauncher.Contracts.Services;
using FpDeviceProfile = FufuLauncher.Models.MiHoYo.Fingerprint.DeviceProfile;

namespace FufuLauncher.Services.MiHoYo.Fingerprint;


internal sealed class ExtFieldsBuilder : IDeviceExtFieldsBuilder
{
    private const string Brand = "Xiaomi";
    private const string Manufacturer = "Xiaomi";
    private const string DeviceName = "Xiaomi 17 Max";
    private const string Model = "2605EPN8EC";       
    private const string Hardware = "Xiaomi";     
    private const string CpuType = "arm64-v8a";
    private const string ScreenSize = "1440x3200";
    private const string PackageName = "com.mihoyo.hyperion";
    private const string PackageVersion = "2.42.0";
    private const string BuildIncremental = "18.6.10"; 
    private const string BuildUser = "builder";     
    private const string Hostname = "xiaomi";        
    private const string BuildTags = "release-keys"; 
    private const string BuildType = "user";         
    private const string UiMode = "UI_MODE_TYPE_NORMAL";
    private const string NetworkType = "WiFi";
    private const long InstallTimeMs = 1785998383082L;
    private const long UpdateTimeMs = 1785998383082L;

    public Dictionary<string, object> Build(FpDeviceProfile p)
    {
        var rng = Random.Shared;

        int battery = rng.Next(50, 95);
        int chargeStatus = battery >= 95 ? 5 : 1;

     
        long storageMb = 512L * 1024L;
        long ramCapacity = storageMb;                            
        long ramRemain = (long)(ramCapacity * (0.5 + rng.NextDouble() * 0.45)); 
        long sdCapacity = ramCapacity;                           
        long sdRemain = ramRemain;                               

      
        string appHeapMb = rng.Next(512, 1025).ToString();

        string accelerometer = $"{0.1 + rng.NextDouble() * 0.5:F7}x{9.78 + rng.NextDouble() * 0.05:F7}x{0.15 + rng.NextDouble() * 0.3:F7}";
        string magnetometer = $"{rng.NextDouble() * 30 - 15:F6}x{rng.NextDouble() * -50 + 10:F6}x{rng.NextDouble() * -50:F6}";
        string gyroscope = $"{rng.NextDouble() * 0.05:F9}x{rng.NextDouble() * 0.05 - 0.025:F9}x{rng.NextDouble() * 0.05 - 0.025:F9}";

        
        string deviceInfo = $"Xiaomi/{p.DeviceType}/{p.DeviceType}:{p.OsVersion}/{p.BuildId}/{BuildIncremental}:user/release-keys";
 
        string display = p.BuildDisplay;

        return new Dictionary<string, object>
        {
            { "proxyStatus", 0 }, { "isRoot", 0 },
            { "romCapacity", appHeapMb },
            { "deviceName", DeviceName },      
            { "productName", p.DeviceType },          
            { "romRemain", rng.Next(200, 480).ToString() },
            { "hostname", Hostname },                
            { "screenSize", ScreenSize },
            { "isTablet", 1 },                     
            { "aaid", "error_1008008" },            
            { "model", Model },                       
            { "brand", Brand },
            { "hardware", Hardware },
            { "deviceType", p.DeviceType },
            { "devId", "REL" },                      
            { "sdCapacity", sdCapacity },
            { "buildTime", p.BuildTime.ToString() },
            { "buildUser", BuildUser },
            { "simState", 5 },                        
            { "ramRemain", ramRemain.ToString() },
            { "appUpdateTimeDiff", UpdateTimeMs },
            { "deviceInfo", deviceInfo },
            { "vaid", "error_1008008" },
            { "buildType", BuildType },
            { "sdkVersion", p.SdkVersion },
            { "ui_mode", UiMode },
            { "isMockLocation", 0 },
            { "cpuType", CpuType },
            { "isAirMode", 0 },
            { "ringMode", 2 },
            { "chargeStatus", chargeStatus },
            { "manufacturer", Manufacturer },
            { "emulatorStatus", 0 },
            { "appMemory", appHeapMb },               
            { "osVersion", p.OsVersion },
            { "vendor", "unknown" },
            { "accelerometer", accelerometer },
            { "sdRemain", sdRemain },
            { "buildTags", BuildTags },
            { "packageName", PackageName },
            { "networkType", NetworkType },
            { "oaid", "error_1008008" },
            { "debugStatus", 0 },
            { "ramCapacity", ramCapacity.ToString() },
            { "magnetometer", magnetometer },
            { "display", display },
            { "appInstallTimeDiff", InstallTimeMs },
            { "packageVersion", PackageVersion },
            { "gyroscope", gyroscope },
            { "batteryStatus", battery },
            { "hasKeyboard", 0 },                     
            { "board", p.Board },
        };
    }
}
