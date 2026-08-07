/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FufuLauncher.Constants;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Models;
using FufuLauncher.Models.MiHoYo.Identity;

namespace FufuLauncher.Services;

public class CommunityCheckinService : ICommunityCheckinService
{
    private readonly HttpClient _httpClient;
    private readonly ILocalSettingsService _localSettingsService;

    public CommunityCheckinService(ILocalSettingsService localSettingsService)
    {
        _localSettingsService = localSettingsService;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<CheckinTypeResult> ExecuteCheckinAsync(
        AccountContext ctx,
        string uid,
        string nickname,
        bool signEnabled,
        bool readEnabled,
        bool likeEnabled,
        bool shareEnabled)
    {
        // 设备画像直接取 AccountContext.Device（指纹体系产物），不再走 LocalSettings 旧键；
        // 指纹字段缺失时不发空头，直接返回失败
        if (ctx.Device is null ||
            string.IsNullOrEmpty(ctx.Device.DeviceFp) ||
            string.IsNullOrEmpty(ctx.Device.BbsDeviceId))
        {
            var result = new CheckinTypeResult { TypeName = "CheckinCommunity_Title".GetLocalized(), Executed = true, Success = false };
            result.Message = "缺少设备信息，无法执行社区签到（未找到指纹画像）";
            Debug.WriteLine($"[社区签到] 账号 {uid}: 无设备画像，跳过社区签到");
            return result;
        }

        var account = new AccountCredentials
        {
            Uid = uid,
            Nickname = nickname,
            Stuid = ctx.Stuid,
            Stoken = ctx.Stoken ?? "",
            Mid = ctx.Mid,
            Cookie = BuildCookieString(ctx),
        };
        return await ExecuteCheckinCoreAsync(account, BuildDeviceInfo(ctx.Device), signEnabled, readEnabled, likeEnabled, shareEnabled);
    }

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

    public async Task<CheckinTypeResult> ExecuteCheckinAsync(
        AccountCredentials account,
        bool signEnabled,
        bool readEnabled,
        bool likeEnabled,
        bool shareEnabled)
    {
        var result = new CheckinTypeResult { TypeName = "CheckinCommunity_Title".GetLocalized(), Executed = true };

        if (string.IsNullOrEmpty(account.Stoken) || string.IsNullOrEmpty(account.Mid))
        {
            result.Success = false;
            result.Message = "CheckinCommunity_MissingToken".GetLocalized();
            return result;
        }

        Dictionary<string, string>? deviceInfo;
        try
        {
            deviceInfo = await LoadDeviceInfoAsync();
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = string.Format("CheckinCommunity_Exception".GetLocalized(), ex.Message);
            Debug.WriteLine($"[社区签到] 账号 {account.Uid} 异常: {ex.Message}");
            return result;
        }
        if (deviceInfo == null)
        {
            result.Success = false;
            result.Message = "缺少设备信息，无法执行社区签到（未找到指纹画像）";
            Debug.WriteLine($"[社区签到] 账号 {account.Uid}: 无设备画像，跳过社区签到");
            return result;
        }
        return await ExecuteCheckinCoreAsync(account, deviceInfo, signEnabled, readEnabled, likeEnabled, shareEnabled);
    }

    private async Task<CheckinTypeResult> ExecuteCheckinCoreAsync(
        AccountCredentials account,
        Dictionary<string, string> deviceInfo,
        bool signEnabled,
        bool readEnabled,
        bool likeEnabled,
        bool shareEnabled)
    {
        var result = new CheckinTypeResult { TypeName = "CheckinCommunity_Title".GetLocalized(), Executed = true };

        try
        {
            var stokenCookie = account.GetStokenCookie();

            // 1. Get task state
            Debug.WriteLine($"[社区签到] 账号 {account.Nickname}({account.Uid}): 正在获取任务状态");
            var taskState = await GetTaskStateAsync(account.Cookie);
            if (taskState == null)
            {
                result.Success = false;
                result.Message = "CheckinCommunity_GetTaskFailed".GetLocalized();
                return result;
            }

            int canGet = GetIntValue(taskState.Value, "can_get_points");
            int received = GetIntValue(taskState.Value, "already_received_points");
            int total = GetIntValue(taskState.Value, "total_points");
            var taskFlags = ParseTaskFlags(taskState.Value);

            Debug.WriteLine($"[社区签到] 账号 {account.Uid}: 可获得 {canGet}，已获得 {received}，总计 {total}");

            if (canGet == 0)
            {
                result.Success = true;
                result.SkippedCount++;
                result.Message = string.Format("CheckinCommunity_TodayDone".GetLocalized(), received);
                return result;
            }

            var headers = BuildHeaders(stokenCookie, deviceInfo);

            // 2. Community sign
            if (signEnabled && taskFlags["sign"] == 0)
            {
                var signResult = await CommunitySignAsync(headers, account.Uid);
                if (signResult.Success) result.SuccessCount++;
                else result.FailCount++;
                result.Details.AddRange(signResult.Details);
                await DelayAsync();
            }
            else if (taskFlags["sign"] != 0)
            {
                result.Details.Add("CheckinCommunity_SignDoneSkipped".GetLocalized());
                result.SkippedCount++;
            }

            // 3. Get posts
            var posts = await GetPostsAsync(headers);
            if (posts == null || posts.Count == 0)
            {
                result.Details.Add("CheckinCommunity_GetPostsFailed".GetLocalized());
            }
            else
            {
                // 4. Read posts
                if (readEnabled && taskFlags["read"] == 0)
                {
                    int readNum = taskFlags["read_num"];
                    var readPosts = posts.Take(readNum).ToList();
                    var readResult = await ReadPostsAsync(headers, readPosts);
                    if (readResult.Success) result.SuccessCount++;
                    else result.FailCount++;
                    result.Details.AddRange(readResult.Details);
                    await DelayAsync();
                }
                else if (taskFlags["read"] != 0)
                {
                    result.Details.Add("CheckinCommunity_ReadDoneSkipped".GetLocalized());
                    result.SkippedCount++;
                }

                // 5. Like posts
                if (likeEnabled && taskFlags["like"] == 0)
                {
                    int likeNum = taskFlags["like_num"];
                    var likePosts = posts.Take(likeNum).ToList();
                    var likeResult = await LikePostsAsync(headers, likePosts);
                    if (likeResult.Success) result.SuccessCount++;
                    else result.FailCount++;
                    result.Details.AddRange(likeResult.Details);
                    await DelayAsync();
                }
                else if (taskFlags["like"] != 0)
                {
                    result.Details.Add("CheckinCommunity_LikeDoneSkipped".GetLocalized());
                    result.SkippedCount++;
                }

                // 6. Share posts
                if (shareEnabled && taskFlags["share"] == 0)
                {
                    var shareResult = await SharePostAsync(headers, posts[0]);
                    if (shareResult.Success) result.SuccessCount++;
                    else result.FailCount++;
                    result.Details.AddRange(shareResult.Details);
                    await DelayAsync();
                }
                else if (taskFlags["share"] != 0)
                {
                    result.Details.Add("CheckinCommunity_ShareDoneSkipped".GetLocalized());
                    result.SkippedCount++;
                }
            }

            // Final state check
            var finalState = await GetTaskStateAsync(account.Cookie);
            if (finalState != null)
            {
                int finalReceived = GetIntValue(finalState.Value, "already_received_points");
                int gained = finalReceived - received;
                result.Success = result.FailCount == 0;
                result.Message = gained > 0
                    ? string.Format("CheckinCommunity_GainedCoins".GetLocalized(), gained, finalReceived)
                    : string.Format("CheckinCommunity_CurrentCoins".GetLocalized(), finalReceived);
            }
            else
            {
                result.Success = result.FailCount == 0;
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = string.Format("CheckinCommunity_Exception".GetLocalized(), ex.Message);
            Debug.WriteLine($"[社区签到] 账号 {account.Uid} 异常: {ex.Message}");
        }

        return result;
    }

    private async Task<Dictionary<string, string>?> LoadDeviceInfoAsync()
    {
        try
        {
            var deviceIdObj = await _localSettingsService.ReadSettingAsync("DeviceId");
            var deviceNameObj = await _localSettingsService.ReadSettingAsync("DeviceName");
            var deviceModelObj = await _localSettingsService.ReadSettingAsync("DeviceModel");
            var deviceFpObj = await _localSettingsService.ReadSettingAsync("DeviceFp");

            // 无画像（新用户/未注册指纹）时返回 null，由调用方跳过签到，不再回退到过时的伪设备
            if (deviceIdObj == null || deviceNameObj == null)
            {
                return null;
            }

            return new Dictionary<string, string>
            {
                ["id"] = deviceIdObj.ToString()!,
                ["name"] = deviceNameObj.ToString()!,
                ["model"] = deviceModelObj?.ToString() ?? "",
                ["fp"] = deviceFpObj?.ToString() ?? ""
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> BuildDeviceInfo(DeviceIdentity device) => new()
    {
        ["id"] = device.BbsDeviceId,
        ["name"] = device.DeviceName,
        ["model"] = device.Model,
        ["fp"] = device.DeviceFp
    };

    private Dictionary<string, string> BuildHeaders(string stokenCookie, Dictionary<string, string> device)
    {
        var ds = MihoyoBBS.Tools.GetDs(web: false);
        return new Dictionary<string, string>
        {
            ["DS"] = ds,
            ["cookie"] = stokenCookie,
            ["x-rpc-client_type"] = "2",
            ["x-rpc-app_version"] = BbsConstants.CnAppVersion,
            ["x-rpc-sys_version"] = "12",
            ["x-rpc-channel"] = "miyousheluodi",
            ["x-rpc-device_id"] = device["id"],
            ["x-rpc-device_name"] = device["name"],
            ["x-rpc-device_model"] = device["model"],
            ["x-rpc-h265_supported"] = "1",
            ["Referer"] = "https://app.mihoyo.com",
            ["x-rpc-verify_key"] = GenshinApiEndpoints.PassportAppId,
            ["x-rpc-csm_source"] = "discussion",
            ["Content-Type"] = "application/json; charset=UTF-8",
            ["Host"] = "bbs-api.miyoushe.com",
            ["Connection"] = "Keep-Alive",
            ["Accept-Encoding"] = "gzip",
            ["User-Agent"] = "okhttp/4.9.3"
        };
    }

    private async Task<JsonElement?> GetTaskStateAsync(string fullCookie)
    {
        try
        {
            var url = $"{GenshinApiEndpoints.BbsTasksUrl}?point_sn=myb";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json, text/plain, */*");
            request.Headers.Add("Origin", "https://webstatic.mihoyo.com");
            request.Headers.Add("User-Agent",
                $"Mozilla/5.0 (Linux; Android 12; Unspecified Device) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/103.0.5060.129 Mobile Safari/537.36 miHoYoBBS/{BbsConstants.CnAppVersion}");
            request.Headers.Add("Referer", "https://webstatic.mihoyo.com");
            request.Headers.Add("Accept-Language", "zh-CN,en-US;q=0.8");
            request.Headers.Add("X-Requested-With", "com.mihoyo.hyperion");
            request.Headers.Add("Cookie", fullCookie);

            var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(text);
            if (json.RootElement.GetProperty("retcode").GetInt32() == 0)
                return json.RootElement.GetProperty("data");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[社区签到] 获取任务状态失败: {ex.Message}");
        }
        return null;
    }

    private static Dictionary<string, int> ParseTaskFlags(JsonElement? state)
    {
        var flags = new Dictionary<string, int> { ["sign"] = 0, ["read"] = 0, ["read_num"] = 3, ["like"] = 0, ["like_num"] = 5, ["share"] = 0 };
        if (state == null) return flags;

        try
        {
            var states = state.Value.GetProperty("states").EnumerateArray();
            foreach (var mission in states)
            {
                int taskId = mission.GetProperty("mission_id").GetInt32();
                bool isGetAward = mission.GetProperty("is_get_award").GetBoolean();

                switch (taskId)
                {
                    case 58:
                        flags["sign"] = isGetAward ? 1 : 0;
                        break;
                    case 59:
                        flags["read"] = isGetAward ? 1 : 0;
                        if (!isGetAward)
                            flags["read_num"] = Math.Max(3 - mission.GetProperty("happened_times").GetInt32(), 0);
                        break;
                    case 60:
                        flags["like"] = isGetAward ? 1 : 0;
                        if (!isGetAward)
                            flags["like_num"] = Math.Max(5 - mission.GetProperty("happened_times").GetInt32(), 0);
                        break;
                    case 61:
                        flags["share"] = isGetAward ? 1 : 0;
                        break;
                }
            }
        }
        catch { }
        return flags;
    }

    private static int GetIntValue(JsonElement? element, string propertyName)
    {
        if (element == null || !element.HasValue) return 0;
        try { return element.Value.GetProperty(propertyName).GetInt32(); }
        catch { return 0; }
    }

    private static string? TryGetString(JsonElement? element, string propertyName)
    {
        if (element == null || !element.HasValue) return null;
        try { return element.Value.GetProperty(propertyName).GetString(); }
        catch { return null; }
    }

    private async Task<(bool Success, List<string> Details)> CommunitySignAsync(Dictionary<string, string> headers, string uid)
    {
        var details = new List<string>();
        bool success = true;

        var body = JsonSerializer.Serialize(new { gids = GenshinApiEndpoints.GenshinForumId });
        var ds = SignatureHelper.GetDsX6(body: body);
        headers["DS"] = ds;

        try
        {
            var data = await PostJsonAsync(GenshinApiEndpoints.BbsSignUrl, body, headers);
            if (data == null)
            {
                details.Add("CheckinCommunity_GenshinSignRequestFailed".GetLocalized());
                success = false;
            }
            else if (GetIntValue(data, "retcode") == 0)
            {
                details.Add("CheckinCommunity_GenshinSignSuccess".GetLocalized());
            }
            else if (GetIntValue(data, "retcode") == 1034)
            {
                details.Add("CheckinCommunity_GenshinSignCaptcha".GetLocalized());
                success = false;
            }
            else
            {
                var msg = TryGetString(data, "message");
                details.Add(string.Format("CheckinCommunity_GenshinSignFailed".GetLocalized(), msg));
                success = false;
            }
        }
        catch (Exception ex)
        {
            details.Add(string.Format("CheckinCommunity_GenshinSignException".GetLocalized(), ex.Message));
            success = false;
        }

        return (success, details);
    }

    private async Task<List<(string PostId, string Title)>?> GetPostsAsync(Dictionary<string, string> headers)
    {
        try
        {
            var url = $"{GenshinApiEndpoints.BbsPostListUrl}?forum_id={GenshinApiEndpoints.GenshinForumId}&is_good=false&is_hot=false&page_size=20&sort_type=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request, headers);

            var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(text);

            if (json.RootElement.GetProperty("retcode").GetInt32() != 0) return null;

            var posts = new List<(string, string)>();
            var list = json.RootElement.GetProperty("data").GetProperty("list").EnumerateArray();
            foreach (var item in list)
            {
                var post = item.GetProperty("post");
                var postId = post.GetProperty("post_id").ToString();
                var title = TryGetString(post, "subject") ?? postId;
                posts.Add((postId, title));
            }

            var random = new Random();
            for (int i = posts.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (posts[i], posts[j]) = (posts[j], posts[i]);
            }

            return posts.Take(5).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[社区签到] 获取帖子失败: {ex.Message}");
            return null;
        }
    }

    private async Task<(bool Success, List<string> Details)> ReadPostsAsync(Dictionary<string, string> headers, List<(string PostId, string Title)> posts)
    {
        var details = new List<string>();
        bool success = true;

        foreach (var (postId, title) in posts)
        {
            try
            {
                var url = $"{GenshinApiEndpoints.BbsPostDetailUrl}?post_id={postId}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaders(request, headers);

                var response = await _httpClient.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                var json = JsonDocument.Parse(text);

                if (TryGetString(json.RootElement, "message") == "OK")
                {
                    details.Add(string.Format("CheckinCommunity_ReadSuccess".GetLocalized(), title));
                }
                else
                {
                    details.Add(string.Format("CheckinCommunity_ReadFailed".GetLocalized(), title));
                    success = false;
                }
            }
            catch (Exception ex)
            {
                details.Add(string.Format("CheckinCommunity_ReadException".GetLocalized(), title, ex.Message));
                success = false;
            }
            await DelayAsync();
        }

        return (success, details);
    }

    private async Task<(bool Success, List<string> Details)> LikePostsAsync(Dictionary<string, string> headers, List<(string PostId, string Title)> posts)
    {
        var details = new List<string>();
        bool success = true;

        foreach (var (postId, title) in posts)
        {
            try
            {
                // Like
                var likeBody = JsonSerializer.Serialize(new { post_id = postId, is_cancel = false });
                var data = await PostJsonAsync(GenshinApiEndpoints.BbsLikeUrl, likeBody, headers);
                if (data != null && TryGetString(data, "message") == "OK")
                {
                    details.Add(string.Format("CheckinCommunity_LikeSuccess".GetLocalized(), title));
                    // Cancel like
                    await DelayAsync();
                    var cancelBody = JsonSerializer.Serialize(new { post_id = postId, is_cancel = true });
                    await PostJsonAsync(GenshinApiEndpoints.BbsLikeUrl, cancelBody, headers);
                }
                else
                {
                    var msg = data != null ? TryGetString(data, "message") : "Status_RequestFailed".GetLocalized();
                    details.Add(string.Format("CheckinCommunity_LikeFailed".GetLocalized(), title, msg));
                    success = false;
                }
            }
            catch (Exception ex)
            {
                details.Add(string.Format("CheckinCommunity_LikeException".GetLocalized(), title, ex.Message));
                success = false;
            }
            await DelayAsync();
        }

        return (success, details);
    }

    private async Task<(bool Success, List<string> Details)> SharePostAsync(Dictionary<string, string> headers, (string PostId, string Title) post)
    {
        var details = new List<string>();
        try
        {
            var url = $"{GenshinApiEndpoints.BbsShareUrl}?entity_id={post.PostId}&entity_type=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request, headers);

            var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(text);

            if (TryGetString(json.RootElement, "message") == "OK")
            {
                details.Add(string.Format("CheckinCommunity_ShareSuccess".GetLocalized(), post.Title));
                return (true, details);
            }
            else
            {
                var msg = TryGetString(json.RootElement, "message");
                details.Add(string.Format("CheckinCommunity_ShareFailed".GetLocalized(), post.Title, msg));
                return (false, details);
            }
        }
        catch (Exception ex)
        {
            details.Add(string.Format("CheckinCommunity_ShareException".GetLocalized(), post.Title, ex.Message));
            return (false, details);
        }
    }

    private async Task<JsonElement?> PostJsonAsync(string url, string body, Dictionary<string, string> headers)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddHeaders(request, headers);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(text).RootElement;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[社区签到] POST {url} 失败: {ex.Message}");
            return null;
        }
    }

    private static void AddHeaders(HttpRequestMessage request, Dictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrEmpty(value)) continue;
            switch (key.ToLower())
            {
                case "cookie":
                    request.Headers.Add("Cookie", value);
                    break;
                case "user-agent":
                    request.Headers.UserAgent.ParseAdd(value);
                    break;
                case "referer":
                    request.Headers.Referrer = new Uri(value);
                    break;
                case "content-type":
                    break; // handled by StringContent
                default:
                    request.Headers.TryAddWithoutValidation(key, value);
                    break;
            }
        }
    }

    private static async Task DelayAsync()
    {
        var random = new Random();
        await Task.Delay(random.Next(1000, 3000));
    }
}

