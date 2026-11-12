using System.ComponentModel;
using DTTS.Core.Models;
using DTTS.ViewModels;
using DTTS.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Views;

public sealed partial class GameTablePage : Page
{
    public GameTableViewModel ViewModel { get; }

    public bool TrueValue => true;
    public bool FalseValue => false;

    public GameTablePage()
    {
        ViewModel = App.Services.GetRequiredService<GameTableViewModel>();
        InitializeComponent();

        ViewModel.Player0.PropertyChanged += OnPlayerPropertyChanged;
        ViewModel.Player1.PropertyChanged += OnPlayerPropertyChanged;
        ViewModel.Player2.PropertyChanged += OnPlayerPropertyChanged;
        ViewModel.Player3.PropertyChanged += OnPlayerPropertyChanged;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        UpdateAllPanels();
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateAllPanels();
    }

    private void UpdateAllPanels()
    {
        var p0 = ViewModel.Player0;
        Player0Panel.SetPlayerInfo(p0.Name, p0.HandCount, p0.Rank, p0.TotalScore, p0.IsTurn, p0.IsThinking, p0.IsPassed, p0.FinishPlace);

        var p1 = ViewModel.Player1;
        Player1Panel.SetPlayerInfo(p1.Name, p1.HandCount, p1.Rank, p1.TotalScore, p1.IsTurn, p1.IsThinking, p1.IsPassed, p1.FinishPlace);

        var p2 = ViewModel.Player2;
        Player2Panel.SetPlayerInfo(p2.Name, p2.HandCount, p2.Rank, p2.TotalScore, p2.IsTurn, p2.IsThinking, p2.IsPassed, p2.FinishPlace);

        var p3 = ViewModel.Player3;
        Player3Panel.SetPlayerInfo(p3.Name, p3.HandCount, p3.Rank, p3.TotalScore, p3.IsTurn, p3.IsThinking, p3.IsPassed, p3.FinishPlace);
    }

    private void OnCardClicked(object? sender, Card card)
    {
        if (sender is CardControl ctrl && ctrl.DataContext is CardViewModel vm)
        {
            vm.IsSelected = !vm.IsSelected;
        }
    }

    private void OnCardDoubleClicked(object? sender, Card card)
    {
        if (sender is CardControl ctrl && ctrl.DataContext is CardViewModel vm)
        {
            vm.IsSelected = true;
            ViewModel.PlaySelectedCardsCommand.Execute(null);
        }
    }
}

