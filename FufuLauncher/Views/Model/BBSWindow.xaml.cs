using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace FufuLauncher.Views
{
    public sealed partial class BBSWindow : Window
    {
        private AppWindow m_AppWindow;

        private string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private const string AppVersion = "2.90.1";
        private const string ClientType = "5";
        private const string ApiSalt = "t0qEgfub6cvueAPgR5m9aQWWVciEer7v";
        private const string ApiSalt2 = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs";
        private const string TargetUrl = "https://webstatic.mihoyo.com/app/community-game-records/index.html?bbs_presentation_style=fullscreen&game_id=2#/ys";
        private Dictionary<string, string> cookieDic = new();
        private string _deviceId;

        private const string miHoYoJSInterface = """
            if (window.MiHoYoJSInterface === undefined) {
                window.MiHoYoJSInterface = {
                    postMessage: function(arg) { window.chrome.webview.postMessage(arg) },
                    closePage: function() { this.postMessage('{"method":"closePage"}') },
                };
            }
            """;

        private const string HideScrollBarScript = """
            (function() {
                const injectStyles = () => {
                    if (document.getElementById('fufu-no-scroll')) return;
                    const style = document.createElement('style');
                    style.id = 'fufu-no-scroll';
                    style.innerHTML = `
                        .container {
                            overflow: auto !important;
                            -ms-overflow-style: none !important;
                            scrollbar-width: none !important;
                        }
                        .container::-webkit-scrollbar, 
                        *::-webkit-scrollbar {
                            display: none !important;
                            width: 0 !important;
                            height: 0 !important;
                        }
                    `;
                    document.documentElement.appendChild(style);
                };
            
                injectStyles();
            
                const observer = new MutationObserver(() => {
                    injectStyles();
                });
                observer.observe(document.documentElement, { childList: true, subtree: true });
            
                setTimeout(injectStyles, 3000);
            })();
            """;

        public BBSWindow()
        {
            InitializeComponent();
            _deviceId = GetMachineGuid();
            InitializeWindowStyle();
            _ = InitializeWebViewAsync();
        }

        private void InitializeWindowStyle()
        {
            m_AppWindow = AppWindow;
            
            var displayArea = DisplayArea.GetFromWindowId(m_AppWindow.Id, DisplayAreaFallback.Primary);
            var screenHeight = displayArea.WorkArea.Height;
            var screenWidth = displayArea.WorkArea.Width;

            var targetHeight = (int)(screenHeight * 0.8);
            var targetWidth = (int)(targetHeight * 9.0 / 16.0);
            
            if (targetWidth > screenWidth)
            {
                targetWidth = (int)(screenWidth * 0.9);
                targetHeight = (int)(targetWidth * 16.0 / 9.0);
            }
            
            m_AppWindow.Resize(new SizeInt32(targetWidth, targetHeight));
            
            var centeredPosition = new PointInt32(
                (displayArea.WorkArea.Width - targetWidth) / 2 + displayArea.WorkArea.X,
                (displayArea.WorkArea.Height - targetHeight) / 2 + displayArea.WorkArea.Y
            );
            m_AppWindow.Move(centeredPosition);
            
            if (AppTitleBar != null)
            {
                m_AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                m_AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                m_AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                SetTitleBar(AppTitleBar);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                await BBSWebView.EnsureCoreWebView2Async();

                var settings = BBSWebView.CoreWebView2.Settings;
                settings.UserAgent = $"Mozilla/5.0 (Linux; Android 13; Pixel 5) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/118.0.0.0 Mobile Safari/537.36 miHoYoBBS/{AppVersion}";
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;

                var cookieToLoad = "";
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var jsonString = File.ReadAllText(ConfigPath);
                        var config = JsonSerializer.Deserialize<AppConfig>(jsonString);
                        cookieToLoad = config?.Account.Cookie ?? "";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Read Config Failed: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(cookieToLoad))
                {
                    ParseCookie(cookieToLoad);
                    InjectCookieToWebView();
                }
                InjectCookieToWebView();
                
                await BBSWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(miHoYoJSInterface);
                await BBSWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HideScrollBarScript);

                BBSWebView.NavigationCompleted += async (_, _) =>
                {
                    await BBSWebView.CoreWebView2.ExecuteScriptAsync(HideScrollBarScript);
                };

                BBSWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                BBSWebView.CoreWebView2.Navigate(TargetUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView Init Failed: {ex.Message}");
            }
        }

        private void ParseCookie(string cookieStr)
        {
            cookieDic.Clear();
            if (string.IsNullOrWhiteSpace(cookieStr)) return;
            var parts = cookieStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in parts)
            {
                var kv = item.Split('=', 2);
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim();
                    var value = kv[1].Trim();
                    if (!string.IsNullOrEmpty(key)) cookieDic[key] = value;
                }
            }
        }

        private void InjectCookieToWebView()
        {
            var manager = BBSWebView.CoreWebView2.CookieManager;
            foreach (var kv in cookieDic)
            {
                var cookie = manager.CreateCookie(kv.Key, kv.Value, ".mihoyo.com", "/");
                manager.AddOrUpdateCookie(cookie);
                var cookieStatic = manager.CreateCookie(kv.Key, kv.Value, "webstatic.mihoyo.com", "/");
                manager.AddOrUpdateCookie(cookieStatic);
            }
        }

        private async void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string message = args.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(message)) return;

                var param = JsonSerializer.Deserialize<JsParam>(message);
                if (param == null) return;

                JsResult? result = await HandleJsMessageAsync(param);

                if (!string.IsNullOrEmpty(param.Callback))
                {
                    await ExecuteCallback(param.Callback, result);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JS Bridge Error: {ex.Message}");
            }
        }

        private async Task<JsResult?> HandleJsMessageAsync(JsParam param)
        {
            return param.Method switch
            {
                "closePage" => HandleClosePage(),
                "getDS" => GetDynamicSecretV1(),
                "getDS2" => GetDynamicSecretV2(param),
                "getHTTPRequestHeaders" => GetHttpRequestHeader(),
                "getCookieInfo" => new JsResult { Data = cookieDic.ToDictionary(x => x.Key, x => (object)x.Value) },
                "getCookieToken" => new JsResult { Data = new() { ["cookie_token"] = cookieDic.GetValueOrDefault("cookie_token") ?? cookieDic.GetValueOrDefault("cookie_token_v2") ?? "" } },
                "getStatusBarHeight" => new JsResult { Data = new() { ["statusBarHeight"] = 0 } },
                "getUserInfo" => GetUserInfo(),
                "getCurrentLocale" => GetCurrentLocale(),
                "pushPage" => HandlePushPage(param),
                "share" => await HandleShareAsync(param),
                _ => null
            };
        }

        private JsResult? HandleClosePage()
        {
            if (BBSWebView.CoreWebView2.CanGoBack) BBSWebView.CoreWebView2.GoBack();
            else Close();
            return null;
        }

        private JsResult? HandlePushPage(JsParam param)
        {
            string? url = param.Payload?["page"]?.ToString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                url = url.Replace("rolePageAccessNotAllowed=&", "");
                BBSWebView.CoreWebView2.Navigate(url);
            }
            return null;
        }

        private JsResult GetHttpRequestHeader()
        {
            return new JsResult
            {
                Data = new()
                {
                    ["x-rpc-client_type"] = ClientType,
                    ["x-rpc-app_version"] = AppVersion,
                    ["x-rpc-device_id"] = _deviceId,
                    ["x-rpc-device_fp"] = cookieDic.GetValueOrDefault("DEVICEFP") ?? "38d815ced3f93"
                }
            };
        }
        
        private JsResult GetDynamicSecretV1()
        {
            var t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var r = GetRandomString(t);
            var check = GetMd5($"salt={ApiSalt}&t={t}&r={r}");
            return new JsResult { Data = new() { ["DS"] = $"{t},{r},{check}" } };
        }
        
        private JsResult GetDynamicSecretV2(JsParam param)
        {
            var t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var r = Random.Shared.Next(100000, 200000).ToString();
            
            var d = JsonSerializer.Deserialize<Dictionary<string, object>>(param.Payload?["query"]);
            var b = param.Payload?["body"]?.ToString();
            string? q = null;
            if (d?.Any() ?? false)
            {
                q = string.Join('&', d.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
            }
            q = q?.Replace("True", "true").Replace("False", "false");

            var check = GetMd5($"salt={ApiSalt2}&t={t}&r={r}&b={b}&q={q}");
            return new JsResult { Data = new() { ["DS"] = $"{t},{r},{check}" } };
        }

        private JsResult GetUserInfo()
        {
            return new JsResult
            {
                Data = new()
                {
                    ["id"] = cookieDic.GetValueOrDefault("ltuid") ?? "",
                    ["nickname"] = "User",
                    ["avatar_url"] = ""
                }
            };
        }

        private JsResult GetCurrentLocale()
        {
            var offset = TimeZoneInfo.Local.BaseUtcOffset.Hours;
            var tz = offset > 0 ? $"GMT+{offset}" : (offset < 0 ? $"GMT{offset}" : "GMT");
            return new JsResult
            {
                Data = new()
                {
                    ["language"] = CultureInfo.CurrentUICulture.Name.ToLower(),
                    ["timeZone"] = tz
                }
            };
        }

        private async Task<JsResult?> HandleShareAsync(JsParam param)
        {
            var type = param.Payload?["type"]?.ToString();
            if (type == "screenshot")
            {
                var data = await BBSWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", """{"captureBeyondViewport": true}""");
                var base64 = JsonNode.Parse(data)?["data"]?.ToString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    Convert.FromBase64String(base64);
                }
            }
            return null;
        }

        private async Task ExecuteCallback(string callback, JsResult? result)
        {
            var json = result == null ? "" : "," + JsonSerializer.Serialize(result);
            await BBSWebView.CoreWebView2.ExecuteScriptAsync($"javascript:mhyWebBridge(\"{callback}\"{json})");
        }

        private string GetMd5(string input)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLower();
        }
        
        private static string GetRandomString(int timestamp)
        {
            var sb = new StringBuilder(6);
            var random = new Random(timestamp);
            for (var i = 0; i < 6; i++)
            {
                var v8 = random.Next(0, 32768) % 26;
                var v9 = v8 < 10 ? 48 : 87;
                _ = sb.Append((char)(v8 + v9));
            }
            return sb.ToString();
        }
        
        private string GetMachineGuid()
        {
            try
            {
                var id = Windows.System.Profile.SystemIdentification.GetSystemIdForPublisher();
                if (id != null)
                {
                    using var sha256 = SHA256.Create();
                    var hash = sha256.ComputeHash(id.Id.ToArray());
                    return BitConverter.ToString(hash).Replace("-", "").ToLower().Substring(0, 32);
                }
            }
            catch { }
            return Guid.NewGuid().ToString();
        }

        private class JsParam
        {
            [JsonPropertyName("method")] public string Method { get; set; } = "";
            [JsonPropertyName("payload")]
            public JsonNode? Payload
            {
                get; set;
            }
            [JsonPropertyName("callback")]
            public string? Callback
            {
                get; set;
            }
        }

        private class JsResult
        {
            [JsonPropertyName("retcode")] public int Code { get; set; } = 0;
            [JsonPropertyName("message")] public string Message { get; set; } = "OK";
            [JsonPropertyName("data")] public Dictionary<string, object> Data { get; set; } = new();
            public override string ToString() => JsonSerializer.Serialize(this);
        }
        public class AppConfig
        {
            [JsonPropertyName("Account")]
            public AccountConfig Account
            {
                get; set;
            }
        }

        public class AccountConfig
        {
            [JsonPropertyName("Cookie")]
            public string Cookie
            {
                get; set;
            }
        }
    }
}