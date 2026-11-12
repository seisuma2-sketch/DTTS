using DTTS.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Views;

public sealed partial class ResultPage : Page
{
    private readonly INavigationService _navigation;

    public ResultPage()
    {
        _navigation = App.Services.GetRequiredService<INavigationService>();
        InitializeComponent();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        _navigation.NavigateToTitle();
    }
}

