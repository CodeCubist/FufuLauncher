/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using FufuLauncher.Helpers;
using FufuLauncher.Messages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace FufuLauncher.Views;

public sealed partial class DesktopNotificationWindow : WindowEx
{
    private const int LogicalWidth = 410;
    private const int MinimumLogicalHeight = 180;
    private const int MaximumLogicalHeight = 470;
    private const int LogicalHeightPerAdditionalNotification = 110;
    private const int NotificationDurationMilliseconds = 2000;
    private const int DesktopMargin = 16;
    private bool _allowClose;

    public DesktopNotificationWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        IsShownInSwitchers = false;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.Closing += AppWindow_Closing;
    }

    public void ShowNotification(NotificationMessage message)
    {
        var infoBar = CreateInfoBar(message);
        NotificationPanel.Children.Insert(0, infoBar);

        PositionAtDesktopBottomRight();
        AppWindow.Show(false);
        PlayEntranceAnimation(infoBar);

        SetupAutoDismiss(infoBar, NotificationDurationMilliseconds);
    }

    public void CloseForAppExit()
    {
        _allowClose = true;
        Close();
    }

    private void PositionAtDesktopBottomRight()
    {
        var logicalHeight = Math.Min(
            MaximumLogicalHeight,
            MinimumLogicalHeight + Math.Max(0, NotificationPanel.Children.Count - 1) * LogicalHeightPerAdditionalNotification);

        WindowManagerHelper.ResizeWithDpi(AppWindow, this, LogicalWidth, logicalHeight);

        var displayArea = DisplayArea.Primary;
        var workArea = displayArea.WorkArea;
        var size = AppWindow.Size;

        AppWindow.Move(new PointInt32(
            workArea.X + workArea.Width - size.Width - DesktopMargin,
            workArea.Y + workArea.Height - size.Height - DesktopMargin));
    }

    private InfoBar CreateInfoBar(NotificationMessage message)
    {
        var infoBar = new InfoBar
        {
            Title = message.Title,
            Message = message.Message,
            Severity = GetInfoBarSeverity(message.Type),
            IsOpen = true,
            IsClosable = true,
            Margin = new Thickness(0, 0, 0, 8),
            RenderTransform = new TranslateTransform { X = 380 },
            Opacity = 0
        };

        infoBar.Closing += (_, args) =>
        {
            args.Cancel = true;
            DismissInfoBar(infoBar);
        };

        return infoBar;
    }

    private static InfoBarSeverity GetInfoBarSeverity(NotificationType type)
    {
        return type switch
        {
            NotificationType.Success => InfoBarSeverity.Success,
            NotificationType.Warning => InfoBarSeverity.Warning,
            NotificationType.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };
    }

    private static void PlayEntranceAnimation(FrameworkElement element)
    {
        var transformAnimation = new DoubleAnimation
        {
            From = 380,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(transformAnimation, element.RenderTransform);
        Storyboard.SetTargetProperty(transformAnimation, "X");

        var opacityAnimation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(300))
        };
        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(transformAnimation);
        storyboard.Children.Add(opacityAnimation);
        storyboard.Begin();
    }

    private void SetupAutoDismiss(FrameworkElement element, int duration)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(duration);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => DismissInfoBar(element);
        timer.Start();
    }

    private void DismissInfoBar(FrameworkElement element)
    {
        if (element.Tag is string state && state == "Closing")
        {
            return;
        }

        element.Tag = "Closing";
        element.IsHitTestVisible = false;

        var transformAnimation = new DoubleAnimation
        {
            From = 0,
            To = 380,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(transformAnimation, element.RenderTransform);
        Storyboard.SetTargetProperty(transformAnimation, "X");

        var opacityAnimation = new DoubleAnimation
        {
            From = element.Opacity,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250))
        };
        Storyboard.SetTarget(opacityAnimation, element);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(transformAnimation);
        storyboard.Children.Add(opacityAnimation);
        storyboard.Completed += (_, _) =>
        {
            NotificationPanel.Children.Remove(element);
            if (NotificationPanel.Children.Count == 0)
            {
                AppWindow.Hide();
            }
            else
            {
                PositionAtDesktopBottomRight();
            }
        };
        storyboard.Begin();
    }

    private void ClearAllNotifications_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in NotificationPanel.Children.OfType<FrameworkElement>().ToList())
        {
            DismissInfoBar(child);
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        AppWindow.Hide();
    }
}
