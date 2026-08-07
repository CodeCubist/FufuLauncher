/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

namespace FufuLauncher.Constants.MiHoYo;

/// <summary>
/// miHoYo game_record 系（CN 官方 / 原神）相关的常量：版本号、DS salt、URL、header 名等。
/// 抽出来集中管理，避免散落在 DailyNoteService / DeviceFingerprintService 等业务类里。
/// 注意：OS / miyoushe 走的常量另见 <see cref="GenshinApiEndpoints"/>（hk4e_cn vs hk4e_global 体系不同）。
/// </summary>
public static class BbsConstants
{
    // 客户端版本（不同接口对应不同 app_version，game_record 系用此版本）
    public const string CnAppVersion = "2.112.0";

    // game_record 系 DS 算法盐（cn）
    public const string CnX4Salt = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";
    public const string CnX6Salt = "t0qEgfub6cvueAPgR5m9aQWWVciEer7v";

    // tool_verison 头（game_record 接口要带；服务器端 header 名就是拼错的 verison，如实保留）
    public const string ToolVersion = "v6.7.2-gr-cn";

    // 请求头附加字段
    public const string Page = "v6.7.2-gr-cn_#/ys";
    public const string Referer = "https://webstatic.mihoyo.com/";
    public const string Origin = "https://webstatic.mihoyo.com";

    // game_record 接口
    public const string DailyNoteUrl = "https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/dailyNote";
    public const string WidgetUrl = "https://api-takumi-record.mihoyo.com/game_record/app/genshin/aapi/widget/v2?game_id=2";

    // 设备指纹 (public-data-api) 接口
    public const string GetFpUrl = "https://public-data-api.mihoyo.com/device-fp/api/getFp";
    public const string GetExtListUrl = "https://public-data-api.mihoyo.com/device-fp/api/getExtList";

    // fp 请求体里固定的 app_name / platform（bbs app / Android）
    public const string FpAppName = "bbs_cn";
    public const string FpPlatform = "2";

    // WebView 体系（baike.mihoyo.com 网页 JS 注册的浏览器指纹）：
    // platform=5、app_name=hk4e_cn，产物写入 DEVICEFP 系 cookie
    public const string FpWebAppName = "hk4e_cn";
    public const string FpWebPlatform = "5";
}

/// <summary>
/// HTTP header 名常量。把所有 header 字符串集中在这一处，避免拼写不一致。
/// </summary>
public static class BbsHeaders
{
    // DS / x-rpc-* 系列
    public const string AppVersion = "x-rpc-app_version";
    public const string ClientType = "x-rpc-client_type";
    public const string DeviceId = "x-rpc-device_id";
    public const string SysVersion = "x-rpc-sys_version";
    public const string Channel = "x-rpc-channel";
    public const string Platform = "x-rpc-platform";
    public const string DeviceFp = "x-rpc-device_fp";
    public const string DeviceModel = "x-rpc-device_model";
    public const string ToolVersion = "x-rpc-tool_verison";
    public const string Page = "x-rpc-page";

    // DS 算法相关
    public const string DS = "DS";

    // 通用 / 反爬
    public const string Referer = "Referer";
    public const string Origin = "Origin";
    public const string Cookie = "Cookie";
    public const string UserAgent = "User-Agent";
    public const string XRequestedWith = "X-Requested-With";
}

/// <summary>
/// 三种场景下的 User-Agent 一处管理（mobile / web / okhttp）。
/// </summary>
public static class BbsUserAgents
{
    // 原神 app 模拟 UA 模板片段（与 device_name / sys_version 必须同源，否则服务端会因设备画像不一致触发 1034）
    // 模板里的 {0}=Android 版本（=x-rpc-sys_version）  {1}=设备型号（=x-rpc-device_name 去掉厂商前缀）  {2}=BuildId  {3}=app_version
    private const string MobileTemplate =
        "Mozilla/5.0 (Linux; Android {0}; {1} Build/{2}; wv) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/110.0.5481.154 Safari/537.36 miHoYoBBS/{3}";

    /// <summary>
    /// 按当前账号的设备画像拼 mobile UA：保证 User-Agent 中的 Android 版本 / 设备型号 / BuildId
    /// 与 <c>x-rpc-sys_version</c> / <c>x-rpc-device_name</c> 完全一致。
    /// </summary>
    public static string MobileFor(string sysVersion, string model, string buildId, string appVersion) =>
        string.Format(MobileTemplate, sysVersion, model, buildId, appVersion);

    // 浏览器（web 端接口）
    public const string Web =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    // okhttp 直连（device-fp / api-takumi）
    public const string OkHttp = "okhttp/4.9.3";
}