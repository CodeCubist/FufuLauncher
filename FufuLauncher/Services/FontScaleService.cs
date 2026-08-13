/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FufuLauncher.Services;

/// <summary>
/// 全局字体缩放服务：在 0.8x ~ 1.6x 之间按比例缩放所有带 FontSize 的控件。
/// WinUI 3 不支持 DynamicResource，因此通过遍历视觉树、为每个元素写入
/// FontSize = 原值 * 缩放比例 的局部值来实现；并通过窗口/导航/容器挂接点
/// 让之后新建的页面、窗口和虚拟化列表项也能自动套用当前缩放。
/// </summary>
public static class FontScaleService
{
    public const double MinScale = 0.8;
    public const double MaxScale = 1.6;
    public const double DefaultScale = 1.0;

    /// <summary>原始字号达到该值时不再放大（原本就大的字体保持原样）。</summary>
    public const double MaxScaleFontSize = 24;

    /// <summary>缩放后的字号上限（防止放大后超出布局导致显示不全）。</summary>
    public const double MaxScaledFontSize = 22;

    private sealed class SizeHolder
    {
        public double Value;
    }

    /// <summary>当前缩放比例。</summary>
    public static double CurrentScale { get; private set; } = DefaultScale;

    private static readonly ConditionalWeakTable<DependencyObject, SizeHolder> _originalSizes = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> _hookedElements = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> _hookedContainers = new();
    private static readonly List<FrameworkElement> _roots = new();

    /// <summary>供代码中创建控件时使用：baseSize * 当前缩放。</summary>
    public static double Scaled(double baseSize) => baseSize * CurrentScale;

    /// <summary>应用新的缩放比例（自动限制在 0.8 ~ 1.6）。</summary>
    public static void Apply(double scale)
    {
        double clamped = Math.Clamp(scale, MinScale, MaxScale);
        CurrentScale = clamped;
        foreach (FrameworkElement root in _roots.ToArray())
        {
            ApplyTo(root);
        }
    }

    /// <summary>挂接一个窗口：将其内容注册为根并立即应用缩放。</summary>
    public static void HookWindow(Window window)
    {
        if (window?.Content is FrameworkElement content)
        {
            HookRoot(content);
            window.Closed += (_, _) => _roots.Remove(content);
        }
    }

    /// <summary>挂接一个根元素（窗口内容 / ContentDialog 内容）。</summary>
    public static void HookRoot(FrameworkElement? root)
    {
        if (root == null)
        {
            return;
        }
        if (!_roots.Contains(root))
        {
            _roots.Add(root);
        }
        ApplyTo(root);
        root.Loaded -= OnRootLoaded;
        root.Loaded += OnRootLoaded;
        root.Unloaded -= OnRootUnloaded;
        root.Unloaded += OnRootUnloaded;
    }

    /// <summary>挂接导航 Frame：每次导航后对新页面应用缩放。</summary>
    public static void HookFrame(Frame? frame)
    {
        if (frame == null)
        {
            return;
        }
        frame.Navigated -= OnFrameNavigated;
        frame.Navigated += OnFrameNavigated;
    }

    /// <summary>对某个根元素立即应用当前缩放。</summary>
    public static void ApplyTo(FrameworkElement root)
    {
        if (root == null)
        {
            return;
        }
        Walk(root);
    }

    private static void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            if (!_roots.Contains(fe))
            {
                _roots.Add(fe);
            }
            ApplyTo(fe);
        }
    }

    private static void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            _roots.Remove(fe);
        }
    }

    private static void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (sender is Frame frame && frame.Content is FrameworkElement page)
        {
            ApplyTo(page);
            HookLoaded(page, walkSubtree: true);
            _ = DelayWalkAsync(page);
        }
    }

    private static async Task DelayWalkAsync(FrameworkElement page)
    {
        try
        {
            await Task.Delay(250);
            page.DispatcherQueue?.TryEnqueue(() => ApplyTo(page));
        }
        catch
        {
        }
    }

    private static void Walk(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
            {
                // 注入设置页整体不参与字体缩放
                if (fe is FufuLauncher.Views.PluginSettingsPage)
                {
                    continue;
                }
                ApplyFontSize(fe);
                if (fe is ListViewBase listView)
                {
                    HookContainer(listView);
                }
                if (fe is ItemsRepeater repeater)
                {
                    HookContainer(repeater);
                }
                Walk(child);
            }
        }
    }

    private static void ApplyFontSize(FrameworkElement element)
    {
        double original;
        if (element is TextBlock textBlock)
        {
            original = GetOriginal(textBlock, textBlock.FontSize);
        }
        else if (element is FontIcon fontIcon)
        {
            original = GetOriginal(fontIcon, fontIcon.FontSize);
        }
        else if (element is RichTextBlock richTextBlock)
        {
            original = GetOriginal(richTextBlock, richTextBlock.FontSize);
        }
        else if (element is ContentPresenter contentPresenter)
        {
            original = GetOriginal(contentPresenter, contentPresenter.FontSize);
        }
        else if (element is Control control)
        {
            original = GetOriginal(control, control.FontSize);
        }
        else
        {
            return;
        }

        if (double.IsNaN(original) || original >= MaxScaleFontSize)
        {
            return;
        }

        double scaled = Math.Round(original * CurrentScale, 2);
        if (scaled > MaxScaledFontSize)
        {
            scaled = MaxScaledFontSize;
        }
        if (element is TextBlock tb)
        {
            tb.FontSize = scaled;
        }
        else if (element is FontIcon fi)
        {
            fi.FontSize = scaled;
        }
        else if (element is RichTextBlock rtb)
        {
            rtb.FontSize = scaled;
        }
        else if (element is ContentPresenter cp)
        {
            cp.FontSize = scaled;
        }
        else if (element is Control c)
        {
            c.FontSize = scaled;
        }

        HookLoaded(element);
    }

    private static double GetOriginal(DependencyObject element, double current)
    {
        if (_originalSizes.TryGetValue(element, out SizeHolder holder))
        {
            return holder.Value;
        }
        _originalSizes.Add(element, new SizeHolder { Value = current });
        return current;
    }

    private static void HookLoaded(FrameworkElement element, bool walkSubtree = false)
    {
        if (_hookedElements.TryGetValue(element, out _))
        {
            return;
        }
        _hookedElements.Add(element, new object());
        element.Loaded += (_, _) =>
        {
            if (walkSubtree)
            {
                Walk(element);
            }
            else
            {
                ApplyFontSize(element);
            }
        };
    }

    private static void HookContainer(ListViewBase listView)
    {
        if (_hookedContainers.TryGetValue(listView, out _))
        {
            return;
        }
        _hookedContainers.Add(listView, new object());
        listView.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer is FrameworkElement container)
            {
                HookLoaded(container, walkSubtree: true);
                if (container.IsLoaded)
                {
                    Walk(container);
                }
            }
        };
    }

    private static void HookContainer(ItemsRepeater repeater)
    {
        if (_hookedContainers.TryGetValue(repeater, out _))
        {
            return;
        }
        _hookedContainers.Add(repeater, new object());
        repeater.ElementPrepared += (_, args) =>
        {
            if (args.Element is FrameworkElement element)
            {
                HookLoaded(element, walkSubtree: true);
                if (element.IsLoaded)
                {
                    Walk(element);
                }
            }
        };
    }
}
