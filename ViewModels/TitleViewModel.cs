using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Core.Ai;
using DTTS.Services.Database;
using DTTS.Services.Settings;

namespace DTTS.ViewModels;

public sealed partial class TitleViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly SettingsRepository _settingsRepo;
    private readonly MatchHistoryRepository _historyRepo;

    public TitleViewModel(INavigationService navigation, SettingsRepository settingsRepo, MatchHistoryRepository historyRepo)
    {
        _navigation = navigation;
        _settingsRepo = settingsRepo;
        _historyRepo = historyRepo;
    }

    // ------------------------------------------------------------------
    // 左カラムのサマリー（通算戦績・現在の設定）
    // ------------------------------------------------------------------

    /// <summary>通算の対戦数（例：「12 戦」）。</summary>
    [ObservableProperty]
    public partial string GamesText { get; set; } = "-";

    /// <summary>1位率（例：「41.7%」）。</summary>
    [ObservableProperty]
    public partial string WinRateText { get; set; } = "-";

    /// <summary>平均順位（例：「2.15 位」）。</summary>
    [ObservableProperty]
    public partial string AveragePlaceText { get; set; } = "-";

    /// <summary>戦績の一言（まだ対戦がないときの案内など）。</summary>
    [ObservableProperty]
    public partial string StatsCaption { get; set; } = "読み込み中...";

    /// <summary>有効なローカルルールの数（例：「21 / 57」）。</summary>
    [ObservableProperty]
    public partial string RulesText { get; set; } = "-";

    /// <summary>ラウンド数（例：「5 ラウンド」）。</summary>
    [ObservableProperty]
    public partial string RoundsText { get; set; } = "-";

    /// <summary>進行スピード（例：「普通」）。</summary>
    [ObservableProperty]
    public partial string SpeedText { get; set; } = "-";

    /// <summary>戦績と設定の概要を読み込む（失敗しても画面は使える）。</summary>
    public async Task LoadSummaryAsync()
    {
        try
        {
            var stats = await _historyRepo.GetLocalHumanStatisticsAsync();
            GamesText = $"{stats.TotalGames} 戦";
            if (stats.TotalGames > 0)
            {
                WinRateText = $"{stats.WinRate * 100:F1}%";
                AveragePlaceText = $"{stats.AveragePlace:F2} 位";
                StatsCaption = stats.CurrentWinStreak > 1 ? $"{stats.CurrentWinStreak} 連勝中！" : "ひとりで対戦の通算成績";
            }
            else
            {
                WinRateText = "-";
                AveragePlaceText = "-";
                StatsCaption = "まだ対戦の記録がありません";
            }
        }
        catch (Exception ex)
        {
            StatsCaption = "戦績を読み込めませんでした";
            App.WriteCrashLog("Title.LoadStats", ex);
        }

        try
        {
            var rules = await _settingsRepo.LoadRulesAsync();
            var (enabled, total) = RuleCatalog.CountEnabled(rules);
            RulesText = $"{enabled} / {total}";
            RoundsText = $"{rules.Progress.RoundCount} ラウンド";
            var appearance = await _settingsRepo.LoadAppearanceAsync();
            SpeedText = NpcTimingOptions.SpeedName(appearance.GameSpeed);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Title.LoadSettings", ex);
        }
    }

    [RelayCommand]
    private void StartSinglePlayer()
    {
        _navigation.NavigateToGameTable();
    }

    [RelayCommand]
    private void OpenLobby()
    {
        _navigation.NavigateToLobby();
    }

    [RelayCommand]
    private void OpenRuleSettings()
    {
        _navigation.NavigateToRuleSettings();
    }

    [RelayCommand]
    private void OpenResult()
    {
        _navigation.NavigateToResult();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        _navigation.NavigateToHelp();
    }

    [RelayCommand]
    private void OpenFeedback()
    {
        _navigation.NavigateToFeedback();
    }
}
