using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    private readonly Seat[] _seats;
    private readonly List<MessageConnection> _pending = new();
    private TcpMessageServer? _server;
    private LanDiscoveryResponder? _discovery;
    private long _viewSequence;

    private sealed class Seat
    {
        public required int Id { get; init; }
        public required string Name { get; set; }
        public required PlayerKind Kind { get; set; }
        public MessageConnection? Connection { get; set; }
        public string? ClientId { get; set; }
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
        _npcScheduler = npcScheduler ?? new DispatcherNpcScheduler(dispatcher);
        if (options.SeatCount < 2 || options.SeatCount > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "席数は2〜8です。");
        }
        _seats = Enumerable.Range(0, options.SeatCount).Select(i => new Seat
        {
            Id = i,
            Name = i == 0 ? options.HostPlayerName : (options.NpcNames.TryGetValue(i, out var n) ? n : $"NPC{i}"),
            Kind = i == 0 ? PlayerKind.Human : PlayerKind.Npc,
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

    /// <summary>現在の席の状況。</summary>
    public IReadOnlyList<SeatInfo> Seats => _seats
        .Select(s => new SeatInfo(s.Id, s.Name, s.Kind, s.Connection is not null && !s.Connection.IsClosed, s.Id == 0))
        .ToArray();

    /// <summary>探索（部屋一覧）に応答できているか。探索用ポートが使用中の場合は false（IP直接指定での参加は可能）。</summary>
    public bool IsDiscoverable => _discovery is not null;

    /// <summary>
    /// 待ち受けと探索応答を開始する。指定ポートが使用中なら空きポートで待ち受け、
    /// 探索用ポートが使えない場合は探索応答なしで続行する。
    /// </summary>
    public Task StartAsync()
    {
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
            engine.EventOccurred += OnEngineEvent;
            Engine = engine;
            Npc = npc;
            GameCreated?.Invoke(this, engine);
            Broadcast(_ => new GameStarted(engine.Rules.ToJson()));
            engine.StartGame();
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _discovery?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var connection in _seats.Select(s => s.Connection).Concat(_pending).Where(c => c is not null).ToList())
        {
            connection!.Send(new LeaveMessage("ホストが部屋を閉じました"));
            await connection.CloseAsync().ConfigureAwait(false);
        }
        Npc?.Dispose();
    }

    // ------------------------------------------------------------------
    // 接続管理
    // ------------------------------------------------------------------

    private void OnConnectionAccepted(MessageConnection connection)
    {
        connection.MessageReceived += (c, m) => _dispatcher.Post(() => OnMessage(c, m));
        connection.Closed += (c, _) => _dispatcher.Post(() => OnClosed(c));
        _dispatcher.Post(() => _pending.Add(connection));
        connection.Start();
    }

    private void OnMessage(MessageConnection connection, NetMessage message)
    {
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
        }
    }

    private void HandleJoin(MessageConnection connection, JoinRequest join)
    {
        _pending.Remove(connection);
        if (join.ProtocolVersion != NetProtocol.Version)
        {
            Reject(connection, "アプリのバージョンが異なります");
            return;
        }

        bool inGame = Engine is not null && Engine.Phase != GamePhase.GameEnded;

        if (_seats.Any(s => s.ClientId == join.ClientId && s.Connection is not null && !s.Connection.IsClosed))
        {
            Reject(connection, "この端末は既に参加しています");
            return;
        }

        // 再接続：同じ端末IDの席（切断中）へ戻す
        var seat = _seats.FirstOrDefault(s => s.Id != 0 && s.ClientId == join.ClientId && (s.Connection is null || s.Connection.IsClosed));
        bool rejoin = seat is not null;
        if (seat is null)
        {
            if (inGame)
            {
                Reject(connection, "対戦中のため参加できません");
                return;
            }
            seat = _seats.FirstOrDefault(s => s.Id != 0 && s.Kind == PlayerKind.Npc && s.ClientId is null);
            if (seat is null)
            {
                Reject(connection, "満席です");
                return;
            }
        }

        seat.Connection = connection;
        seat.ClientId = join.ClientId;
        seat.Kind = PlayerKind.Remote;
        if (!inGame)
        {
            seat.Name = SanitizeName(join.PlayerName, seat.Id);
        }
        connection.Send(new JoinAccepted(seat.Id, _options.RoomName, rejoin));

        if (inGame)
        {
            connection.Send(new GameStarted(Engine!.Rules.ToJson()));
            Engine.SetPlayerKind(seat.Id, PlayerKind.Remote); // NPC から操作を取り戻す
            SendView(seat);
        }
        NotifyLobby();
    }

    private void HandleCommand(MessageConnection connection, CommandRequest request)
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

    private void OnClosed(MessageConnection connection)
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
        }
        else
        {
            // ロビーでの退出：NPC 席に戻す
            seat.Kind = PlayerKind.Npc;
            seat.ClientId = null;
            seat.Name = seat.NpcName;
        }
        NotifyLobby();
    }

    private static void Reject(MessageConnection connection, string reason)
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
        seat.Connection.Send(new ViewUpdate(ClientViewBuilder.Build(Engine, seat.Id, ++_viewSequence)));
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
        int open = _seats.Count(s => s.Id != 0 && s.Kind == PlayerKind.Npc && s.ClientId is null);
        bool playing = Engine is not null && Engine.Phase != GamePhase.GameEnded;
        return new RoomInfo(_options.RoomName, _options.HostPlayerName, null, Port, open, playing, NetProtocol.Version);
    }
}

/// <summary>
/// NPCの思考時間を一回限りの遅延（ワンショットタイマー）で待ってから、ディスパッチャ上で実行するスケジューラー。
/// 状態を繰り返し確認するポーリングではなく、判断要求1件につきタイマーが1回だけ発火する。
/// </summary>
public sealed class DispatcherNpcScheduler : INpcScheduler
{
    private readonly ISerialDispatcher _dispatcher;

    /// <summary>スケジューラーを生成する。</summary>
    public DispatcherNpcScheduler(ISerialDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public void Schedule(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            _dispatcher.Post(action);
            return;
        }
        _ = Task.Delay(delay).ContinueWith(_ => _dispatcher.Post(action), TaskScheduler.Default);
    }
}
