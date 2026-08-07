/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

namespace FufuLauncher.Models.MiHoYo.Identity;

/// <summary>
/// 单账号运行期身份聚合：业务 service 拿到这一个对象就能完成"我是谁、我在哪个服、带什么设备、用什么 UA"的所有组装。
/// 不可变 record；如需更新（如指纹刷新），由 <see cref="FufuLauncher.Contracts.Services.IAccountIdentityService"/> 返回新实例。
/// </summary>
public sealed record AccountContext(
    string AccountId,
    ServerType ServerType,
    IReadOnlyDictionary<string, string> Cookies,
    AccountIdentity Identity,
    DeviceIdentity Device,
    UserAgent UserAgent)
{
    /// <summary>取 ltuid / stuid / ltuid_v2 等关键 id；cn/os 各有差异。</summary>
    public string Stuid => ServerType == ServerType.Cn
        ? Cookies.TryGetValue("stuid", out var s) && !string.IsNullOrEmpty(s) ? s : Identity.Stuid
        : Identity.Stuid;

    public string Mid => Identity.Mid;

    public string? Stoken => Cookies.TryGetValue("stoken", out var t) ? t : null;
}

/// <summary>账号级身份字段（从 cookies 派生）；不带设备字段。</summary>
public sealed record AccountIdentity(string Stuid, string Mid);

/// <summary>
/// 设备指纹与设备档案的并集：服务端鉴权需要的 device_id / bbs_device_id / device_fp，
/// 以及 DS / headers 可能用到的设备型号 / 系统版本字段。
/// </summary>
public sealed record DeviceIdentity(
    string DeviceId,
    string BbsDeviceId,
    string DeviceFp,
    string DeviceName,
    string SysVersion,
    string Model,
    DateTimeOffset FpLastUpdate);

/// <summary>三种 UA 一处管；不同 endpoint 用不同 UA（mobile / web / okhttp）。</summary>
public sealed record UserAgent(string Mobile, string Web, string OkHttp);

/// <summary>枚举化的服务端类型，避免裸 "cn" / "os" 字符串满天飞。</summary>
public enum ServerType
{
    Cn,
    Os
}

public static class ServerTypeExtensions
{
    public static string ToWire(this ServerType s) => s switch
    {
        ServerType.Cn => "cn",
        ServerType.Os => "os",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
    };

    public static ServerType ParseServerType(string? raw) =>
        string.Equals(raw, "os", StringComparison.OrdinalIgnoreCase)
            ? ServerType.Os
            : ServerType.Cn;
}