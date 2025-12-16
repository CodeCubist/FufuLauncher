using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using FufuLauncher.ViewModels;

namespace FufuLauncher.Views;

public sealed partial class PanelPage : Page
{
    public ControlPanelModel ViewModel { get; }

    public PanelPage()
    {
        ViewModel = App.GetService<ControlPanelModel>();
        DataContext = ViewModel;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
    }

    private void RootScrollViewer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Move focus to the ScrollViewer to dismiss NumberBox input focus
        RootScrollViewer.Focus(FocusState.Programmatic);
    }

    private void NumberBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Optional: handle when numberbox gets focus
    }

    private void NumberBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Optional: handle when numberbox loses focus
    }
}
