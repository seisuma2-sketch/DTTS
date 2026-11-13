using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DTTS.Services.Network;

/// <summary>LAN 上で見つかった部屋。</summary>
/// <param name="RoomName">部屋名。</param>
/// <param name="HostName">ホストのプレイヤー名。</param>
/// <param name="Address">ホストのIPアドレス（応答元から設定）。</param>
/// <param name="Port">TCPポート。</param>
/// <param name="OpenSeats">空き席数。</param>
/// <param name="IsPlaying">対戦中か。</param>
/// <param name="ProtocolVersion">プロトコルバージョン。</param>
public sealed record RoomInfo(string RoomName, string HostName, string? Address, int Port, int OpenSeats, bool IsPlaying, int ProtocolVersion);

/// <summary>
/// ホスト側の探索応答。UDP で探索要求を待ち、部屋情報を返信する（要求を await するだけで、定期送信はしない）。
/// </summary>
public sealed class LanDiscoveryResponder : IDisposable
{
    internal static readonly byte[] ProbeBytes = Encoding.ASCII.GetBytes("DTTS?" + NetProtocol.Version);

    private readonly UdpClient _udp;
    private readonly Func<RoomInfo> _roomProvider;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>応答を開始する。</summary>
    /// <param name="roomProvider">現在の部屋情報を返す関数。</param>
    /// <param name="port">待ち受けUDPポート。</param>
    /// <param name="bindAddress">待ち受けアドレス（null なら全アドレス）。</param>
    public LanDiscoveryResponder(Func<RoomInfo> roomProvider, int port = NetProtocol.DefaultDiscoveryPort, IPAddress? bindAddress = null)
    {
        _roomProvider = roomProvider ?? throw new ArgumentNullException(nameof(roomProvider));
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
        UdpSocketHelper.IgnoreConnectionReset(_udp.Client);
        _ = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // 返信先が既に閉じていた（ICMP 到達不能）。待ち受けは続ける
                }
                if (!received.Buffer.AsSpan().SequenceEqual(ProbeBytes))
                {
                    continue; // 他アプリ・他バージョンの探索は無視
                }
                var reply = JsonSerializer.SerializeToUtf8Bytes(_roomProvider(), NetJsonContext.Default.RoomInfo);
                await _udp.SendAsync(reply, received.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}

/// <summary>クライアント側の部屋探索。</summary>
public static class LanDiscovery
{
    /// <summary>
    /// 探索要求をブロードキャストし、指定時間内に返ってきた部屋を返す（同一端末のホストも検出できるよう loopback にも送る）。
    /// </summary>
    public static async Task<IReadOnlyList<RoomInfo>> DiscoverAsync(
        TimeSpan timeout,
        int port = NetProtocol.DefaultDiscoveryPort,
        CancellationToken cancellationToken = default,
        IPAddress? localAddress = null)
    {
        var rooms = new Dictionary<string, RoomInfo>(StringComparer.Ordinal);
        var local = localAddress ?? IPAddress.Any;
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = !IPAddress.IsLoopback(local) };
        udp.Client.Bind(new IPEndPoint(local, 0));
        UdpSocketHelper.IgnoreConnectionReset(udp.Client);

        var targets = IPAddress.IsLoopback(local) ? new[] { IPAddress.Loopback } : GetBroadcastTargets();
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(LanDiscoveryResponder.ProbeBytes, new IPEndPoint(target, port), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // ブロードキャスト不可のネットワークでは loopback のみ
            }
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout); // 収集期間（一回限りのタイムアウト）
        try
        {
            // 応答の受信を await する（収集期間が終わるとキャンセルで抜ける）
            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // 応答者のいない宛先（loopback 等）からの ICMP 到達不能。探索は続ける
                }
                RoomInfo? room;
                try
                {
                    room = JsonSerializer.Deserialize(received.Buffer, NetJsonContext.Default.RoomInfo);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (room is null || room.ProtocolVersion != NetProtocol.Version)
                {
                    continue;
                }
                var remote = received.RemoteEndPoint.Address;
                // 同じホストが loopback と LAN の両方から応答した場合は LAN 側のアドレスを優先する
                var key = $"{room.RoomName}|{room.HostName}|{room.Port}";
                if (!rooms.TryGetValue(key, out var existing) || (IPAddress.IsLoopback(IPAddress.Parse(existing.Address!)) && !IPAddress.IsLoopback(remote)))
                {
                    rooms[key] = room with { Address = remote.ToString() };
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 収集期間の終了
        }
        return rooms.Values.OrderBy(r => r.RoomName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// 探索要求の送り先。全体ブロードキャストに加え、稼働中の各ネットワークアダプタのサブネット向けブロードキャストにも送る
    /// （Hyper-V・WSL の仮想スイッチ等で全体ブロードキャストが意図しないアダプタから出る環境への対策）。
    /// </summary>
    private static IReadOnlyList<IPAddress> GetBroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    }
                    var address = new IPAddress(broadcast);
                    if (!targets.Contains(address))
                    {
                        targets.Add(address);
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // アダプタ情報が取れない環境では全体ブロードキャストのみ
        }
        return targets;
    }
}

/// <summary>UDP ソケットの補助。</summary>
internal static class UdpSocketHelper
{
    /// <summary>SIO_UDP_CONNRESET（Windows 固有の制御コード）。</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Windows では、送った UDP の宛先から ICMP「ポート到達不能」が返ると、以降の受信が
    /// ConnectionReset（「既存の接続はリモート ホストに強制的に切断されました」）で失敗する。
    /// 探索では応答者のいない宛先にも送るため、この通知を無効にする。
    /// </summary>
    public static void IgnoreConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch (SocketException)
        {
            // 未対応の環境では受信側の ConnectionReset 無視で対応する
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
