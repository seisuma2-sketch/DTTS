using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Models;

namespace DTTS.ViewModels;

public sealed partial class CardViewModel : ObservableObject
{
    public Card Card { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _canPlay = true;

    public CardViewModel(Card card)
    {
        Card = card;
    }
}

