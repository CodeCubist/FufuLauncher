using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using MihoyoBBS;

namespace FufuLauncher.Views;

public sealed partial class LoginWebViewDialog : Window
{
    private bool _loginCompleted = false;
    private AppWindow _appWindow;
    private DispatcherTimer _autoCheckTimer;
    private bool _isChecking = false;

    public LoginWebViewDialog()
    {
        InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow != null)
        {
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(new Grid() { Height = 0 });

        _autoCheckTimer = new DispatcherTimer();
        _autoCheckTimer.Interval = TimeSpan.FromSeconds(3); 
        _autoCheckTimer.Tick += AutoCheckTimer_Tick;
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _autoCheckTimer?.Stop();

        await ClearMiyousheCookiesAsync();

        Close();
    }

    private async void AutoCheckTimer_Tick(object sender, object e)
    {
        await CheckAndSaveLoginStatus();
    }

    private async Task ClearMiyousheCookiesAsync()
    {
        try
        {
            if (LoginWebView?.CoreWebView2?.CookieManager != null)
            {
                var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.miyoushe.com");
                foreach (var cookie in cookies)
                {
                    LoginWebView.CoreWebView2.CookieManager.DeleteCookie(cookie);
                }
                Debug.WriteLine("已清除米游社Cookie");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"清除Cookie失败: {ex.Message}");
        }
    }

    private async void LoginWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;

        try
        {
            if (args.IsSuccess)
            {
                await sender.ExecuteScriptAsync("""
                    if (window.location.host === 'www.miyoushe.com') {
                        var openLoginDialogIntervalId = setInterval(function() {
                            var ele = document.getElementsByClassName('header__avatarwrp');
                            if (ele.length > 0) {
                                clearInterval(openLoginDialogIntervalId);
                                ele[0].click();
                            }
                        }, 100);
                    }
                """);
            }

            if (sender.Source?.AbsoluteUri.Contains("miyoushe.com") == true)
            {
                var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.miyoushe.com");

                if (cookies.Count > 0)
                {
                    StatusText.Text = "点击'完成登录'保存";
                }
            }
            if (sender.Source?.AbsoluteUri.Contains("miyoushe.com") == true)
            {
                if (!_autoCheckTimer.IsEnabled)
                {
                    _autoCheckTimer.Start();
                    Debug.WriteLine("开始自动检测登录状态");
                }

                await CheckAndSaveLoginStatus();
            }
        }

        catch (Exception ex)
        {
            Debug.WriteLine($"操作失败: {ex.Message}");
            StatusText.Text = "自动操作失败，请手动登录";
        }
    }

    private async Task CheckAndSaveLoginStatus()
    {
        if (_isChecking || _loginCompleted || LoginWebView?.CoreWebView2?.CookieManager == null)
            return;

        _isChecking = true;

        try
        {
            var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.miyoushe.com");

            Debug.WriteLine($"检测到 {cookies.Count} 个Cookie");
            foreach (var cookie in cookies)
            {
                Debug.WriteLine($"  {cookie.Name}: {cookie.Value}");
            }

            var loginCookieNames = new[] { "account_id", "ltuid", "ltoken", "cookie_token", "login_ticket", "stuid", "stoken" };
            var hasKeyCookies = cookies.Any(c => loginCookieNames.Contains(c.Name));
            if (cookies.Count >= 3 && hasKeyCookies)
            {
                var latestCookieString = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));

                if (!string.IsNullOrEmpty(latestCookieString))
                {
                    StatusText.Text = "检测到登录成功，正在保存...";
                    await SaveCookiesAsync(latestCookieString);
                    await ClearMiyousheCookiesAsync();
                    _loginCompleted = true;
                    StatusText.Text = "登录成功！正在关闭...";
                    _autoCheckTimer.Stop();
                    await Task.Delay(2000);
                    Close();
                }
            }
            else if (cookies.Count > 0)
            {
                StatusText.Text = "等待登录完成...";
            }
            else
            {
                StatusText.Text = "请登录米游社账号";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"检查登录状态失败: {ex.Message}");
        }
        finally
        {
            _isChecking = false;
        }
    }


    private async Task SaveCookiesAsync(string cookieString)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var config = new Config();
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }

            if (config.Account == null) config.Account = new AccountConfig();
            config.Account.Cookie = cookieString;
            if (cookieString.Contains("account_id="))
            {
                var match = System.Text.RegularExpressions.Regex.Match(cookieString, @"account_id=(\d+)");
                if (match.Success) config.Account.Stuid = match.Groups[1].Value;
            }
            else if (cookieString.Contains("ltuid="))
            {
                var match = System.Text.RegularExpressions.Regex.Match(cookieString, @"ltuid=(\d+)");
                if (match.Success) config.Account.Stuid = match.Groups[1].Value;
            }
            else if (cookieString.Contains("stuid="))
            {
                var match = System.Text.RegularExpressions.Regex.Match(cookieString, @"stuid=(\d+)");
                if (match.Success) config.Account.Stuid = match.Groups[1].Value;
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            var newJson = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(path, newJson);

            Debug.WriteLine($"文件已保存: {path}");
            Debug.WriteLine($"保存的Cookie: {cookieString}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存失败: {ex.Message}");
            StatusText.Text = $"保存失败: {ex.Message}";
        }
    }

    public bool DidLoginSucceed() => _loginCompleted;
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _autoCheckTimer?.Stop();
    }
}