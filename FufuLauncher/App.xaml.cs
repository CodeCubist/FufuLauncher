/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using System.Runtime.InteropServices;
using FufuLauncher.Activation;
using CommunityToolkit.Mvvm.Messaging;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Data.Repositories;
using FufuLauncher.Helpers;
using FufuLauncher.Models;
using FufuLauncher.Services;
using FufuLauncher.Services.Background;
using FufuLauncher.ViewModels;
using FufuLauncher.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Media.Core;
using Windows.Media.Playback;
using System.Net.Sockets;
using Sentry;
using QuestPDF.Infrastructure;

namespace FufuLauncher;

public partial class App : Application
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    public IHost Host
    {
        get;
    }
    
    private void ShowCrashDialog(string source, Exception? ex)
    {
        if (ex == null) return;

        SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).Wait();

        var message = string.Format("Crash_Message".GetLocalized(), source, ex.Message, ex.StackTrace);
        
        MessageBox(IntPtr.Zero, message, "Crash_Title".GetLocalized(), MB_OK | MB_ICONERROR);
    }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    public static WindowEx MainWindow { get; private set; }

    /// <summary>
    /// Tracks the language selected on the first-run language selection page,
    /// so the AgreementPage can show the appropriate agreement text.
    /// </summary>
    public static ViewModels.AppLanguage? FirstRunSelectedLanguage { get; set; }

    public static UIElement? AppTitlebar
    {
        get; set;
    }
    private static Microsoft.UI.Dispatching.DispatcherQueue _mainDispatcherQueue = null!;
    private ProcessCpuUsageMonitor? _cpuUsageMonitor;
    public App()
    {
        Helpers.AppPaths.EnsureDirectories();
        QuestPDF.Settings.License = LicenseType.Community;

        string userDataFolder = Path.Combine(Helpers.AppPaths.CacheDir, "WebView2Data");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        UnhandledException += App_UnhandledException;
        
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        InitializeComponent();

        // 清理旧版遗留的 resources.pri
        try
        {
            string oldPri = Path.Combine(AppContext.BaseDirectory, "resources.pri");
            if (File.Exists(oldPri))
            {
                File.Delete(oldPri);
                Debug.WriteLine("[App] 已删除残留的 resources.pri");
            }
        }
        catch { }

        var appInstance = AppInstance.GetCurrent();
        appInstance.Activated += App_Activated!;

        try
        {
            Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureHostConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["hostBuilder:reloadConfigOnChange"] = "false"
                    });
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

                    services.AddSingleton<LocalSettingsRepository>();
                    services.AddSingleton<MetadataRepository>();
                    services.AddSingleton<AchievementRepository>();

                    services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
                    services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();

                    services.AddSingleton<IHoyoverseBackgroundService, HoyoverseBackgroundService>();
                    services.AddSingleton<IHoyoverseContentService, HoyoverseContentService>();
                    services.AddSingleton<IBackgroundRenderer, BackgroundRenderer>();

                    services.AddSingleton<IActivationService, ActivationService>();
                    services.AddSingleton<IPageService, PageService>();
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<IFileService, FileService>();

                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<MainPage>();
                    
                    services.AddSingleton<GameStatsService>();
                    services.AddSingleton<IDataCenterPdfReportService, DataCenterPdfReportService>();
                    services.AddTransient<DataViewModel>();
                    services.AddTransient<DataPage>();

                    services.AddSingleton<Services.Backpack.BackpackRuntimeService>();
                    services.AddTransient<BackpackPage>();
                    
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<SettingsPage>();
                    services.AddTransient<BlankPage>();

                    services.AddTransient<NullToVisibilityConverter>();
                    services.AddTransient<BoolToVisibilityConverter>();
                    services.AddTransient<BoolToGlyphConverter>();
                    services.AddTransient<IntToVisibilityConverter>();

                    services.AddTransient<AccountViewModel>();
                    services.AddTransient<AccountPage>();

                    services.AddSingleton<IGameLauncherService, GameLauncherService>();
                    services.AddSingleton<IGameConfigService, GameConfigService>();
                    services.AddSingleton<Services.AuthTicket.IAuthTicketService, Services.AuthTicket.AuthTicketService>();

                    services.AddSingleton<IHoyoverseCheckinService, HoyoverseCheckinService>();
                    services.AddSingleton<ICommunityCheckinService, CommunityCheckinService>();
                    services.AddSingleton<ICloudGameCheckinService, CloudGameCheckinService>();
                    services.AddSingleton<IHoyolabRoleResolverService, HoyolabRoleResolverService>();
                    services.AddSingleton<IUnifiedCheckinService, UnifiedCheckinService>();
                    services.AddSingleton<DailyNoteCardService>();
                    services.AddSingleton<IDeviceFingerprintService, Services.MiHoYo.DeviceFingerprintService>();
                    services.AddSingleton<BlankViewModel>();
                    services.AddTransient<BlankPage>();
                    services.AddSingleton<ILauncherService, LauncherService>();
                    services.AddTransient<OtherViewModel>();
                    services.AddTransient<OtherPage>();
                    services.AddSingleton<IAutoClickerService, AutoClickerService>();
                    services.AddSingleton<IScreenshotService, ScreenshotService>();
                    services.AddTransient<LanguageSelectionViewModel>();
                    services.AddTransient<LanguageSelectionPage>();
                    services.AddTransient<AgreementViewModel>();
                    services.AddTransient<AgreementPage>();
                    services.AddSingleton<IDevBuildDetectionService, DevBuildDetectionService>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddSingleton<ControlPanelModel>();
                    services.AddTransient<PanelPage>();
                    services.AddSingleton<IUserInfoService, UserInfoService>();
                    services.AddSingleton<IUidLookupService, Services.UID.UidLookupService>();

                    services.AddSingleton<AccountManager>();
                    services.AddSingleton<Services.MiHoYo.Fingerprint.DeviceFpService>();
                    services.AddSingleton<Services.MiHoYo.AccountIdentityService>();
                    services.AddSingleton<IBbsRequestBuilder, Services.MiHoYo.Transport.BbsRequestBuilder>();

                    services.AddLogging(builder =>
                    {
                        builder.ClearProviders(); 
                        
                        builder.AddDebug();
                        builder.SetMinimumLevel(LogLevel.Information);
                    });
                    services.AddSingleton<GenshinApiClient>();
                    services.AddSingleton<IGenshinService, GenshinService>();
                    services.AddTransient<GenshinViewModel>();
                    services.AddTransient<GenshinDataWindow>();
                    services.AddSingleton<IFilePickerService, FilePickerService>();
                    services.AddSingleton<INotificationService, NotificationService>();
                    services.AddTransient<CalculatorViewModel>();
                    services.AddTransient<CalculatorPage>();
                    services.AddTransient<PluginViewModel>();
                    services.AddTransient<PluginPage>();
                    services.AddTransient<GachaViewModel>();
                    services.AddSingleton<GachaService>();
                    services.AddSingleton<IAnnouncementService, AnnouncementService>();
                    services.AddTransient<IPluginUpdateService, PluginUpdateService>();
                    services.AddTransient<GachaAnalysisModel>();
                    services.AddTransient<CommunityViewModel>();
                    services.AddTransient<CommunityPage>();
                    services.AddSingleton<PluginStoreService>();
                    services.AddSingleton<LuaPluginInstaller>();
                    services.AddSingleton<Services.PluginMirror.MirrorSiteProvider>();
                    services.AddSingleton<Services.PluginMirror.PluginMirrorDownloadService>();
                    services.AddSingleton<PluginStoreViewModel>();
                    services.AddTransient<PluginStorePage>();
                    
                    services.AddSingleton<Services.GameServer.GameServerHttpClientProvider>();
                    services.AddSingleton<Services.GameServer.ChunkDownloader>();
                    services.AddSingleton<Services.GameServer.SophonBuildClient>();
                    services.AddSingleton<Services.GameServer.GameServerConfigurationService>();
                    services.AddSingleton<Services.GameServer.GameChannelSdkService>();
                    services.AddTransient<Services.GameServer.GameServerConverter>();
                    
                    services.AddSingleton<Services.GameServer.GameUpdateService>();
                    
                    services.AddSingleton<DeveloperAuthorizationService>();

                    services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
                })
                .Build();

            CleanupOldSettings();
            
            Services.GameServer.GameServerCacheMaintenance.CleanLegacyCaches();
        }
        catch (Exception ex)
        {
            LogException(ex, "App Constructor");
            ShowCrashDialog("核心配置及服务初始化异常", ex);
            Environment.Exit(-1);
        }
    }
    
    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        
        var baseEx = e.Exception?.GetBaseException();
        if (baseEx is SocketException
            || baseEx is ObjectDisposedException
            || baseEx is OperationCanceledException)
        {
            Debug.WriteLine($"[UnobservedTask] 已忽略的后台异常: {baseEx.GetType().Name}: {baseEx.Message}");
            return;
        }

        LogException(e.Exception, "UnobservedTaskException");
        ShowCrashDialog("后台异步任务异常", e.Exception);
    }
    
    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogException(e.Exception, "App_UnhandledException");
        
        if (e.Exception is InvalidCastException || 
            (e.Exception is COMException comEx && comEx.HResult == unchecked((int)0x80000019)))
        {
            Debug.WriteLine($"已抑制非致命异常: {e.Exception.Message}");
            return;
        }

        ShowCrashDialog("UI 界面交互异常", e.Exception);
        Environment.Exit(-1);
    }

    private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        LogException(ex, "CurrentDomain_UnhandledException");
        ShowCrashDialog("应用程序域致命异常", ex);
        Environment.Exit(-1);
    }

    private void App_Activated(object sender, AppActivationArguments e)
    {
        _mainDispatcherQueue?.TryEnqueue(() =>
        {
            MainWindow.Activate();
        });
    }

    private void CleanupOldSettings()
    {
        try
        {
            var filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FufuLauncher", "ApplicationData", "LocalSettings.json"
            );

            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                if (content.Contains("System.Private.CoreLib") || content.Contains("True") || content.Contains("False"))
                {
                    File.Delete(filePath);
                    Debug.WriteLine("清理了旧的无效设置文件");
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private void LogException(Exception? ex, string source)
    {
        if (ex == null) return;

        try
        {
            SentrySdk.CaptureException(ex, scope => 
            {
                scope.SetTag("source", source);
            });

            var logPath = Path.Combine(Helpers.AppPaths.RootDir, "CrashLog.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            var log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n" +
                     $"Exception: {ex.GetType().Name}\n" +
                     $"Message: {ex.Message}\n" +
                     $"StackTrace: {ex.StackTrace}\n" +
                     new string('-', 80) + "\n";

            File.AppendAllText(logPath, log);
        }
        catch
        {

        }
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            base.OnLaunched(args);
            Debug.WriteLine("App启动开始");

            _mainDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            _ = Task.Run(async () =>
            {
                try
                {
                    var uidService = GetService<IUidLookupService>();
                    await uidService.LoadAndWriteUidsAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UidLookup] 写入失败: {ex.Message}");
                }
            });

            await VerifyResourceFilesAsync();

            if (!AppPaths.IsFirstRun)
            {
                await ApplyLanguageSettingAsync();
                await SetDefaultThemeAsync();
            }
            else
            {
                await ApplyLanguageSettingAsync();

                WeakReferenceMessenger.Default.Register<Messages.AgreementAcceptedMessage>(this, (r, m) =>
                {
                    WeakReferenceMessenger.Default.Unregister<Messages.AgreementAcceptedMessage>(r);
                    _mainDispatcherQueue.TryEnqueue(async () =>
                    {
                        await SetDefaultThemeAsync();
                    });
                });
            }
            var accountManager = GetService<AccountManager>();
            await accountManager.InitializeAsync();
            MainWindow = new MainWindow();
            var fontScaleJson = await GetService<ILocalSettingsService>().ReadSettingAsync("FontScale");
            if (fontScaleJson != null && double.TryParse(fontScaleJson.ToString(), out var savedFontScale))
            {
                FontScaleService.Apply(savedFontScale);
            }
            _cpuUsageMonitor = new ProcessCpuUsageMonitor(_mainDispatcherQueue, GetService<ILocalSettingsService>());
            _cpuUsageMonitor.Start();
            if (MainWindow is MainWindow mainWindow)
            {
                await mainWindow.InitializeWindowSizeAsync();
            }

            var activationService = GetService<IActivationService>();
            await activationService.ActivateAsync(args);

            Debug.WriteLine("App主窗口已激活");
            var shouldRunBackgroundTasks = !AppPaths.IsFirstRun && 
                !(MainWindow is MainWindow mw && mw.IsAgreementShowing);
            if (shouldRunBackgroundTasks)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(800);
                        await PlayStartupSoundAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"启动语音播放失败: {ex.Message}");
                    }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500);

                        var announcementService = GetService<IAnnouncementService>();
                        var announcementUrl = await announcementService.CheckForNewAnnouncementAsync();

                        if (!string.IsNullOrEmpty(announcementUrl))
                        {
                            await _mainDispatcherQueue.EnqueueAsync(() =>
                            {
                                var announcementWindow = new AnnouncementWindowL(announcementUrl);
                                announcementWindow.Activate();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Announcement] 公告检查失败: {ex.Message}");
                    }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine("[Background] 后台任务开始，等待主窗口渲染...");
                        await Task.Delay(500);

                        Debug.WriteLine("[Background] 准备调度到UI线程...");

                        await _mainDispatcherQueue.EnqueueAsync(async () =>
                        {
                            Debug.WriteLine("[Background] 已在UI线程，执行更新检查...");
                            await CheckAndShowUpdateWindowAsync();
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Background] 后台更新检查失败: {ex.Message}");
                        Debug.WriteLine($"[Background] 异常类型: {ex.GetType().FullName}");
                        Debug.WriteLine($"[Background] 堆栈: {ex.StackTrace}");
                    }
                });
            }

            Debug.WriteLine("App启动完成");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动失败: {ex.Message}");
            LogException(ex, "OnLaunched (启动流程异常)");
            ShowCrashDialog("应用启动流程异常", ex);
            Environment.Exit(-1);
        }
    }
    
    private async Task SetDefaultThemeAsync()
    {
        try
        {
            var localSettings = GetService<ILocalSettingsService>();
            var isThemeInitialized = await localSettings.ReadSettingAsync("IsThemeInitialized");
            
            if (isThemeInitialized == null)
            {
                Debug.WriteLine("Initializing default theme to Dark.");
                var themeService = GetService<IThemeSelectorService>();
                
                await themeService.SetThemeAsync(ElementTheme.Dark);
                
                await localSettings.SaveSettingAsync("IsThemeInitialized", true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set default theme: {ex.Message}");
        }
    }

    internal async Task PlayStartupSoundAsync()
    {
        try
        {
            var localSettingsService = GetService<ILocalSettingsService>();

            var soundEnabled = await localSettingsService.ReadSettingAsync("IsStartupSoundEnabled");
            bool isSoundEnabled = soundEnabled != null && Convert.ToBoolean(soundEnabled);

            if (!isSoundEnabled) return;

            var soundPath = await localSettingsService.ReadSettingAsync("StartupSoundPath");
            if (soundPath == null || string.IsNullOrEmpty(soundPath.ToString())) return;

            string path = soundPath.ToString();
            if (!File.Exists(path))
            {
                Debug.WriteLine($"启动语音文件不存在: {path}");
                return;
            }

            await _mainDispatcherQueue.EnqueueAsync(() =>
            {
                try
                {
                    var mediaPlayer = new MediaPlayer();
                    MediaPlayerHelper.DisableSystemMediaControls(mediaPlayer);
                    mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
                    mediaPlayer.Volume = 0.7;

                    int disposed = 0;
                    void DisposeOnce()
                    {
                        if (Interlocked.Exchange(ref disposed, 1) == 0)
                        {
                            try { mediaPlayer.Dispose(); } catch { }
                        }
                    }

                    mediaPlayer.MediaEnded += (s, e) => DisposeOnce();
                    mediaPlayer.MediaFailed += (s, e) => DisposeOnce();
                    mediaPlayer.Play();

                    var timer = _mainDispatcherQueue.CreateTimer();
                    timer.Interval = TimeSpan.FromSeconds(30);
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        DisposeOnce();
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"启动语音播放异常: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动语音处理失败: {ex.Message}");
        }
    }
    private async Task CheckAndShowUpdateWindowAsync()
    {
        try
        {
            var updateService = GetService<IUpdateService>();
            var result = await updateService.CheckUpdateAsync();

            var devBuildService = GetService<IDevBuildDetectionService>();
            WeakReferenceMessenger.Default.Send(new Messages.DevBuildDetectionCompletedMessage(devBuildService.IsDevBuild));

            if (result.IsDevBuild && MainWindow is MainWindow mainWindow)
            {
                mainWindow.ShowDevBuildBadge();
                
                WeakReferenceMessenger.Default.Send(new Messages.BackgroundRefreshMessage());
            }

            if (result.ShouldShowUpdate)
            {
                Debug.WriteLine($"准备显示更新窗口，版本: {result.ServerVersion}，预览版: {result.IsPreview}");
                Debug.WriteLine($"[App] 动态更新公告URL: {result.UpdateInfoUrl}");

                MainWindow.Activate();

                var updateWindow = new UpdateNotificationWindow(result.UpdateInfoUrl, result.IsPreview);
                updateWindow.Title = result.IsPreview
                    ? $"预览版更新公告 - v{result.ServerVersion}"
                    : $"版本更新公告 - v{result.ServerVersion}";
                updateWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新检查失败: {ex.Message}");
            Debug.WriteLine($"[App] 异常详情: {ex.StackTrace}");
        }
    }
    private async Task VerifyResourceFilesAsync()
    {
        try
        {
            var resourceManager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();

            var resourceMap = resourceManager.MainResourceMap;

            var resourceCandidate = resourceMap.GetValue("Resources/AppDisplayName");

            if (resourceCandidate != null)
            {
                var test = resourceCandidate.ValueAsString;
                Debug.WriteLine($"资源加载成功: {test}");
            }
            else
            {
                Debug.WriteLine("警告: 找不到资源 AppDisplayName");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"资源加载严重失败: {ex.Message}");
        }
    }
    private async Task ApplyLanguageSettingAsync()
    {
        try
        {
            var localSettingsService = GetService<ILocalSettingsService>();
            var languageValue = await localSettingsService.ReadSettingAsync("AppLanguage");

            Debug.WriteLine($"[App] ApplyLanguageSettingAsync: raw value='{languageValue}' (type={languageValue?.GetType().Name ?? "null"})");

            var language = languageValue != null && int.TryParse(languageValue.ToString(), out var languageCode)
                ? (AppLanguage)languageCode
                : AppLanguage.Default;
            var culture = LanguagePreferenceResolver.Resolve(
                language,
                Windows.System.UserProfile.GlobalizationPreferences.Languages);

            Debug.WriteLine($"[App] ApplyLanguageSettingAsync: language={language}, culture='{culture}'");
            ResourceExtensions.SetLanguage(culture);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] ApplyLanguageSettingAsync ERROR: {ex.Message}");
        }
    }
    private void ApplyLanguageSetting()
    {
        try
        {
            var localSettingsService = GetService<ILocalSettingsService>();
            var languageValue = localSettingsService.ReadSettingAsync("AppLanguage").Result;

            if (languageValue != null)
            {
                if (!int.TryParse(languageValue.ToString(), out var languageCode))
                    return;

                var culture = LanguagePreferenceResolver.Resolve(
                    (AppLanguage)languageCode,
                    Windows.System.UserProfile.GlobalizationPreferences.Languages);
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"应用语言设置失败: {ex.Message}");
        }
    }
}

