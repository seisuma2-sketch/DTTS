using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Views;

public sealed partial class TitlePage : Page
{
    public TitleViewModel ViewModel { get; }

    public TitlePage()
    {
        ViewModel = App.Services.GetRequiredService<TitleViewModel>();
        InitializeComponent();
    }
}

