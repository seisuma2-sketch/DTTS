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

    /// <summary>キーボード操作のカーソルがこのカードにあるか（表示専用）。</summary>
    [ObservableProperty]
    public partial bool IsKeyboardCursor { get; set; }

    public CardViewModel(Card card)
    {
        Card = card;
    }
}

