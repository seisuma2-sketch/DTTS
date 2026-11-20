using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Core.Enums;
using DTTS.Services.Database;

namespace DTTS.ViewModels;

public sealed record MatchHistoryItemViewModel(
    int Place,
    string PlayedAtText,
    string RoundsText,
    string ScoreText
)
{
    /// <summary>順位の表示（記録がないときは「-」）。</summary>
    public string PlaceText => Place > 0 ? $"{Place}位" : "-";
}

public sealed partial class ResultViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly MatchHistoryRepository _historyRepo;

    [ObservableProperty]
    public partial int TotalGames { get; set; }

    [ObservableProperty]
    public partial string WinRateText { get; set; } = "0%";

    [ObservableProperty]
    public partial string AveragePlaceText { get; set; } = "- 位";

    [ObservableProperty]
    public partial int BestScore { get; set; }

    [ObservableProperty]
    public partial int WinStreak { get; set; }

    [ObservableProperty]
    public partial int DaifugoCount { get; set; }

    [ObservableProperty]
    public partial int FugoCount { get; set; }

    [ObservableProperty]
    public partial int HinminCount { get; set; }

    [ObservableProperty]
    public partial int DaihinminCount { get; set; }

    /// <summary>階級の獲得回数の最大値（分布の棒グラフの長さの基準。0 回のときも割り算できるよう 1 以上）。</summary>
    [ObservableProperty]
    public partial int RankMax { get; set; } = 1;

    public ObservableCollection<MatchHistoryItemViewModel> RecentMatches { get; } = new();

    public ResultViewModel(INavigationService navigation, MatchHistoryRepository historyRepo)
    {
        _navigation = navigation;
        _historyRepo = historyRepo;
    }

    public async Task InitializeAsync()
    {
        var stats = await _historyRepo.GetLocalHumanStatisticsAsync();
        TotalGames = stats.TotalGames;
        WinRateText = $"{stats.WinRate * 100:F1}%";
        AveragePlaceText = stats.TotalGames > 0 ? $"{stats.AveragePlace:F2} 位" : "- 位";
        BestScore = stats.BestScore;
        WinStreak = stats.CurrentWinStreak;

        DaifugoCount = stats.RankCounts.TryGetValue(PlayerRank.Daifugo, out int df) ? df : 0;
        FugoCount = stats.RankCounts.TryGetValue(PlayerRank.Fugo, out int fg) ? fg : 0;
        HinminCount = stats.RankCounts.TryGetValue(PlayerRank.Hinmin, out int hm) ? hm : 0;
        DaihinminCount = stats.RankCounts.TryGetValue(PlayerRank.Daihinmin, out int dh) ? dh : 0;
        RankMax = Math.Max(1, Math.Max(Math.Max(DaifugoCount, FugoCount), Math.Max(HinminCount, DaihinminCount)));

        RecentMatches.Clear();
        var recent = await _historyRepo.GetRecentAsync(15);
        foreach (var m in recent)
        {
            int place = m.LocalHuman?.Place ?? 0;
            int score = m.LocalHuman?.TotalScore ?? 0;
            string dateStr = m.PlayedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
            string roundsStr = $"{m.RoundsPlayed} ラウンド{(m.IsNetwork ? " • 通信対戦" : " • ひとりで対戦")}";
            string scoreStr = (score >= 0 ? $"+{score}" : score.ToString()) + "pt";

            RecentMatches.Add(new MatchHistoryItemViewModel(place, dateStr, roundsStr, scoreStr));
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _historyRepo.ClearAsync();
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("ResultViewModel.ClearHistory", ex);
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.NavigateToTitle();
    }
}
