/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using FufuLauncher.Models.MiHoYo.Fingerprint;
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Contracts.Services;

/// <summary>
/// 单账号身份聚合入口：把 cookies + 设备指纹 + UA 装成不可变 <see cref="AccountContext"/>，
/// 并负责把新指纹写回账号 cookie 文件。所有业务 service 应通过它取得身份信息，
/// 而不是直接依赖 <c>DeviceFingerprintService</c> / <c>AccountManager</c>。
/// </summary>
public interface IAccountIdentityService
{
    /// <summary>
    /// 为账号构建完整身份上下文：读 cookies → 必要时注册 / 刷新设备指纹 → 派生 device_id / bbs_device_id → 装成 <see cref="AccountContext"/>。
    /// 不写盘；如指纹是新建的，调用方拿到结果后可调 <see cref="SaveFpAsync"/> 持久化。
    /// </summary>
    Task<AccountContext> BuildAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// 用已存在的 context 触发一次服务端指纹更新（复用 device_id/seed_id/seed_time/device_fp，ext_fields 重新采样）。
    /// 成功后会同步写盘，调用方拿到的是最新的 <see cref="AccountContext"/>；失败返回原 ctx 不变。
    /// </summary>
    Task<AccountContext> RefreshAsync(AccountContext ctx, CancellationToken ct = default);

    /// <summary>
    /// 仅把指纹请求体持久化到账号 cookie 文件（不影响 cookies 段）；与 <see cref="IDeviceFingerprintService.UpdateFingerprintAsync"/> 配合使用。
    /// </summary>
    Task SaveFpAsync(string accountId, DeviceFpRequest fp);

    /// <summary>
    /// 从账号 cookie 文件读取已持久化的完整指纹请求体；无则返回 null。
    /// </summary>
    Task<DeviceFpRequest?> LoadFpAsync(string accountId);
}