using System;
using DTTS.Common;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace DTTS.Views;

public sealed partial class ResultPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public ResultViewModel ViewModel { get; }

    public ResultPage()
    {
        ViewModel = App.Services.GetRequiredService<ResultViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }
}
