/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using System.Net;
using System.Net.Http;
using System.Text;
using FufuLauncher.Constants.MiHoYo;

namespace FufuLauncher.Services.MiHoYo.Transport;

/// <summary>
/// 业务 service 调一行拿到预制 <see cref="HttpRequestMessage"/>，不再各自拼 header 字符串。
///
/// 4 个方法对应 4 套完全不同的 header 模板（DS 算法 / UA / cookie 形态 / device 字段来源 / Referer-Origin / ClientType 都不一样）：
///   - <see cref="ForDailyNote"/>：game_record 系（api-takumi-record.mihoyo.com）。CN 移动端 UA + bbs_device_id 鉴权。
///   - <see cref="ForUserInfo"/>：binding/getUserGameRolesByCookie 系（api-takumi.mihoyo.com）。web DS salt + 随机 device_id。
///   - <see cref="ForGacha"/>：genAuthKey 系（api-takumi.mihoyo.com）。lk2 salt + 6 字符 base36 r。
///   - <see cref="ForCommunity"/>：bbs-api.miyoushe.com 任务/签到系。社区签到专用头（h265_supported / verify_key / channel / csm_source）。
///
/// 接受参数都尽量"按需暴露"——仅传实际会用到的 cookie / 设备字段，不强行统一形状。
/// </summary>
public static class BbsRequestHeaders
{
    /// <summary>cookie 字符串拼装模式：<see cref="SToken"/> 只取 stoken + mid + stuid（game_record widget 用），<see cref="Full"/> 全量拼接。</summary>
    public enum CookieMode
    {
        /// <summary>所有非空 cookie 用 ";" 拼接。</summary>
        Full,
        /// <summary>只取 stoken + mid + stuid（widget 端只验这 3 个）。</summary>
        SToken
    }

    #region game_record 系（DailyNoteService 用）

