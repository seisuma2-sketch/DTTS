using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Models;

namespace DTTS.ViewModels;

public sealed partial class CardViewModel : ObservableObject
{
    public Card Card { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool CanPlay { get; set; } = true;

    public CardViewModel(Card card)
    {
        Card = card;
    }
}

