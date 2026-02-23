using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace update;

public class UpdateInfo
{
    [JsonPropertyName("Version")]
    public string? Version { get; set; }
}

public class DocumentInfo
{
    [JsonPropertyName("Document")]
    public string? Document { get; set; }
}
    
public class IgnoreConfig
{
    [JsonPropertyName("IgnoredVersions")]
    public List<string> IgnoredVersions { get; set; } = new();
}

internal static class Program
{
    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    private const string RemoteUpdateUrl = "http://philia093.cyou/Update.json";
    private const string RemoteDocUrl = "http://philia093.xyz/api/Document.json";

    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            CheckWindowsVersion();

            CheckForUpdatesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"检查更新时发生异常:\n{ex.Message}", "更新错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    
    private static void CheckWindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var buildStr = key.GetValue("CurrentBuild")?.ToString();
                if (int.TryParse(buildStr, out int buildNumber))
                {
                    if (buildNumber < 19041)
                    {
                        ShowWindowsUpdateNotification();
                    }
                }
            }
        }
        catch
        {
            // ignored
        }
    }
    
    private static void ShowWindowsUpdateNotification()
    {
        NotifyIcon notifyIcon = new()
        {
            Visible = true,
            Icon = SystemIcons.Warning,
            BalloonTipTitle = "系统版本过低",
            BalloonTipText = "您的 Windows 版本低于 20H1，可能无法运行主程序，建议更新系统以获得最佳运行体验",
            BalloonTipIcon = ToolTipIcon.Warning
        };
        
        notifyIcon.ShowBalloonTip(5000);
        
        Task.Delay(8000).ContinueWith(_ => notifyIcon.Dispose());
    }

    private static async Task CheckForUpdatesAsync()
    {
        var localJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Update.json");
        var localVersion = "0.0.0.0";

        if (File.Exists(localJsonPath))
        {
            var localJsonText = await File.ReadAllTextAsync(localJsonPath);
            var localInfo = JsonSerializer.Deserialize<UpdateInfo>(localJsonText);
            localVersion = localInfo?.Version ?? "0.0.0.0";
        }
            
        var remoteJsonText = await httpClient.GetStringAsync(RemoteUpdateUrl);
        var remoteInfo = JsonSerializer.Deserialize<UpdateInfo>(remoteJsonText);

        if (remoteInfo == null || string.IsNullOrEmpty(remoteInfo.Version))
        {
            return; 
        }
            
        if (localVersion != remoteInfo.Version)
        {
            var ignoreJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IgnoreConfig.json");
            List<string> ignoredVersions = new();

            if (File.Exists(ignoreJsonPath))
            {
                try
                {
                    var ignoreJsonText = await File.ReadAllTextAsync(ignoreJsonPath);
                    var ignoreConfig = JsonSerializer.Deserialize<IgnoreConfig>(ignoreJsonText);
                    if (ignoreConfig?.IgnoredVersions != null)
                    {
                        ignoredVersions = ignoreConfig.IgnoredVersions;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            if (ignoredVersions.Contains(remoteInfo.Version))
            {
                return;
            }
                
            var docJsonText = await httpClient.GetStringAsync(RemoteDocUrl);
            var docInfo = JsonSerializer.Deserialize<DocumentInfo>(docJsonText);
                
            if (docInfo == null || string.IsNullOrEmpty(docInfo.Document))
            {
                MessageBox.Show("发现新版本，但获取下载地址失败！(JSON解析为空)", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
                
            DialogResult result = MessageBox.Show(
                $"发现新版本: {remoteInfo.Version}\n\n" +
                $"【是】 立即前往浏览器下载更新\n" +
                $"【否】 暂不更新，下次启动再问我\n" +
                $"【取消】 忽略此版本，不再提示",
                "更新提示",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Information);
                
            if (result == DialogResult.Yes)
            {
                OpenUrlInBrowser(docInfo.Document);
            }
            else if (result == DialogResult.Cancel)
            {
                if (!ignoredVersions.Contains(remoteInfo.Version))
                {
                    ignoredVersions.Add(remoteInfo.Version);
                    var newConfig = new IgnoreConfig { IgnoredVersions = ignoredVersions };
                        
                    var newJson = JsonSerializer.Serialize(newConfig, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(ignoreJsonPath, newJson);
                }
            }
        }
    }

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开浏览器，请手动访问以下链接:\n{url}\n\n错误信息: {ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}