/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using System.Security.Cryptography;
using System.Text;
using FufuLauncher.Constants;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Services.MiHoYo.Transport;

/// <summary>
/// 统一请求头构建（<see cref="IBbsRequestBuilder"/> 的实现）。
/// 各场景头模板收敛在这里：版本号 / 设备指纹（ctx.Device）/ UA（ctx.UserAgent）/ DS / cookie 顺序全部一处管理。
/// </summary>
public sealed class BbsRequestBuilder : IBbsRequestBuilder
{
    // WebLogin（passport 系）专用 DS salt（原 LoginQrWindow 私有常量）
    private const string WebLoginSalt = "dDIQHbKOdaPaLuvQKVzUzqdeCaxjtaPV";

    // 社区签到 X6 salt（原 MihoyoBBS.Tools.GetDs(web:false) 内部值）
    private const string CommunityX6Salt = "idMMaGYmVgPzh3wxmWudUXKUPGidO7GM";

    private const string CommunityReferer = "https://app.mihoyo.com";
    private const string GeetestReferer = "https://webstatic.mihoyo.com";
    private const string GeetestChallengePath = "/game_record/app/genshin/api/dailyNote";

    public HttpRequestMessage Build(
        AccountContext ctx,
        BbsRequestScene scene,
        HttpMethod method,
        string url,
        string? body = null,
        string? challenge = null,
        BbsRequestOptions? options = null)
    {
        Dictionary<string, string> cookies = ctx.Cookies as Dictionary<string, string>
            ?? new Dictionary<string, string>(ctx.Cookies);

        return scene switch
        {
            BbsRequestScene.DailyNote => BuildDailyNote(ctx, method, url, cookies, challenge),
            BbsRequestScene.DailyNoteWidget => BuildDailyNote(ctx, method, url, cookies, null, widget: true),
            BbsRequestScene.UserFullInfo => BuildUserFullInfo(ctx, method, url, cookies),
            BbsRequestScene.CommunitySign => BuildCommunitySign(ctx, method, url, cookies, body),
            BbsRequestScene.Geetest => BuildGeetest(ctx, method, url, cookies, body, options),
            BbsRequestScene.WebLogin => BuildWebLogin(ctx, method, url, body, options),
            BbsRequestScene.GetFpNative => BuildGetFpNative(ctx, method, url, body),
            BbsRequestScene.GetFpWebView => BuildGetFpWebView(ctx, method, url, body, options),
            _ => throw new ArgumentOutOfRangeException(nameof(scene), scene, null)
        };
    }

    // ---- 便签 / Widget（复用 BbsRequestHeaders.ForDailyNote）----
    private static HttpRequestMessage BuildDailyNote(
        AccountContext ctx, HttpMethod method, string url, Dictionary<string, string> cookies,
        string? challenge, bool widget = false) =>
        BbsRequestHeaders.ForDailyNote(
            method, url, cookies,
            device: (ctx.Device.BbsDeviceId, ctx.Device.DeviceFp),
            deviceName: ctx.Device.DeviceName,
            sysVersion: ctx.Device.SysVersion,
            userAgent: ctx.UserAgent.Mobile,
            dsSalt: widget ? BbsConstants.CnX6Salt : BbsConstants.CnX4Salt,
            cookieMode: widget ? BbsRequestHeaders.CookieMode.SToken : BbsRequestHeaders.CookieMode.Full,
            xrpcChallenge: challenge);

