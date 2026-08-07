using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Models.MiHoYo.Fingerprint;
using FufuLauncher.Models.MiHoYo.Identity;
using FufuLauncher.Services.MiHoYo;
using FufuLauncher.Services.MiHoYo.Fingerprint;
// 统一使用 Models 里的指纹画像类型（曾与 Services.MiHoYo 下同名类型冲突，现冲突源已删除，别名保留以明确来源）
using FpDeviceProfile = FufuLauncher.Models.MiHoYo.Fingerprint.DeviceProfile;

namespace FufuLauncher.Services.MiHoYo;

internal sealed class DeviceFingerprintService : IDeviceFingerprintService
{
    #region
    private const string GetFpUrl = BbsConstants.GetFpUrl;
    private const string GetExtListUrl = BbsConstants.GetExtListUrl;

    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };


    private DeviceFpRequest? _lastRequest;
    private DeviceFpRequest? _lastWebRequest;
    private bool _fpRegistered;
    private string _currentAccountId = "";
    private readonly SemaphoreSlim _fpLock = new(1, 1);

    private readonly IDeviceExtFieldsBuilder _extFieldsBuilder;
    private readonly AccountManager _accountManager;
    private readonly IBbsRequestBuilder _requestBuilder;
    private bool _lastRegisterSucceeded;
    #endregion

    #region
    public DeviceFingerprintService(AccountManager accountManager, IDeviceExtFieldsBuilder extFieldsBuilder, IBbsRequestBuilder requestBuilder)
    {
        _accountManager = accountManager;
        _extFieldsBuilder = extFieldsBuilder;
        _requestBuilder = requestBuilder;
    }

    public DeviceFpRequest? GetCurrentFingerprint(string accountId)
    {
        _fpLock.Wait();
        try
        {
            return _currentAccountId == accountId && _lastRequest is { DeviceFp.Length: > 0 }
                ? _lastRequest
                : null;
        }
        finally
        {
            _fpLock.Release();
        }
    }

    public DeviceFpRequest? GetCurrentWebFingerprint()
    {
        _fpLock.Wait();
        try
        {
            return _lastWebRequest;
        }
        finally
        {
            _fpLock.Release();
        }
    }

    public async Task<DeviceFpRequest> RegisterStandaloneAsync()
    {
        await _fpLock.WaitAsync();
        try
        {
            // 与 GetOrRegisterFingerprintAsync 不同：不改 _currentAccountId/_lastRequest/_fpRegistered，
            // 仅注册一次并返回，避免污染当前账号的内存缓存（登录窗口预注册场景）
            return await RegisterDeviceFpAsync("", new Dictionary<string, string>());
        }
        finally
        {
            _fpLock.Release();
        }
    }

    public async Task<DeviceFpRequest> GetOrRegisterFingerprintAsync(string accountId, Dictionary<string, string> cookies)
    {
        await _fpLock.WaitAsync();
        try
        {
            if (_currentAccountId != accountId)
            {
                _fpRegistered = false;
                _lastRequest = null;
                _lastWebRequest = null;
                _currentAccountId = accountId;
            }

            if (_fpRegistered && _lastRequest is { DeviceFp.Length: > 0 })
                return _lastRequest;

            // 切账号 / 冷启动场景：内存里没有缓存，但 disk 上可能已有 saved fp；
            // 直接读 disk 的 saved fp 灌进内存缓存并返回，避免每次切换都触发一次全新注册（device_id / seed_id / seed_time 全丢）
            if (!string.IsNullOrEmpty(accountId))
            {
                var saved = await _accountManager.LoadFingerprintAsync(accountId);
                if (saved is { DeviceFp.Length: > 0 })
                {
                    _lastRequest = saved;
                    _fpRegistered = true;
                    // 一并恢复 WebView 体系指纹（DEVICEFP 系），保证冷启动首次业务请求 cookie 完整
                    bool webInjected = await InjectSavedWebFingerprintAsync(accountId, cookies);
                    if (!webInjected)
                    {
                        // FingerprintWeb 缺失（旧账号迁移 / 上次 Web 注册失败）：补注册 Web 体系，避免 DEVICEFP 系永久缺失
                        Debug.WriteLine("[DeviceFingerprint][Web] disk 无已保存 Web 指纹，补注册");
                        await RegisterWebFpAsync(accountId, cookies);
                    }
                    Debug.WriteLine($"[DeviceFingerprint] 从 disk 恢复已保存指纹: account={accountId}, fp={saved.DeviceFp}");
                    return _lastRequest;
                }
            }

            _lastRequest = await RegisterDeviceFpAsync(accountId, cookies);
            // 注册失败（返回 errorFp）不缓存为已注册，下次调用重新注册
            _fpRegistered = _lastRegisterSucceeded;
            return _lastRequest!;
        }
        finally
        {
            _fpLock.Release();
        }
    }

    public async Task ResetFingerprintAsync(string accountId)
    {
        await _fpLock.WaitAsync();
        try
        {
            if (_currentAccountId == accountId)
            {
                _fpRegistered = false;
                _lastRequest = null;
                _lastWebRequest = null;
            }

            // 强制重新注册（打出请求日志）；注册成功且指纹变化则写入 cookie 文件
            var cookies = await _accountManager.LoadCookiesAsync(accountId);
            if (cookies.Count == 0)
            {
                Debug.WriteLine($"[DeviceFingerprint] 账号 {accountId} 无 cookies，跳过重注册");
                return;
            }

            var reg = await RegisterDeviceFpAsync(accountId, cookies);
            if (!_lastRegisterSucceeded)
            {
                // 注册失败：不缓存 errorFp，保持原指纹，下次调用重试
                Debug.WriteLine("[DeviceFingerprint] 重注册失败，保持原指纹");
                return;
            }
            _lastRequest = reg;
            _fpRegistered = true;
            _currentAccountId = accountId;

            var saved = await _accountManager.LoadFingerprintAsync(accountId);
            if (saved == null || saved.DeviceFp != reg.DeviceFp)
            {
                await _accountManager.SaveFingerprintAsync(accountId, reg);
                Debug.WriteLine($"[DeviceFingerprint] 重注册后指纹已写入 cookie: {reg.DeviceFp}");
            }
        }
        finally
        {
            _fpLock.Release();
        }
    }

    public async Task<DeviceFpRequest?> UpdateFingerprintAsync(string accountId)
    {
        // 从账号 cookie 文件取上次注册时保存的完整请求体；无则无法更新
        var saved = await _accountManager.LoadFingerprintAsync(accountId);
        if (saved == null)
        {
            Debug.WriteLine($"[DeviceFingerprint] 账号 {accountId} 无已保存指纹，跳过更新");
            return null;
        }
        Debug.WriteLine($"[DeviceFingerprint] >>> 开始更新指纹: account={accountId}, saved_fp={saved.DeviceFp}, seed_id={saved.SeedId}, seed_time={saved.SeedTime}");

        await _fpLock.WaitAsync();
        try
        {
            // ext_fields 重新构造（实时字段重新采样；时间偏移已固定不变）
            var extList = await FetchExtListAsync();
            var allFields = _extFieldsBuilder.Build(DefaultProfile);
            var filtered = allFields.Where(kv => extList.Contains(kv.Key))
                                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            // 复用已保存请求头的 device_id/seed_id/seed_time/device_fp/bbs_device_id
            var update = new DeviceFpRequest
            {
                DeviceId = saved.DeviceId,
                SeedId = saved.SeedId,
                Platform = BbsConstants.FpPlatform,
                SeedTime = saved.SeedTime,
                ExtFields = JsonSerializer.Serialize(filtered, _jsonOptions),
                AppName = BbsConstants.FpAppName,
                BbsDeviceId = saved.BbsDeviceId,
                DeviceFp = saved.DeviceFp
            };

            string bodyJson = JsonSerializer.Serialize(update, _jsonOptions);
            Debug.WriteLine($"[DeviceFingerprint] >>> 更新请求体: {bodyJson.Substring(0, Math.Min(bodyJson.Length, 800))}");

            string serverFp = await TryGetFpViaHttpAsync(bodyJson);
            if (string.IsNullOrEmpty(serverFp))
            {
                Debug.WriteLine("[DeviceFingerprint] 更新请求失败，保留原指纹");
                return null;
            }

            if (serverFp != saved.DeviceFp)
            {
                Debug.WriteLine($"[DeviceFingerprint] 指纹已更新: {saved.DeviceFp} -> {serverFp}");
                update.DeviceFp = serverFp;
                await _accountManager.SaveFingerprintAsync(accountId, update);
                Debug.WriteLine($"[DeviceFingerprint] 更新后指纹已写入 cookie: {serverFp}");
            }
            else
            {
                Debug.WriteLine($"[DeviceFingerprint] 指纹未变化: {saved.DeviceFp}");
            }

            // 同步进程内缓存（仅当仍是该账号：fire-and-forget 的延迟更新可能在切号后才完成，
            // 不能把旧账号的 update 覆盖到新账号缓存；disk 已按账号写对，BuildAsync 会重新恢复）
            if (_currentAccountId == accountId)
            {
                _lastRequest = update;
                _fpRegistered = true;
                Debug.WriteLine($"[DeviceFingerprint] 已同步内存缓存，后续请求使用: {update.DeviceFp}");
            }
            else
            {
                Debug.WriteLine($"[DeviceFingerprint] 更新完成但已切换账号（当前 {_currentAccountId}），不覆盖缓存");
            }

            // WebView 体系同步更新（复用保存的 device_id/seed_id/seed_time/device_fp，ext_fields 为固定浏览器画像）
            await UpdateWebFingerprintAsync(accountId);
            return update;
        }
        finally
        {
            _fpLock.Release();
        }
    }
    #endregion

    #region 设备档案
    
    private static readonly FpDeviceProfile DefaultProfile = new(
        DeviceModel: "Xiaomi 2605EPN8EC",
        ProductName: "2605EPN8EC",
        Board: "2605EPN8EC",
        DeviceType: "2605EPN8EC",
        OsVersion: "12",
        SdkVersion: "32",
        BuildId: "V417IR",
        BuildDisplay: "2605EPN8EC-user 12 V417IR release-keys",
        BuildTime: 1779448087000L
    );

    // getFp 请求用的轻量 ctx：请求头只用 UA（MobileFor 与画像同源），设备 id 字段不参与 getFp 请求头，占位即可
    private static readonly AccountContext FpContext = new(
        AccountId: "",
        ServerType: ServerType.Cn,
        Cookies: new Dictionary<string, string>(),
        Identity: new AccountIdentity("", ""),
        Device: new DeviceIdentity(
            DeviceId: "",
            BbsDeviceId: "",
            DeviceFp: "",
            DeviceName: "Xiaomi " + DefaultProfile.DeviceModel,
            SysVersion: DefaultProfile.OsVersion,
            Model: DefaultProfile.DeviceModel,
            FpLastUpdate: DateTimeOffset.UtcNow),
        UserAgent: new UserAgent(
            Mobile: BbsUserAgents.MobileFor(DefaultProfile.OsVersion, DefaultProfile.ProductName, DefaultProfile.BuildId, BbsConstants.CnAppVersion),
            Web: BbsUserAgents.Web,
            OkHttp: BbsUserAgents.OkHttp));
    #endregion

    #region WebView 体系（DEVICEFP 系）

    // WebView 体系 ext_fields：浏览器画像（FingerprintJS 产物）；
    // 键序与值固定（JsonObject 按插入顺序输出、自动转义）；userAgent 与原生体系同源（DefaultProfile + MobileFor）
    private static string BuildWebViewExtFields()
    {
        var obj = new JsonObject
        {
            ["userAgent"] = BbsUserAgents.MobileFor(DefaultProfile.OsVersion, DefaultProfile.ProductName, DefaultProfile.BuildId, BbsConstants.CnAppVersion),
            ["browserScreenSize"] = "unknown",
            ["maxTouchPoints"] = "5",
            ["isTouchSupported"] = "1",
            ["browserLanguage"] = "zh-CN",
            ["browserPlat"] = "Linux armv81",
            ["browserTimeZone"] = "Asia/Shanghai",
            ["webGlRender"] = "Adreno (TM) 640",
            ["webGlVendor"] = "Qualcomm",
            ["numOfPlugins"] = "0",
            ["listOfPlugins"] = "unknown",
            ["screenRatio"] = "1.75",
            ["deviceMemory"] = "4",
            ["hardwareConcurrency"] = "4",
            ["cpuClass"] = "unknown",
            ["ifNotTrack"] = "unknown",
            ["ifAdBlock"] = "0",
            ["hasLiedLanguage"] = "0",
            ["hasLiedResolution"] = "1",
            ["hasLiedOs"] = "0",
            ["hasLiedBrowser"] = "0",
            ["canvas"] = "5cb59f11fe6a2fc144b63aa591b6f877d2cd04f85e15a7514e0421670e68606d",
            ["webDriver"] = "0",
            ["colorDepth"] = "24",
            ["pixelRatio"] = "1.75",
            ["packageName"] = "unknown",
            ["packageVersion"] = "2.50.1",
            ["webgl"] = "5d002095daced4b8637e7453e62e0af26fc9c597c8d615e249270e5f1a73ce2e"
        };
        return obj.ToJsonString();
    }

    // 拼 Cookie 头：过滤空值，`k=v; k=v`
    private static string BuildCookieHeader(Dictionary<string, string> cookies)
    {
        if (cookies.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var kv in cookies)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"{kv.Key}={kv.Value}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 注册 WebView 体系指纹：baike.mihoyo.com 网页 JS 注册的浏览器指纹，
    /// 与原生体系（platform=2, bbs_cn）相互独立。注册成功后 DEVICEFP 系 cookie 用真实返回值：
    /// DEVICEFP=返回 fp、DEVICEFP_SEED_ID=16hex、DEVICEFP_SEED_TIME=毫秒、_MHYUUID=v4 UUID。
    /// </summary>
    private async Task<DeviceFpRequest?> RegisterWebFpAsync(string accountId, Dictionary<string, string> cookies)
    {
        try
        {
            var webData = new DeviceFpRequest
            {
                DeviceId = Guid.NewGuid().ToString(),
                SeedId = GenerateRandomHex(16),
                SeedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                Platform = BbsConstants.FpWebPlatform,
                ExtFields = BuildWebViewExtFields(),
                AppName = BbsConstants.FpWebAppName,
                BbsDeviceId = null,
                DeviceFp = GenerateDefaultDeviceId()
            };

            string bodyJson = JsonSerializer.Serialize(webData, _jsonOptions);
            Debug.WriteLine($"[DeviceFingerprint][Web] >>> 请求体: {bodyJson.Substring(0, Math.Min(bodyJson.Length, 800))}");

            string serverFp = await TryGetFpViaHttpAsync(bodyJson, webView: true);
            if (string.IsNullOrEmpty(serverFp))
            {
                Debug.WriteLine("[DeviceFingerprint][Web] 注册失败");
                _lastWebRequest = null;
                return null;
            }

            webData.DeviceFp = serverFp;
            _lastWebRequest = webData;
            // 有账号才持久化（登录窗口空账号预注册不写盘）
            if (!string.IsNullOrEmpty(accountId))
                await _accountManager.SaveWebFingerprintAsync(accountId, webData);

            cookies["DEVICEFP"] = serverFp;
            cookies["DEVICEFP_SEED_ID"] = webData.SeedId;
            cookies["DEVICEFP_SEED_TIME"] = webData.SeedTime;
            cookies["_MHYUUID"] = webData.DeviceId;
            Debug.WriteLine($"[DeviceFingerprint][Web] 已注册并注入 cookie: DEVICEFP={serverFp}, seed_id={webData.SeedId}, seed_time={webData.SeedTime}, _MHYUUID={webData.DeviceId}");
            return webData;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceFingerprint][Web] 注册异常: {ex.Message}");
            _lastWebRequest = null;
            return null;
        }
    }

    /// <summary>更新 WebView 指纹：复用保存的 device_id/seed_id/seed_time/device_fp（ext_fields 是固定浏览器画像，不变），指纹变化才写回。</summary>
    private async Task UpdateWebFingerprintAsync(string accountId)
    {
        try
        {
            var saved = await _accountManager.LoadWebFingerprintAsync(accountId);
            if (saved == null)
            {
                Debug.WriteLine($"[DeviceFingerprint][Web] 账号 {accountId} 无已保存 Web 指纹，跳过更新");
                return;
            }

            // 更新请求带 Cookie（登录后全账号 cookie + DEVICEFP 系）
            var cookies = await _accountManager.LoadCookiesAsync(accountId) ?? new Dictionary<string, string>();
            cookies["DEVICEFP"] = saved.DeviceFp;
            cookies["DEVICEFP_SEED_ID"] = saved.SeedId;
            cookies["DEVICEFP_SEED_TIME"] = saved.SeedTime;
            cookies["_MHYUUID"] = saved.DeviceId;

            var update = new DeviceFpRequest
            {
                DeviceId = saved.DeviceId,
                SeedId = saved.SeedId,
                SeedTime = saved.SeedTime,
                Platform = BbsConstants.FpWebPlatform,
                ExtFields = saved.ExtFields,
                AppName = BbsConstants.FpWebAppName,
                BbsDeviceId = null,
                DeviceFp = saved.DeviceFp
            };

            string bodyJson = JsonSerializer.Serialize(update, _jsonOptions);
            Debug.WriteLine($"[DeviceFingerprint][Web] >>> 更新请求体: {bodyJson.Substring(0, Math.Min(bodyJson.Length, 800))}");

            string serverFp = await TryGetFpViaHttpAsync(bodyJson, webView: true, cookieHeader: BuildCookieHeader(cookies));
            if (string.IsNullOrEmpty(serverFp))
            {
                Debug.WriteLine("[DeviceFingerprint][Web] 更新请求失败，保留原指纹");
                return;
            }

            if (serverFp != saved.DeviceFp)
            {
                update.DeviceFp = serverFp;
                await _accountManager.SaveWebFingerprintAsync(accountId, update);
                Debug.WriteLine($"[DeviceFingerprint][Web] 指纹已更新: {saved.DeviceFp} -> {serverFp}");
            }
            else
            {
                Debug.WriteLine($"[DeviceFingerprint][Web] 指纹未变化: {saved.DeviceFp}");
            }
            // 同步内存缓存：GetCurrentWebFingerprint() 返回更新后的值，不与 disk 落后（仅当仍是该账号）
            if (_currentAccountId == accountId)
                _lastWebRequest = update;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceFingerprint][Web] 更新异常: {ex.Message}");
        }
    }

    /// <summary>从 disk 恢复 WebView 指纹并注入 cookie（冷启动首次业务请求前调用）；返回是否注入成功。</summary>
    private async Task<bool> InjectSavedWebFingerprintAsync(string accountId, Dictionary<string, string> cookies)
    {
        var saved = await _accountManager.LoadWebFingerprintAsync(accountId);
        if (saved is { DeviceFp.Length: > 0 })
        {
            cookies["DEVICEFP"] = saved.DeviceFp;
            cookies["DEVICEFP_SEED_ID"] = saved.SeedId;
            cookies["DEVICEFP_SEED_TIME"] = saved.SeedTime;
            cookies["_MHYUUID"] = saved.DeviceId;
            Debug.WriteLine($"[DeviceFingerprint][Web] 从 disk 恢复已保存 Web 指纹并注入 cookie: fp={saved.DeviceFp}");
            return true;
        }
        return false;
    }
    #endregion

    #region 主注册流程
    private async Task<DeviceFpRequest> RegisterDeviceFpAsync(string accountId, Dictionary<string, string> cookies)
    {
        string defaultFp = GenerateDefaultDeviceId();
        string errorFp = GenerateErrorDeviceId();

        // 注册时全部随机生成，不依赖账号、不做任何持久化；重启后重新注册即新设备
        string deviceId = GenerateRandomHex(16);
        string seedId = Guid.NewGuid().ToString();
        string seedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var extList = await FetchExtListAsync();
        var allFields = _extFieldsBuilder.Build(DefaultProfile);
        var filtered = allFields.Where(kv => extList.Contains(kv.Key))
                                .ToDictionary(kv => kv.Key, kv => kv.Value);

        var fpData = new DeviceFpRequest
        {
            DeviceId = deviceId,
            SeedId = seedId,
            Platform = BbsConstants.FpPlatform,
            SeedTime = seedTime,
            ExtFields = JsonSerializer.Serialize(filtered, _jsonOptions),
            AppName = BbsConstants.FpAppName,
            // bbs_device_id = UUID.nameUUIDFromBytes(device_id)（标准 v3，BBS 头 x-rpc-device_id 同源）
            BbsDeviceId = GenerateBbsDeviceId(deviceId),
            DeviceFp = defaultFp
        };

        string bodyJson = JsonSerializer.Serialize(fpData, _jsonOptions);
        Debug.WriteLine($"[DeviceFingerprint] >>> 请求体: {bodyJson.Substring(0, Math.Min(bodyJson.Length, 800))}");

        Debug.WriteLine("[DeviceFingerprint] 使用 HttpClient 请求 device_fp");
        string serverFp = await TryGetFpViaHttpAsync(bodyJson);

        if (!string.IsNullOrEmpty(serverFp))
        {
            _lastRegisterSucceeded = true;
            fpData.DeviceFp = serverFp;
            // 注册成功即写回 cookie 的 Fingerprint 段，保证重启后仍可用同一指纹（登录窗口空账号预注册不写盘）
            if (!string.IsNullOrEmpty(accountId))
                await _accountManager.SaveFingerprintAsync(accountId, fpData);
            // 原生（platform=2, bbs_cn）之外再注册 WebView 体系（platform=5, hk4e_cn）；DEVICEFP 系 cookie 用 WebView 注册的真实返回值
            var webResult = await RegisterWebFpAsync(accountId, cookies);
            if (webResult == null && !string.IsNullOrEmpty(accountId))
            {
                // WebView 体系注册失败：不缓存为已注册，下次调用重试（否则 DEVICEFP 系 cookie 一直缺失）
                _lastRegisterSucceeded = false;
                Debug.WriteLine("[DeviceFingerprint] WebView 指纹注册失败，标记未注册以便下次重试");
            }
        }
        else
        {
            _lastRegisterSucceeded = false;
            fpData.DeviceFp = errorFp;
            Debug.WriteLine($"[DeviceFingerprint] 请求失败，使用 errorFp={errorFp}");
        }
        return fpData;
    }
    #endregion

    #region ext_list 请求
    // getExtList 结果进程内缓存：注册/更新每次拉一次太浪费（同一画像短时间内 ext_list 不变），TTL 10 分钟
    private static readonly TimeSpan ExtListCacheTtl = TimeSpan.FromMinutes(10);
    private static DateTime _extListFetchedAt;
    private static HashSet<string>? _extListCache;

    private static async Task<HashSet<string>> FetchExtListAsync()
    {
        if (_extListCache is not null && DateTime.UtcNow - _extListFetchedAt < ExtListCacheTtl)
            return _extListCache;
        try
        {
            string url = $"{GetExtListUrl}?platform={BbsConstants.FpPlatform}&app_name={BbsConstants.FpAppName}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add(BbsHeaders.UserAgent, BbsUserAgents.OkHttp);
            using var resp = await _httpClient.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("ext_list", out var extList))
            {
                var list = new HashSet<string>();
                foreach (var item in extList.EnumerateArray())
                    if (item.GetString() is string name) list.Add(name);
                _extListCache = list;
                _extListFetchedAt = DateTime.UtcNow;
                return list;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceFingerprint] FetchExtListAsync 异常: {ex.Message}");
        }
        return new HashSet<string>
        {
            "oaid","vaid","aaid","board","brand","hardware","cpuType","deviceType","display",
            "hostname","manufacturer","productName","model","deviceInfo","sdkVersion","osVersion",
            "devId","buildTags","buildType","buildUser","buildTime","screenSize","vendor",
            "romCapacity","romRemain","ramCapacity","ramRemain","appMemory","accelerometer",
            "gyroscope","magnetometer","isRoot","debugStatus","proxyStatus","emulatorStatus",
            "isTablet","simState","ui_mode","sdCapacity","sdRemain","hasKeyboard","isMockLocation",
            "ringMode","isAirMode","batteryStatus","chargeStatus","deviceName",
            "appInstallTimeDiff","appUpdateTimeDiff","packageName","packageVersion","networkType"
        };
    }
    #endregion


    private async Task<string?> TryGetFpViaHttpAsync(string bodyJson, bool webView = false, string? cookieHeader = null)
    {
        try
        {
            // 请求头统一走 IBbsRequestBuilder（GetFpNative / GetFpWebView 场景；UA/Content-Type/gzip/Cookie 一处管理）
            using var req = _requestBuilder.Build(FpContext,
                webView ? BbsRequestScene.GetFpWebView : BbsRequestScene.GetFpNative,
                HttpMethod.Post, GetFpUrl, body: bodyJson,
                options: webView ? new BbsRequestOptions { Cookie = cookieHeader } : null);
            Debug.WriteLine($"[DeviceFingerprint]{(webView ? "[Web]" : "")} >>> HttpClient POST {GetFpUrl}");
            using var resp = await _httpClient.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            Debug.WriteLine($"[DeviceFingerprint]{(webView ? "[Web]" : "")} <<< HttpClient 状态码: {(int)resp.StatusCode}, 响应: {json.Substring(0, Math.Min(json.Length, 500))}");
            return ParseFpResponse(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceFingerprint]{(webView ? "[Web]" : "")} <<< HttpClient 异常: {ex.Message}");
            return null;
        }
    }
   

    #region 响应解析
    private static string? ParseFpResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("retcode", out var rc))
                Debug.WriteLine($"[DeviceFingerprint] ParseFpResponse: retcode={rc.GetInt32()}");
            if (doc.RootElement.TryGetProperty("device_fp", out var rootFp))
            {
                string fp = rootFp.GetString() ?? "";
                Debug.WriteLine($"[DeviceFingerprint] ParseFpResponse: 根级 device_fp={fp}");
                return string.IsNullOrEmpty(fp) ? null : fp;
            }
            if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("device_fp", out var nestedFp))
            {
                string fp = nestedFp.GetString() ?? "";
                Debug.WriteLine($"[DeviceFingerprint] ParseFpResponse: data.device_fp={fp}");
                return string.IsNullOrEmpty(fp) ? null : fp;
            }
            Debug.WriteLine($"[DeviceFingerprint] ParseFpResponse: 未找到 device_fp");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceFingerprint] ParseFpResponse 解析异常: {ex.Message}");
            return null;
        }
    }
    #endregion

    #region 工具方法
    private static string GenerateRandomHex(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
            chars[i] = "0123456789abcdef"[Random.Shared.Next(16)];
        return new string(chars);
    }

    private static string GenerateDefaultDeviceId()
    {
        var rng = Random.Shared;
        return new string(new[] { (char)('1' + rng.Next(9)) }.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + rng.Next(10)))).ToArray());
    }

    private static string GenerateErrorDeviceId()
    {
        var rng = Random.Shared;
        return new string(new[] { (char)('1' + rng.Next(9)) }.Concat(Enumerable.Range(0, 10).Select(_ => (char)('0' + rng.Next(10)))).ToArray());
    }

    // 等价 Java UUID.nameUUIDFromBytes(name)：MD5(name 的 UTF-8 字节) → 设 version=3 / variant → v3 UUID 字符串
    private static string GenerateBbsDeviceId(string deviceId)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(deviceId));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant 10xx
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }


    #endregion


}
