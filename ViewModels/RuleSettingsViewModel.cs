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

/// <summary>マイプリセットの一覧の1行。</summary>
/// <param name="Name">プリセット名。</param>
/// <param name="Summary">内容の要約（有効なルールの数・ラウンド数・保存日時）。</param>
public sealed record CustomPresetItemViewModel(string Name, string Summary);

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

    /// <summary>ローカルルールのカテゴリ（折りたたみカード）。<see cref="Rules"/> を差し替えると作り直す。</summary>
    public ObservableCollection<RuleCategoryViewModel> RuleCategories { get; } = new();

    /// <summary>有効なローカルルールの合計（例：「有効 18 / 60」）。</summary>
    [ObservableProperty]
    public partial string RuleSummaryText { get; set; } = "";

    partial void OnRulesChanged(RuleSettings value)
    {
        RuleCategories.Clear();
        foreach (var category in RuleCatalog.Build(value, OnRuleToggled))
        {
            RuleCategories.Add(category);
        }
        OnPropertyChanged(nameof(RoundCount));
        UpdateRuleSummary();
    }

    // ------------------------------------------------------------------
    // ルール詳細ガイド（右ペイン）
    // ------------------------------------------------------------------

    /// <summary>右ペインに表示しているルールの詳しい説明（左でホバー・フォーカス・クリックした項目）。</summary>
    [ObservableProperty]
    public partial RuleGuide SelectedGuide { get; set; } = RuleGuide.Placeholder;

    /// <summary>ガイドに表示する項目を切り替える。</summary>
    public void ShowGuide(RuleGuide? guide)
    {
        if (guide is not null && !ReferenceEquals(guide, SelectedGuide))
        {
            SelectedGuide = guide;
        }
    }

    // ------------------------------------------------------------------
    // 進行スピード（変更は即座に保存。対戦画面のヘッダーからも変えられる）
    // ------------------------------------------------------------------

    /// <summary>進行スピードの選択肢（ゆっくり・普通・速い・超速）。</summary>
    public IReadOnlyList<string> SpeedOptions { get; } =
        Enum.GetValues<GameSpeed>().Select(NpcTimingOptions.SpeedName).ToArray();

    /// <summary>選んでいる進行スピードの番号。</summary>
    public int SpeedIndex
    {
        get => (int)Appearance.GameSpeed;
        set
        {
            // コンボボックスは項目の読み込み中に -1 を書き戻すことがある。範囲外の値は無視する
            if (value < 0 || value >= SpeedOptions.Count || (int)Appearance.GameSpeed == value) return;
            Appearance.GameSpeed = (GameSpeed)value;
            OnPropertyChanged();
            StatusMessage = $"進行スピードを「{SpeedOptions[value]}」にしました";
            _ = SaveAppearanceAsync();
        }
    }

    // ------------------------------------------------------------------
    // マイプリセット（現在のルール構成に名前を付けて保存・読み込み・削除）
    // ------------------------------------------------------------------

    private CustomRulePresetList _presetStore = new();

    /// <summary>保存済みのマイプリセット（新しい順）。</summary>
    public ObservableCollection<CustomPresetItemViewModel> CustomPresets { get; } = new();

    /// <summary>マイプリセットが1件もないか（空の案内を出す）。</summary>
    public bool HasNoCustomPresets => CustomPresets.Count == 0;

    /// <summary>保存するプリセットの名前（入力欄）。</summary>
    [ObservableProperty]
    public partial string NewPresetName { get; set; } = "";

    /// <summary>マイプリセットの操作結果（保存しました・読み込みました など）。</summary>
    [ObservableProperty]
    public partial string PresetStatus { get; set; } = "";

    private void RefreshCustomPresets()
    {
        CustomPresets.Clear();
        foreach (var preset in _presetStore.Presets)
        {
            var rules = preset.ToRules();
            if (rules is null) continue;
            var (enabled, total) = RuleCatalog.CountEnabled(rules);
            string saved = preset.SavedAt == default ? "" : $" ・ {preset.SavedAt.ToLocalTime():M/d HH:mm} 保存";
            CustomPresets.Add(new CustomPresetItemViewModel(
                preset.Name,
                $"有効 {enabled} / {total} ・ {rules.Progress.RoundCount}ラウンド{saved}"));
        }
        OnPropertyChanged(nameof(HasNoCustomPresets));
    }

    /// <summary>現在のルール構成を、入力した名前でマイプリセットに保存する（同じ名前は上書き）。</summary>
    [RelayCommand]
    private async Task SaveCurrentAsPresetAsync()
    {
        var name = CustomRulePresetList.SanitizeName(NewPresetName);
        if (name is null)
        {
            PresetStatus = "プリセット名を入力してください";
            return;
        }
        bool overwrite = _presetStore.Presets.Any(p => p.Name == name);
        if (_presetStore.Upsert(name, Rules, DateTimeOffset.Now) is null)
        {
            PresetStatus = $"マイプリセットは {CustomRulePresetList.MaxCount} 件までです。不要なものを削除してください";
            return;
        }
        try
        {
            await _settingsRepo.SaveRulePresetsAsync(_presetStore);
            PresetStatus = overwrite ? $"「{name}」を現在のルールで上書きしました" : $"「{name}」を保存しました";
            NewPresetName = "";
        }
        catch (Exception ex)
        {
            PresetStatus = $"保存に失敗しました: {ex.Message}";
        }
        RefreshCustomPresets();
    }

    /// <summary>マイプリセットを読み込み、現在のルールとして編集できるようにする。</summary>
    [RelayCommand]
    private void LoadCustomPreset(CustomPresetItemViewModel? item)
    {
        var rules = item is null ? null : _presetStore.Presets.FirstOrDefault(p => p.Name == item.Name)?.ToRules();
        if (item is null || rules is null)
        {
            PresetStatus = "プリセットを読み込めませんでした";
            return;
        }
        Rules = rules;
        PresetStatus = $"「{item.Name}」を読み込みました";
        StatusMessage = $"マイプリセット「{item.Name}」を適用しました（「設定を保存」またはタイトルへ戻ると保存されます）";
    }

    /// <summary>マイプリセットを削除する（確認はページ側で行う）。</summary>
    [RelayCommand]
    private async Task DeleteCustomPresetAsync(CustomPresetItemViewModel? item)
    {
        if (item is null || !_presetStore.Remove(item.Name)) return;
        try
        {
            await _settingsRepo.SaveRulePresetsAsync(_presetStore);
            PresetStatus = $"「{item.Name}」を削除しました";
        }
        catch (Exception ex)
        {
            PresetStatus = $"削除を保存できませんでした: {ex.Message}";
        }
        RefreshCustomPresets();
    }

    private void OnRuleToggled()
    {
        UpdateRuleSummary();
        StatusMessage = "ルールを変更しました（「設定を保存」またはタイトルへ戻ると保存されます）";
    }

    private void UpdateRuleSummary()
    {
        int on = RuleCategories.Sum(c => c.Items.Count(i => i.IsOn));
        int all = RuleCategories.Sum(c => c.Items.Count);
        RuleSummaryText = $"有効 {on} / {all}";
    }

    /// <summary>1ゲームのラウンド数（1〜20）。</summary>
    public double RoundCount
    {
        get => Rules.Progress.RoundCount;
        set
        {
            int v = (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 1, 1, 20));
            if (Rules.Progress.RoundCount == v) return;
            Rules.Progress.RoundCount = v;
            OnPropertyChanged();
            OnRuleToggled();
        }
    }

    /// <summary>プリセット（初心者用・中級者用・完全上級者）を適用する。ラウンド数はそのまま引き継ぐ。</summary>
    [RelayCommand]
    private void ApplyPreset(string? name)
    {
        if (!Enum.TryParse<RulePreset>(name, out var preset) || !Enum.IsDefined(preset)) return;
        Rules = RulePresets.Create(preset, Rules);
        StatusMessage = $"プリセット「{RulePresets.DisplayName(preset)}」を適用しました（「設定を保存」またはタイトルへ戻ると保存されます）";
    }

    /// <summary>すべてのカテゴリを開く・閉じる。</summary>
    [RelayCommand]
    private void SetAllCategoriesExpanded(string? expand)
    {
        bool open = string.Equals(expand, "true", StringComparison.OrdinalIgnoreCase);
        foreach (var c in RuleCategories)
        {
            c.IsExpanded = open;
        }
    }

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
        OnPropertyChanged(nameof(SpeedIndex));
        try
        {
            _presetStore = await _settingsRepo.LoadRulePresetsAsync();
        }
        catch (Exception ex)
        {
            _presetStore = new CustomRulePresetList();
            PresetStatus = $"マイプリセットを読み込めませんでした: {ex.Message}";
        }
        RefreshCustomPresets();
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

