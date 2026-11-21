using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

namespace DTTS.Services.Network;

/// <summary>ホストの設定。</summary>
public sealed record HostOptions
{
    /// <summary>部屋名。</summary>
    public string RoomName { get; init; } = "DTTS の部屋";

    /// <summary>ホスト（この端末）のプレイヤー名。座席0に着く。</summary>
    public string HostPlayerName { get; init; } = "ホスト";

    /// <summary>席数（ホストを含む）。</summary>
    public int SeatCount { get; init; } = 4;

    /// <summary>TCPポート（0 なら自動割り当て）。</summary>
    public int Port { get; init; } = NetProtocol.DefaultTcpPort;

    /// <summary>待ち受けアドレス（既定は全アドレス＝LAN公開。同一端末内の確認なら Loopback）。</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>探索応答のUDPポート（null なら探索に応答しない）。</summary>
    public int? DiscoveryPort { get; init; } = NetProtocol.DefaultDiscoveryPort;

    /// <summary>空席・切断席を引き継ぐNPCの設定（席番号 → プロファイル。未指定は ふつう・堅実）。</summary>
    public IReadOnlyDictionary<int, NpcProfile> NpcProfiles { get; init; } = new Dictionary<int, NpcProfile>();

    /// <summary>空席のNPCの名前（席番号 → 名前）。</summary>
    public IReadOnlyDictionary<int, string> NpcNames { get; init; } = new Dictionary<int, string>();

    /// <summary>NPCの思考時間。</summary>
    public NpcTimingOptions NpcTiming { get; init; } = new();

    /// <summary>ホスト（座席0）のアイコンID。</summary>
    public string? HostAvatarIcon { get; init; }

    /// <summary>
    /// ホスト（座席0）には、止められる手を持っていなくても割り込み（4止め・砂嵐）の確認を出す（既定）。
    /// false なら止められる手を持つときだけ確認する（NPC と同じ扱い。自動テスト用）。
    /// </summary>
    public bool HostAlwaysAskInterrupt { get; init; } = true;

    /// <summary>部屋参加時のPINコード（null または空ならPINなし）。</summary>
    public string? JoinPin { get; init; }
}

