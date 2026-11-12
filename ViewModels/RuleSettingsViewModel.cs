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
using DTTS.Services.Audio;
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
    private readonly IAudioService _audio;
    private bool _appearanceSaveRunning;
    private bool _appearanceSaveQueued;

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

    // --- サウンド（変更は即座に再生音量へ反映し、保存する） ---

    /// <summary>効果音の有効・無効。</summary>
    public bool SoundEnabled
    {
        get => Appearance.SoundEnabled;
        set
        {
            if (Appearance.SoundEnabled == value) return;
            Appearance.SoundEnabled = value;
            _audio.SetSoundEnabled(value);
            OnPropertyChanged();
            _ = SaveAppearanceAsync();
        }
    }

    /// <summary>効果音の音量（0〜100）。</summary>
    public double SeVolume
    {
        get => Appearance.SeVolume;
        set
        {
            int v = (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100));
            if (Appearance.SeVolume == v) return;
            Appearance.SeVolume = v;
            _audio.SetSeVolume(AppearanceSettings.ToUnitVolume(v));
            OnPropertyChanged();
            _ = SaveAppearanceAsync();
        }
    }

    /// <summary>BGM の音量（0〜100）。BGM は未実装で、値の保存のみ。</summary>
    public double BgmVolume
    {
        get => Appearance.BgmVolume;
        set
        {
            int v = (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100));
            if (Appearance.BgmVolume == v) return;
            Appearance.BgmVolume = v;
            _audio.SetBgmVolume(AppearanceSettings.ToUnitVolume(v));
            OnPropertyChanged();
            _ = SaveAppearanceAsync();
        }
    }

    public RuleSettingsViewModel(
        INavigationService navigation,
        SettingsRepository settingsRepo,
        ICardSkinManager skinManager,
        IAudioService audio)
    {
        _navigation = navigation;
        _settingsRepo = settingsRepo;
        _skinManager = skinManager;
        _audio = audio;
    }

    /// <summary>現在の音量で効果音を試聴する。</summary>
    [RelayCommand]
    private void PreviewSound()
    {
        _audio.Play(SoundEffect.CardPlay);
    }

    public async Task InitializeAsync()
    {
        Rules = await _settingsRepo.LoadRulesAsync();
        Appearance = await _settingsRepo.LoadAppearanceAsync();
        Npc = await _settingsRepo.LoadNpcAsync();
        OnPropertyChanged(nameof(SoundEnabled));
        OnPropertyChanged(nameof(SeVolume));
        OnPropertyChanged(nameof(BgmVolume));
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

    /// <summary>
    /// 外見設定を保存する。スライダー操作などで連続して呼ばれた場合は、
    /// 保存中に来た要求を1回にまとめ、最後の値を必ず保存する。
    /// </summary>
    private async Task SaveAppearanceAsync()
    {
        if (_appearanceSaveRunning)
        {
            _appearanceSaveQueued = true;
            return;
        }
        _appearanceSaveRunning = true;
        try
        {
            do
            {
                _appearanceSaveQueued = false;
                await _settingsRepo.SaveAppearanceAsync(Appearance);
            }
            while (_appearanceSaveQueued);
        }
        catch (Exception ex)
        {
            StatusMessage = $"設定の保存に失敗しました: {ex.Message}";
        }
        finally
        {
            _appearanceSaveRunning = false;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _ = SaveAllAsync();
        _navigation.NavigateToTitle();
    }
}

