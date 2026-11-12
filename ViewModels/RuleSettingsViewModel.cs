using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Core.Ai;
using DTTS.Core.Models;
using DTTS.Services.Database;
using DTTS.Services.Settings;
using DTTS.Services.Skin;
using Windows.Storage.Pickers;

namespace DTTS.ViewModels;

public sealed partial class RuleSettingsViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly SettingsRepository _settingsRepo;
    private readonly ICardSkinManager _skinManager;

    [ObservableProperty]
    public partial RuleSettings Rules { get; set; } = RuleSettings.CreateDefault();

    [ObservableProperty]
    public partial AppearanceSettings Appearance { get; set; } = new();

    [ObservableProperty]
    public partial NpcSettings Npc { get; set; } = new();

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    public string Seat1Name
    {
        get => Npc.Seats.Count > 0 ? Npc.Seats[0].Name : "";
        set { if (Npc.Seats.Count > 0) { Npc.Seats[0].Name = value; OnPropertyChanged(); } }
    }

    public string Seat2Name
    {
        get => Npc.Seats.Count > 1 ? Npc.Seats[1].Name : "";
        set { if (Npc.Seats.Count > 1) { Npc.Seats[1].Name = value; OnPropertyChanged(); } }
    }

    public string Seat3Name
    {
        get => Npc.Seats.Count > 2 ? Npc.Seats[2].Name : "";
        set { if (Npc.Seats.Count > 2) { Npc.Seats[2].Name = value; OnPropertyChanged(); } }
    }

    public IReadOnlyList<ThemeColorInfo> AvailableThemes => ThemeColorInfo.All;

    public ObservableCollection<SkinPackInfo> AvailableSkins { get; } = new();

    public RuleSettingsViewModel(
        INavigationService navigation,
        SettingsRepository settingsRepo,
        ICardSkinManager skinManager)
    {
        _navigation = navigation;
        _settingsRepo = settingsRepo;
        _skinManager = skinManager;
    }

    public async Task InitializeAsync()
    {
        Rules = await _settingsRepo.LoadRulesAsync();
        Appearance = await _settingsRepo.LoadAppearanceAsync();
        Npc = await _settingsRepo.LoadNpcAsync();
        OnPropertyChanged(nameof(Seat1Name));
        OnPropertyChanged(nameof(Seat2Name));
        OnPropertyChanged(nameof(Seat3Name));

        RefreshSkins();
    }

    private void RefreshSkins()
    {
        AvailableSkins.Clear();
        foreach (var p in _skinManager.AvailablePacks)
        {
            AvailableSkins.Add(p);
        }
    }

    [RelayCommand]
    private void SelectTheme(ThemeColor color)
    {
        Appearance.ThemeColor = color;
        ThemeManager.ApplyTheme(color);
        _ = SaveAppearanceAsync();
    }

    [RelayCommand]
    private void SelectSkin(string? packId)
    {
        Appearance.SkinPackId = packId;
        _skinManager.SetActivePack(packId);
        _ = SaveAppearanceAsync();
    }

    [RelayCommand]
    private async Task ImportSkinZipAsync(string zipPath)
    {
        if (File.Exists(zipPath))
        {
            try
            {
                var pack = _skinManager.Install(zipPath);
                RefreshSkins();
                SelectSkin(pack.Id);
                StatusMessage = $"スキンパック「{pack.Name}」をインストールしました";
            }
            catch (Exception ex)
            {
                StatusMessage = $"スキン読み込みエラー: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        await _settingsRepo.SaveRulesAsync(Rules);
        await _settingsRepo.SaveAppearanceAsync(Appearance);
        await _settingsRepo.SaveNpcAsync(Npc);
        StatusMessage = "すべての設定を保存しました";
    }

    [RelayCommand]
    private async Task ResetRulesToDefaultAsync()
    {
        Rules = RuleSettings.CreateDefault();
        await _settingsRepo.SaveRulesAsync(Rules);
        StatusMessage = "ルールを標準設定に初期化しました";
    }

    private async Task SaveAppearanceAsync()
    {
        await _settingsRepo.SaveAppearanceAsync(Appearance);
    }

    [RelayCommand]
    private void GoBack()
    {
        _ = SaveAllAsync();
        _navigation.NavigateToTitle();
    }
}

