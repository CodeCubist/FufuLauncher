/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Contracts.Services;

/// <summary>业务请求场景：决定 UA / DS salt+r 格式 / cookie 模式 / 额外头的组合。</summary>
public enum BbsRequestScene
{
    /// <summary>game_record 每日便签（api-takumi-record.mihoyo.com）：X4 DS + Full cookie。</summary>
    DailyNote,

    /// <summary>便签 widget：X6 DS + SToken cookie。</summary>
    DailyNoteWidget,

    /// <summary>bbs-api getUserFullInfo：无 DS。</summary>
    UserFullInfo,

    /// <summary>社区签到（bbs-api.miyoushe.com）：X6 minimal DS + stoken 系 cookie。</summary>
    CommunitySign,

    /// <summary>极验验证码（1034 兜底）：X4 DS + challenge 头。</summary>
    Geetest,

    /// <summary>passport 登录（okhttp UA）：app_id / client_type 等由 options 提供。</summary>
    WebLogin,

    /// <summary>device-fp getFp 原生通道（platform=2, bbs_cn）：okhttp UA + gzip，不带 Cookie。</summary>
    GetFpNative,

    /// <summary>device-fp getFp WebView 通道（platform=5, hk4e_cn）：WebView UA + baike 系头，可选 Cookie（更新请求带）。</summary>
    GetFpWebView
}

/// <summary>场景可变参数；有默认值的场景可不传。</summary>
public sealed class BbsRequestOptions
{
    /// <summary>WebLogin：x-rpc-app_id（如 ddxf5dufpuyo / bll8iq97cem8）。</summary>
    public string? AppId { get; init; }

    /// <summary>WebLogin：x-rpc-client_type（2 / 3）。</summary>
    public string? ClientType { get; init; }

    /// <summary>WebLogin：x-rpc-sdk_version，默认 "2.42.0"。</summary>
    public string? SdkVersion { get; init; }

    /// <summary>WebLogin：完整 Cookie 串（可选）。</summary>
    public string? Cookie { get; init; }

    /// <summary>WebLogin：Referer（可选）。</summary>
    public string? Referer { get; init; }

    /// <summary>WebLogin：x-rpc-lifecycle_id，默认随机 Guid。</summary>
    public string? LifecycleId { get; init; }

    /// <summary>Geetest：x-rpc-challenge_game，默认 "2"。</summary>
    public string? ChallengeGame { get; init; }

    /// <summary>Geetest：x-rpc-challenge_path，默认每日便签路径。</summary>
    public string? ChallengePath { get; init; }

    /// <summary>WebLogin：是否带 DS。扫码建码/轮询不带，扫码确认/换 token 带。</summary>
    public bool IncludeDs { get; init; }

    /// <summary>WebLogin：扫码建码/轮询用极简头（HYPContainer UA + device_id + app_id + client_type，无 DS/版本/画像头）。</summary>
    public bool Minimal { get; init; }
}

/// <summary>
/// 统一请求头构建服务：所有业务请求的请求头（版本号 / 设备指纹 / UA / DS / cookie）从这里出，
/// 一处管理，避免各服务自行拼头导致版本/指纹不一致。
/// </summary>
public interface IBbsRequestBuilder
{
    /// <summary>按场景构建完整请求（含所有请求头）。</summary>
    /// <param name="ctx">账号运行期身份（cookies + 设备指纹 + UA）。</param>
    /// <param name="scene">请求场景。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="url">完整 URL（query 参与 DS 计算）。</param>
    /// <param name="body">POST body（参与 DS 计算；Geetest/WebLogin 用）。</param>
    /// <param name="challenge">极验 challenge（DailyNote 1034 兜底用）。</param>
    /// <param name="options">场景可变参数。</param>
    HttpRequestMessage Build(
        AccountContext ctx,
        BbsRequestScene scene,
        HttpMethod method,
        string url,
        string? body = null,
        string? challenge = null,
        BbsRequestOptions? options = null);
}
