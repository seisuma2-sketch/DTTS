using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.Services.Network;

/// <summary>参加に失敗した。</summary>
public sealed class JoinFailedException : Exception
{
    /// <summary>例外を生成する。</summary>
    public JoinFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 通信対戦のクライアント。ホストから自分視点の盤面（<see cref="ClientView"/>）と演出用の通知を受け取り、コマンドを送る。
/// <para>
/// イベントは指定したディスパッチャ上で発行する（UI層では DispatcherQueue のラッパーを渡す）。
/// </para>
/// </summary>
public sealed class RemoteGameClient : IAsyncDisposable
{
    private readonly MessageConnection _connection;
    private readonly ISerialDispatcher? _dispatcher;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<CommandResponse>> _requests = new();
    private readonly TaskCompletionSource<NetMessage> _joinResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestId;

    private RemoteGameClient(MessageConnection connection, ISerialDispatcher? dispatcher)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _connection.MessageReceived += OnMessage;
        _connection.Closed += OnClosed;
    }

    /// <summary>自分の席番号。</summary>
    public int SeatId { get; private set; } = -1;

    /// <summary>部屋名。</summary>
    public string RoomName { get; private set; } = string.Empty;

    /// <summary>最新の盤面（ゲーム開始前は null）。</summary>
    public ClientView? View { get; private set; }

    /// <summary>このゲームのルール（ゲーム開始前は null）。</summary>
    public RuleSettings? Rules { get; private set; }

    /// <summary>最新の席の状況。</summary>
    public IReadOnlyList<SeatInfo> Seats { get; private set; } = Array.Empty<SeatInfo>();

    /// <summary>接続中か。</summary>
    public bool IsConnected => !_connection.IsClosed;

    /// <summary>盤面が更新された。</summary>
    public event EventHandler<ClientView>? ViewUpdated;

    /// <summary>演出用の通知を受け取った。</summary>
    public event EventHandler<NoticeDto>? NoticeReceived;

    /// <summary>席の状況が更新された。</summary>
    public event EventHandler<IReadOnlyList<SeatInfo>>? LobbyUpdated;

    /// <summary>ゲームが始まった。</summary>
    public event EventHandler<RuleSettings>? GameStarted;

    /// <summary>ホストとの接続が切れた（理由のメッセージ）。</summary>
    public event EventHandler<string>? Disconnected;

    /// <summary>
    /// ホストへ接続して参加する。
    /// <para>
    /// <b>購読の手順</b>：参加が承認された直後から、ホストは席の状況・ゲーム開始・盤面を送ってくるため、
    /// 戻り値を受け取った時点で一部のイベントは既に発行済みの場合がある（特に対戦中の再接続）。
    /// 呼び出し側は「イベントを購読してから、<see cref="View"/>・<see cref="Seats"/>・<see cref="Rules"/> を1回読む」こと。
    /// プロパティは常に最新の受信内容を保持している。
    /// </para>
    /// </summary>
    /// <param name="host">ホストのアドレス。</param>
    /// <param name="port">ポート。</param>
    /// <param name="playerName">表示名。</param>
    /// <param name="clientId">端末ID（再接続で同じ席に戻るため、端末ごとに固定のランダム値を使う）。</param>
    /// <param name="dispatcher">イベントを発行するディスパッチャ（null なら通信スレッドで発行）。</param>
    /// <param name="timeout">参加応答の待ち時間。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    /// <exception cref="JoinFailedException">接続できない・参加を拒否された場合。</exception>
    public static async Task<RemoteGameClient> JoinAsync(
        string host,
        int port,
        string playerName,
        string clientId,
        ISerialDispatcher? dispatcher = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            tcp.Dispose();
            throw new JoinFailedException($"ホストに接続できませんでした（{host}:{port}）");
        }

        var client = new RemoteGameClient(new MessageConnection(tcp, "host"), dispatcher);
        client._connection.Start();
        client._connection.Send(new JoinRequest(NetProtocol.Version, playerName, clientId));

