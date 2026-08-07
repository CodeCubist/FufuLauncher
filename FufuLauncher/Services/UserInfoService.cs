/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Text;
using System.Text.Json;
using FufuLauncher.Constants;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Models;
using FufuLauncher.Models.MiHoYo.Identity;
using FufuLauncher.Services.MiHoYo.Transport;
using Microsoft.Extensions.Logging;

namespace FufuLauncher.Services;

public class UserInfoService : IUserInfoService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UserInfoService> _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IHoyolabRoleResolverService _hoyolabRoleResolverService;
    private readonly IBbsRequestBuilder _requestBuilder;
    private readonly IAccountIdentityService _identityService;
    private readonly AccountManager _accountManager;

    public UserInfoService(
        ILogger<UserInfoService> logger,
        ILocalSettingsService localSettingsService,
        IHoyolabRoleResolverService hoyolabRoleResolverService,
        IBbsRequestBuilder requestBuilder,
        IAccountIdentityService identityService,
        AccountManager accountManager)
    {
        _logger = logger;
        _localSettingsService = localSettingsService;
        _hoyolabRoleResolverService = hoyolabRoleResolverService;
        _requestBuilder = requestBuilder;
        _identityService = identityService;
        _accountManager = accountManager;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    private void ApplyCommonHeaders(string cookie)
    {

        var keys = new[] { "ltoken", "ltuid", "cookie_token", "account_id", "ltoken_v2", "ltuid_v2", "cookie_token_v2", "account_id_v2" };
        var found = keys.Where(k => cookie.Contains(k + "=", StringComparison.OrdinalIgnoreCase)).ToArray();
        var missing = keys.Where(k => !found.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();
        System.Diagnostics.Debug.WriteLine($"[UserInfoService] Cookie length={cookie.Length}, found=[{string.Join(", ", found)}], missing=[{string.Join(", ", missing)}]");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("DS", GenerateDS());
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-rpc-device_id", Guid.NewGuid().ToString("N"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-rpc-client_type", "5");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://act.mihoyo.com/");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://act.mihoyo.com");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            $"Mozilla/5.0 (Linux; Android 12; Unspecified Device) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/103.0.5060.129 Mobile Safari/537.36 miHoYoBBS/{BbsConstants.CnAppVersion}");
    }

    private string GenerateDS() =>
        DynamicSignature.ComputeMinimal(
            salt: "xV8v4Qu54lUKrEYFZkJhB8cuoh9NXmz9",
            rFormat: DynamicSignature.RFormat.Decimal100k);

    private async Task<bool> IsInternationalAsync(string cookie)
    {
        var hasCnFields = cookie.Contains("ltuid=", StringComparison.OrdinalIgnoreCase) ||
                          cookie.Contains("stuid=", StringComparison.OrdinalIgnoreCase);
        var hasOsFields = cookie.Contains("ltuid_v2=", StringComparison.OrdinalIgnoreCase) ||
                          cookie.Contains("account_id_v2=", StringComparison.OrdinalIgnoreCase) ||
                          cookie.Contains("cookie_token_v2=", StringComparison.OrdinalIgnoreCase);

        // 优先国服：同时有国服和国际服 cookie 字段时走国服，避免 region_block
        if (hasCnFields) return false;
        if (hasOsFields) return true;

        var isOsObj = await _localSettingsService.ReadSettingAsync("IsInternationalAccount");
        return isOsObj is bool isOs && isOs;
    }

    public async Task<GameRolesResponse> GetUserGameRolesAsync(string cookie)
    {
        try
        {
            bool isOs = await IsInternationalAsync(cookie);
            ApplyCommonHeaders(cookie);

            if (isOs)
            {
                var rolesResult = await _hoyolabRoleResolverService.ResolveRolesAsync(cookie);
                return new GameRolesResponse(
                    rolesResult.RetCode,
                    rolesResult.Message,
                    new GameRolesData(rolesResult.Roles));
            }
            else
            {
                var response = await _httpClient.GetAsync(ApiEndpoints.MihoyoBbsUserGameRolesUrl);
                var json = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[UserInfoService] GameRoles HTTP {response.StatusCode} | Body({json?.Length ?? 0}): {(json?.Length > 300 ? json[..300] : json ?? "(null)")}");
                return JsonSerializer.Deserialize<GameRolesResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取角色信息失败");
            return new GameRolesResponse(-1, ex.Message, null);
        }
    }

    /// <summary>推荐入口：从 ctx 取 cookies，serverType 决定走 CN/OS。</summary>
    public Task<GameRolesResponse> GetUserGameRolesAsync(AccountContext ctx) =>
        GetUserGameRolesAsync(BuildCookieString(ctx));


    public async Task<UserFullInfoResponse> GetUserFullInfoAsync(string cookie)
    {
        try
        {
            bool isOs = await IsInternationalAsync(cookie);
            string uid = ExtractUid(cookie, isOs);

            string url = isOs
                ? "https://bbs-api-os.hoyolab.com/community/painter/wapi/user/full"
                : string.Format(ApiEndpoints.MiyousheUserFullInfoUrl, uid);

            // 用真实活跃账号 id 构造 ctx（避免合成 id 污染指纹缓存 / LoadFingerprint 找不到 entry）；无活跃账号时回退合成 id
            var accountId = _accountManager.ActiveAccountId ?? $"{(isOs ? "os" : "cn")}_{uid}";
            var ctx = await _identityService.BuildAsync(accountId);
            return await GetUserFullInfoAsync(ctx, url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取用户信息失败");
            return new UserFullInfoResponse(-1, ex.Message, null);
        }
    }

    // 从 cookie 字符串解析用户 uid：国服 stuid/ltuid，国际服 account_id_v2/ltuid_v2
    private static string ExtractUid(string cookie, bool isOs)
    {
        string[] keys = isOs
            ? new[] { "account_id_v2", "ltuid_v2" }
            : new[] { "stuid", "ltuid" };
        foreach (string key in keys)
        {
            int idx = cookie.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int start = idx + key.Length + 1;
            int end = cookie.IndexOf(';', start);
            if (end < 0) end = cookie.Length;
            string value = cookie[start..end].Trim();
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    /// <summary>推荐入口：直接用 ctx（含指纹/画像），不再经 cookie 字符串重解析。</summary>
    public Task<UserFullInfoResponse> GetUserFullInfoAsync(AccountContext ctx)
    {
        string url = ctx.ServerType == FufuLauncher.Models.MiHoYo.Identity.ServerType.Os
            ? "https://bbs-api-os.hoyolab.com/community/painter/wapi/user/full"
            : string.Format(ApiEndpoints.MiyousheUserFullInfoUrl, ctx.Stuid);
        return GetUserFullInfoAsync(ctx, url);
    }

    private async Task<UserFullInfoResponse> GetUserFullInfoAsync(AccountContext ctx, string url)
    {
        try
        {
            using var request = _requestBuilder.Build(ctx, BbsRequestScene.UserFullInfo, HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[UserInfoService] UserFullInfo HTTP {response.StatusCode} | URL: {url} | Body({json?.Length ?? 0}): {(json?.Length > 300 ? json[..300] : json ?? "(null)")}");
            return JsonSerializer.Deserialize<UserFullInfoResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取用户信息失败");
            return new UserFullInfoResponse(-1, ex.Message, null);
        }
    }

    public async Task<GameRecordCardResponse> GetGameRecordCardAsync(string stuid, string cookie)
    {
        return await Task.FromResult(new GameRecordCardResponse(-1, "UserInfo_FeatureRemoved".GetLocalized(), null));
    }

    // ctx 里的 cookies 是 Dict<string,string>，转成 "k=v;k2=v2" 字符串给现有 IsInternationalAsync / ApplyCommonHeaders 用
    private static string BuildCookieString(AccountContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var kv in ctx.Cookies)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append($"{kv.Key}={kv.Value}");
        }
        return sb.ToString();
    }
}

