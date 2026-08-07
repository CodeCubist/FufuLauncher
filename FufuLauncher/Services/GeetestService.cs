// Copyright (c) FufuLauncher Dev Team. All rights reserved.
// By kyxsan.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FufuLauncher.Constants;
using FufuLauncher.Constants.MiHoYo;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Models.MiHoYo.Identity;
using FufuLauncher.Services.MiHoYo.Transport;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace FufuLauncher.Services;

public class GeetestService : IGeetestService
{
    private const string CreateVerificationUrl = "https://api-takumi-record.mihoyo.com/game_record/app/card/wapi/createVerification?is_high=true";
    private const string VerifyVerificationUrl = "https://api-takumi-record.mihoyo.com/game_record/app/card/wapi/verifyVerification";
    private const string DailyNoteChallengePath = "/game_record/app/genshin/api/dailyNote";

    private const string CNVersion = BbsConstants.CnAppVersion;
    private const string CNX4 = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";
    private const string Referer = "https://webstatic.mihoyo.com";

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly IDeviceFingerprintService _fingerprintService;

    public GeetestService(
        IDeviceFingerprintService fingerprintService)
    {
        _fingerprintService = fingerprintService;
    }

    public async Task<string> TryVerifyForDailyNoteAsync(AccountContext ctx)
    {
        // cookies 从 ctx 取（单一事实来源）：RefreshAsync 后调用方能拿到最新值，避免外部传入过期 cookies；
        // 复制成可变 Dictionary（下游指纹注册/拼接需要）
        return await TryVerifyForDailyNoteAsync(ctx, new Dictionary<string, string>(ctx.Cookies));
    }

    private async Task<string> TryVerifyForDailyNoteAsync(AccountContext ctx, Dictionary<string, string> cookies)
    {
        string mobileUserAgent = ctx.UserAgent.Mobile;
        string bbsDeviceId = ctx.Device.BbsDeviceId;
        string activeId = ctx.AccountId;
        string createJson = await CallCreateVerificationAsync(mobileUserAgent, bbsDeviceId, activeId, cookies);
        string gt = null;
        string challenge = null;

        using (JsonDocument doc = JsonDocument.Parse(createJson))
        {
            int retcode = doc.RootElement.TryGetProperty("retcode", out JsonElement rc) ? rc.GetInt32() : -1;
            if (retcode != 0 || !doc.RootElement.TryGetProperty("data", out JsonElement data))
            {
                Debug.WriteLine($"[GeetestService] TryVerifyForDailyNote: createVerification 失败 retcode={retcode}");
                return null;
            }

            gt = data.TryGetProperty("gt", out JsonElement gtProp) ? gtProp.GetString() : null;
            challenge = data.TryGetProperty("challenge", out JsonElement chProp) ? chProp.GetString() : null;
            Debug.WriteLine($"[GeetestService] TryVerifyForDailyNote: createVerification 成功 gt={gt}, challenge={challenge}");
        }

        if (string.IsNullOrEmpty(gt) || string.IsNullOrEmpty(challenge))
            return null;

        GeetestResult result = await ShowGeetestWebViewAsync(gt, challenge);
        if (result == null || string.IsNullOrEmpty(result.Validate))
        {
            Debug.WriteLine($"[GeetestService] TryVerifyForDailyNote: 用户未完成验证 (result=null) 或 validate 为空");
            return null;
        }
        Debug.WriteLine($"[GeetestService] TryVerifyForDailyNote: 验证码完成 challenge={result.Challenge}, validate={result.Validate}");

        string verifyJson = await CallVerifyVerificationAsync(mobileUserAgent, bbsDeviceId, activeId, cookies, result.Challenge, result.Validate);
        using (JsonDocument doc = JsonDocument.Parse(verifyJson))
        {
            int retcode = doc.RootElement.TryGetProperty("retcode", out JsonElement rc) ? rc.GetInt32() : -1;
            if (retcode != 0 || !doc.RootElement.TryGetProperty("data", out JsonElement data))
            {
                Debug.WriteLine($"[GeetestService] TryVerifyForDailyNote: verifyVerification 失败 retcode={retcode}");
                return null;
            }

            string finalChallenge = data.TryGetProperty("challenge", out JsonElement chProp) ? chProp.GetString() : null;
            Debug.WriteLine($"[GeetestService] TryVerifyForDailyNote: verifyVerification 成功 xrpc_challenge={finalChallenge}");
            return finalChallenge;
        }
    }

