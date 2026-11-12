using DTTS.Common;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Views;

public sealed partial class HelpPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public HelpViewModel ViewModel { get; }

    public HelpPage()
    {
        ViewModel = App.Services.GetRequiredService<HelpViewModel>();
        InitializeComponent();
    }
}
