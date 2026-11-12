using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Core.Models;
using DTTS.Services.Database;
using DTTS.Services.Network;
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

    [ObservableProperty]
    public partial bool IsInRoom { get; set; }

    [ObservableProperty]
    public partial bool IsHost { get; set; }

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
    public partial string StatusMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    public ObservableCollection<SeatInfo> Seats { get; } = new();
    public ObservableCollection<RoomInfo> DiscoveredRooms { get; } = new();

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

    public async Task InitializeAsync()
    {
        _rules = await _settingsRepo.LoadRulesAsync();
        var npc = await _settingsRepo.LoadNpcAsync();
        PlayerName = npc.PlayerName;

        await SearchRoomsAsync();
    }

    [RelayCommand]
    private async Task SearchRoomsAsync()
    {
        IsSearching = true;
        StatusMessage = "LAN内の部屋を探索中...";
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
        }
    }

    [RelayCommand]
    private async Task HostRoomAsync()
    {
        StatusMessage = "部屋を立ち上げています...";
        try
        {
            var options = new HostOptions
            {
                RoomName = HostRoomName,
                HostPlayerName = PlayerName,
                Port = NetProtocol.DefaultTcpPort
            };

            _hostSession = await _networkService.HostAsync(options);
            IsHost = true;
            IsInRoom = true;
            RoomTitle = $"{HostRoomName} (ポート: {_hostSession.Port})";

            _hostSession.LobbyChanged += OnHostLobbyChanged;
            UpdateSeats(_hostSession.Seats);

            StatusMessage = "プレイヤーの参加を待っています...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"ホスト起動エラー: {ex.Message}";
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
        StatusMessage = $"{host}:{port} に接続中...";
        try
        {
            _client = await _networkService.JoinAsync(host, port, PlayerName);
            IsHost = false;
            IsInRoom = true;
            RoomTitle = $"対戦部屋 ({host}:{port})";

            _client.LobbyUpdated += OnClientLobbyUpdated;
            _client.GameStarted += OnClientGameStarted;
            _client.Disconnected += OnClientDisconnected;

            UpdateSeats(_client.Seats);
            StatusMessage = "ホストの開始を待っています...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"接続失敗: {ex.Message}";
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
    }

    [RelayCommand]
    private void StartGameAsHost()
    {
        if (!IsHost || _hostSession == null) return;

        // ゲーム開始
        _hostSession.StartGame(_rules);

        // ゲーム画面へ遷移
        _navigation.NavigateToGameTable(new NetworkGameParams(_hostSession, null, _rules));
    }

    private void OnClientGameStarted(object? sender, RuleSettings rules)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_client != null)
            {
                _navigation.NavigateToGameTable(new NetworkGameParams(null, _client, rules));
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

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
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
        Seats.Clear();
        StatusMessage = "部屋から退出しました";
        await SearchRoomsAsync();
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        await LeaveRoomAsync();
        _navigation.NavigateToTitle();
    }
}

