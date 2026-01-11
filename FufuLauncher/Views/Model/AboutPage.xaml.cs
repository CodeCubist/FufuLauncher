using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Input;


namespace FufuLauncher.Views;

public sealed partial class AboutPage : Page
{
    private static readonly HttpClient httpClient = new HttpClient();
    private static readonly string batchFilePath = System.IO.Path.Combine(System.Environment.CurrentDirectory, "..\\download_build.bat");

    public AboutPage()
    {
        this.InitializeComponent();
    }
    private async void GetBuildFormActions(object sender, RoutedEventArgs e)
    {
        GetBuildFormActionsToggle.IsEnabled = false;
        GetBuildFormActionsToggle.Content = "正在获取...";
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
        var jsonString = await GetJsonFromUrl("https://api.github.com/repos/CodeCubist/FufuLauncher/actions/workflows");
        var workflows = jsonString.RootElement.GetProperty("workflows").EnumerateArray();
        string workflowBaseUrl = "";
        foreach (var workflow in workflows)
        {
            if (workflow.GetProperty("name").GetString() == ".NET Core Desktop")
            {
                workflowBaseUrl = workflow.GetProperty("url").GetString();
                Debug.WriteLine("[GetBuildFromActions] 找到工作流ID: " + workflowBaseUrl);
                break;
            }
        }
        if (workflowBaseUrl != "")
        {
            string workflowRunsUrl = workflowBaseUrl + "/runs";
            var runsJson = await GetJsonFromUrl(workflowRunsUrl);
            var runs = runsJson.RootElement.GetProperty("workflow_runs").EnumerateArray();
            var lastSuccessfulRunUrl = "";
            foreach (var run in runs)
            {
                if (run.GetProperty("conclusion").GetString() == "success")
                {
                    lastSuccessfulRunUrl = run.GetProperty("url").GetString();
                    Debug.WriteLine("[GetBuildFromActions] 找到最近成功的运行: " + lastSuccessfulRunUrl);
                    break;
                }
            }
            if (lastSuccessfulRunUrl != "")
            {
                string artifactsUrl = lastSuccessfulRunUrl + "/artifacts";
                var artifactsJson = await GetJsonFromUrl(artifactsUrl);
                var artifacts = artifactsJson.RootElement.GetProperty("artifacts").EnumerateArray();
                string downloadUrl = "";
                foreach (var artifact in artifacts)
                {
                    if (artifact.GetProperty("name").GetString() == "FufuLauncher_Release")
                    {
                        downloadUrl = artifact.GetProperty("archive_download_url").GetString();
                        Debug.WriteLine("[GetBuildFromActions] 找到构建工件下载链接: " + downloadUrl);
                        break;
                    }
                }
                if (downloadUrl != "")
                {
                   var userToken = await PromptForTokenAsync();
                     if (!string.IsNullOrEmpty(userToken))
                    {
                        string DownloadShell = "";
                        DownloadShell += $"taskkill /F /IM FufuLauncher.exe *> $null\n";
                        DownloadShell += $"del \"{System.Environment.CurrentDirectory}\\*\" /f /s /q\n";
                        DownloadShell += $"curl -H \"Authorization: Bearer {userToken}\" -L \"{downloadUrl}\" --ssl-no-revoke -o \"{System.Environment.CurrentDirectory}\\FufuLauncher_Build.zip\"\n";
                        DownloadShell += $"tar -xf \"{System.Environment.CurrentDirectory}\\FufuLauncher_Build.zip\" -C \"{System.Environment.CurrentDirectory}\"\n";
                        DownloadShell += $"del \"{System.Environment.CurrentDirectory}\\FufuLauncher_Build.zip\" /f /s /q\n";
                        DownloadShell += $"start {System.Environment.CurrentDirectory}\\FufuLauncher.exe\n";
                        DownloadShell += $"del %0";
                        GetBuildFormActionsToggle.Content = "获取成功! 已生成下载脚本.";
                        Debug.WriteLine("[GetBuildFromActions] 生成的下载脚本内容: \n" + DownloadShell);
                        Debug.WriteLine("[GetBuildFromActions] 下载脚本路径: " + batchFilePath);
                        System.IO.File.WriteAllText(batchFilePath, DownloadShell, System.Text.Encoding.UTF8);
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c \"{batchFilePath}\"",
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        Process.Start(psi);
                        System.Environment.Exit(0);
                    }
                    else
                    {
                        GetBuildFormActionsToggle.Content = "请输入Token! ";
                        await Task.Delay(1000);
                        GetBuildFormActionsToggle.Content = "从Github Actions获取构建";
                        GetBuildFormActionsToggle.IsEnabled = true;
                    }
                }
                else
                {
                    GetBuildFormActionsToggle.Content = "获取失败! ";
                    await Task.Delay(1000);
                    GetBuildFormActionsToggle.Content = "从Github Actions获取构建";
                    GetBuildFormActionsToggle.IsEnabled = true;
                }
            }
            else
            {
                GetBuildFormActionsToggle.Content = "获取失败! ";
                await Task.Delay(1000);
                GetBuildFormActionsToggle.Content = "从Github Actions获取构建";
                GetBuildFormActionsToggle.IsEnabled = true;
            }
        }
        else
        {
            GetBuildFormActionsToggle.Content = "获取失败! ";
            await Task.Delay(1000);
            GetBuildFormActionsToggle.Content = "从Github Actions获取构建";
            GetBuildFormActionsToggle.IsEnabled = true;
        }

    }
    private async Task<JsonDocument> GetJsonFromUrl(string url)
    {
        var responseString = await httpClient.GetAsync(url);
        var responseContent = await responseString.Content.ReadAsStringAsync();
        Debug.WriteLine("[GetBuildFromActions] 从<"+url+">获取到: " + responseContent);
        return JsonDocument.Parse(responseContent);
    }
    private async Task<string> PromptForTokenAsync()
    {
        TextBox tokenInput = new TextBox
        {
            PlaceholderText = "请输入你的 GitHub Token",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap
        };
        ContentDialog tokenDialog = new ContentDialog
        {
            Title = "GitHub Token",
            Content = tokenInput,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        tokenDialog.XamlRoot = this.XamlRoot;
        ContentDialogResult result = await tokenDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            return tokenInput.Text;
        }
        return string.Empty;
    }
}