    private async Task<string> CallCreateVerificationAsync(string mobileUserAgent, string bbsDeviceId, string activeId, Dictionary<string, string> cookies)
    {
        if (string.IsNullOrEmpty(activeId)) throw new InvalidOperationException("Geetest_NoActiveAccount".GetLocalized());
        string cookieStr = BbsRequestHeaders.BuildCookieStringForTest(cookies, BbsRequestHeaders.CookieMode.Full);
        string ds = DynamicSignature.Compute(CNX4, query: "is_high=true", body: "");
        string fp = (await _fingerprintService.GetOrRegisterFingerprintAsync(activeId, cookies)).DeviceFp;
        Debug.WriteLine($"[GeetestService] CallCreateVerification: device_fp={fp}");

        using HttpRequestMessage req = new(HttpMethod.Get, CreateVerificationUrl);
        req.Headers.Add("Cookie", cookieStr);
        req.Headers.Add("x-rpc-app_version", CNVersion);
        req.Headers.Add("x-rpc-client_type", "5");
        req.Headers.Add("x-rpc-device_id", bbsDeviceId);
        req.Headers.Add("x-rpc-device_fp", fp);
        req.Headers.Add("x-rpc-challenge_game", "2");
        req.Headers.Add("x-rpc-challenge_path", DailyNoteChallengePath);
        req.Headers.Add("DS", ds);
        req.Headers.Add("Referer", Referer);
        req.Headers.UserAgent.ParseAdd(mobileUserAgent);

        using HttpResponseMessage resp = await _httpClient.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> CallVerifyVerificationAsync(string mobileUserAgent, string bbsDeviceId, string activeId, Dictionary<string, string> cookies, string challenge, string validate)
    {
        if (string.IsNullOrEmpty(activeId)) throw new InvalidOperationException("Geetest_NoActiveAccount".GetLocalized());
        string cookieStr = BbsRequestHeaders.BuildCookieStringForTest(cookies, BbsRequestHeaders.CookieMode.Full);
        GeetestWebResponse body = new()
        {
            Challenge = challenge,
            Validate = validate,
            Seccode = $"{validate}|jordan"
        };
        string bodyJson = JsonSerializer.Serialize(body);
        string ds = DynamicSignature.Compute(CNX4, query: "", body: bodyJson);
        string fp = (await _fingerprintService.GetOrRegisterFingerprintAsync(activeId, cookies)).DeviceFp;
        Debug.WriteLine($"[GeetestService] CallVerifyVerification: device_fp={fp}");

        using HttpRequestMessage req = new(HttpMethod.Post, VerifyVerificationUrl);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        req.Headers.Add("Cookie", cookieStr);
        req.Headers.Add("x-rpc-app_version", CNVersion);
        req.Headers.Add("x-rpc-client_type", "5");
        req.Headers.Add("x-rpc-device_id", bbsDeviceId);
        req.Headers.Add("x-rpc-device_fp", fp);
        req.Headers.Add("x-rpc-challenge_game", "2");
        req.Headers.Add("x-rpc-challenge_path", DailyNoteChallengePath);
        req.Headers.Add("DS", ds);
        req.Headers.Add("Referer", Referer);
        req.Headers.UserAgent.ParseAdd(mobileUserAgent);

        using HttpResponseMessage resp = await _httpClient.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    private static async Task<GeetestResult> ShowGeetestWebViewAsync(string gt, string challenge)
    {
        TaskCompletionSource<GeetestResult> tcs = new();

        if (App.MainWindow == null)
        {
            Debug.WriteLine("[GeetestService] App.MainWindow 为 null，无法显示验证码窗口");
            tcs.TrySetResult(null);
            return await tcs.Task;
        }

        App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                Window geetestWindow = new();
                geetestWindow.SystemBackdrop = new MicaBackdrop();
                geetestWindow.Title = "Geetest_CaptchaTitle".GetLocalized();

                Grid rootGrid = new() { Background = new SolidColorBrush(Colors.Transparent) };
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                Grid titleBar = new() { Height = 32 };
                titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Image icon = new()
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/WindowIcon.ico")),
                    Height = 16,
                    Width = 16,
                    Margin = new Thickness(16, 0, 12, 0)
                };
                Grid.SetColumn(icon, 0);
                titleBar.Children.Add(icon);

                TextBlock titleText = new()
                {
                    Text = "Geetest_CaptchaTitle".GetLocalized(),
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
                };
                Grid.SetColumn(titleText, 1);
                titleBar.Children.Add(titleText);

                Grid.SetRow(titleBar, 0);
                rootGrid.Children.Add(titleBar);

                WebView2 webView = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                Grid.SetRow(webView, 1);
                rootGrid.Children.Add(webView);

                geetestWindow.Content = rootGrid;
                
                AppWindow appWindow = geetestWindow.AppWindow;
                if (appWindow != null)
                {
                    appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                    appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    appWindow.Resize(new SizeInt32(1270, 720));

                    AppWindow mainAppWindow = App.MainWindow.AppWindow;
                    if (mainAppWindow != null)
                    {
                        PointInt32 mainPos = mainAppWindow.Position;
                        SizeInt32 mainSize = mainAppWindow.Size;
                        appWindow.Move(new PointInt32(
                            mainPos.X + (mainSize.Width - 400) / 2,
                            mainPos.Y + (mainSize.Height - 450) / 2));
                    }
                }

                geetestWindow.SetTitleBar(titleBar);

                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    try
                    {
                        string msg = e.WebMessageAsJson;
                        GeetestResult result = JsonSerializer.Deserialize<GeetestResult>(msg);
                        tcs.TrySetResult(result);
                        geetestWindow.Close();
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                };

                geetestWindow.Closed += (s, e) =>
                {
                    tcs.TrySetResult(null);
                };

                string html = GetGeetestHtml(gt, challenge);
                webView.NavigateToString(html);
                geetestWindow.Activate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeetestService] 验证码窗口创建失败: {ex.Message}");
                tcs.TrySetResult(null);
            }
        });

        return await tcs.Task;
    }

    private static string GetGeetestHtml(string gt, string challenge)
    {
        var captchaTitle = "Geetest_CaptchaTitle".GetLocalized();
        return $$"""
            <html>
                <head>
                    <meta charset="utf-8"/>
                    <title>{{captchaTitle}}</title>
                    <style>
                        * { margin:0; padding:0; box-sizing:border-box; }
                        body {
                            background: transparent;
                            display: flex;
                            align-items: center;
                            justify-content: center;
                            height: 100vh;
                            font-family: 'Segoe UI', sans-serif;
                        }
                        #geetest-div { }
                    </style>
                </head>
                <body>
                    <div id="geetest-div"></div>
                </body>
                <script src="https://static.geetest.com/static/js/gt.0.5.2.js"></script>
                <script>
                    initGeetest(
                        {
                            protocol: "https://",
                            gt: "{{gt}}",
                            challenge: "{{challenge}}",
                            new_captcha: true,
                            product: 'bind',
                            api_server: 'api.geetest.com'
                        },
                        function (captchaObj) {
                            captchaObj.onReady(function () {
                                captchaObj.verify();
                            });
                            captchaObj.onSuccess(function () {
                                var result = captchaObj.getValidate();
                                chrome.webview.postMessage(result);
                            });
                        }
                    );
                </script>
            </html>
            """;
    }

    private sealed class GeetestWebResponse
    {
        [JsonPropertyName("geetest_challenge")]
        public string Challenge
        {
            get; set;
        }

        [JsonPropertyName("geetest_validate")]
        public string Validate
        {
            get; set;
        }

        [JsonPropertyName("geetest_seccode")]
        public string Seccode
        {
            get; set;
        }
    }
}

public sealed class GeetestResult
{
    [JsonPropertyName("geetest_challenge")]
    public string Challenge
    {
        get; set;
    }

    [JsonPropertyName("geetest_validate")]
    public string Validate
    {
        get; set;
    }
}