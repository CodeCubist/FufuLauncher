/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using System.Security.Cryptography;
using System.Text;

namespace FufuLauncher.Services.MiHoYo.Transport;

/// <summary>
/// miHoYo 服务端 DS 签名算法的统一入口。
///
/// DS 形如 <c>"t,r,md5hex"</c>，原始字符串为 <c>"salt={salt}&amp;t={t}&amp;r={r}&amp;b={body}&amp;q={query}"</c>，
/// 其中 <c>q</c> 是 query string 按 ASCII 升序排序后的字符串（query 为空时直接空字符串参与拼接）。
///
/// 不同接口的差异集中在两点：
///   1. <c>salt</c> 不一样（X4 / X6 / web / lk2 各不同）；
///   2. <c>r</c> 的格式不一样（X4/X6/web 是 5-6 位十进制，lk2 是 6 字符 base36）。
///
/// 调用方需要按自家接口的盐和 r 格式自行选参数；本类不藏具体场景的 salt，让"我在算什么"显式可见。
/// </summary>
public static class DynamicSignature
{
    /// <summary>
    /// 计算 miHoYo DS 签名。
    /// </summary>
    /// <param name="salt">服务端约定的密钥（每个接口不同，参考 <see cref="FufuLauncher.Constants.MiHoYo.BbsConstants"/> 与 <c>Constants/GenshinApiEndpoints</c>）。</param>
    /// <param name="query">URL query string（不带 <c>?</c>）。本方法内部会按 ASCII 升序排序后参与哈希，调用方无需自己排序。</param>
    /// <param name="body">POST body（GET 接口传空串）。</param>
    /// <param name="rDigits">r 的字符格式。默认 5-6 位十进制（X4 / X6 / web 用）。lk2 用 <see cref="RBase36"/> 显式说明。</param>
    /// <returns>形如 <c>"1700000000,12345,abcdef..."</c> 的 DS 字符串。</returns>
    public static string Compute(
        string salt,
        string query = "",
        string body = "",
        RFormat rFormat = RFormat.Decimal5To6)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = GenerateR(rFormat);
        string sortedQuery = SortQuery(query);
        string raw = $"salt={salt}&t={t}&r={r}&b={body}&q={sortedQuery}";
        string hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{t},{r},{hash}";
    }

    /// <summary>r 生成策略。</summary>
    public enum RFormat
    {
        /// <summary>5 位十进制（X4 / X6 / web 默认）；范围 [10000, 99999]。</summary>
        Decimal5To6,

        /// <summary>5-6 位十进制（X4/X6/web 实际值域 [100000, 200000)）；随机值为 100000 时换成 642367。</summary>
        Decimal100k,

        /// <summary>6 字符 base36（lk2 接口用）。</summary>
        Base36_6
    }

    /// <summary>
    /// 极简模式 DS：raw 字符串只含 <c>"salt={salt}&amp;t={t}&amp;r={r}"</c>，不追加 <c>&amp;b=</c> / <c>&amp;q=</c>。
    /// 适用于 binding/getUserGameRolesByCookie 系（历史兼容格式）。
    /// </summary>
    public static string ComputeMinimal(string salt, RFormat rFormat = RFormat.Decimal5To6)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = GenerateR(rFormat);
        string raw = $"salt={salt}&t={t}&r={r}";
        string hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{t},{r},{hash}";
    }

    private const string Base36Chars = "abcdefghijklmnopqrstuvwxyz0123456789";

    private static string GenerateR(RFormat format) => format switch
    {
        RFormat.Decimal5To6 => Random.Shared.Next(10000, 99999).ToString(),
        RFormat.Decimal100k => ToR100k(),
        RFormat.Base36_6 => new string(Enumerable.Range(0, 6)
            .Select(_ => Base36Chars[Random.Shared.Next(Base36Chars.Length)]).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static string ToR100k()
    {
        int r = Random.Shared.Next(100000, 200000);
        return r == 100000 ? "642367" : r.ToString();
    }

    /// <summary>把 query 按 ASCII 升序排序后用 '&amp;' 拼接。空字符串返回空串。</summary>
    public static string SortQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        return string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(s => s, StringComparer.Ordinal));
    }
}