    // ---- UserFullInfo（bbs-api wapi，无 DS）----
    private static HttpRequestMessage BuildUserFullInfo(
        AccountContext ctx, HttpMethod method, string url, Dictionary<string, string> cookies)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Cookie",
            BbsRequestHeaders.BuildCookieStringForTest(cookies, BbsRequestHeaders.CookieMode.Full));
        req.Headers.TryAddWithoutValidation("x-rpc-device_id", ctx.Device.BbsDeviceId);
        if (!string.IsNullOrEmpty(ctx.Device.DeviceFp))
            req.Headers.TryAddWithoutValidation("x-rpc-device_fp", ctx.Device.DeviceFp);
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", BbsConstants.CnAppVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        req.Headers.TryAddWithoutValidation("Referer", "https://bbs.mihoyo.com/");
        req.Headers.TryAddWithoutValidation("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) miHoYoBBS/{BbsConstants.CnAppVersion}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return req;
    }

    // ---- 社区签到（bbs-api.miyoushe.com：X6 minimal DS + stoken 系 cookie）----
    private static HttpRequestMessage BuildCommunitySign(
        AccountContext ctx, HttpMethod method, string url, Dictionary<string, string> cookies, string? body)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Cookie", BuildStokenCookie(ctx));
        req.Headers.TryAddWithoutValidation("DS",
            DynamicSignature.ComputeMinimal(CommunityX6Salt, DynamicSignature.RFormat.Base36_6));
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", "2");
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", BbsConstants.CnAppVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-sys_version", ctx.Device.SysVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-channel", "miyousheluodi");
        req.Headers.TryAddWithoutValidation("x-rpc-device_id", ctx.Device.BbsDeviceId);
        req.Headers.TryAddWithoutValidation("x-rpc-device_name", ctx.Device.DeviceName);
        req.Headers.TryAddWithoutValidation("x-rpc-device_model", ctx.Device.Model);
        req.Headers.TryAddWithoutValidation("x-rpc-h265_supported", "1");
        req.Headers.TryAddWithoutValidation("Referer", CommunityReferer);
        req.Headers.TryAddWithoutValidation("x-rpc-verify_key", GenshinApiEndpoints.PassportAppId);
        req.Headers.TryAddWithoutValidation("x-rpc-csm_source", "discussion");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        req.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent.OkHttp);
        if (method == HttpMethod.Post)
        {
            req.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        }
        return req;
    }

    // ---- 极验验证码（1034 兜底：X4 DS + challenge 头）----
    private static HttpRequestMessage BuildGeetest(
        AccountContext ctx, HttpMethod method, string url, Dictionary<string, string> cookies,
        string? body, BbsRequestOptions? options)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Cookie",
            BbsRequestHeaders.BuildCookieStringForTest(cookies, BbsRequestHeaders.CookieMode.Full));
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", BbsConstants.CnAppVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        req.Headers.TryAddWithoutValidation("x-rpc-device_id", ctx.Device.BbsDeviceId);
        req.Headers.TryAddWithoutValidation("x-rpc-device_fp", ctx.Device.DeviceFp);
        req.Headers.TryAddWithoutValidation("x-rpc-challenge_game", options?.ChallengeGame ?? "2");
        req.Headers.TryAddWithoutValidation("x-rpc-challenge_path", options?.ChallengePath ?? GeetestChallengePath);
        string query = new Uri(url).Query.TrimStart('?');
        req.Headers.TryAddWithoutValidation("DS",
            DynamicSignature.Compute(BbsConstants.CnX4Salt, query: query, body: body ?? ""));
        req.Headers.TryAddWithoutValidation("Referer", GeetestReferer);
        req.Headers.UserAgent.ParseAdd(ctx.UserAgent.Mobile);
        if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    // ---- passport 登录（okhttp UA；app_id/client_type 等来自 options）----
    private static HttpRequestMessage BuildWebLogin(
        AccountContext ctx, HttpMethod method, string url, string? body, BbsRequestOptions? options)
    {
        var req = new HttpRequestMessage(method, url);
        // POST body → Content（扫码建码/轮询/scanQRLogin 都要 body）
        if (!string.IsNullOrEmpty(body))
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Content = content;
        }
        // 扫码建码/轮询用极简头（只有 UA + device_id + app_id + client_type，无 DS/Cookie/画像头）
        if (options?.Minimal == true)
        {
            req.Headers.TryAddWithoutValidation("User-Agent", "HYPContainer/1.3.3.182");
            req.Headers.TryAddWithoutValidation("x-rpc-device_id", ctx.Device.DeviceId);
            req.Headers.TryAddWithoutValidation("x-rpc-app_id", options?.AppId ?? "");
            req.Headers.TryAddWithoutValidation("x-rpc-client_type", options?.ClientType ?? "2");
            return req;
        }
        // 头顺序（passport 请求）：Accept → app_id → client_type → device_id → device_fp
        // → device_name → device_model → sys_version → game_biz → app_version → sdk_version → lifecycle_id
        // → account_version → DS → Cookie → Accept-Encoding → User-Agent（UA 在最后）；无 Accept-Language
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("x-rpc-app_id", options?.AppId ?? "");
        req.Headers.TryAddWithoutValidation("x-rpc-client_type", options?.ClientType ?? "2");
        req.Headers.TryAddWithoutValidation("x-rpc-device_id", ctx.Device.DeviceId);
        req.Headers.TryAddWithoutValidation("x-rpc-device_fp", ctx.Device.DeviceFp);
        req.Headers.TryAddWithoutValidation("x-rpc-device_name", ctx.Device.DeviceName);
        req.Headers.TryAddWithoutValidation("x-rpc-device_model", ctx.Device.Model);
        req.Headers.TryAddWithoutValidation("x-rpc-sys_version", ctx.Device.SysVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-game_biz", "bbs_cn");
        req.Headers.TryAddWithoutValidation("x-rpc-app_version", BbsConstants.CnAppVersion);
        req.Headers.TryAddWithoutValidation("x-rpc-sdk_version", options?.SdkVersion ?? "2.42.0");
        req.Headers.TryAddWithoutValidation("x-rpc-lifecycle_id", options?.LifecycleId ?? Guid.NewGuid().ToString());
        req.Headers.TryAddWithoutValidation("x-rpc-account_version", "2.42.0");
        // 轮询/建码不带 DS；scanQRLogin/confirm/getCookieAccountInfoBySToken 带
        if (options?.IncludeDs == true)
            req.Headers.TryAddWithoutValidation("DS", ComputeWebLoginDs(body, new Uri(url).Query.TrimStart('?')));
        if (!string.IsNullOrEmpty(options?.Referer))
            req.Headers.TryAddWithoutValidation("Referer", options.Referer);
        if (!string.IsNullOrEmpty(options?.Cookie))
            req.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        req.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent.OkHttp);
        return req;
    }

    // ---- device-fp getFp（原生 / WebView 两条通道）----
    private static HttpRequestMessage BuildGetFpNative(AccountContext ctx, HttpMethod method, string url, string? body)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(body))
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Content = content;
        }
        // okhttp 只发 gzip；手动设置后 handler 的 AutomaticDecompression 不再追加 gzip, deflate
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        req.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent.OkHttp);
        return req;
    }

    private static HttpRequestMessage BuildGetFpWebView(AccountContext ctx, HttpMethod method, string url, string? body, BbsRequestOptions? options)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(body))
        {
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
            req.Content = content;
        }
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Origin", "https://baike.mihoyo.com");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "com.mihoyo.hyperion");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        req.Headers.TryAddWithoutValidation("Referer", "https://baike.mihoyo.com/");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
        // 更新请求带 Cookie（登录后全账号 cookie + DEVICEFP 系）；注册不带（getFp 不鉴权）
        if (!string.IsNullOrEmpty(options?.Cookie))
            req.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", ctx.UserAgent.Mobile);
        return req;
    }

    // stoken 系 cookie：stoken;mid;stuid（社区签到接口专用，顺序与现实现一致）
    private static string BuildStokenCookie(AccountContext ctx)
    {
        var sb = new StringBuilder();
        if (ctx.Cookies.TryGetValue("stoken", out var stoken) && !string.IsNullOrEmpty(stoken))
            sb.Append($"stoken={stoken}");
        if (ctx.Cookies.TryGetValue("mid", out var mid) && !string.IsNullOrEmpty(mid))
            sb.Append($";mid={mid}");
        string stuid = ctx.Cookies.GetValueOrDefault("stuid") ?? ctx.Cookies.GetValueOrDefault("account_id") ?? ctx.Cookies.GetValueOrDefault("ltuid_v2") ?? "";
        if (!string.IsNullOrEmpty(stuid))
            sb.Append($";stuid={stuid}");
        return sb.ToString();
    }

    // WebLogin DS：raw = salt&t&r&b&q（q 不排序，与 LoginQrWindow 原 GenerateDS 行为一致），r 为 6 位 base36
    private static string ComputeWebLoginDs(string? body, string query)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = GenerateBase36(6);
        string b = string.IsNullOrEmpty(body) ? "" : body;
        string q = string.IsNullOrEmpty(query) ? "" : query;
        string raw = $"salt={WebLoginSalt}&t={t}&r={r}&b={b}&q={q}";
        string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{t},{r},{sign}";
    }

    private static string GenerateBase36(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, length)
            .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }
}
