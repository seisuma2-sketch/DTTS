using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Core.Models;
using DTTS.Services.Database;
using DTTS.Services.Network;
using DTTS.Services.Settings;
using Microsoft.UI.Dispatching;

namespace DTTS.ViewModels;

public sealed record NetworkGameParams(
    GameHostSession? HostSession,
    RemoteGameClient? Client,
    RuleSettings Rules
);

public sealed partial class LobbyViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly INetworkService _networkService;
    private readonly SettingsRepository _settingsRepo;
    private readonly DispatcherQueue _dispatcherQueue;

    private GameHostSession? _hostSession;
    private RemoteGameClient? _client;
    private RuleSettings _rules = RuleSettings.CreateDefault();
    private bool _isInitializing;
    private bool _isNetworkBusy;
    private bool _handedOffToGame;
    private bool _isLeftPage;
    private readonly SemaphoreSlim _nameSaveLock = new(1, 1);
    private string _latestPlayerName = "あなた";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuestWaiting))]
    public partial bool IsInRoom { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuestWaiting))]
    public partial bool IsHost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuestWaiting))]
    public partial bool IsWindowSuspended { get; set; }

    /// <summary>ゲストとして部屋に入りホストの開始を待っているか（待機スピナーの稼働条件）。</summary>
    public bool IsGuestWaiting => IsInRoom && !IsHost && !IsWindowSuspended;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthCodeSummary))]
    public partial string AuthCodeSummary { get; set; } = "";

    /// <summary>MITM 検証用の 6 桁確認コードが表示対象か。</summary>
    public bool HasAuthCodeSummary => !string.IsNullOrEmpty(AuthCodeSummary);

    [ObservableProperty]
    public partial string RoomTitle { get; set; } = "";

    [ObservableProperty]
    public partial string HostRoomName { get; set; } = "DTTSの対戦部屋";

    [ObservableProperty]
    public partial string DirectJoinHost { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial int DirectJoinPort { get; set; } = NetProtocol.DefaultTcpPort;

    [ObservableProperty]
    public partial string PlayerName { get; set; } = "あなた";

    [ObservableProperty]
    public partial string SelectedIconId { get; set; } = "default";

    [ObservableProperty]
    public partial string? CustomAvatarPath { get; set; }

    [ObservableProperty]
    public partial ThemeColor SelectedThemeColor { get; set; } = ThemeColor.DeepAzure;

    [ObservableProperty]
    public partial RevolutionBgColor SelectedRevolutionBg { get; set; } = RevolutionBgColor.DeepCrimson;

    public int SelectedRevolutionBgIndex
    {
        get => (int)SelectedRevolutionBg;
        set
        {
            if (Enum.IsDefined((RevolutionBgColor)value))
            {
                var bg = (RevolutionBgColor)value;
                if (SelectedRevolutionBg != bg)
                {
                    SelectedRevolutionBg = bg;
                    OnPropertyChanged();
                }
            }
        }
    }

    [ObservableProperty]
    public partial string PresetNameInput { get; set; } = "";

    [ObservableProperty]
    public partial string RulesSummaryText { get; set; } = "標準ルール";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    public ObservableCollection<SeatInfo> Seats { get; } = new();
    public ObservableCollection<RoomInfo> DiscoveredRooms { get; } = new();
    public ObservableCollection<CustomRulePreset> SavedPresets { get; } = new();

    public IReadOnlyList<ThemeColorInfo> AvailableThemeColors => ThemeColorInfo.All;
    public IReadOnlyList<RevolutionBgColorInfo> AvailableRevolutionBgColors => RevolutionBgColorInfo.All;
    public IReadOnlyList<PlayerIconInfo> AvailablePlayerIcons => PlayerIconInfo.All;

    public LobbyViewModel(
        INavigationService navigation,
        INetworkService networkService,
        SettingsRepository settingsRepo)
    {
        _navigation = navigation;
        _networkService = networkService;
        _settingsRepo = settingsRepo;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync(bool autoHost = false)
    {
        _isInitializing = true;
        try
        {
            _rules = await _settingsRepo.LoadRulesAsync();
            var npc = await _settingsRepo.LoadNpcAsync();
            var appearance = await _settingsRepo.LoadAppearanceAsync();
            var presets = await _settingsRepo.LoadRulePresetsAsync();

            PlayerName = npc.PlayerName;
            SelectedIconId = appearance.PlayerIconId;
            CustomAvatarPath = appearance.CustomAvatarPath;
            SelectedThemeColor = appearance.ThemeColor;
            SelectedRevolutionBg = appearance.RevolutionBgColor;
            OnPropertyChanged(nameof(SelectedRevolutionBgIndex));
            UpdateRulesSummary();

            SavedPresets.Clear();
            foreach (var p in presets.Presets)
            {
                SavedPresets.Add(p);
            }
        }
        finally
        {
            _isInitializing = false;
        }

        RefreshLocalAddresses();

        if (autoHost)
        {
            await HostRoomAsync();
        }
        else
        {
            await SearchRoomsAsync();
        }
    }

    partial void OnPlayerNameChanged(string value)
    {
        if (_isInitializing) return;
        _latestPlayerName = value;
        _ = SavePlayerNameAsync();
    }

    private async Task SavePlayerNameAsync()
    {
        try
        {
            await _nameSaveLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            var clean = string.IsNullOrWhiteSpace(_latestPlayerName) ? "あなた" : _latestPlayerName.Trim();
            var npc = await _settingsRepo.LoadNpcAsync();
            npc.PlayerName = clean;
            await _settingsRepo.SaveNpcAsync(npc);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.SavePlayerName", ex);
        }
        finally
        {
            try { _nameSaveLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    [RelayCommand]
    private async Task SelectPlayerIconAsync(string? iconId)
    {
        try
        {
            var resolved = PlayerIconInfo.Get(iconId).Id;
            SelectedIconId = resolved;
            await _settingsRepo.UpdateAppearanceAsync(a => a.PlayerIconId = resolved);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.SelectPlayerIcon", ex);
            StatusMessage = "アイコンの保存に失敗しました";
        }
    }

    public async Task SetCustomAvatarAsync(string? filePath)
    {
        try
        {
            var normalized = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim();
            CustomAvatarPath = normalized;
            await _settingsRepo.UpdateAppearanceAsync(a => a.CustomAvatarPath = normalized);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.SetCustomAvatar", ex);
            StatusMessage = "アバター画像の保存に失敗しました";
        }
    }

    [RelayCommand]
    private Task ClearCustomAvatarAsync() => SetCustomAvatarAsync(null);

    [RelayCommand]
    private async Task SelectThemeColorAsync(ThemeColor color)
    {
        try
        {
            SelectedThemeColor = color;
            ThemeManager.ApplyTheme(color);
            await _settingsRepo.UpdateAppearanceAsync(a => a.ThemeColor = color);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.SelectThemeColor", ex);
            StatusMessage = "テーマカラーの保存に失敗しました";
        }
    }

    partial void OnSelectedRevolutionBgChanged(RevolutionBgColor value)
    {
        if (_isInitializing) return;
        _ = SaveRevolutionBgAsync(value);
    }

    private async Task SaveRevolutionBgAsync(RevolutionBgColor value)
    {
        try
        {
            await _settingsRepo.UpdateAppearanceAsync(a => a.RevolutionBgColor = value);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.SaveRevolutionBg", ex);
        }
    }

    [RelayCommand]
    private async Task ApplyBuiltinPresetAsync(string? key)
    {
        try
        {
            _rules = key switch
            {
                "simple" => RulePresets.Create(RulePreset.Beginner, _rules),
                "all" => RulePresets.Create(RulePreset.Expert, _rules),
                _ => RulePresets.Create(RulePreset.Intermediate, _rules),
            };
            await _settingsRepo.SaveRulesAsync(_rules);
            UpdateRulesSummary();
            StatusMessage = $"ルールプリセット「{RulesSummaryText}」を適用しました";
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.ApplyBuiltinPreset", ex);
            StatusMessage = "ルールプリセットの適用に失敗しました";
        }
    }

    [RelayCommand]
    private async Task ApplyCustomPresetAsync(CustomRulePreset? preset)
    {
        if (preset is null) return;
        try
        {
            if (preset.ToRules() is { } loaded)
            {
                _rules = loaded;
                await _settingsRepo.SaveRulesAsync(_rules);
            }
            if (preset.ThemeColor is { } tc)
            {
                await SelectThemeColorAsync(tc);
            }
            if (preset.RevolutionBgColor is { } rbg)
            {
                SelectedRevolutionBg = rbg;
            }
            UpdateRulesSummary();
            StatusMessage = $"プリセット「{preset.Name}」を適用しました";
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.ApplyCustomPreset", ex);
            StatusMessage = "カスタムプリセットの適用に失敗しました";
        }
    }

    [RelayCommand]
    private async Task SaveRoomPresetAsync()
    {
        try
        {
            var list = await _settingsRepo.LoadRulePresetsAsync();
            var name = string.IsNullOrWhiteSpace(PresetNameInput) ? HostRoomName : PresetNameInput;
            var saved = list.Upsert(name, _rules, DateTimeOffset.UtcNow, SelectedThemeColor, SelectedRevolutionBg);
            if (saved is null)
            {
                StatusMessage = "プリセット名を入力してください（最大30件まで保存できます）";
                return;
            }
            await _settingsRepo.SaveRulePresetsAsync(list);
            PresetNameInput = "";
            SavedPresets.Clear();
            foreach (var p in list.Presets)
            {
                SavedPresets.Add(p);
            }
            StatusMessage = $"テーマとルールをプリセット「{saved.Name}」として保存しました";
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.SaveRoomPreset", ex);
            StatusMessage = "プリセットの保存に失敗しました";
        }
    }

    [RelayCommand]
    private async Task DeleteCustomPresetAsync(CustomRulePreset? preset)
    {
        if (preset is null) return;
        try
        {
            var list = await _settingsRepo.LoadRulePresetsAsync();
            if (list.Remove(preset.Name))
            {
                await _settingsRepo.SaveRulePresetsAsync(list);
                SavedPresets.Remove(preset);
                StatusMessage = $"プリセット「{preset.Name}」を削除しました";
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("Lobby.DeleteCustomPreset", ex);
            StatusMessage = "プリセットの削除に失敗しました";
        }
    }

    [RelayCommand]
    private void OpenRuleSettings()
    {
        _navigation.NavigateToRuleSettings();
    }

    private void UpdateRulesSummary()
    {
        var tags = new List<string>();
        if (_rules.Revolution.Enabled) tags.Add("革命");
        if (_rules.Flow.EightCutEnabled) tags.Add("8切り");
        if (_rules.ElevenBack.Enabled) tags.Add("11バック");
        if (_rules.Lock.SuitLockEnabled) tags.Add("縛り");
        if (_rules.Special.TwelveBomberEnabled) tags.Add("12ボンバー");
        RulesSummaryText = tags.Count > 0
            ? $"{_rules.Progress.RoundCount}ラウンド・{string.Join(" / ", tags)}"
            : $"{_rules.Progress.RoundCount}ラウンド・基本";
    }

    [RelayCommand]
    private async Task SearchRoomsAsync()
    {
        if (_isNetworkBusy) return;
        _isNetworkBusy = true;
        IsSearching = true;
        StatusMessage = "近くの Bluetooth LE の部屋を探索中...";
        DiscoveredRooms.Clear();

        try
        {
            var rooms = await _networkService.DiscoverRoomsAsync(TimeSpan.FromSeconds(2));
            foreach (var r in rooms)
            {
                DiscoveredRooms.Add(r);
            }
            StatusMessage = rooms.Count > 0 ? $"{rooms.Count}件の部屋が見つかりました" : "部屋が見つかりませんでした";
        }
        catch (Exception ex)
        {
            StatusMessage = $"探索エラー: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
            _isNetworkBusy = false;
        }
    }

    [RelayCommand]
    private async Task HostRoomAsync()
    {
        if (_isNetworkBusy) return;
        _isNetworkBusy = true;
        StatusMessage = "部屋を立ち上げています...";
        try
        {
            await CloseExistingSessionsAsync();
            var appearance = await _settingsRepo.LoadAppearanceAsync();
            var cleanName = string.IsNullOrWhiteSpace(PlayerName) ? "あなた" : PlayerName.Trim();
            var options = new HostOptions
            {
                RoomName = HostRoomName,
                HostPlayerName = cleanName,
                HostAvatarIcon = SelectedIconId,
                Port = NetProtocol.DefaultTcpPort,
                HostAlwaysAskInterrupt = appearance.GuideLevel.AlwaysAsksInterrupt(),
                JoinPin = EffectiveHostPin,
            };

            var session = await _networkService.HostAsync(options);
            if (_isLeftPage)
            {
                await session.DisposeAsync();
                return;
            }
            _hostSession = session;
            IsHost = true;
            IsInRoom = true;
            RoomTitle = $"{HostRoomName} (Bluetooth LE)";

            _hostSession.LobbyChanged += OnHostLobbyChanged;
            UpdateSeats(_hostSession.Seats);
            RefreshLocalAddresses();

            StatusMessage = EffectiveHostPin is { } pin
                ? $"プレイヤーの参加を待っています...（参加 PIN：{pin}）"
                : "プレイヤーの参加を待っています...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"ホスト起動エラー: {ex.Message}";
        }
        finally
        {
            _isNetworkBusy = false;
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync(RoomInfo? room)
    {
        if (room == null || string.IsNullOrEmpty(room.Address)) return;
        await JoinByAddressAsync(room.Address, room.Port);
    }

    [RelayCommand]
    private async Task DirectJoinAsync()
    {
        if (string.IsNullOrWhiteSpace(DirectJoinHost)) return;
        await JoinByAddressAsync(DirectJoinHost.Trim(), DirectJoinPort);
    }

    private async Task JoinByAddressAsync(string host, int port)
    {
        if (_isNetworkBusy) return;
        _isNetworkBusy = true;
        string displayTarget = BleProtocol.TryParseAddress(host, out _) ? "Bluetooth LE" : $"{host}:{port}";
        StatusMessage = $"{displayTarget} に接続中...";
        try
        {
            await CloseExistingSessionsAsync();
            var appearance = await _settingsRepo.LoadAppearanceAsync();
            var cleanName = string.IsNullOrWhiteSpace(PlayerName) ? "あなた" : PlayerName.Trim();
            var client = await _networkService.JoinAsync(host, port, cleanName, appearance.GuideLevel.AlwaysAsksInterrupt(), SelectedIconId, EffectiveJoinPin);
            if (_isLeftPage)
            {
                await client.DisposeAsync();
                return;
            }
            _client = client;
            IsReady = false;
            IsHost = false;
            IsInRoom = true;
            RoomTitle = $"対戦部屋 ({displayTarget})";

            _client.LobbyUpdated += OnClientLobbyUpdated;
            _client.GameStarted += OnClientGameStarted;
            _client.Disconnected += OnClientDisconnected;

            // 対戦中の再接続では JoinAsync 完了前に GameStarted が届いている場合があるため、初期値を確認する
            if (_client.Rules is { } startedRules)
            {
                OnClientGameStarted(_client, startedRules);
                return;
            }

            UpdateSeats(_client.Seats);
            StatusMessage = "ホストの開始を待っています...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"接続失敗: {ex.Message}";
        }
        finally
        {
            _isNetworkBusy = false;
        }
    }

    private void OnHostLobbyChanged(object? sender, IReadOnlyList<SeatInfo> seats)
    {
        _dispatcherQueue.TryEnqueue(() => UpdateSeats(seats));
    }

    private void OnClientLobbyUpdated(object? sender, IReadOnlyList<SeatInfo> seats)
    {
        _dispatcherQueue.TryEnqueue(() => UpdateSeats(seats));
    }

    private void UpdateSeats(IReadOnlyList<SeatInfo> seats)
    {
        Seats.Clear();
        foreach (var s in seats)
        {
            Seats.Add(s);
        }
        UpdateReadiness();
        UpdateAuthCodeSummary();
    }

    private void UpdateAuthCodeSummary()
    {
        if (!IsInRoom)
        {
            AuthCodeSummary = "";
            return;
        }
        if (IsHost && _hostSession is not null)
        {
            var codes = _hostSession.GetGuestAuthCodes()
                .Where(c => !string.IsNullOrEmpty(c.ShortAuthCode))
                .Select(c => $"{c.PlayerName}: {c.ShortAuthCode}")
                .ToList();
            AuthCodeSummary = codes.Count > 0
                ? $"確認コード: {string.Join(" / ", codes)}"
                : "";
            return;
        }
        if (!IsHost && _client?.ShortAuthCode is { Length: > 0 } code)
        {
            AuthCodeSummary = $"確認コード: {code}（ホスト画面と一致するか確認できます）";
            return;
        }
        AuthCodeSummary = "";
    }

    [RelayCommand]
    private void StartGameAsHost()
    {
        if (!IsHost || _hostSession == null) return;
        if (!_hostSession.AreAllGuestsReady)
        {
            StatusMessage = "参加者全員の「準備完了」を待っています";
            return;
        }

        // ゲーム開始前にロビー側の購読を解除し、セッションは対戦画面へ引き継ぐ
        var session = _hostSession;
        DetachSessionEvents();
        _handedOffToGame = true;
        session.StartGame(_rules);

        // ゲーム画面へ遷移
        _navigation.NavigateToGameTable(new NetworkGameParams(session, null, _rules));
    }

    private void OnClientGameStarted(object? sender, RuleSettings rules)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_client != null)
            {
                var client = _client;
                DetachSessionEvents();
                _handedOffToGame = true;
                _navigation.NavigateToGameTable(new NetworkGameParams(null, client, rules));
            }
        });
    }

    private void OnClientDisconnected(object? sender, string reason)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusMessage = $"切断されました: {reason}";
            IsInRoom = false;
        });
    }

    private void DetachSessionEvents()
    {
        if (_hostSession != null)
        {
            _hostSession.LobbyChanged -= OnHostLobbyChanged;
        }
        if (_client != null)
        {
            _client.LobbyUpdated -= OnClientLobbyUpdated;
            _client.GameStarted -= OnClientGameStarted;
            _client.Disconnected -= OnClientDisconnected;
        }
    }

    private async Task CloseExistingSessionsAsync()
    {
        DetachSessionEvents();
        if (_hostSession != null)
        {
            await _hostSession.DisposeAsync();
            _hostSession = null;
        }
        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        IsInRoom = false;
        IsReady = false;
        AuthCodeSummary = "";
        Seats.Clear();
        UpdateReadiness();
    }

    /// <summary>ロビー画面から離脱するときに呼ぶ（対戦画面へ引き継いだ場合はセッションを閉じない）。</summary>
    public async Task OnNavigatedFromAsync()
    {
        _isLeftPage = true;
        DetachSessionEvents();
        if (_handedOffToGame)
        {
            _hostSession = null;
            _client = null;
            _nameSaveLock.Dispose();
            return;
        }
        await CloseExistingSessionsAsync();
        _nameSaveLock.Dispose();
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        await CloseExistingSessionsAsync();
        StatusMessage = "部屋から退出しました";
        await SearchRoomsAsync();
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await CloseExistingSessionsAsync();
        _navigation.NavigateToTitle();
    }
}

