/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;

namespace FufuLauncher.Views;

public sealed partial class UpdateNotificationWindow : WindowEx
{
    private readonly bool _isPreview;

    public UpdateNotificationWindow(string updateInfoUrl, bool isPreview = false)
    {
        InitializeComponent();
        FufuLauncher.Services.FontScaleService.HookWindow(this);

        _isPreview = isPreview;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        TitleBarText.Text = _isPreview
            ? "UpdateNotification_PreviewTitle".GetLocalized()
            : "UpdateNotification_Title".GetLocalized();

        if (_isPreview)
        {
            // 预览版公告的特殊样式：黄色横幅 + 标题栏淡黄底色
            PreviewBanner.Visibility = Visibility.Visible;
            AppTitleBar.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x40, 0xFF, 0xD7, 0x00));
            AppTitleBar.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        }

        UpdateWebView.NavigationStarting += UpdateWebView_NavigationStarting;
        UpdateWebView.Source = new Uri(updateInfoUrl);

        this.CenterOnScreen();
        SystemBackdrop = new DesktopAcrylicBackdrop();
        IsShownInSwitchers = true;
    }

    private void UpdateWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        try
        {
            sender.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateNotificationWindow] {ex.Message}");
        }
    }
    
    private async void OnUpdateBtnClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateWebView?.Close();
        }
        catch { }
        
        await Task.Delay(200);

        if (_isPreview)
        {
            LaunchPreviewUpdater();
        }
        else if (App.MainWindow is MainWindow mainWindow)
        {
            await mainWindow.NavigateToSettingsUpdateSectionAsync();
        }

        Close();
    }

    private void LaunchPreviewUpdater()
    {
        try
        {
            string updaterPath = Path.Combine(AppContext.BaseDirectory, "UpdateFufuLauncher.exe");

            if (!File.Exists(updaterPath))
            {
                Debug.WriteLine("未找到 UpdateFufuLauncher.exe");
                return;
            }

            bool useThirdPartyCdn = true;
            try
            {
                var localSettingsService = App.GetService<ILocalSettingsService>();
                var cdnSetting = localSettingsService.ReadSettingAsync("IsUseThirdPartyCDNEnabled").Result;
                if (cdnSetting != null)
                {
                    useThirdPartyCdn = Convert.ToBoolean(cdnSetting);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateNotificationWindow] 读取CDN设置失败: {ex.Message}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--use-third-party-cdn={useThirdPartyCdn.ToString().ToLower()} --preview" +
                            $" --installed-version={AppVersionHelper.FullVersion}"
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动预览版更新程序失败: {ex.Message}");
        }
    }
}
