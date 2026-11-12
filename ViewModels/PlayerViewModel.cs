using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Enums;

namespace DTTS.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject
{
    public int PlayerId { get; }
    public bool IsHuman { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private int _handCount;

    [ObservableProperty]
    private PlayerRank _rank = PlayerRank.Heimin;

    [ObservableProperty]
    private int _totalScore;

    [ObservableProperty]
    private bool _isTurn;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private bool _isPassed;

    [ObservableProperty]
    private int? _finishPlace;

    public PlayerViewModel(int playerId, string name, bool isHuman)
    {
        PlayerId = playerId;
        _name = name;
        IsHuman = isHuman;
    }
}
