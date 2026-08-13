/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using FufuLauncher.Helpers;
using FufuLauncher.Models.GameServer;
using FufuLauncher.Services;
using FufuLauncher.Services.GameServer;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace FufuLauncher.Views
{
    public sealed partial class DownloadWindow : Window
    {
        private readonly string _installPath;
        private CancellationTokenSource? _cts;
        private bool _isDownloading = false;
        private readonly GameServerDownloadMonitor _downloadMonitor = new();
        private DownloadSpeedChartController? _chartController;

        public DownloadWindow(string installPath)
        {
            InitializeComponent();
        FufuLauncher.Services.FontScaleService.HookWindow(this);
            _installPath = installPath;
            PathBox.Text = _installPath;

            _chartController = new DownloadSpeedChartController(SpeedGraphChart, _downloadMonitor);
            SpeedGraphChart.NoDataText = "SpeedGraph_NoData".GetLocalized();

            ServerCombo.ItemsSource = GameServerScheme.Selectable;
            ServerCombo.SelectedItem = GameServerScheme.ChineseOfficialOfficial;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var hWnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            }

            Closed += (s, e) => { if (_isDownloading) _cts?.Cancel(); };
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            _isDownloading = true;
            SetUIState(false);

            _chartController!.Start();
            MainProgressBar.Value = 0;
            StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];

            _cts = new CancellationTokenSource();

            var scheme = ServerCombo.SelectedItem as GameServerScheme ?? GameServerScheme.ChineseOfficialOfficial;

            var downloader = new GenshinDownloader(
                App.GetService<SophonBuildClient>(),
                App.GetService<ChunkDownloader>(),
                scheme);

            downloader.ProgressChanged += (downloaded, total, doneFiles, totalFiles) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (total <= 0) return;
                    double percent = (double)downloaded / total * 100;
                    _chartController!.UpdateProgress(percent);
                    MainProgressBar.Value = percent;

                    StatusText.Text = string.Format("DownloadWindow_Processing".GetLocalized(), doneFiles, totalFiles);
                    double dMB = downloaded / 1024.0 / 1024.0;
                    double tMB = total / 1024.0 / 1024.0;
                    ProgressText.Text = $"{dMB:F1} MB / {tMB:F1} MB ({percent:F1}%)";
                });
            };

            try
            {
                var lang = ((ComboBoxItem)LanguageCombo.SelectedItem).Tag?.ToString() ?? "zh-cn";
                var downloadBase = BaseGameCheck.IsChecked == true;

                await Task.Run(() => downloader.StartDownloadAsync(_installPath, lang, downloadBase, 16, _cts.Token, _downloadMonitor));
                _chartController.Stop();

                DispatcherQueue.TryEnqueue(async () =>
                {
                    MainProgressBar.Value = 100;
                    StatusText.Text = "DownloadWindow_Success".GetLocalized();
                    StatusText.Foreground = new SolidColorBrush(Colors.Green);
                    CancelButton.Content = "CloseBtn".GetLocalized();

                    var dialog = new ContentDialog
                    {
                        Title = "DownloadWindow_Complete".GetLocalized(),
                        Content = "DownloadWindow_AllDone".GetLocalized(),
                        CloseButtonText = "OkBtn".GetLocalized(),
                        XamlRoot = Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                });
            }
            catch (OperationCanceledException)
            {
                _chartController.SetPaused();
                DispatcherQueue.TryEnqueue(() => StatusText.Text = "DownloadWindow_Cancelled".GetLocalized());
            }
            catch (Exception ex)
            {
                _chartController.SetFailed();
                DispatcherQueue.TryEnqueue(async () =>
                {
                    StatusText.Text = "DownloadWindow_Error".GetLocalized();
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    var dialog = new ContentDialog
                    {
                        Title = "ErrorTitle".GetLocalized(),
                        Content = ex.Message,
                        CloseButtonText = "CloseBtn".GetLocalized(),
                        XamlRoot = Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                });
            }
            finally
            {
                _isDownloading = false;
                _cts = null;
                SetUIState(true);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) _cts?.Cancel();
            else Close();
        }

        private void SetUIState(bool enabled)
        {
            StartButton.IsEnabled = enabled;
            LanguageCombo.IsEnabled = enabled;
            ServerCombo.IsEnabled = enabled;
            BaseGameCheck.IsEnabled = enabled;
            PathBox.IsEnabled = enabled;
            CancelButton.IsEnabled = !enabled;
            CancelButton.Content = enabled ? "CloseBtn".GetLocalized() : "CancelBtn".GetLocalized();
        }
    }
}
