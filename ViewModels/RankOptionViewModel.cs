using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Enums;

namespace DTTS.ViewModels;

/// <summary>12ボンバーの数字宣言で選ぶ数字ボタン1つ分。</summary>
public sealed partial class RankOptionViewModel : ObservableObject
{
    public RankOptionViewModel(Rank rank)
    {
        Rank = rank;
        Label = LabelOf(rank);
    }

    public Rank Rank { get; }

    /// <summary>表示名（3〜10・J・Q・K・A・2）。</summary>
    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>キーボード操作のカーソルがこのボタンにあるか。</summary>
    [ObservableProperty]
    public partial bool IsKeyboardCursor { get; set; }

    /// <summary>数字の表示名。</summary>
    public static string LabelOf(Rank rank) => rank switch
    {
        Rank.Ace => "A",
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Joker => "JK",
        _ => ((int)rank).ToString(),
    };

    /// <summary>宣言の候補を弱い順（3〜K・A・2）に並べたもの。</summary>
    public static readonly Rank[] DeclarationOrder =
    {
        Rank.Three, Rank.Four, Rank.Five, Rank.Six, Rank.Seven, Rank.Eight, Rank.Nine, Rank.Ten,
        Rank.Jack, Rank.Queen, Rank.King, Rank.Ace, Rank.Two,
    };
}
