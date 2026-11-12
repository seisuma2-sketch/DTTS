using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DTTS.Services.Network;

/// <summary>
/// 近距離オフライン対戦の窓口。
/// 現在の実装は同一LAN（Wi-Fi・テザリング）上の UDP 探索 ＋ TCP 通信。
/// 送受信は <see cref="MessageConnection"/> に閉じているため、Bluetooth RFCOMM 等の別経路は同じプロトコルのまま追加できる。
/// </summary>
public interface INetworkService
{
    /// <summary>LAN 上の部屋を探す。</summary>
    Task<IReadOnlyList<RoomInfo>> DiscoverRoomsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>部屋を作って待ち受けを開始する。</summary>
    Task<GameHostSession> HostAsync(HostOptions options, CancellationToken cancellationToken = default);

    /// <summary>部屋に参加する。</summary>
    Task<RemoteGameClient> JoinAsync(string host, int port, string playerName, CancellationToken cancellationToken = default);
}

/// <summary>LAN（UDP探索＋TCP）による <see cref="INetworkService"/> 実装。</summary>
public sealed class LanNetworkService : INetworkService
{
    private readonly ISerialDispatcher _dispatcher;
    private readonly string _clientId;
    private readonly int _discoveryPort;

    /// <summary>サービスを生成する。</summary>
    /// <param name="dispatcher">ゲーム処理・イベント発行を直列化するディスパッチャ（UIスレッドのラッパー推奨）。</param>
    /// <param name="clientId">この端末の固定ID（初回起動時に生成して設定に保存した値）。</param>
    /// <param name="discoveryPort">探索用UDPポート。</param>
    public LanNetworkService(ISerialDispatcher dispatcher, string clientId, int discoveryPort = NetProtocol.DefaultDiscoveryPort)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clientId = string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId;
        _discoveryPort = discoveryPort;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RoomInfo>> DiscoverRoomsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => LanDiscovery.DiscoverAsync(timeout, _discoveryPort, cancellationToken);

    /// <inheritdoc />
    public async Task<GameHostSession> HostAsync(HostOptions options, CancellationToken cancellationToken = default)
    {
        // DiscoveryPort が null の部屋は探索に応答しない（招待制）。既定値のままなら、このサービスの探索ポートを使う
        var effective = options.DiscoveryPort == NetProtocol.DefaultDiscoveryPort ? options with { DiscoveryPort = _discoveryPort } : options;
        var session = new GameHostSession(effective, _dispatcher);
        await session.StartAsync().ConfigureAwait(false);
        return session;
    }

    /// <inheritdoc />
    public Task<RemoteGameClient> JoinAsync(string host, int port, string playerName, CancellationToken cancellationToken = default)
        => RemoteGameClient.JoinAsync(host, port, playerName, _clientId, _dispatcher, cancellationToken: cancellationToken);
}
