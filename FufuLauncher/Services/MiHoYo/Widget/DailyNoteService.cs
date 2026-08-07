/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Models.MiHoYo.Identity;
using FufuLauncher.Services.MiHoYo.Transport;

namespace FufuLauncher.Services.MiHoYo;

public class DailyNoteService : IDailyNoteService
{
    private readonly IDeviceFingerprintService _fingerprintService;
    private readonly IGeetestService _geetestService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IAccountIdentityService _identityService;
    private readonly IBbsRequestBuilder _requestBuilder;

    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public DailyNoteService(
        IDeviceFingerprintService fingerprintService,
        IGeetestService geetestService,
        ILocalSettingsService localSettingsService,
        IAccountIdentityService identityService,
        IBbsRequestBuilder requestBuilder)
    {
        _fingerprintService = fingerprintService;
        _geetestService = geetestService;
        _localSettingsService = localSettingsService;
        _identityService = identityService;
        _requestBuilder = requestBuilder;
    }

    public async Task<DailyNoteCardData> GetDailyNoteAsync(string roleId, string server)
    {
        // 兼容旧调用方：自动构造 AccountContext 后转给新重载
        string activeId = App.GetService<AccountManager>().ActiveAccountId
            ?? throw new InvalidOperationException("DailyNote_NoActiveAccount".GetLocalized());
        var ctx = await _identityService.BuildAsync(activeId);
        return await GetDailyNoteAsync(ctx, roleId, server);
    }

    /// <summary>
    /// 推荐入口：调用方先 <see cref="IAccountIdentityService.BuildAsync"/> 拿到 ctx，再传入此方法。
    /// 请求头统一走 <see cref="IBbsRequestBuilder"/>（ctx 内的指纹 / UA / cookies 一处管理）；1034 兜底时通过 ctx 传给 <see cref="IGeetestService"/>。
    /// </summary>
    public async Task<DailyNoteCardData> GetDailyNoteAsync(AccountContext ctx, string roleId, string server)
    {
        await _semaphore.WaitAsync();
        try
        {
            // 2. 调主接口
            string apiUrl = $"{BbsConstants.DailyNoteUrl}?server={Uri.EscapeDataString(server)}&role_id={Uri.EscapeDataString(roleId)}";
            string json = await RequestDailyNoteAsync(ctx, apiUrl, null);
            int retcode = ParseRetcode(json);

            // 3. 1034 兜底：重置 fp → 重注册 → 跑验证码
            if (retcode == 1034)
            {
                var captchaDisabledJson = await _localSettingsService.ReadSettingAsync("IsCaptchaPopupDisabled");
                bool isCaptchaDisabled = captchaDisabledJson != null && Convert.ToBoolean(captchaDisabledJson);

                if (!isCaptchaDisabled)
                {
                    await _fingerprintService.ResetFingerprintAsync(ctx.AccountId);
                    ctx = await _identityService.RefreshAsync(ctx);

                    string xrpcChallenge = await _geetestService.TryVerifyForDailyNoteAsync(ctx);
                    if (!string.IsNullOrEmpty(xrpcChallenge))
                    {
                        json = await RequestDailyNoteAsync(ctx, apiUrl, xrpcChallenge);
                        retcode = ParseRetcode(json);
                    }
                }
                else
                {
                    Debug.WriteLine("[DailyNoteService] 风控验证码弹窗已被用户禁用，跳过验证");
                }
            }

            if (retcode == 5003 || retcode == 1034)
            {
                json = await RequestWidgetAsync(ctx);
                retcode = ParseRetcode(json);
            }

            if (retcode != 0)
                throw new InvalidOperationException(string.Format("DailyNote_FetchFailed".GetLocalized(), ExtractMessage(json), retcode));

            return DailyNoteParser.Parse(json);
        }
        finally { _semaphore.Release(); }
    }

    private async Task<string> RequestDailyNoteAsync(AccountContext ctx, string apiUrl, string? xrpcChallenge)
    {
        using var req = _requestBuilder.Build(ctx, BbsRequestScene.DailyNote, HttpMethod.Get, apiUrl, challenge: xrpcChallenge);

        var resp = await _httpClient.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> RequestWidgetAsync(AccountContext ctx)
    {
        using var req = _requestBuilder.Build(ctx, BbsRequestScene.DailyNoteWidget, HttpMethod.Get, BbsConstants.WidgetUrl);

        var resp = await _httpClient.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    private static int ParseRetcode(string json)
    {
        // 响应可能不是 JSON（网关错误页 / 风控 HTML / 空体），解析失败按 -1 处理，不抛异常
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("retcode", out var rc) ? rc.GetInt32() : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }
    private static string ExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "Status_UnknownError".GetLocalized() : "Status_UnknownError".GetLocalized();
        }
        catch (JsonException)
        {
            return "Status_UnknownError".GetLocalized();
        }
    }
}