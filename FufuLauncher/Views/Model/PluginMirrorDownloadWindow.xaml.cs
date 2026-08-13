/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using FufuLauncher.Helpers;
using FufuLauncher.Models;
using FufuLauncher.Services;
using FufuLauncher.Services.PluginMirror;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FufuLauncher.Views;

public sealed partial class PluginMirrorDownloadWindow : Window
{
    private readonly PluginStoreService _storeService;
    private readonly MirrorSiteProvider _mirrorProvider;
    private readonly string _fileUrl;
    private readonly string _destinationPath;
    private readonly IProgress<DownloadProgressInfo>? _progress;
    private readonly string? _expectedHash;
    private readonly CancellationToken _outerToken;
    private readonly TaskCompletionSource<bool> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _windowCts = new();

    private MirrorSiteConfig _config = new();
    private CancellationTokenSource? _attemptCts;
    private bool _userInitiatedStop;
    private bool _completed;
    private bool _isDownloading;
    private bool _isTesting;
    private long _currentReceivedBytes;
    
    private readonly DispatcherTimer _stuckTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long _lastTickBytes;
    private int _stuckTicks;
    private bool _stuckDialogOpen;

    public PluginMirrorDownloadWindow(PluginStoreService storeService, MirrorSiteProvider mirrorProvider,
        string fileUrl, string destinationPath, IProgress<DownloadProgressInfo>? progress, string? expectedHash,
        CancellationToken cancellationToken)
    {
        InitializeComponent();
        FufuLauncher.Services.FontScaleService.HookWindow(this);

        _storeService = storeService;
        _mirrorProvider = mirrorProvider;
        _fileUrl = fileUrl;
        _destinationPath = destinationPath;
        _progress = progress;
        _expectedHash = expectedHash;
        _outerToken = cancellationToken;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow != null)
        {
            WindowManagerHelper.ResizeWithDpi(appWindow, this, 560, 640);
            WindowManagerHelper.CenterWindowOnScreen(appWindow, 560, 640);
        }

        Closed += OnWindowClosed;
        _stuckTimer.Tick += OnStuckTimerTick;
        
