using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Network;
using DTTS.Services.Presentation;

/// <summary>
/// 通信の暗号化（鍵交換・改ざん検出・盗聴されても手札が読めないこと）と、参加 PIN・ロビー管理（準備完了・外す・NPC 専用席）・
/// 定型リアクションのテスト。通信はすべてループバックで行う。
/// </summary>
internal static class NetworkSecurityTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(security): " + name); }
    }

    public static async Task<(int Pass, int Fail)> RunAsync()
    {
        ChannelBasics();
        await EavesdropperSeesNoPlaintextAsync();
        await PlaintextPeerIsRejectedAsync();
        await JoinPinAsync();
        await DuplicateJoinOnSameConnectionIsRejectedAsync();
        await DisposeCompletesPendingCommandAsync();
        await LobbyControlsAsync();
        await ReactionsAsync();
        Texts();
        return (_pass, _fail);
    }

    // ------------------------------------------------------------------
    // SecureChannel 単体：鍵交換・往復・改ざん・再送・向き違い
    // ------------------------------------------------------------------

    private static void ChannelBasics()
    {
        using var guest = new SecureChannel(SecureChannelRole.Initiator);
        using var host = new SecureChannel(SecureChannelRole.Responder);
        Check(!guest.IsEstablished && !host.IsEstablished, "channel: not established before hello");
        try { guest.Seal(new byte[] { 1 }); Check(false, "channel: sealing before the handshake must fail"); }
        catch (InvalidOperationException) { Check(true, "channel: sealing before the handshake fails"); }

        var gh = guest.CreateHello();
        var hh = host.CreateHello();
        Check(SecureChannel.IsHello(gh) && !SecureChannel.IsHello(Encoding.UTF8.GetBytes("{\"$type\":\"join\"}")), "channel: hello is recognizable, JSON is not");
        host.AcceptHello(gh);
        guest.AcceptHello(hh);
        Check(guest.IsEstablished && host.IsEstablished, "channel: established after both hellos");
        Check(guest.ShortAuthCode is { Length: 6 } && guest.ShortAuthCode == host.ShortAuthCode, "channel: matching 6-digit SAS code derived");

        var join = new JoinRequest(NetProtocol.Version, "あなた", "dev-1", JoinPin: "424242", ReconnectToken: "secret-token");
        var frame = guest.SealMessage(join);
        string asText = Encoding.UTF8.GetString(frame);
        Check(!asText.Contains("424242") && !asText.Contains("secret-token") && !asText.Contains("$type"), "channel: PIN, token and JSON are not visible in the frame");
        var opened = host.OpenMessage(frame);
        Check(opened is JoinRequest { JoinPin: "424242", ReconnectToken: "secret-token" }, "channel: guest -> host round trip");

        var view = new LeaveMessage("bye");
        Check(guest.OpenMessage(host.SealMessage(view)) is LeaveMessage { Reason: "bye" }, "channel: host -> guest round trip");

        // 改ざん：暗号文の1ビットを反転すると拒否される
        var f2 = guest.SealMessage(new LeaveMessage("x"));
        var tampered = (byte[])f2.Clone();
        tampered[^1] ^= 0x01;
        try { host.Open(tampered); Check(false, "channel: tampered frame must be rejected"); }
        catch (CryptographicException) { Check(true, "channel: tampered frame is rejected"); }

        // 改ざん検出の後も、正しい次のフレームは受け取れる（連番は進んでいない）
        Check(host.OpenMessage(f2) is LeaveMessage, "channel: the genuine frame still opens after a rejected one");
        // 再送（同じフレームをもう一度）は拒否
        try { host.Open(f2); Check(false, "channel: replayed frame must be rejected"); }
        catch (CryptographicException) { Check(true, "channel: replayed frame is rejected"); }

        // 自分が送ったフレームは自分では開けない（向きごとに鍵が違う）
        var own = guest.SealMessage(new LeaveMessage("me"));
        try { guest.Open(own); Check(false, "channel: own frame must not open"); }
        catch (CryptographicException) { Check(true, "channel: keys differ per direction"); }

        // 別の接続の鍵では開けない
        using var other = new SecureChannel(SecureChannelRole.Responder);
        using var otherGuest = new SecureChannel(SecureChannelRole.Initiator);
        other.AcceptHello(otherGuest.CreateHello());
        try { other.Open(guest.SealMessage(new LeaveMessage("y"))); Check(false, "channel: other session must not open"); }
        catch (CryptographicException) { Check(true, "channel: another session's key cannot open the frame"); }

        // Hello の二度受け・不正な Hello
        try { host.AcceptHello(gh); Check(false, "channel: second hello must be rejected"); }
        catch (InvalidDataException) { Check(true, "channel: second hello is rejected"); }
        using var fresh = new SecureChannel(SecureChannelRole.Responder);
        try { fresh.AcceptHello(Encoding.UTF8.GetBytes("{\"$type\":\"join\",\"playerName\":\"old\"}")); Check(false, "channel: plaintext JSON as hello must fail"); }
        catch (InvalidDataException) { Check(true, "channel: plaintext JSON (old app) is not accepted as a hello"); }
        Check(fresh.Established.IsFaulted, "channel: a failed handshake faults the established task");
    }

    // ------------------------------------------------------------------
    // 経路上の盗聴：TCP を中継して全バイトを記録しても、手札（JSON）が読めない
    // ------------------------------------------------------------------

    private static async Task EavesdropperSeesNoPlaintextAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions
        {
            Port = 0,
            BindAddress = IPAddress.Loopback,
            DiscoveryPort = null,
            NpcTiming = new NpcTimingOptions { BaseDelay = TimeSpan.Zero, Jitter = TimeSpan.Zero, SideDecisionDelay = TimeSpan.Zero },
        }, queue);
        await host.StartAsync();
        await using var proxy = new RecordingProxy(host.Port);

        var client = await RemoteGameClient.JoinAsync("127.0.0.1", proxy.Port, "盗聴テスト", "dev-sniff", joinPin: null);
        Check(client.SeatId == 1, "sniff: joined through the relay");
        var gotView = new TaskCompletionSource<ClientView>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ViewUpdated += (_, v) => { if (v.MyHand.Length > 0) gotView.TrySetResult(v); };
        host.StartGame(RuleSettings.CreateDefault(), new SystemRandomSource(5));
        var view = await gotView.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(view.MyHand.Length > 0, "sniff: the guest received its hand");

        byte[] captured = proxy.Captured();
        string text = Encoding.UTF8.GetString(captured);
        Check(captured.Length > 1000, $"sniff: traffic was recorded ({captured.Length} bytes)");
        Check(!text.Contains("myHand", StringComparison.OrdinalIgnoreCase) && !text.Contains("$type") && !text.Contains("盗聴テスト"),
            "sniff: no JSON, hand field or player name is visible on the wire");
        string handJson = "[" + string.Join(",", view.MyHand) + "]";
        Check(!text.Contains(handJson), "sniff: the hand's card IDs are not visible on the wire");

        await client.DisposeAsync();
        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // 暗号化しない（旧版の）相手は、鍵交換の段階で切断される
    // ------------------------------------------------------------------

    private static async Task PlaintextPeerIsRejectedAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions { Port = 0, BindAddress = IPAddress.Loopback, DiscoveryPort = null }, queue);
        await host.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        var stream = tcp.GetStream();
        await FrameCodec.WriteAsync(stream, NetProtocol.Serialize(new JoinRequest(NetProtocol.Version, "old", "dev-old")), default);
        byte[]? reply = null;
        bool closed;
        try
        {
            reply = await FrameCodec.ReadAsync(stream, default).WaitAsync(TimeSpan.FromSeconds(5));
            closed = reply is null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException)
        {
            closed = true;
        }
        Check(closed, "plaintext peer: the host closes the connection without answering");
        await queue.FlushAsync();
        Check(host.Seats.All(s => s.SeatId == 0 || s.Kind == PlayerKind.Npc), "plaintext peer: no seat was taken");
        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // 参加 PIN（暗号化した参加要求で送る）と、誤入力が続いたときの受付停止
    // ------------------------------------------------------------------

    private static async Task JoinPinAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions { Port = 0, BindAddress = IPAddress.Loopback, DiscoveryPort = null, JoinPin = "123456" }, queue);
        await host.StartAsync();
        Check(host.HasJoinPin, "pin: host reports a PIN");

        async Task<string?> TryJoin(string? pin, string id)
        {
            try
            {
                var c = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "p", id, joinPin: pin);
                await c.DisposeAsync();
                return null;
            }
            catch (JoinFailedException ex)
            {
                return ex.Message;
            }
        }

        Check((await TryJoin(null, "pin-a"))?.Contains("PIN") == true, "pin: joining without the PIN is rejected");
        Check(await TryJoin("123456", "pin-b") is null, "pin: the right PIN is accepted");
        await queue.FlushAsync();
        for (int i = 0; i < 5; i++)
        {
            await TryJoin("000000", "pin-x" + i);
        }
        string? locked = await TryJoin("123456", "pin-c");
        Check(locked is not null && locked.Contains("しばらく"), "pin: after repeated wrong PINs even the right PIN waits (lockout): " + locked);

        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // ロビー：準備完了・外す（入り直し不可）・NPC 専用席
    // ------------------------------------------------------------------

    private static async Task LobbyControlsAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions { Port = 0, BindAddress = IPAddress.Loopback, DiscoveryPort = null }, queue);
        await host.StartAsync();

        var a = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "A", "lobby-a");
        Check(a.SeatId == 1 && !string.IsNullOrEmpty(a.ReconnectToken), "lobby: joined seat 1 and got a reconnect token");
        await WaitAsync(queue, () => host.Seats[1].Kind == PlayerKind.Remote);
        Check(!host.Seats[1].IsReady && !host.AreAllGuestsReady, "lobby: a new guest is not ready");
        Check(a.SetReady(true), "lobby: ready is sent");
        await WaitAsync(queue, () => host.Seats[1].IsReady);
        Check(host.Seats[1].IsReady && host.AreAllGuestsReady, "lobby: the host sees the guest ready");
        var lobbySeenByGuest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.LobbyUpdated += (_, seats) => { if (seats.Count > 2 && seats[2].IsLocked) lobbySeenByGuest.TrySetResult(true); };

        // 席2を NPC 専用にすると、次の参加者は席3に座る
        host.SetSeatLocked(2, true);
        await WaitAsync(queue, () => host.Seats[2].IsLocked);
        Check(await lobbySeenByGuest.Task.WaitAsync(TimeSpan.FromSeconds(5)), "lobby: guests see the locked seat");
        var b = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "B", "lobby-b");
        Check(b.SeatId == 3, "lobby: the locked seat is skipped (got seat " + b.SeatId + ")");

        // 外す：参加者に通知が届き、席は NPC に戻り、その端末は入り直せない
        var kicked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Disconnected += (_, reason) => kicked.TrySetResult(reason);
        host.KickSeat(3);
        string reason = await kicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(reason.Contains("外され"), "lobby: the kicked guest is told why: " + reason);
        await WaitAsync(queue, () => host.Seats[3].Kind == PlayerKind.Npc);
        Check(host.Seats[3].Kind == PlayerKind.Npc && !host.Seats[3].IsConnected, "lobby: the kicked seat is back to an NPC");
        try
        {
            await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "B", "lobby-b");
            Check(false, "lobby: the kicked device must not rejoin");
        }
        catch (JoinFailedException ex) { Check(ex.Message.Contains("外された"), "lobby: the kicked device cannot rejoin: " + ex.Message); }

        // NPC 専用を解除すると、また座れる
        host.SetSeatLocked(2, false);
        await WaitAsync(queue, () => !host.Seats[2].IsLocked);
        var c = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "C", "lobby-c");
        Check(c.SeatId == 2, "lobby: the unlocked seat can be taken again");

        // 参加者が座っている席を NPC 専用にすると、その参加者は外れる（入り直しは可能だが、その席には座れない）
        var cLeft = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Disconnected += (_, r) => cLeft.TrySetResult(r);
        host.SetSeatLocked(2, true);
        Check((await cLeft.Task.WaitAsync(TimeSpan.FromSeconds(5))).Contains("NPC"), "lobby: locking an occupied seat removes its guest");

        await a.DisposeAsync();
        await c.DisposeAsync();
        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // 同一接続からの複数回 JoinRequest（複数席乗っ取り・手札窃取）の拒否
    // ------------------------------------------------------------------

    private static async Task DuplicateJoinOnSameConnectionIsRejectedAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions { Port = 0, BindAddress = IPAddress.Loopback, DiscoveryPort = null }, queue);
        await host.StartAsync();

        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        var conn = new MessageConnection(tcp, "dup-join-test");
        var firstAccepted = new TaskCompletionSource<JoinAccepted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRejected = new TaskCompletionSource<JoinRejected>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.MessageReceived += (_, m) =>
        {
            if (m is JoinAccepted ja) firstAccepted.TrySetResult(ja);
            if (m is JoinRejected jr) secondRejected.TrySetResult(jr);
        };
        conn.Start();

        conn.Send(new JoinRequest(NetProtocol.Version, "Attacker1", "attacker-1"));
        var accepted = await firstAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(accepted.SeatId == 1, "dup-join: first join succeeds on seat 1");

        // 同じ接続から別の ClientId で 2 回目の JoinRequest を送り、空き席を乗っ取ろうとする
        conn.Send(new JoinRequest(NetProtocol.Version, "Attacker2", "attacker-2"));
        var rejected = await secondRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(rejected.Reason.Contains("既に参加済み"), "dup-join: second join on same connection is rejected");

        await WaitAsync(queue, () => host.Seats.Count(s => s.Kind == PlayerKind.Remote) == 0);
        Check(host.Seats.All(s => s.Name != "Attacker2"), "dup-join: second seat is never claimed");

        await conn.CloseAsync();
        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // RemoteGameClient.DisposeAsync が応答待ちコマンドをハングさせず即完了させること
    // ------------------------------------------------------------------

    private static async Task DisposeCompletesPendingCommandAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions { Port = 0, BindAddress = IPAddress.Loopback, DiscoveryPort = null }, queue);
        await host.StartAsync();
        var client = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "A", "dispose-hang-a");

        // 非手番中にコマンドを送り、直後に DisposeAsync しても永久待機しないこと
        var passTask = client.PassAsync();
        await client.DisposeAsync();
        var res = await passTask.WaitAsync(TimeSpan.FromSeconds(5));
        Check(res.Error == CommandError.WrongPhase, "dispose-hang: pending command finishes immediately on DisposeAsync");

        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // 定型リアクション：参加者 → ホスト → 全員。知らないコード・連打は配らない
    // ------------------------------------------------------------------

    private static async Task ReactionsAsync()
    {
        var queue = new SerialActionQueue();
        var host = new GameHostSession(new HostOptions { Port = 0, BindAddress = IPAddress.Loopback, DiscoveryPort = null }, queue);
        await host.StartAsync();
        var hostSeen = new List<(int, string)>();
        host.ReactionReceived += (_, r) => { lock (hostSeen) hostSeen.Add(r); };
        var a = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "A", "react-a");
        var b = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "B", "react-b");
        var bSeen = new List<NoticeDto>();
        b.NoticeReceived += (_, n) => { if (n.Kind == "Reaction") lock (bSeen) bSeen.Add(n); };

        Check(!a.SendReaction("unknown-code"), "reaction: an unknown code is not even sent");
        Check(a.SendReaction("nice"), "reaction: a known code is sent");
        a.SendReaction("thanks"); // 連打（間引かれる）
        host.SendReaction("gg");
        await WaitAsync(queue, () => { lock (hostSeen) return hostSeen.Count >= 2; });
        await Task.Delay(300);
        lock (bSeen)
        {
            Check(bSeen.Any(n => n.PlayerId == a.SeatId && n.Text == "nice"), "reaction: the other guest receives it with the sender's seat");
            Check(bSeen.Any(n => n.PlayerId == 0 && n.Text == "gg"), "reaction: the host's reaction reaches guests");
            Check(!bSeen.Any(n => n.Text == "thanks"), "reaction: a burst from the same seat is thinned out");
        }
        lock (hostSeen)
        {
            Check(hostSeen.Contains((a.SeatId, "nice")) && hostSeen.Contains((0, "gg")), "reaction: the host UI is told as well");
        }
        await a.DisposeAsync();
        await b.DisposeAsync();
        await host.DisposeAsync();
        await queue.DisposeAsync();
    }

    private static void Texts()
    {
        Check(QuickReactions.All.Count >= 4 && QuickReactions.All.All(r => QuickReactions.TextOf(r.Code) == r.Text), "reaction: every preset has a text");
        Check(QuickReactions.All.Select(r => r.Code).Distinct().Count() == QuickReactions.All.Count, "reaction: codes are unique");
        Check(!QuickReactions.IsKnown(null) && !QuickReactions.IsKnown("NICE"), "reaction: codes are exact");
        foreach (var reason in Enum.GetValues<PlayRejectReason>().Where(r => r != PlayRejectReason.None))
        {
            Check(PlayBlockedReasons.Describe(reason).Length > 0, "blocked reason text: " + reason);
        }
        Check(PlayBlockedReasons.Describe(PlayRejectReason.None) == "", "blocked reason: none is empty");
        var addrs = LocalNetworkInfo.GetLocalIPv4Addresses();
        Check(addrs.All(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)), "local ip: only non-loopback IPv4 (" + addrs.Count + ")");
    }

    private static async Task WaitAsync(SerialActionQueue queue, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Post(() => tcs.SetResult(condition()));
            if (await tcs.Task) return;
            await Task.Delay(20);
        }
    }

    /// <summary>TCP を中継して、通ったバイトをすべて記録する（経路上の盗聴者の代わり）。</summary>
    private sealed class RecordingProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly MemoryStream _captured = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly int _targetPort;
        private readonly Task _accept;
        private readonly List<TcpClient> _sockets = new();

        public RecordingProxy(int targetPort)
        {
            _targetPort = targetPort;
            _listener.Start();
            _accept = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public byte[] Captured()
        {
            lock (_captured) return _captured.ToArray();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var outbound = new TcpClient();
                    await outbound.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token);
                    lock (_sockets) { _sockets.Add(inbound); _sockets.Add(outbound); }
                    _ = PumpAsync(inbound.GetStream(), outbound.GetStream());
                    _ = PumpAsync(outbound.GetStream(), inbound.GetStream());
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to)
        {
            var buffer = new byte[8192];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buffer, _cts.Token)) > 0)
                {
                    lock (_captured) _captured.Write(buffer, 0, n);
                    await to.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                }
            }
            catch (Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            lock (_sockets) foreach (var s in _sockets) s.Dispose();
            try { await _accept; } catch (Exception) { }
        }
    }
}