    /// <summary>
    /// game_record 系（api-takumi-record.mihoyo.com）：DailyNote / Widget。
    /// DS salt 走 <see cref="BbsConstants.CnX4Salt"/> 或 <see cref="BbsConstants.CnX6Salt"/>（<paramref name="dsSalt"/> 由调用方按接口选）。
    /// </summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="url">完整 URL（含 query）。</param>
    /// <param name="cookies">账号 cookie 字典。</param>
    /// <param name="device">登录时持久化的设备指纹（用 BbsDeviceId / DeviceFp）。</param>
    /// <param name="deviceName">x-rpc-device_name；如 "Xiaomi 2605EPN8EC"。本方法内部做 URL 编码（Xiaomi%202605EPN8EC）。</param>
    /// <param name="sysVersion">x-rpc-sys_version；如 "14"。</param>
    /// <param name="userAgent">mobile UA（用 <see cref="BbsUserAgents.MobileFor"/> 由 <see cref="FufuLauncher.Services.MiHoYo.AccountIdentityService"/> 按账号画像拼出）。</param>
    /// <param name="dsSalt">DS salt；DailyNote 走 <see cref="BbsConstants.CnX4Salt"/>，Widget 走 <see cref="BbsConstants.CnX6Salt"/>。</param>
    /// <param name="cookieMode">cookie 拼接模式：DailyNote 用 <see cref="CookieMode.Full"/>，Widget 用 <see cref="CookieMode.SToken"/>。</param>
    /// <param name="xrpcChallenge">可选，1034 验证码兜底时填。</param>
    public static HttpRequestMessage ForDailyNote(
        HttpMethod method,
        string url,
        Dictionary<string, string> cookies,
        (string BbsDeviceId, string DeviceFp) device,
        string deviceName,
        string sysVersion,
        string userAgent,
        string dsSalt,
        CookieMode cookieMode = CookieMode.Full,
        string? xrpcChallenge = null)
    {
        string cookieStr = BuildCookieString(cookies, cookieMode);
        string query = new Uri(url).Query.TrimStart('?');
        // 便签 DS 的 r 是 6 位十进制 [100000,200000)，不能用默认 5 位
        string ds = DynamicSignature.Compute(dsSalt, query: query, body: "", rFormat: DynamicSignature.RFormat.Decimal100k);

        var req = new HttpRequestMessage(method, url);
        // header 顺序按客户端请求原序：业务自定义头（DS / app_version / tool / UA / device_* / sys / client_type）
        // → 标准头（Origin / X-Requested-With / Sec-Fetch-* / Referer / Accept-Encoding / Accept-Language / Cookie）。
        // HttpClient 按 TryAddWithoutValidation 调用顺序输出，顺序对不上容易被反爬识别为"非客户端壳发起"
        req.Headers.TryAddWithoutValidation(BbsHeaders.DS, ds);
        req.Headers.TryAddWithoutValidation(BbsHeaders.AppVersion, BbsConstants.CnAppVersion);
        req.Headers.TryAddWithoutValidation(BbsHeaders.ToolVersion, BbsConstants.ToolVersion);
        req.Headers.UserAgent.ParseAdd(userAgent);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DeviceId, device.BbsDeviceId);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        // x-rpc-device_name 必须 URL 编码（空格 → %20），否则服务端按 header value 解析时会触发 1034
        req.Headers.TryAddWithoutValidation("x-rpc-device_name", Uri.EscapeDataString(deviceName));
        req.Headers.TryAddWithoutValidation(BbsHeaders.Page, BbsConstants.Page);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DeviceFp, device.DeviceFp);
        req.Headers.TryAddWithoutValidation(BbsHeaders.SysVersion, sysVersion);
        req.Headers.TryAddWithoutValidation(BbsHeaders.ClientType, "5");
        req.Headers.TryAddWithoutValidation(BbsHeaders.Origin, BbsConstants.Origin);
        req.Headers.TryAddWithoutValidation(BbsHeaders.XRequestedWith, "com.mihoyo.hyperion");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        req.Headers.TryAddWithoutValidation(BbsHeaders.Referer, BbsConstants.Referer);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
        if (!string.IsNullOrEmpty(xrpcChallenge))
            req.Headers.TryAddWithoutValidation("x-rpc-challenge", xrpcChallenge);
        req.Headers.TryAddWithoutValidation(BbsHeaders.Cookie, cookieStr);
        return req;
    }

    #endregion

    #region binding 系（UserInfoService 用）

    /// <summary>
    /// binding/getUserGameRolesByCookie 系（api-takumi.mihoyo.com）。
    /// DS salt 走 web 系列（X4 系列另一份 web 版本），device_id 随机生成（无 fp 体系）。
    /// </summary>
    /// <param name="method">HTTP 方法（GET / POST）。</param>
    /// <param name="url">完整 URL。</param>
    /// <param name="cookie">完整 cookie 字符串（来自 UserInfoService.ApplyCommonHeaders 风格的合并字符串）。</param>
    /// <param name="webDsSalt">web 系 DS salt（如 <c>"xV8v4Qu54lUKrEYFZkJhB8cuoh9NXmz9"</c>）。</param>
    /// <param name="userAgent">移动端 UA。</param>
    /// <param name="referer">如 <c>"https://act.mihoyo.com/"</c>。</param>
    /// <param name="origin">如 <c>"https://act.mihoyo.com"</c>。</param>
    public static HttpRequestMessage ForUserInfo(
        HttpMethod method,
        string url,
        string cookie,
        string webDsSalt,
        string userAgent,
        string referer,
        string origin)
    {
        string ds = DynamicSignature.ComputeMinimal(webDsSalt, DynamicSignature.RFormat.Decimal100k);

        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation(BbsHeaders.Cookie, cookie);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DS, ds);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DeviceId, Guid.NewGuid().ToString("N"));
        req.Headers.TryAddWithoutValidation(BbsHeaders.ClientType, "5");
        req.Headers.TryAddWithoutValidation(BbsHeaders.Referer, referer);
        req.Headers.TryAddWithoutValidation(BbsHeaders.Origin, origin);
        req.Headers.TryAddWithoutValidation(BbsHeaders.UserAgent, userAgent);
        return req;
    }

    #endregion

    #region genAuthKey 系（GachaService 用）

    /// <summary>
    /// genAuthKey 系（api-takumi.mihoyo.com）。lk2 salt + 6 字符 base36 r。
    /// </summary>
    public static HttpRequestMessage ForGacha(
        HttpMethod method,
        string url,
        string stuidStokenMidCookie,
        string lk2Salt,
        string gachaAppVersion,
        string? body = null,
        string? deviceId = null)
    {
        string ds = DynamicSignature.ComputeMinimal(lk2Salt, DynamicSignature.RFormat.Base36_6);

        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation(BbsHeaders.Cookie, stuidStokenMidCookie);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DS, ds);
        req.Headers.TryAddWithoutValidation(BbsHeaders.AppVersion, gachaAppVersion);
        req.Headers.TryAddWithoutValidation(BbsHeaders.ClientType, "5");
        // 优先用指纹体系 device_id（16 hex），无指纹时回退随机
        req.Headers.TryAddWithoutValidation(BbsHeaders.DeviceId,
            string.IsNullOrEmpty(deviceId) ? Guid.NewGuid().ToString("N") : deviceId);
        req.Headers.TryAddWithoutValidation(BbsHeaders.Referer, "https://app.mihoyo.com");
        req.Headers.TryAddWithoutValidation(BbsHeaders.UserAgent, $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) miHoYoBBS/{gachaAppVersion}");
        return req;
    }

    #endregion

    #region 社区签到系（CommunityCheckinService 用）

    /// <summary>
    /// 社区签到 / 任务 / 帖子接口（bbs-api.miyoushe.com）。
    /// DS 由调用方传入（既有 <c>SignatureHelper.GetDsX6</c> 也有 <c>MihoyoBBS.Tools.GetDs</c>，留给 service 内部选择）。
    /// device 字段从 LocalSettings 读后再传入。
    /// </summary>
    public static HttpRequestMessage ForCommunity(
        HttpMethod method,
        string url,
        string stokenCookie,
        string deviceId,
        string deviceName,
        string deviceModel,
        string deviceFp,
        string ds,
        string userAgent = "okhttp/4.9.3")
    {
        var req = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post)
        {
            // POST 时 body 由调用方通过 SetJsonBody 追加；本方法不接管 body
        }
        req.Headers.TryAddWithoutValidation(BbsHeaders.Cookie, stokenCookie);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DS, ds);
        req.Headers.TryAddWithoutValidation(BbsHeaders.ClientType, "2");
        req.Headers.TryAddWithoutValidation(BbsHeaders.AppVersion, BbsConstants.CnAppVersion);
        req.Headers.TryAddWithoutValidation(BbsHeaders.SysVersion, "12");
        req.Headers.TryAddWithoutValidation(BbsHeaders.Channel, "miyousheluodi");
        req.Headers.TryAddWithoutValidation(BbsHeaders.DeviceId, deviceId);
        req.Headers.TryAddWithoutValidation("x-rpc-device_name", deviceName);
        req.Headers.TryAddWithoutValidation(BbsHeaders.DeviceModel, deviceModel);
        req.Headers.TryAddWithoutValidation("x-rpc-h265_supported", "1");
        req.Headers.TryAddWithoutValidation(BbsHeaders.Referer, "https://app.mihoyo.com");
        req.Headers.TryAddWithoutValidation("x-rpc-verify_key", FufuLauncher.Constants.GenshinApiEndpoints.PassportAppId);
        req.Headers.TryAddWithoutValidation("x-rpc-csm_source", "discussion");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        req.Headers.UserAgent.ParseAdd(userAgent);
        return req;
    }

    /// <summary>给已有 <see cref="HttpRequestMessage"/> 追加 JSON body（用于 ForCommunity 之后调 POST）。</summary>
    public static void SetJsonBody(this HttpRequestMessage request, string body)
    {
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "UTF-8"
        };
    }

    #endregion

    #region 工具

    /// <summary>
    /// 暴露 cookie 拼接函数供 service 间复用（如 DailyNoteService → GeetestService 内部仍要拼 cookie）。
    /// </summary>
    public static string BuildCookieStringForTest(Dictionary<string, string> cookies, CookieMode mode) =>
        BuildCookieString(cookies, mode);

    /// <summary>cookie 键序（WebView CookieJar 写入序）：login_ticket → 设备系(_MHYUUID/DEVICEFP 系) → mi18nLang → 账号系 → 风控(aliyungf_tc/acw_tc) → 其余按字典序追加。</summary>
    private static readonly string[] CookieOrder =
    {
        "login_ticket",
        "_MHYUUID", "_ga", "_gid",
        "DEVICEFP_SEED_ID", "DEVICEFP_SEED_TIME", "DEVICEFP",
        "mi18nLang",
        "ltuid", "account_id_v2", "account_id", "ltmid_v2",
        "cookie_token_v2", "ltoken_v2", "cookie_token", "ltuid_v2", "account_mid_v2", "ltoken",
        "aliyungf_tc", "acw_tc", "_gat_gtag_UA_133007358_5"
    };

    private static string BuildCookieString(Dictionary<string, string> cookies, CookieMode mode)
    {
        var sb = new StringBuilder();
        if (mode == CookieMode.SToken)
        {
            if (cookies.TryGetValue("stoken", out var stoken) && !string.IsNullOrEmpty(stoken)) sb.Append($"stoken={stoken}");
            if (cookies.TryGetValue("mid", out var mid) && !string.IsNullOrEmpty(mid)) sb.Append($";mid={mid}");
            string stuid = cookies.GetValueOrDefault("stuid") ?? cookies.GetValueOrDefault("account_id") ?? cookies.GetValueOrDefault("ltuid_v2") ?? "";
            if (!string.IsNullOrEmpty(stuid)) sb.Append($";stuid={stuid}");
        }
        else
        {
            // cookie 顺序按 WebView CookieJar 写入序；空值也照发（login_ticket= 空值开头）；
            // stoken/mid/stuid 不进入普通业务请求（stoken 系只走 stoken 专用接口，带上会被识别为异常客户端）。
            var ordered = cookies.Keys
                .Where(k => k is not ("stoken" or "mid" or "stuid"))
                .OrderBy(k => { int i = Array.IndexOf(CookieOrder, k); return i >= 0 ? i : CookieOrder.Length; })
                .ThenBy(k => k, StringComparer.Ordinal);
            foreach (var key in ordered)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append($"{key}={cookies[key]}");
            }
        }
        return sb.ToString();
    }

    #endregion
}