        _ = RunSpeedTestAsync();
    }
    
    public Task<bool> CompletionTask => _completionTcs.Task;

    private async Task RunSpeedTestAsync()
    {
        if (_isTesting) return;
        _isTesting = true;
        try
        {
            ShowTestingPhase();
            TestStatusText.Text = "PluginMirrorTesting".GetLocalized();

            _config = _mirrorProvider.LoadConfig();
            if (_config.Mirrors.Count == 0)
            {
                Debug.WriteLine("[PluginMirrorWindow] No mirrors available");
                CompleteWithAbort();
                return;
            }

            var progress = new Progress<MirrorTestProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_completed) return;
                    TestStatusText.Text = string.Format("PluginMirrorTestingCount".GetLocalized(), p.Tested, p.Total);
                });
            });

            var results = await _mirrorProvider.TestMirrorsAsync(_config, progress, _windowCts.Token);
            if (_completed) return;

            MirrorListView.ItemsSource = results;
            MirrorListView.SelectedIndex = results.Count > 0 ? 0 : -1;
            StartDownloadButton.IsEnabled = results.Count > 0;
            ShowSelectionPhase();

            if (results.Count == 0)
            {
                SelectionHintText.Text = "PluginMirrorAllFailedMessage".GetLocalized();
                await ShowAllMirrorsFailedDialogAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginMirrorWindow] Speed test error: {ex}");
            if (_completed) return;
            CompleteWithAbort();
        }
        finally
        {
            _isTesting = false;
        }
    }

    private void OnRetestClick(object sender, RoutedEventArgs e)
    {
        _ = RunSpeedTestAsync();
    }

    private async void OnStartDownloadClick(object sender, RoutedEventArgs e)
    {
        if (MirrorListView.SelectedItem is not MirrorTestResult selected)
        {
            await ShowChoiceDialogAsync("PluginMirrorWindowTitle",
                "PluginMirrorSelectSourceFirst".GetLocalized());
            return;
        }

        await StartDownloadAsync(MirrorSiteProvider.BuildMirrorUrl(selected.Domain, _fileUrl));
    }

    private async void OnDirectClick(object sender, RoutedEventArgs e)
    {
        await StartDownloadAsync(_fileUrl);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (!_isDownloading) return;
        _userInitiatedStop = true;
        _attemptCts?.Cancel();
    }

    private void OnSwitchSourceClick(object sender, RoutedEventArgs e)
    {
        OnStopClick(sender, e);
    }

    private async Task StartDownloadAsync(string url)
    {
        if (_isDownloading) return;
        _isDownloading = true;
        _userInitiatedStop = false;
        _stuckDialogOpen = false;
        _stuckTicks = 0;
        _lastTickBytes = 0;
        _currentReceivedBytes = 0;

        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = "PluginMirrorDownloading".GetLocalized();
        PercentText.Text = "0%";
        DetailText.Text = string.Empty;
        ShowDownloadPhase();
        StartStuckTimer();

        _attemptCts?.Dispose();
        _attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_outerToken, _windowCts.Token);
        
        var downloadProgress = new Progress<DownloadProgressInfo>(OnDownloadProgress);
        try
        {
            await Task.Run(() => _storeService.DownloadFileCoreAsync(
                url, _destinationPath, downloadProgress, _expectedHash,
                checkErrorGate: false, _attemptCts.Token));

            StopStuckTimer();
            DownloadProgressBar.Value = 100;
            DownloadStatusText.Text = "PluginMirrorDownloadComplete".GetLocalized();
            PercentText.Text = "100%";
            _isDownloading = false;
            CompleteWithSuccess();
        }
        catch (OperationCanceledException)
        {
            StopStuckTimer();
            _isDownloading = false;
            if (_userInitiatedStop)
            {
                ShowSelectionPhase();
            }
            else
            {
                CompleteWithCancel();
            }
        }
        catch (HashMismatchException)
        {
            StopStuckTimer();
            _isDownloading = false;
            await HandleHashMismatchAsync();
        }
        catch (Exception ex)
        {
            StopStuckTimer();
            _isDownloading = false;
            await HandleDownloadFailedAsync(ex);
        }
        finally
        {
            _attemptCts?.Dispose();
            _attemptCts = null;
        }
    }

    private void OnDownloadProgress(DownloadProgressInfo info)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_completed) return;
            _currentReceivedBytes = info.BytesDownloaded;

            DownloadProgressBar.Value = Math.Min(100, info.Percent);
            PercentText.Text = $"{info.Percent:F1}%";

            double dMB = info.BytesDownloaded / 1024.0 / 1024.0;
            double tMB = info.TotalBytes / 1024.0 / 1024.0;
            double speedMB = info.SpeedBytesPerSecond / 1024.0 / 1024.0;
            DetailText.Text = $"{dMB:F2} MB / {tMB:F2} MB • {speedMB:F2} MB/s";
            
            _progress?.Report(info);
        });
    }

    private void StartStuckTimer()
    {
        _lastTickBytes = _currentReceivedBytes;
        _stuckTicks = 0;
        _stuckTimer.Start();
    }

    private void StopStuckTimer()
    {
        _stuckTimer.Stop();
    }

    private void OnStuckTimerTick(object? sender, object e)
    {
        if (!_isDownloading || _stuckDialogOpen) return;

        var delta = _currentReceivedBytes - _lastTickBytes;
        _lastTickBytes = _currentReceivedBytes;

        if (delta < 20480) _stuckTicks++;
        else _stuckTicks = 0;

        if (_stuckTicks >= 10)
        {
            _stuckDialogOpen = true;
            _stuckTimer.Stop();
            _ = PromptStuckAsync();
        }
    }

    private async Task PromptStuckAsync()
    {
        var result = await ShowChoiceDialogAsync("PluginMirrorStuckTitle",
            "PluginMirrorStuckMessage".GetLocalized(),
            primaryKey: "PluginMirrorStuckSwitch",
            closeKey: "PluginMirrorStuckContinue",
            defaultButton: ContentDialogButton.Primary);
        _stuckDialogOpen = false;

        if (result == ContentDialogResult.Primary)
        {
            _userInitiatedStop = true;
            _attemptCts?.Cancel();
        }
        else
        {
            _stuckTicks = 0;
            _lastTickBytes = _currentReceivedBytes;
            _stuckTimer.Start();
        }
    }

    private async Task HandleHashMismatchAsync()
    {
        var result = await ShowChoiceDialogAsync("PluginMirrorHashFailedTitle",
            "PluginMirrorHashFailedMessage".GetLocalized(),
            primaryKey: "PluginMirrorRetrySwitch",
            secondaryKey: "PluginMirrorDirectOfficial",
            closeKey: "PluginMirrorCancelInstall",
            defaultButton: ContentDialogButton.Primary);

        if (result == ContentDialogResult.Primary) ShowSelectionPhase();
        else if (result == ContentDialogResult.Secondary) await StartDownloadAsync(_fileUrl);
        else CompleteWithAbort();
    }

    private async Task HandleDownloadFailedAsync(Exception ex)
    {
        var result = await ShowChoiceDialogAsync("PluginMirrorDownloadFailedTitle",
            string.Format("PluginMirrorDownloadFailedMessage".GetLocalized(), ex.Message),
            primaryKey: "PluginMirrorRetrySwitch",
            secondaryKey: "PluginMirrorDirectOfficial",
            closeKey: "PluginMirrorCancelInstall",
            defaultButton: ContentDialogButton.Primary);

        if (result == ContentDialogResult.Primary) ShowSelectionPhase();
        else if (result == ContentDialogResult.Secondary) await StartDownloadAsync(_fileUrl);
        else CompleteWithAbort();
    }

    private async Task ShowAllMirrorsFailedDialogAsync()
    {
        var result = await ShowChoiceDialogAsync("PluginMirrorAllFailedTitle",
            "PluginMirrorAllFailedMessage".GetLocalized(),
            primaryKey: "PluginMirrorDirectOfficial",
            closeKey: "PluginMirrorCancelInstall",
            defaultButton: ContentDialogButton.Primary);

        if (result == ContentDialogResult.Primary) await StartDownloadAsync(_fileUrl);
        else CompleteWithAbort();
    }

    private async Task<ContentDialogResult> ShowChoiceDialogAsync(string titleKey, string message,
        string? primaryKey = null, string? secondaryKey = null, string? closeKey = null,
        ContentDialogButton defaultButton = ContentDialogButton.Close)
    {
        try
        {
            if (Content?.XamlRoot == null) return ContentDialogResult.None;

            var dialog = new ContentDialog
            {
                Title = titleKey.GetLocalized(),
                Content = message,
                XamlRoot = Content.XamlRoot
            };
            if (!string.IsNullOrEmpty(primaryKey))
                dialog.PrimaryButtonText = primaryKey.GetLocalized();
            if (!string.IsNullOrEmpty(secondaryKey))
                dialog.SecondaryButtonText = secondaryKey.GetLocalized();
            dialog.CloseButtonText = (closeKey ?? "CloseBtn").GetLocalized();
            dialog.DefaultButton = defaultButton;

            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PluginMirrorWindow] Dialog failed to show: {ex.Message}");
            return ContentDialogResult.None;
        }
    }

    private void CompleteWithSuccess()
    {
        if (!_completionTcs.TrySetResult(true)) return;
        _completed = true;
        _ = AutoCloseAsync();
    }

    private void CompleteWithCancel()
    {
        if (_completed) return;
        _completed = true;
        _completionTcs.TrySetException(new OperationCanceledException("PluginStoreCancelled".GetLocalized()));
        Close();
    }

    private void CompleteWithAbort()
    {
        if (_completed) return;
        _completed = true;
        _completionTcs.TrySetException(new InvalidOperationException("PluginMirrorAbortedMessage".GetLocalized()));
        Close();
    }

    private async Task AutoCloseAsync()
    {
        await Task.Delay(1200);
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _stuckTimer.Stop();
        _windowCts.Cancel();
        if (_completed) return;
        _completed = true;
        _attemptCts?.Cancel();
        _completionTcs.TrySetException(new OperationCanceledException("PluginStoreCancelled".GetLocalized()));
    }

    private void ShowTestingPhase()
    {
        TestingPanel.Visibility = Visibility.Visible;
        SelectionPanel.Visibility = Visibility.Collapsed;
        DownloadPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowSelectionPhase()
    {
        TestingPanel.Visibility = Visibility.Collapsed;
        SelectionPanel.Visibility = Visibility.Visible;
        DownloadPanel.Visibility = Visibility.Collapsed;
        SelectionHintText.Text = "PluginMirrorSelectSourceHint".GetLocalized();
    }

    private void ShowDownloadPhase()
    {
        TestingPanel.Visibility = Visibility.Collapsed;
        SelectionPanel.Visibility = Visibility.Collapsed;
        DownloadPanel.Visibility = Visibility.Visible;
    }
}
