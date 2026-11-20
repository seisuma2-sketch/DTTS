using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DTTS.Services.Network;

/// <summary>
/// 近距離オフライン対戦の窓口。
/// 現在の標準実装（<see cref="BleNetworkService"/>）は Bluetooth LE 探索・GATT 通信を行う。
/// </summary>
public interface INetworkService
{
    /// <summary>近くの部屋を探す。</summary>
    Task<IReadOnlyList<RoomInfo>> DiscoverRoomsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>部屋を作って待ち受けを開始する。</summary>
    Task<GameHostSession> HostAsync(HostOptions options, CancellationToken cancellationToken = default);

    /// <summary>部屋に参加する。</summary>
    /// <param name="alwaysAskInterrupt">止められる手がなくても割り込みの確認を受けたい（操作ガイド「初心者用」）。</param>
    /// <param name="avatarIcon">プレイヤーが選択したアイコンID。</param>
    /// <param name="joinPin">ホストが設定した参加 PIN（なければ null）。</param>
    Task<RemoteGameClient> JoinAsync(string host, int port, string playerName, bool alwaysAskInterrupt = false, string? avatarIcon = null, string? joinPin = null, CancellationToken cancellationToken = default);
}
