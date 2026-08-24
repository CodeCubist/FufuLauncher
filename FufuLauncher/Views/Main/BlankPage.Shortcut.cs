/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Text.Json;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Services;
using FufuLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using File = System.IO.File;

namespace FufuLauncher.Views;

public sealed partial class BlankPage
{
    #region 创建桌面快捷方式与启动命令

    private async void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var localSettings = App.GetService<ILocalSettingsService>();
            var settingObj = await localSettings.ReadSettingAsync("GameInstallationPath");
            var rawPath = settingObj as string;

            if (string.IsNullOrEmpty(rawPath))
            {
                await ShowError("Err_GamePathNotSet".GetLocalized());
                return;
            }

            var finalExePath = rawPath;

            if (Directory.Exists(rawPath))
            {
                var exeNames = await GameExeManager.GetExeNamesAsync();
                bool found = false;
                foreach (var name in exeNames)
                {
                    var testPath = Path.Combine(rawPath, name);
                    if (File.Exists(testPath))
                    {
                        finalExePath = testPath;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    await ShowError(string.Format("Err_ExeNotFoundInFolder_Format".GetLocalized(), rawPath));
                    return;
                }
            }

            var appPath = Environment.ProcessPath;

            var presetsDir = Path.Combine(AppContext.BaseDirectory, "Plugins", "Presets");
            var presets = new List<PresetModel>();

            if (Directory.Exists(presetsDir))
            {
                var files = Directory.GetFiles(presetsDir, "*.json").Where(f => !f.EndsWith("active_state.json"));
                foreach (var file in files)
                {
                    try
                    {
                        var preset = JsonSerializer.Deserialize<PresetModel>(File.ReadAllText(file));
                        if (preset != null) presets.Add(preset);
                    }
                    catch { }
                }
            }

            var presetComboBox = new ComboBox
            {
                ItemsSource = presets,
                DisplayMemberPath = "Name",
                PlaceholderText = "Placeholder_DefaultUseCurrentPreset".GetLocalized(),
                Width = 300,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var customParamsObj = await localSettings.ReadSettingAsync("CustomLaunchParameters");
            var customLaunchParams = customParamsObj as string;
            string customParamsDisplay = string.IsNullOrWhiteSpace(customLaunchParams) ? "None_Value".GetLocalized() : customLaunchParams;

            var contentPanel = new StackPanel { Spacing = 10 };
            contentPanel.Children.Add(new TextBlock { Text = "Msg_ChooseShortcutAction".GetLocalized(), TextWrapping = TextWrapping.Wrap });
            contentPanel.Children.Add(new TextBlock { Text = string.Format("Msg_ImportedLaunchParams_Format".GetLocalized(), customParamsDisplay), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            contentPanel.Children.Add(new TextBlock { Text = "Label_SpecifyInjectionPreset".GetLocalized(), Margin = new Thickness(0, 5, 0, 0) });
            contentPanel.Children.Add(presetComboBox);

            var choiceDialog = new ContentDialog
            {
                Title = "Title_ChooseAction".GetLocalized(),
                Content = contentPanel,
                PrimaryButtonText = "Btn_CreateDesktopShortcut".GetLocalized(),
                SecondaryButtonText = "Btn_CopyLaunchCommand".GetLocalized(),
                CloseButtonText = "CancelBtn".GetLocalized(),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var choiceResult = await choiceDialog.ShowAsync();

            if (choiceResult == ContentDialogResult.None)
            {
                return;
            }

            string presetArg = "";
            if (presetComboBox.SelectedItem is PresetModel selectedPreset)
            {
                presetArg = $" --preset {GameLauncherService.QuoteArgument(selectedPreset.Id, forceQuotes: true)}";
            }

            string customParamsArg = "";
            if (!string.IsNullOrWhiteSpace(customLaunchParams))
            {
                customParamsArg = $" {customLaunchParams}";
            }

            var argsOnly = $"--elevated-inject {GameLauncherService.QuoteArgument(finalExePath)}{presetArg} --{customParamsArg}";
            var fullCommandLine = $"{GameLauncherService.QuoteArgument(appPath ?? string.Empty)} {argsOnly}";

            if (choiceResult == ContentDialogResult.Primary)
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var shortcutPath = Path.Combine(desktopPath, "FileName_ShortcutName".GetLocalized());

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                var shortcut = shell.CreateShortcut(shortcutPath);

                shortcut.TargetPath = appPath;
                shortcut.Arguments = argsOnly;
                shortcut.WorkingDirectory = AppContext.BaseDirectory;
                shortcut.IconLocation = finalExePath + ",0";
                shortcut.Description = "Desc_ShortcutDescription".GetLocalized();

                shortcut.Save();

                using (FileStream fs = new(shortcutPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.Seek(21, SeekOrigin.Begin);
                    int b = fs.ReadByte();
                    fs.Seek(21, SeekOrigin.Begin);
                    fs.WriteByte((byte)(b | 0x20));
                }

                var dialog = new ContentDialog
                {
                    Title = "Title_ShortcutCreated".GetLocalized(),
                    Content = "Msg_ShortcutCreated".GetLocalized(),
                    CloseButtonText = "OkBtn".GetLocalized(),
                    XamlRoot = XamlRoot
                };
                await dialog.ShowAsync();
            }
            else if (choiceResult == ContentDialogResult.Secondary)
            {
                var argTextBox = new TextBox
                {
                    Text = fullCommandLine,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Height = 100,
                    AcceptsReturn = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Courier New, Monospace")
                };

                var copyDialog = new ContentDialog
                {
                    Title = "Title_LaunchCommand".GetLocalized(),
                    Content = new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Msg_LaunchCommandInstructions".GetLocalized(),
                                TextWrapping = TextWrapping.Wrap
                            },
                            argTextBox
                        }
                    },
                    PrimaryButtonText = "Btn_CopyAndClose".GetLocalized(),
                    CloseButtonText = "CloseBtn".GetLocalized(),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var copyResult = await copyDialog.ShowAsync();

                if (copyResult == ContentDialogResult.Primary)
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(fullCommandLine);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowError(string.Format("Err_OperationFailed_Format".GetLocalized(), ex.Message));
        }
    }

    #endregion
}
