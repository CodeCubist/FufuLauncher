/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using System.Diagnostics;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Models.MiHoYo.Fingerprint;
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Services.MiHoYo;




internal sealed class AccountIdentityService : IAccountIdentityService
{
    private readonly AccountManager _accountManager;
    private readonly IDeviceFingerprintService _fingerprintService;

    public AccountIdentityService(AccountManager accountManager, IDeviceFingerprintService fingerprintService)
    {
        _accountManager = accountManager;
        _fingerprintService = fingerprintService;
    }

    public async Task<AccountContext> BuildAsync(string accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 1. 读 cookies（账号文件不存在 / 解析失败：返回 null cookies，由调用方决定如何处理）
        var cookies = await _accountManager.LoadCookiesAsync(accountId);
        if (cookies == null)
        {
            cookies = new Dictionary<string, string>();
            Debug.WriteLine($"[AccountIdentity] 账号 {accountId} 未找到 cookies，返回空 ctx");
        }

        // 2. 注册 / 读指纹（fp service 内部会按 accountId 缓存）
        var fp = await _fingerprintService.GetOrRegisterFingerprintAsync(accountId, cookies);

        // 3. 派生身份字段
        var serverType = ServerTypeExtensions.ParseServerType(ExtractServerType(accountId));
        var accountIdentity = new AccountIdentity(
            Stuid: ExtractStuid(cookies, serverType),
            Mid: cookies.TryGetValue("mid", out var mid) ? mid : "");

        // 设备画像与 ext_fields builder 的 DefaultProfile 同源（deviceName = "Xiaomi " + Model）；
        // deviceName / sysVersion / UA 三处必须自洽，否则服务端设备画像校验会触发 1034
        const string model = "2605EPN8EC";
        const string sysVersion = "12";
        const string buildId = "V417IR";
        var device = new DeviceIdentity(
            DeviceId: fp.DeviceId,
            BbsDeviceId: fp.BbsDeviceId ?? "",
            DeviceFp: fp.DeviceFp,
            DeviceName: "Xiaomi " + model,
            SysVersion: sysVersion,
            Model: model,
            FpLastUpdate: DateTimeOffset.UtcNow);

        var ua = new UserAgent(
            Mobile: BbsUserAgents.MobileFor(sysVersion, model, buildId, BbsConstants.CnAppVersion),
            Web: BbsUserAgents.Web,
            OkHttp: BbsUserAgents.OkHttp);

        return new AccountContext(
            AccountId: accountId,
            ServerType: serverType,
            Cookies: cookies,
            Identity: accountIdentity,
            Device: device,
            UserAgent: ua);
    }

    public async Task<AccountContext> RefreshAsync(AccountContext ctx, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var updatedFp = await _fingerprintService.UpdateFingerprintAsync(ctx.AccountId);
        if (updatedFp == null)
        {
            Debug.WriteLine($"[AccountIdentity] 指纹刷新失败，保留原 ctx: {ctx.Device.DeviceFp}");
            return ctx;
        }

        // 写盘由 UpdateFingerprintAsync 内部完成（仅指纹变化时）；这里不再重复写
        // 复用其他字段，只替换 Device；返回一个"新 ctx"，原 ctx 不变（record 不可变语义）
        var newDevice = ctx.Device with
        {
            DeviceId = updatedFp.DeviceId,
            BbsDeviceId = updatedFp.BbsDeviceId ?? "",
            DeviceFp = updatedFp.DeviceFp,
            FpLastUpdate = DateTimeOffset.UtcNow
        };

        Debug.WriteLine($"[AccountIdentity] 指纹已刷新并落盘: {ctx.Device.DeviceFp} -> {updatedFp.DeviceFp}");
        return ctx with { Device = newDevice };
    }

    public Task SaveFpAsync(string accountId, DeviceFpRequest fp) =>
        _accountManager.SaveFingerprintAsync(accountId, fp);

    public Task<DeviceFpRequest?> LoadFpAsync(string accountId) =>
        _accountManager.LoadFingerprintAsync(accountId);

    #region 工具

    private static string ExtractServerType(string accountId)
    {
        var idx = accountId.IndexOf('_');
        return idx > 0 ? accountId[..idx] : "cn";
    }

    private static string ExtractStuid(Dictionary<string, string> cookies, ServerType serverType)
    {
        if (serverType == ServerType.Cn)
        {
            if (cookies.TryGetValue("ltuid", out var ltuid) && !string.IsNullOrEmpty(ltuid))
                return ltuid;
            if (cookies.TryGetValue("stuid", out var stuid) && !string.IsNullOrEmpty(stuid))
                return stuid;
        }
        else
        {
            if (cookies.TryGetValue("ltuid_v2", out var ltuidV2) && !string.IsNullOrEmpty(ltuidV2))
                return ltuidV2;
            if (cookies.TryGetValue("stuid", out var stuid) && !string.IsNullOrEmpty(stuid))
                return stuid;
        }
        return "";
    }

    #endregion
}