        NetMessage joinResult;
        try
        {
            joinResult = await client._joinResult.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new JoinFailedException("ホストから応答がありません");
        }
        switch (joinResult)
        {
            case JoinAccepted accepted:
                client.SeatId = accepted.SeatId;
                client.RoomName = accepted.RoomName;
                return client;
            case JoinRejected rejected:
                await client.DisposeAsync().ConfigureAwait(false);
                throw new JoinFailedException(rejected.Reason);
            default:
                await client.DisposeAsync().ConfigureAwait(false);
                throw new JoinFailedException("ホストとの接続が切れました");
        }
    }

    // ------------------------------------------------------------------
    // コマンド
    // ------------------------------------------------------------------

    /// <summary>カードを出す。</summary>
    public Task<CommandResponse> PlayAsync(IEnumerable<Card> cards, CancellationToken cancellationToken = default)
        => SendCommandAsync(new CommandDto(CommandKind.Play, cards.Select(c => c.Id).ToArray()), cancellationToken);

    /// <summary>パス（4止めの見送り）。</summary>
    public Task<CommandResponse> PassAsync(CancellationToken cancellationToken = default)
        => SendCommandAsync(new CommandDto(CommandKind.Pass), cancellationToken);

    /// <summary>カードを選ぶ（交換・7渡し・10捨て）。</summary>
    public Task<CommandResponse> SelectCardsAsync(IEnumerable<Card> cards, CancellationToken cancellationToken = default)
        => SendCommandAsync(new CommandDto(CommandKind.SelectCards, cards.Select(c => c.Id).ToArray()), cancellationToken);

    /// <summary>12ボンバーの数字を宣言する。</summary>
    public Task<CommandResponse> DeclareRanksAsync(IEnumerable<Rank> ranks, CancellationToken cancellationToken = default)
        => SendCommandAsync(new CommandDto(CommandKind.DeclareRanks, Ranks: ranks.ToArray()), cancellationToken);

    /// <summary>革命を起こすか選ぶ。</summary>
    public Task<CommandResponse> ChooseRevolutionAsync(bool apply, CancellationToken cancellationToken = default)
        => SendCommandAsync(new CommandDto(CommandKind.RevolutionChoice, Flag: apply), cancellationToken);

    private async Task<CommandResponse> SendCommandAsync(CommandDto command, CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = tcs;
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        if (!_connection.Send(new CommandRequest(id, command)))
        {
            _requests.TryRemove(id, out _);
            return new CommandResponse(id, CommandError.WrongPhase, PlayRejectReason.None);
        }
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    /// <summary>退出する。</summary>
    public async Task LeaveAsync()
    {
        _connection.Send(new LeaveMessage("退出"));
        await _connection.CloseAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _connection.CloseAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------
    // 受信
    // ------------------------------------------------------------------

    private void OnMessage(MessageConnection connection, NetMessage message)
    {
        switch (message)
        {
            case JoinAccepted or JoinRejected:
                _joinResult.TrySetResult(message);
                break;
            case CommandResponse response:
                if (_requests.TryGetValue(response.RequestId, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
                break;
            case ViewUpdate update:
                Raise(() =>
                {
                    if (View is null || update.View.Sequence > View.Sequence)
                    {
                        View = update.View;
                        ViewUpdated?.Invoke(this, update.View);
                    }
                });
                break;
            case GameNotice notice:
                Raise(() => NoticeReceived?.Invoke(this, notice.Notice));
                break;
            case LobbyUpdate lobby:
                Raise(() =>
                {
                    Seats = lobby.Seats;
                    LobbyUpdated?.Invoke(this, lobby.Seats);
                });
                break;
            case Network.GameStarted started:
                var rules = RuleSettings.FromJson(started.RulesJson);
                Raise(() =>
                {
                    Rules = rules;
                    View = null;
                    GameStarted?.Invoke(this, rules);
                });
                break;
            case LeaveMessage leave:
                Raise(() => Disconnected?.Invoke(this, leave.Reason));
                break;
        }
    }

    private void OnClosed(MessageConnection connection, Exception? error)
    {
        _joinResult.TrySetResult(new LeaveMessage("切断"));
        foreach (var pending in _requests.Values)
        {
            pending.TrySetResult(new CommandResponse(0, CommandError.WrongPhase, PlayRejectReason.None));
        }
        Raise(() => Disconnected?.Invoke(this, error?.Message ?? "ホストとの接続が切れました"));
    }

    private void Raise(Action action)
    {
        if (_dispatcher is null)
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }
}
