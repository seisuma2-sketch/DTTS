using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Enums;

namespace DTTS.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject
{
    public int PlayerId { get; }
    public bool IsHuman { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial int HandCount { get; set; }

    [ObservableProperty]
    public partial PlayerRank Rank { get; set; } = PlayerRank.Heimin;

    [ObservableProperty]
    public partial int TotalScore { get; set; }

    [ObservableProperty]
    public partial bool IsTurn { get; set; }

    [ObservableProperty]
    public partial bool IsThinking { get; set; }

    [ObservableProperty]
    public partial bool IsPassed { get; set; }

    [ObservableProperty]
    public partial int? FinishPlace { get; set; }

    /// <summary>アバター（性格を表す線画アイコン。Segoe Fluent Icons）。</summary>
    [ObservableProperty]
    public partial string AvatarGlyph { get; set; } = "\uE77B";

    /// <summary>アバターの説明（性格の名前など。読み上げ・ツールチップ用）。</summary>
    [ObservableProperty]
    public partial string AvatarLabel { get; set; } = "プレイヤー";

    public PlayerViewModel(int playerId, string name, bool isHuman)
    {
        PlayerId = playerId;
        Name = name;
        IsHuman = isHuman;
    }
}
