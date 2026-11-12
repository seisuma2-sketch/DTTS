using System;
using DTTS.Common;
using DTTS.Services.Network;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace DTTS.Views;

public sealed partial class LobbyPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public LobbyViewModel ViewModel { get; }

    public LobbyPage()
    {
        ViewModel = App.Services.GetRequiredService<LobbyViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private async void OnJoinRoomClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is RoomInfo room)
        {
            await ViewModel.JoinRoomCommand.ExecuteAsync(room);
        }
    }
}

