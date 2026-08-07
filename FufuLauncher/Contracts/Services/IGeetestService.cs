/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Contracts.Services;

public interface IGeetestService
{
    /// <summary>
    /// 跑一次 geetest 验证码并返回 xrpc_challenge；ctx 用于取 mobile UA / bbs_device_id / cookies 等设备与账号 state。
    /// 不依赖 IDailyNoteService（避免循环依赖）。
    /// </summary>
    Task<string> TryVerifyForDailyNoteAsync(AccountContext ctx);
}