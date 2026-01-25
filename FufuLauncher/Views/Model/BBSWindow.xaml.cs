using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FufuLauncher.Views
{
    public sealed partial class BBSWindow : Window
    {
        private AppWindow m_AppWindow;
        private string ConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        
        private byte[] _screenshotBytes;
        private class ClientConfig
        {
            public string ClientType { get; set; }     // x-rpc-client_type
            public string AppVersion { get; set; }     // x-rpc-app_version
            public string Salt { get; set; }           // Salt
            public string UserAgent { get; set; }      // UA
            public string DeviceModel { get; set; }    
            public string SysVersion { get; set; }     
            public bool UseDS2 { get; set; }           // 是否使用DS2
        }
        
        private readonly Dictionary<string, ClientConfig> _clientConfigs = new()
        {
            ["2"] = new ClientConfig 
            {
                ClientType = "2",
                AppVersion = "2.71.1", 
                Salt = "rtvTthKxEyreVXQCnhluFgLXPOFKPHlA", 
                UserAgent = "Mozilla/5.0 (Linux; Android 12; Unspecified Device) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/103.0.5060.129 Mobile Safari/537.36 miHoYoBBS/2.71.1",
                DeviceModel = "Mi 6",
                SysVersion = "12",
                UseDS2 = false
            },
            ["4"] = new ClientConfig 
            {
                ClientType = "4",
                AppVersion = "2.71.1", 
                Salt = "EJncUPGnOHajenjLhBOsdpwEMZmiCmQX", 
                UserAgent = "Mozilla/5.0 (Linux; Android 12; Unspecified Device) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/103.0.5060.129 Mobile Safari/537.36 miHoYoBBS/2.71.1",
                DeviceModel = "Mi 6",
                SysVersion = "12",
                UseDS2 = false
            },
            ["5"] = new ClientConfig 
            {
                ClientType = "5",
                AppVersion = "2.71.1",
                Salt = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs", 
                UserAgent = "Mozilla/5.0 (Linux; Android 12; Unspecified Device) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/103.0.5060.129 Mobile Safari/537.36 miHoYoBBS/2.71.1",
                DeviceModel = "Mi 6",
                SysVersion = "12",
                UseDS2 = true
            }
        };

        private ClientConfig _currentConfig;
        private string _deviceId;
        private Dictionary<string, string> cookieDic = new();

        private const string DefaultUrl = "https://webstatic.mihoyo.com/app/community-game-records/index.html?bbs_presentation_style=fullscreen&game_id=2";

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
                                                       const observer = new MutationObserver(() => { injectStyles(); });
                                                       observer.observe(document.documentElement, { childList: true, subtree: true });
                                                       setTimeout(injectStyles, 3000);
                                                   })();
                                                   """;
        
        private const string miHoYoJSInterface = """
            if (window.MiHoYoJSInterface === undefined) {
                window.MiHoYoJSInterface = {
                    postMessage: function(arg) { chrome.webview.postMessage(arg) },
                    closePage: function() { this.postMessage('{"method":"closePage"}') },
                };
            }
            """;

        public BBSWindow()
        {
            InitializeComponent();
            _deviceId = GetMachineGuid();
            _currentConfig = _clientConfigs["2"]; 
            
            InitializeWindowStyle();
            UrlTextBox.Text = DefaultUrl;
            _ = InitializeWebViewAsync();
        }

        private void InitializeWindowStyle()
        {
            m_AppWindow = AppWindow;
            var displayArea = DisplayArea.GetFromWindowId(m_AppWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
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
            }
            
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
                
                UpdateWebViewSettings();
                
                BBSWebView.CoreWebView2.AddWebResourceRequestedFilter("*://*.mihoyo.com/*", CoreWebView2WebResourceContext.All);
                BBSWebView.CoreWebView2.AddWebResourceRequestedFilter("*://*.miyoushe.com/*", CoreWebView2WebResourceContext.All);
                
                BBSWebView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
                BBSWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                BBSWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                BBSWebView.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;

                await BBSWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HideScrollBarScript);
                BBSWebView.NavigationCompleted += async (_, _) =>
                {
                    await BBSWebView.CoreWebView2.ExecuteScriptAsync(HideScrollBarScript);
                };
                await LoadPageAsync(DefaultUrl);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebView Init Failed: {ex.Message}");
            }
        }

        private void UpdateWebViewSettings()
        {
            if (BBSWebView != null && BBSWebView.CoreWebView2 != null)
            {
                BBSWebView.CoreWebView2.Settings.UserAgent = _currentConfig.UserAgent;
            }
        }
        
        private async void CoreWebView2_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var uri = args.Request.Uri;
                if (uri.Contains("mihoyo.com") || uri.Contains("miyoushe.com"))
                {
                    var headers = args.Request.Headers;
                    headers.RemoveHeader("x-rpc-client_type");
                    headers.RemoveHeader("DS");
                    
                    headers.SetHeader("x-rpc-client_type", _currentConfig.ClientType);
                    headers.SetHeader("x-rpc-app_version", _currentConfig.AppVersion);
                    headers.SetHeader("x-rpc-device_id", _deviceId);
                    headers.SetHeader("x-rpc-device_model", _currentConfig.DeviceModel);
                    headers.SetHeader("x-rpc-sys_version", _currentConfig.SysVersion);
                    headers.SetHeader("x-rpc-channel", "miyoushe");

                    if (cookieDic.TryGetValue("DEVICEFP", out var fp) && !string.IsNullOrWhiteSpace(fp))
                    {
                        headers.SetHeader("x-rpc-device_fp", fp);
                    }
                    
                    string ds;
                    if (_currentConfig.UseDS2)
                    {
                        string query = GetSortedQuery(uri);
                        string body = "";
                        if (args.Request.Method == "POST" && args.Request.Content != null)
                        {
                            body = await GetJsonBodyAsync(args.Request.Content);
                        }
                        ds = CalculateDS2(_currentConfig.Salt, query, body);
                    }
                    else
                    {
                        ds = CalculateDS1(_currentConfig.Salt);
                    }

                    headers.SetHeader("DS", ds);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DS Injection Error: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
        
        private async Task<JsResult?> HandleJsMessageAsync(JsParam param)
        {
            if (param.Method == "getDS" || param.Method == "getDS2")
            {
                string ds;
                if (_currentConfig.UseDS2)
                {
                    string q = "", b = "";
                    if (param.Payload != null)
                    {
                        if (param.Payload["query"] != null) q = param.Payload["query"]!.ToString();
                        if (param.Payload["body"] is JsonObject obj) b = SortJson(obj);
                        else if (param.Payload["body"] != null) b = param.Payload["body"]!.ToString();
                    }
                    ds = CalculateDS2(_currentConfig.Salt, q, b);
                }
                else
                {
                    ds = CalculateDS1(_currentConfig.Salt);
                }
                return new JsResult { Data = new() { ["DS"] = ds } };
            }

            return param.Method switch
            {
                "closePage" => HandleClosePage(),
                "getHTTPRequestHeaders" => GetHttpRequestHeader(),
                "getCookieInfo" => new JsResult { Data = cookieDic.ToDictionary(x => x.Key, x => (object)x.Value) },
                "getCookieToken" => new JsResult { Data = new() { ["cookie_token"] = cookieDic.GetValueOrDefault("cookie_token") ?? cookieDic.GetValueOrDefault("cookie_token_v2") ?? "" } },
                "getStatusBarHeight" => new JsResult { Data = new() { ["statusBarHeight"] = 0 } },
                "getUserInfo" => GetUserInfo(),
                "getCurrentLocale" => GetCurrentLocale(),
                "pushPage" => HandlePushPage(param),
                "share" => await HandleShareAsync(param),
                "eventTrack" => null, 
                "configure_share" => null,
                _ => null
            };
        }

        private async Task<string> GetJsonBodyAsync(IRandomAccessStream stream)
        {
            try
            {
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                var jsonStr = reader.ReadString(reader.UnconsumedBufferLength);
                if (string.IsNullOrWhiteSpace(jsonStr)) return "";

                try
                {
                    var jsonNode = JsonNode.Parse(jsonStr);
                    if (jsonNode is JsonObject jsonObj) return SortJson(jsonObj);
                    return jsonNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "";
                }
                catch { return jsonStr; }
            }
            catch { return ""; }
        }

        private string SortJson(JsonObject jsonObj)
        {
            var sortedKeys = jsonObj.Select(k => k.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder();
            sb.Append("{");
            for (int i = 0; i < sortedKeys.Count; i++)
            {
                var key = sortedKeys[i];
                var value = jsonObj[key];
                sb.Append($"\"{key}\":");
                if (value is JsonObject nestedObj) sb.Append(SortJson(nestedObj));
                else sb.Append(value?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                if (i < sortedKeys.Count - 1) sb.Append(",");
            }
            sb.Append("}");
            return sb.ToString();
        }

        private string CalculateDS1(string salt)
        {
            var t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var r = GetRandomString(6);
            var check = GetMd5($"salt={salt}&t={t}&r={r}");
            return $"{t},{r},{check}";
        }

        private string CalculateDS2(string salt, string query, string body)
        {
            var t = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var random = new Random();
            var r = random.Next(100000, 200001);
            if (r == 100000) r = 642367;
            var check = GetMd5($"salt={salt}&t={t}&r={r}&b={body}&q={query}");
            return $"{t},{r},{check}";
        }

        private string GetSortedQuery(string url)
        {
            try 
            {
                var uriObj = new Uri(url);
                var query = uriObj.Query.TrimStart('?');
                if (string.IsNullOrEmpty(query)) return "";
                var dict = System.Web.HttpUtility.ParseQueryString(query);
                if (dict.Count == 0) return "";
                
                var sortedKeys = dict.AllKeys.Where(k => k != null).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var sb = new StringBuilder();
                for (int i = 0; i < sortedKeys.Count; i++)
                {
                    var key = sortedKeys[i];
                    var val = dict[key];
                    sb.Append($"{key}={val}");
                    if (i < sortedKeys.Count - 1) sb.Append("&");
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static string GetRandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, length).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private string GetMd5(string input)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLower();
        }

        private async void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            await BBSWebView.CoreWebView2.ExecuteScriptAsync(miHoYoJSInterface);
        }

        private void CoreWebView2_SourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
        {
            if (UrlTextBox != null) UrlTextBox.Text = sender.Source;
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

                if (result != null && !string.IsNullOrEmpty(param.Callback))
                {
                    await ExecuteCallback(param.Callback, result);
                }
            }
            catch { }
        }

        private JsResult GetHttpRequestHeader()
        {
            var data = new Dictionary<string, object>
            {
                ["x-rpc-client_type"] = _currentConfig.ClientType,
                ["x-rpc-app_version"] = _currentConfig.AppVersion,
                ["x-rpc-device_id"] = _deviceId,
                ["x-rpc-sys_version"] = _currentConfig.SysVersion,
                ["x-rpc-channel"] = "miyoushe",
                ["x-rpc-device_name"] = _currentConfig.DeviceModel,
                ["x-rpc-device_model"] = _currentConfig.DeviceModel
            };
            if (cookieDic.TryGetValue("DEVICEFP", out var fp)) data["x-rpc-device_fp"] = fp;
            return new JsResult { Data = data };
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
        
        private async Task<JsResult?> HandleShareAsync(JsParam param)
        {
            string type = param.Payload?["type"]?.ToString();
            
            if (type == "screenshot")
            {
                try
                {
                    string resultJson = await BBSWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", """{"captureBeyondViewport": true}""");
                    
                    var node = JsonNode.Parse(resultJson);
                    string base64 = node?["data"]?.ToString();
            
                    if (!string.IsNullOrEmpty(base64))
                    {
                        await ShowScreenshotAsync(base64);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Screenshot Failed: {ex.Message}");
                }
            }
            else if (type == "image")
            {
                string base64 = param.Payload?["content"]?["image_base64"]?.ToString();
                if (!string.IsNullOrEmpty(base64))
                {
                    await ShowScreenshotAsync(base64);
                }
            }

            return new JsResult(); 
        }
        private async Task ShowScreenshotAsync(string base64)
        {
            try
            {
                _screenshotBytes = Convert.FromBase64String(base64);
                
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(_screenshotBytes.AsBuffer());
                stream.Seek(0);
                
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                
                ScreenshotImage.Source = bitmap;
                ScreenshotGrid.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Show Screenshot Error: {ex.Message}");
            }
        }

        private async void SaveScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (_screenshotBytes == null) return;

            try
            {
                var picker = new FileSavePicker();

                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeChoices.Add("PNG Image", new List<string>() { ".png" });
                picker.SuggestedFileName = $"mihoyo_bbs_{DateTime.Now:yyyyMMddHHmmss}";

                StorageFile file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    await File.WriteAllBytesAsync(file.Path, _screenshotBytes);
                    CloseScreenshot_Click(null, null);
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"Save Error: {ex.Message}");
            }
        }

        private async void CopyScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (_screenshotBytes == null) return;

            try
            {
                var dataPackage = new DataPackage();
                dataPackage.RequestedOperation = DataPackageOperation.Copy;
                
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(_screenshotBytes.AsBuffer());
                stream.Seek(0);
                
                var streamRef = RandomAccessStreamReference.CreateFromStream(stream);
                dataPackage.SetBitmap(streamRef);
                
                Clipboard.SetContent(dataPackage);
                
                CloseScreenshot_Click(null, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Copy Error: {ex.Message}");
            }
        }

        private void CloseScreenshot_Click(object sender, RoutedEventArgs e)
        {
            ScreenshotGrid.Visibility = Visibility.Collapsed;
            _screenshotBytes = null;
            ScreenshotImage.Source = null;
        }

        private JsResult GetUserInfo()
        {
            var uid = cookieDic.GetValueOrDefault("ltuid_v2") ?? cookieDic.GetValueOrDefault("ltuid") ?? "";
            
            return new JsResult 
            { 
                Data = new() 
                { 
                    ["id"] = uid, 
                    ["gender"] = "0", 
 //                 ["nickname"] = "",
                    ["introduce"] = "",
                    ["avatar_url"] = "https://bbs-static.miyoushe.com/avatar/avatarDefault.png"
                } 
            };
        }

        private JsResult GetCurrentLocale() => new JsResult { Data = new() { ["language"] = "zh-cn", ["timeZone"] = "GMT+8" } };

        private async Task ExecuteCallback(string callback, JsResult result)
        {
            await BBSWebView.CoreWebView2.ExecuteScriptAsync($"javascript:mhyWebBridge(\"{callback}\",{result})");
        }

        private string GetMachineGuid() => Guid.NewGuid().ToString();

        private void GoButton_Click(object sender, RoutedEventArgs e) => NavigateToUrl();
        private void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) NavigateToUrl(); }
        private void NavigateToUrl() 
        {
            var url = UrlTextBox.Text;
            if (!string.IsNullOrEmpty(url) && !url.StartsWith("http")) url = "https://" + url;
            if (!string.IsNullOrEmpty(url)) BBSWebView.CoreWebView2.Navigate(url);
        }

        private void ClientTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BBSWebView == null) return;

            if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string type)
            {
                if (_clientConfigs.TryGetValue(type, out var config))
                {
                    _currentConfig = config;
                    UpdateWebViewSettings();
                    
                    if (BBSWebView.CoreWebView2 != null) 
                    {
                        BBSWebView.Reload();
                    }
                }
            }
        }

        private async Task LoadPageAsync(string url)
        {
            if (File.Exists(ConfigPath))
            {
                try {
                    var json = await File.ReadAllTextAsync(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    ParseCookie(cfg?.Account.Cookie ?? "");
                } catch { }
            }
            
            var manager = BBSWebView.CoreWebView2.CookieManager;
            if (BBSWebView.Source == null || BBSWebView.Source.ToString() == "about:blank")
            {
                var cookies = await manager.GetCookiesAsync("https://webstatic.mihoyo.com");
                foreach (var c in cookies) manager.DeleteCookie(c);
            }
            
            foreach (var kv in cookieDic)
            {
                var cookie = manager.CreateCookie(kv.Key, kv.Value, ".mihoyo.com", "/");
                manager.AddOrUpdateCookie(cookie);
            }
            BBSWebView.CoreWebView2.Navigate(url);
        }

        private void ParseCookie(string cookieStr)
        {
            cookieDic.Clear();
            if (string.IsNullOrWhiteSpace(cookieStr)) return;
            foreach (var item in cookieStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = item.Split('=', 2);
                if (kv.Length == 2) cookieDic[kv[0].Trim()] = kv[1].Trim();
            }
        }
        
        private class JsParam
        {
            [JsonPropertyName("method")] public string Method { get; set; } = "";
            [JsonPropertyName("payload")] public JsonNode? Payload { get; set; }
            [JsonPropertyName("callback")] public string? Callback { get; set; }
        }
        private class JsResult
        {
            [JsonPropertyName("retcode")] public int Code { get; set; } = 0;
            [JsonPropertyName("message")] public string Message { get; set; } = "OK";
            [JsonPropertyName("data")] public Dictionary<string, object> Data { get; set; } = new();
            public override string ToString() => JsonSerializer.Serialize(this);
        }
        public class AppConfig { public AccountConfig Account { get; set; } }
        public class AccountConfig { public string Cookie { get; set; } }
    }
}