/// <summary>
/// 通信対戦のホスト。ゲーム進行（GameEngine）はホストだけが持ち、各クライアントにはその席から見える情報だけを送る。
/// <list type="bullet">
/// <item>参加：空いているNPC席を通信プレイヤーの席に置き換える。</item>
/// <item>切断：その席を即座にNPCが引き継ぐ（同じ端末IDで再接続すれば席に戻れる）。</item>
/// <item>スレッド：通信スレッドからの処理はすべて <see cref="ISerialDispatcher"/> 上で直列に実行する。</item>
/// </list>
/// ホスト自身（座席0）の操作は、UIが <see cref="Engine"/> を直接操作する（ディスパッチャ上で呼ぶこと）。
/// </summary>
public sealed class GameHostSession : IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly ISerialDispatcher _dispatcher;
    private readonly INpcScheduler _npcScheduler;
    private readonly DispatcherNpcScheduler? _ownedNpcScheduler;
    private readonly Seat[] _seats;
    private readonly List<IMessageConnection> _pending = new();
    private const int MaxPendingConnections = 16;
    private int _pendingAcceptCount;
    private readonly List<IAsyncDisposable> _auxTransports = new();
    private TcpMessageServer? _server;
    private LanDiscoveryResponder? _discovery;
    private long _viewSequence;
    private int _disposedFlag;

    private bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    // --- ロビー管理 ---
    /// <summary>ホストが外した端末（この部屋には入り直せない）。</summary>
    private readonly HashSet<string> _kickedClientIds = new(StringComparer.Ordinal);

    // --- PIN・再接続トークンの総当たり対策：接続元単位＋部屋全体で一定回数まちがえたら、しばらく参加を受け付けない ---
    private const int AuthFailureLimit = 5;
    private const int GlobalAuthFailureLimit = 15;
    private const int MaxTrackedAuthSources = 256;
    private static readonly TimeSpan AuthLockout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, (int Failures, DateTime LockedUntil)> _authFailures = new(StringComparer.Ordinal);
    private int _globalAuthFailures;
    private DateTime _globalAuthLockedUntil = DateTime.MinValue;

    // --- 1接続あたりの受信メッセージレート制限（高速連打による DoS / UI 飢餓を防止） ---
    private const int MaxMessagesPerSecond = 200;
    private readonly Dictionary<IMessageConnection, (DateTime WindowStart, int Count)> _messageRates = new();

    // --- 定型リアクション：同じ席からの連打は間引く ---
    private static readonly TimeSpan ReactionInterval = TimeSpan.FromMilliseconds(1200);
    private readonly Dictionary<int, DateTime> _lastReaction = new();

    private sealed class Seat
    {
        public required int Id { get; init; }
        public required string Name { get; set; }
        public required PlayerKind Kind { get; set; }
        public IMessageConnection? Connection { get; set; }
        public string? ClientId { get; set; }
        public string? ReconnectToken { get; set; }
        public string? AvatarIcon { get; set; }
        /// <summary>参加者が準備完了にしているか。</summary>
        public bool IsReady { get; set; }
        /// <summary>ホストが NPC 専用にした席か（参加者は座れない）。</summary>
        public bool IsLocked { get; set; }
        /// <summary>止められる手がなくても割り込みの確認を受けたい（参加者の操作ガイドが「初心者用」）。</summary>
        public bool AlwaysAskInterrupt { get; set; }
        public required NpcProfile NpcProfile { get; init; }
        public required string NpcName { get; init; }
    }

    /// <summary>ホストを生成する（<see cref="StartAsync"/> で待ち受け開始）。</summary>
    /// <param name="options">設定。</param>
    /// <param name="dispatcher">直列実行のディスパッチャ（UIスレッドのラッパーでもよい）。</param>
    /// <param name="npcScheduler">NPCの思考スケジューラー（null なら思考時間後にディスパッチャ上で実行）。</param>
    public GameHostSession(HostOptions options, ISerialDispatcher dispatcher, INpcScheduler? npcScheduler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (npcScheduler is null)
        {
            _ownedNpcScheduler = new DispatcherNpcScheduler(dispatcher);
            _npcScheduler = _ownedNpcScheduler;
        }
        else
        {
            _npcScheduler = npcScheduler;
        }
        if (options.SeatCount < 2 || options.SeatCount > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "席数は2〜8です。");
        }
        _seats = Enumerable.Range(0, options.SeatCount).Select(i => new Seat
        {
            Id = i,
            Name = i == 0 ? options.HostPlayerName : (options.NpcNames.TryGetValue(i, out var n) ? n : $"NPC{i}"),
            Kind = i == 0 ? PlayerKind.Human : PlayerKind.Npc,
            AvatarIcon = i == 0 ? options.HostAvatarIcon : "bot",
            NpcProfile = options.NpcProfiles.TryGetValue(i, out var p) ? p : NpcProfile.Default,
            NpcName = options.NpcNames.TryGetValue(i, out var nn) ? nn : $"NPC{i}",
        }).ToArray();
    }

    /// <summary>待ち受けポート。</summary>
    public int Port => _server?.Port ?? 0;

    /// <summary>ゲームエンジン（ゲーム開始前は null）。</summary>
    public GameEngine? Engine { get; private set; }

    /// <summary>NPCコントローラー（ゲーム開始前は null）。</summary>
    public NpcController? Npc { get; private set; }

    /// <summary>席の状況が変わった（ディスパッチャ上で呼ばれる）。</summary>
    public event EventHandler<IReadOnlyList<SeatInfo>>? LobbyChanged;

    /// <summary>ゲームが作成された（ディスパッチャ上で呼ばれる。UIはここでエンジンのイベントを購読する）。</summary>
    public event EventHandler<GameEngine>? GameCreated;

    /// <summary>定型リアクションが届いた（ディスパッチャ上で呼ばれる。席番号とリアクションのコード）。ホスト自身の送信も含む。</summary>
    public event EventHandler<(int SeatId, string Code)>? ReactionReceived;

    /// <summary>現在の席の状況。</summary>
    public IReadOnlyList<SeatInfo> Seats => _seats
        .Select(s => new SeatInfo(
            s.Id, s.Name, s.Kind, s.Connection is not null && !s.Connection.IsClosed, s.Id == 0, s.AvatarIcon,
            IsReady: s.Kind != PlayerKind.Remote || s.IsReady,
            IsLocked: s.IsLocked))
        .ToArray();

    /// <summary>接続中の参加者が全員「準備完了」か（参加者がいなければ true）。</summary>
    public bool AreAllGuestsReady => _seats.Where(s => s.Kind == PlayerKind.Remote && s.Connection is { IsClosed: false }).All(s => s.IsReady);

    /// <summary>参加 PIN が設定されているか。</summary>
    public bool HasJoinPin => !string.IsNullOrEmpty(_options.JoinPin);

    /// <summary>
    /// 参加者を部屋から外す（ディスパッチャ上で実行）。その端末はこの部屋には入り直せない。
    /// 対戦中なら、その席は NPC が引き継ぐ。
    /// </summary>
    public void KickSeat(int seatId)
    {
        _dispatcher.Post(() =>
        {
            if (seatId <= 0 || seatId >= _seats.Length) return;
            var seat = _seats[seatId];
            if (seat.ClientId is { } clientId)
            {
                _kickedClientIds.Add(clientId);
            }
            var connection = seat.Connection;
            if (connection is not null && !connection.IsClosed)
            {
                connection.Send(new LeaveMessage("ホストにより部屋から外されました"));
                _ = connection.CloseAsync(); // 席の後始末（NPC への切り替え）は切断の処理で行う
            }
            else if (Engine is null || Engine.Phase == GamePhase.GameEnded)
            {
                ResetSeatToNpc(seat);
                NotifyLobby();
            }
        });
    }

    /// <summary>
    /// 席を NPC 専用にする／参加者に開放する（ディスパッチャ上で実行。ロビーでのみ有効）。
    /// 参加者が座っている席を NPC 専用にすると、その参加者は外される（入り直しは可能）。
    /// </summary>
    public void SetSeatLocked(int seatId, bool locked)
    {
        _dispatcher.Post(() =>
        {
            if (seatId <= 0 || seatId >= _seats.Length) return;
            if (Engine is not null && Engine.Phase != GamePhase.GameEnded) return; // 対戦中は席の構成を変えない
            var seat = _seats[seatId];
            seat.IsLocked = locked;
            if (locked && seat.Connection is { IsClosed: false } connection)
            {
                connection.Send(new LeaveMessage("ホストがこの席を NPC にしました"));
                _ = connection.CloseAsync();
                return; // 切断の処理で NPC 席に戻り、ロビーも更新される
            }
            NotifyLobby();
        });
    }

    /// <summary>ホスト（座席0）が定型リアクションを送る（ディスパッチャ上で全員へ配る）。</summary>
    public void SendReaction(string code) => _dispatcher.Post(() => RelayReaction(0, code));

    /// <summary>カード交換結果の表示中など、ホスト側 NPC の思考・行動を一時停止／再開する。</summary>
    public void SetNpcPaused(bool paused) => _dispatcher.Post(() => Npc?.SetPaused(paused));

    /// <summary>その席の NPC の設定（人が操作している席でも、切断時に引き継ぐ NPC の設定を返す）。</summary>
    public NpcProfile NpcProfileOf(int seatId)
        => seatId >= 0 && seatId < _seats.Length ? _seats[seatId].NpcProfile : NpcProfile.Default;

    /// <summary>探索（部屋一覧）に応答できているか。探索用ポートが使用中の場合は false（IP直接指定での参加は可能）。</summary>
    public bool IsDiscoverable => _discovery is not null;

    /// <summary>
    /// 待ち受けと探索応答を開始する。指定ポートが使用中なら空きポートで待ち受け、
    /// 探索用ポートが使えない場合は探索応答なしで続行する。
    /// </summary>
    public Task StartAsync(bool startTcp = true)
    {
        if (!startTcp)
        {
            return Task.CompletedTask;
        }
        try
        {
            _server = CreateServer(_options.Port);
        }
        catch (SocketException) when (_options.Port != 0)
        {
            _server = CreateServer(0); // 既定ポートが使用中（別の DTTS が起動中など）
        }
        if (_options.DiscoveryPort is { } dp)
        {
            try
            {
                _discovery = new LanDiscoveryResponder(BuildRoomInfo, dp, _options.BindAddress);
            }
            catch (SocketException)
            {
                _discovery = null;
            }
        }
        return Task.CompletedTask;
    }

    private TcpMessageServer CreateServer(int port)
    {
        var server = new TcpMessageServer(_options.BindAddress, port);
        server.ConnectionAccepted += OnConnectionAccepted;
        server.Start();
        return server;
    }

    /// <summary>ゲームを開始する（ディスパッチャ上で実行される）。</summary>
    public void StartGame(RuleSettings rules, IRandomSource? random = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _dispatcher.Post(() =>
        {
            if (Engine is not null && Engine.Phase != GamePhase.GameEnded)
            {
                return;
            }
            Npc?.Dispose();
            var engine = new GameEngine(rules, _seats.Select(s => new PlayerSetup(s.Name, s.Kind)).ToList(), random);
            var npc = new NpcController(engine, _npcScheduler, _options.NpcTiming);
            foreach (var seat in _seats.Where(s => s.Id != 0))
            {
                npc.Assign(seat.Id, seat.NpcProfile); // 切断時の引き継ぎに備えて全席に割り当てておく（Npc 席のみ動作）
            }
            foreach (var seat in _seats)
            {
                SyncAlwaysAsk(engine, seat);
            }
            engine.EventOccurred += OnEngineEvent;
            Engine = engine;
            Npc = npc;
            GameCreated?.Invoke(this, engine);
            Broadcast(_ => new GameStarted(engine.Rules.ToJson()));
            engine.StartGame();
        });
    }

    /// <summary>対戦途中でホスト（親）がルールを変更し、全ゲストへ反映する。</summary>
    public void UpdateRules(RuleSettings rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _dispatcher.Post(() =>
        {
            if (Engine is null)
            {
                return;
            }
            Engine.UpdateRules(rules);
            Broadcast(_ => new RulesUpdated(Engine.Rules.ToJson()));
            foreach (var seat in ConnectedSeats())
            {
                SendView(seat);
            }
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        // 先に新規接続の受付と NPC 遅延スケジュールを止める
        _ownedNpcScheduler?.Dispose();
        _discovery?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var transport in _auxTransports)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        // 共有コレクション（_seats / _pending）と Engine / Npc の解除は _dispatcher 上で直列実行する
        List<IMessageConnection> connections;
        var tcs = new TaskCompletionSource<List<IMessageConnection>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatcher.Post(() =>
            {
                try
                {
                    if (Engine is not null)
                    {
                        Engine.EventOccurred -= OnEngineEvent;
                    }
                    Npc?.Dispose();
                    var list = _seats.Select(s => s.Connection).Concat(_pending).Where(c => c is not null).Cast<IMessageConnection>().Distinct().ToList();
                    foreach (var seat in _seats)
                    {
                        seat.Connection = null;
                    }
                    _pending.Clear();
                    tcs.TrySetResult(list);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            connections = completed == tcs.Task && tcs.Task.IsCompletedSuccessfully
                ? tcs.Task.Result
                : _seats.Select(s => s.Connection).Concat(_pending).Where(c => c is not null).Cast<IMessageConnection>().Distinct().ToList();
        }
        catch
        {
            connections = _seats.Select(s => s.Connection).Concat(_pending).Where(c => c is not null).Cast<IMessageConnection>().Distinct().ToList();
        }

        foreach (var connection in connections)
        {
            connection.MessageReceived -= OnConnectionMessageReceived;
            connection.Closed -= OnConnectionClosed;
            connection.Send(new LeaveMessage("ホストが部屋を閉じました"));
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>現在の部屋情報（探索応答用）。</summary>
    public RoomInfo CurrentRoomInfo => BuildRoomInfo();

    /// <summary>外部トランスポート（Bluetooth LE 等）を登録する（終了時に自動破棄）。</summary>
    public void AttachTransport(IAsyncDisposable transport) => _auxTransports.Add(transport);

    /// <summary>接続中の各ゲスト席の確認コード（SAS）一覧を返す（MITM 目視検証用）。</summary>
    public IReadOnlyList<(int SeatId, string PlayerName, string? ShortAuthCode)> GetGuestAuthCodes()
    {
        var list = new List<(int, string, string?)>();
        foreach (var s in _seats)
        {
            if (s.Id != 0 && s.Kind == PlayerKind.Remote && s.Connection is { IsClosed: false } conn)
            {
                list.Add((s.Id, s.Name, conn.ShortAuthCode));
            }
        }
        return list;
    }

    /// <summary>外部トランスポート（Bluetooth LE 等）から受け付けた接続を登録する。</summary>
    public void AcceptConnection(IMessageConnection connection) => OnConnectionAccepted(connection);

    // ------------------------------------------------------------------
    // 接続管理
    // ------------------------------------------------------------------

    private void OnConnectionAccepted(IMessageConnection connection)
    {
        if (IsDisposed)
        {
            _ = connection.CloseAsync();
            return;
        }
        // ディスパッチャへの Post 前に同期的に未認証接続の上限を検証し、DoS による ECDH 鍵生成の乱発を防ぐ
        if (Interlocked.Increment(ref _pendingAcceptCount) > MaxPendingConnections)
        {
            Interlocked.Decrement(ref _pendingAcceptCount);
            _ = connection.CloseAsync();
            return;
        }
        connection.MessageReceived += OnConnectionMessageReceived;
        connection.Closed += OnConnectionClosed;
        _dispatcher.Post(() =>
        {
            Interlocked.Decrement(ref _pendingAcceptCount);
            if (IsDisposed || _pending.Count >= MaxPendingConnections)
            {
                _ = connection.CloseAsync();
                return;
            }
            _pending.Add(connection);
            // 未認証接続のまま放置する DoS を防ぐため、5秒以内に JoinRequest が来なければ切断する
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            {
                if (IsDisposed) return;
                _dispatcher.Post(() =>
                {
                    if (!IsDisposed && _pending.Remove(connection))
                    {
                        _ = connection.CloseAsync();
                    }
                });
            }, TaskScheduler.Default);
        });
        connection.Start();
    }

    private void OnConnectionMessageReceived(IMessageConnection connection, NetMessage message)
    {
        if (IsDisposed) return;
        _dispatcher.Post(() =>
        {
            if (!IsDisposed) OnMessage(connection, message);
        });
    }

    private void OnConnectionClosed(IMessageConnection connection, Exception? _)
    {
        connection.MessageReceived -= OnConnectionMessageReceived;
        connection.Closed -= OnConnectionClosed;
        if (IsDisposed) return;
        _dispatcher.Post(() =>
        {
            if (IsDisposed) return;
            _messageRates.Remove(connection);
            OnClosed(connection);
        });
    }

    private void OnMessage(IMessageConnection connection, NetMessage message)
    {
        // 1接続あたりの受信レート制限（1秒間に上限を超える連打は切断）
        var now = DateTime.UtcNow;
        if (_messageRates.TryGetValue(connection, out var rate) && (now - rate.WindowStart).TotalSeconds < 1.0)
        {
            if (rate.Count + 1 > MaxMessagesPerSecond)
            {
                _messageRates.Remove(connection);
                _ = connection.CloseAsync();
                return;
            }
            _messageRates[connection] = (rate.WindowStart, rate.Count + 1);
        }
        else
        {
            _messageRates[connection] = (now, 1);
        }

        switch (message)
        {
            case JoinRequest join:
                HandleJoin(connection, join);
                break;
            case CommandRequest command:
                HandleCommand(connection, command);
                break;
            case LeaveMessage:
                _ = connection.CloseAsync();
                break;
            case ReadyRequest ready:
                HandleReady(connection, ready.IsReady);
                break;
            case ReactionRequest reaction:
                if (_seats.FirstOrDefault(s => ReferenceEquals(s.Connection, connection)) is { } sender)
                {
                    RelayReaction(sender.Id, reaction.Code);
                }
                break;
        }
    }

    private void HandleReady(IMessageConnection connection, bool ready)
    {
        var seat = _seats.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));
        if (seat is null || seat.IsReady == ready) return;
        seat.IsReady = ready;
        NotifyLobby();
    }

    /// <summary>リアクション（定型または自由入力）を確かめて全員へ配る（不正ペイロード・連打は捨てる）。</summary>
    private void RelayReaction(int seatId, string? code)
    {
        if (!DTTS.Services.Presentation.QuickReactions.IsValidPayload(code)) return;
        // 自由入力の場合はホスト側でもサニタイズし直して長さ上限を強制する
        string normalized = code!;
        if (!DTTS.Services.Presentation.QuickReactions.IsKnown(code))
        {
            if (!DTTS.Services.Presentation.QuickReactions.TryEncodeCustom(code![1..], out normalized)) return;
        }
        var now = DateTime.UtcNow;
        if (_lastReaction.TryGetValue(seatId, out var last) && now - last < ReactionInterval) return;
        _lastReaction[seatId] = now;
        var notice = new NoticeDto("Reaction", seatId, Text: normalized);
        Broadcast(_ => new GameNotice(notice));
        ReactionReceived?.Invoke(this, (seatId, normalized));
    }

    /// <summary>席を空きの NPC 席に戻す（ロビーでの退出・外したとき）。</summary>
    private static void ResetSeatToNpc(Seat seat)
    {
        seat.Kind = PlayerKind.Npc;
        seat.ClientId = null;
        seat.ReconnectToken = null;
        seat.IsReady = false;
        seat.Name = seat.NpcName;
        seat.AvatarIcon = "bot";
    }

    private void HandleJoin(IMessageConnection connection, JoinRequest join)
    {
        _pending.Remove(connection);
        // すでにいずれかの席へ割り当て済みの接続からの再参加要求（複数席乗っ取り・手札閲覧チート）を拒否する
        if (_seats.Any(s => ReferenceEquals(s.Connection, connection)))
        {
            Reject(connection, "既に参加済みです");
            return;
        }
        if (join.ProtocolVersion != NetProtocol.Version)
        {
            Reject(connection, "アプリのバージョンが異なります");
            return;
        }
        if (string.IsNullOrWhiteSpace(join.ClientId) || join.ClientId.Length > 64)
        {
            Reject(connection, "不正なクライアントIDです");
            return;
        }
        if (_kickedClientIds.Contains(join.ClientId))
        {
            Reject(connection, "ホストにより部屋から外されたため、参加できません");
            return;
        }
        string authSource = GetAuthSourceKey(connection);
        if (IsAuthLocked(authSource))
        {
            Reject(connection, "PIN の誤入力が続いたため、しばらく参加を受け付けていません");
            return;
        }
        if (!string.IsNullOrEmpty(_options.JoinPin))
        {
            if (!FixedTimeEquals(join.JoinPin, _options.JoinPin))
            {
                RecordAuthFailure(authSource);
                Reject(connection, "参加PINが正しくありません");
                return;
            }
        }

        bool inGame = Engine is not null && Engine.Phase != GamePhase.GameEnded;

        if (_seats.Any(s => s.ClientId == join.ClientId && s.Connection is not null && !s.Connection.IsClosed))
        {
            Reject(connection, "この端末は既に参加しています");
            return;
        }

        // 再接続：同じ端末IDの席（切断中）へ戻す（トークン発行済みの場合は定数時間比較で一致を必須とする）
        var seat = _seats.FirstOrDefault(s => s.Id != 0 && s.ClientId == join.ClientId && (s.Connection is null || s.Connection.IsClosed));
        bool rejoin = seat is not null;
        if (seat is not null && !string.IsNullOrEmpty(seat.ReconnectToken) &&
            !FixedTimeEquals(join.ReconnectToken, seat.ReconnectToken))
        {
            RecordAuthFailure(authSource);
            Reject(connection, "再接続トークンが一致しません");
            return;
        }
        ClearAuthFailure(authSource);
        if (seat is null)
        {
            if (inGame)
            {
                Reject(connection, "対戦中のため参加できません");
                return;
            }
            seat = _seats.FirstOrDefault(s => s.Id != 0 && s.Kind == PlayerKind.Npc && s.ClientId is null && !s.IsLocked);
            if (seat is null)
            {
                Reject(connection, "満席です");
                return;
            }
        }

        seat.Connection = connection;
        seat.ClientId = join.ClientId;
        seat.ReconnectToken ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        seat.Kind = PlayerKind.Remote;
        seat.IsReady = false;
        seat.AlwaysAskInterrupt = join.AlwaysAskInterrupt;
        seat.AvatarIcon = SanitizeAvatarIcon(join.AvatarIcon) ?? seat.AvatarIcon;
        if (!inGame)
        {
            seat.Name = SanitizeName(join.PlayerName, seat.Id);
        }
        connection.Send(new JoinAccepted(seat.Id, _options.RoomName, rejoin, seat.ReconnectToken));

        if (inGame)
        {
            connection.Send(new GameStarted(Engine!.Rules.ToJson()));
            Engine.SetPlayerKind(seat.Id, PlayerKind.Remote); // NPC から操作を取り戻す
            SyncAlwaysAsk(Engine, seat);
            SendView(seat);
        }
        NotifyLobby();
    }

    private void HandleCommand(IMessageConnection connection, CommandRequest request)
    {
        var seat = _seats.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));
        var engine = Engine;
        if (seat is null || engine is null)
        {
            connection.Send(new CommandResponse(request.RequestId, CommandError.WrongPhase, PlayRejectReason.None));
            return;
        }

        CommandResult result;
        try
        {
            var c = request.Command;
            if ((c.CardIds is { Length: > 60 }) || (c.Ranks is { Length: > 20 }))
            {
                connection.Send(new CommandResponse(request.RequestId, CommandError.IllegalPlay, PlayRejectReason.None));
                return;
            }
            var cards = (c.CardIds ?? Array.Empty<int>()).Select(Card.FromId).ToList();
            result = c.Kind switch
            {
                CommandKind.Play => engine.SubmitPlay(seat.Id, cards),
                CommandKind.Pass => engine.SubmitPass(seat.Id),
                CommandKind.SelectCards => engine.SubmitCardSelection(seat.Id, cards),
                CommandKind.DeclareRanks => engine.SubmitRankDeclaration(seat.Id, c.Ranks ?? Array.Empty<Rank>()),
                CommandKind.RevolutionChoice => engine.SubmitRevolutionChoice(seat.Id, c.Flag),
                _ => new CommandResult(CommandError.InvalidSelection, seat.Id),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            result = new CommandResult(CommandError.CardsNotInHand, seat.Id); // 不正なカードID
        }
        connection.Send(new CommandResponse(request.RequestId, result.Error, result.PlayReject));
    }

    private void OnClosed(IMessageConnection connection)
    {
        _pending.Remove(connection);
        var seat = _seats.FirstOrDefault(s => ReferenceEquals(s.Connection, connection));
        if (seat is null)
        {
            return;
        }
        seat.Connection = null;

        if (Engine is not null && Engine.Phase != GamePhase.GameEnded)
        {
            // 対戦中の切断：NPC が即座に引き継ぐ（席の ClientId は残し、再接続で戻れるようにする）
            seat.Kind = PlayerKind.Npc;
            Engine.SetPlayerKind(seat.Id, PlayerKind.Npc);
            SyncAlwaysAsk(Engine, seat); // NPC が引き継いだ席は、止められるときだけ聞く
        }
        else
        {
            // ロビーでの退出：NPC 席に戻す
            ResetSeatToNpc(seat);
        }
        seat.IsReady = false;
        NotifyLobby();
    }

    /// <summary>その席に「止められなくても割り込みの確認を出す」かをエンジンへ反映する（人が操作している席だけ）。</summary>
    private void SyncAlwaysAsk(GameEngine engine, Seat seat)
    {
        // 人が操作している席（ホスト・参加者）には、止め札がなくても必ず確認を出す。
        // 参加者は旧版（確認の要否を送らない）でも同じ。NPC が引き継いだ席は、止められるときだけ聞く
        bool ask = seat.Id == 0
            ? _options.HostAlwaysAskInterrupt
            : seat.Kind == PlayerKind.Remote;
        if (ask)
        {
            engine.AlwaysAskInterruptPlayerIds.Add(seat.Id);
        }
        else
        {
            engine.AlwaysAskInterruptPlayerIds.Remove(seat.Id);
        }
    }

    /// <summary>PIN を比べる（文字列の長さも含めて漏れないよう SHA-256 ハッシュ同士を一定時間で比較する）。</summary>
    private static bool FixedTimeEquals(string? given, string expected)
    {
        var a = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(given ?? string.Empty));
        var b = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string GetAuthSourceKey(IMessageConnection connection)
        => connection is MessageConnection tcp && tcp.RemoteEndPoint is { } ep
            ? ep.Address.ToString()
            : connection.Id;

    private bool IsAuthLocked(string sourceKey)
    {
        var now = DateTime.UtcNow;
        if (_globalAuthLockedUntil != DateTime.MinValue)
        {
            if (now < _globalAuthLockedUntil)
            {
                return true;
            }
            _globalAuthLockedUntil = DateTime.MinValue;
            _globalAuthFailures = 0;
        }
        if (_authFailures.TryGetValue(sourceKey, out var state))
        {
            if (now < state.LockedUntil)
            {
                return true;
            }
            if (state.LockedUntil != DateTime.MinValue)
            {
                _authFailures.Remove(sourceKey);
            }
        }
        return false;
    }

    private void RecordAuthFailure(string sourceKey)
    {
        if (!_authFailures.ContainsKey(sourceKey) && _authFailures.Count >= MaxTrackedAuthSources)
        {
            var now = DateTime.UtcNow;
            var expired = _authFailures.Where(kv => kv.Value.LockedUntil != DateTime.MinValue && kv.Value.LockedUntil <= now)
                .Select(kv => kv.Key).ToList();
            foreach (var k in expired) _authFailures.Remove(k);
            // 期限切れ削除後も上限に達している場合は全消去せず、最古の1件のみを追い出して既存のロックアウトを維持する
            if (_authFailures.Count >= MaxTrackedAuthSources)
            {
                string? victimKey = null;
                DateTime oldestLock = DateTime.MaxValue;
                foreach (var kv in _authFailures)
                {
                    if (kv.Value.LockedUntil == DateTime.MinValue)
                    {
                        victimKey = kv.Key;
                        break;
                    }
                    if (kv.Value.LockedUntil < oldestLock)
                    {
                        oldestLock = kv.Value.LockedUntil;
                        victimKey = kv.Key;
                    }
                }
                if (victimKey is not null)
                {
                    _authFailures.Remove(victimKey);
                }
            }
        }
        _authFailures.TryGetValue(sourceKey, out var state);
        int next = state.Failures + 1;
        if (next >= AuthFailureLimit)
        {
            _authFailures[sourceKey] = (0, DateTime.UtcNow + AuthLockout);
        }
        else
        {
            _authFailures[sourceKey] = (next, DateTime.MinValue);
        }
        if (++_globalAuthFailures >= GlobalAuthFailureLimit)
        {
            _globalAuthFailures = 0;
            _globalAuthLockedUntil = DateTime.UtcNow + AuthLockout;
        }
    }

    private void ClearAuthFailure(string sourceKey)
    {
        _authFailures.Remove(sourceKey);
        _globalAuthFailures = 0;
    }

    private static void Reject(IMessageConnection connection, string reason)
    {
        connection.Send(new JoinRejected(reason));
        _ = connection.CloseAsync();
    }

    private static string SanitizeName(string name, int seatId)
    {
        var trimmed = new string((name ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (trimmed.Length == 0)
        {
            trimmed = $"プレイヤー{seatId}";
        }
        return trimmed.Length > 16 ? trimmed[..16] : trimmed;
    }

    // ------------------------------------------------------------------
    // 送信
    // ------------------------------------------------------------------

    private void OnEngineEvent(object? sender, GameEvent e)
    {
        // 演出用の通知（受け手ごとに非公開情報をフィルタ）
        foreach (var seat in ConnectedSeats())
        {
            if (ClientViewBuilder.ToNotice(e, seat.Id) is { } notice)
            {
                seat.Connection!.Send(new GameNotice(notice));
            }
        }

        // 盤面の更新は、判断要求・ラウンド/ゲーム終了・操作主体の変更のタイミングで送る
        if (e is DecisionRequestedEvent or RoundEndedEvent or GameEndedEvent or PlayerKindChangedEvent or FieldFlushedEvent)
        {
            foreach (var seat in ConnectedSeats())
            {
                SendView(seat);
            }
        }
    }

    private IEnumerable<Seat> ConnectedSeats()
        => _seats.Where(s => s.Connection is not null && !s.Connection.IsClosed);

    private void SendView(Seat seat)
    {
        if (Engine is null || seat.Connection is null)
        {
            return;
        }
        var avatarMap = _seats.ToDictionary(s => s.Id, s => s.AvatarIcon);
        seat.Connection.Send(new ViewUpdate(ClientViewBuilder.Build(Engine, seat.Id, ++_viewSequence, avatarMap)));
    }

    private void Broadcast(Func<Seat, NetMessage> factory)
    {
        foreach (var seat in ConnectedSeats())
        {
            seat.Connection!.Send(factory(seat));
        }
    }

    private void NotifyLobby()
    {
        var seats = Seats;
        Broadcast(_ => new LobbyUpdate(seats));
        LobbyChanged?.Invoke(this, seats);
    }

    private RoomInfo BuildRoomInfo()
    {
        // UDP 応答スレッドから呼ばれるため、読み取りのみ（多少古い値でも問題ない）
        int open = _seats.Count(s => s.Id != 0 && s.Kind == PlayerKind.Npc && s.ClientId is null && !s.IsLocked);
        bool playing = Engine is not null && Engine.Phase != GamePhase.GameEnded;
        return new RoomInfo(
            SanitizeText(_options.RoomName, 64, "DTTS の部屋"),
            SanitizeText(_options.HostPlayerName, 32, "ホスト"),
            null,
            Port,
            open,
            playing,
            NetProtocol.Version);
    }

    private static string? SanitizeAvatarIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var clean = new string(icon.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > 32 ? clean[..32] : clean;
    }

    private static string SanitizeText(string? raw, int maxLen, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }
}

/// <summary>
/// NPCの思考時間を一回限りの遅延（ワンショットタイマー）で待ってから、ディスパッチャ上で実行するスケジューラー。
/// 状態を繰り返し確認するポーリングではなく、判断要求1件につきタイマーが1回だけ発火する。
/// </summary>
public sealed class DispatcherNpcScheduler : INpcScheduler, IDisposable
{
    private readonly ISerialDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    /// <summary>スケジューラーを生成する。</summary>
    public DispatcherNpcScheduler(ISerialDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public void Schedule(TimeSpan delay, Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (delay <= TimeSpan.Zero)
        {
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0) action();
            });
            return;
        }
        var token = _cts.Token;
        _ = Task.Delay(delay, token).ContinueWith(t =>
        {
            if (t.IsCanceled || Volatile.Read(ref _disposed) != 0) return;
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0) action();
            });
        }, TaskScheduler.Default);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
    }
}
