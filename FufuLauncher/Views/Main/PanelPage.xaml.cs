using FufuLauncher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;

namespace FufuLauncher.Views;

public sealed partial class PanelPage
{
    private bool _hasConfirmedHighFps;
    private bool _isRevertingFps;
    private bool _isDialogOpen;

    public ControlPanelModel ViewModel
    {
        get;
    }

    public MainViewModel MainViewModel
    {
        get;
    }

    public PanelPage()
    {
        ViewModel = App.GetService<ControlPanelModel>();
        MainViewModel = App.GetService<MainViewModel>();
        DataContext = ViewModel;
        
        if (ViewModel.TargetFps > 120)
        {
            _hasConfirmedHighFps = true;
        }

        InitializeComponent();
    }

    private void ContentScrollViewer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        ContentScrollViewer.Focus(FocusState.Programmatic);
    }

    private void NumberBox_GotFocus(object sender, RoutedEventArgs e)
    {
    }

    private void NumberBox_LostFocus(object sender, RoutedEventArgs e)
    {
    }

    private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            string tag = selectedItem.Tag?.ToString();

            switch (tag)
            {
                case "BasicSettings":
                    BasicSettingsPage.Visibility = Visibility.Visible;
                    AdvancedSettingsPage.Visibility = Visibility.Collapsed;
                    break;

                case "AdvancedSettings":
                    BasicSettingsPage.Visibility = Visibility.Collapsed;
                    AdvancedSettingsPage.Visibility = Visibility.Visible;
                    break;
            }
        }
    }
    
    private async Task CheckFpsLimitAsync(double newValue, Action<double> resetAction)
    {
        if (!IsLoaded || _isRevertingFps || newValue <= 120 || _hasConfirmedHighFps) 
        {
            return;
        }
        
        if (_isDialogOpen || XamlRoot == null)
        {
            return;
        }

        try
        {
  
            _isDialogOpen = true;

            StackPanel contentPanel = new() { Spacing = 8 };
            
            TextBlock normalText = new()
            {
                Text = "您正在尝试将帧率设置为超过 120 FPS，可能会导致被游戏封禁或被踢出游戏，您造成的后果需自行承担！",
                TextWrapping = TextWrapping.Wrap
            };
            
            TextBlock redWarningText = new()
            {
                Text = "请确认您了解此风险并希望继续？",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };

            contentPanel.Children.Add(normalText);
            contentPanel.Children.Add(redWarningText);

            ContentDialog warningDialog = new()
            {
                Title = "警告",
                Content = contentPanel,
                PrimaryButtonText = "我了解风险，继续设置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await warningDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                _hasConfirmedHighFps = true;
            }
            else
            {
                _isRevertingFps = true;
                
                resetAction(120);
                ViewModel.TargetFps = 120;
                
                _isRevertingFps = false;
            }
        }
        catch (Exception)
        {
             _isRevertingFps = true;
             resetAction(120);
             ViewModel.TargetFps = 120;
             _isRevertingFps = false;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }
    private async void FpsSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider)
        {
            await CheckFpsLimitAsync(e.NewValue, (val) => slider.Value = val);
        }
    }

    // NumberBox 输入事件
    private async void FpsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        await CheckFpsLimitAsync(args.NewValue, (val) => sender.Value = val);
    }
}