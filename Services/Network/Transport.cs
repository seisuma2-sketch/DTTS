using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DTTS.Services.Network;

/// <summary>
/// 長さプレフィックス方式のフレーム（4バイト・ビッグエンディアンの長さ ＋ 本体）。
/// </summary>
public static class FrameCodec
{
    /// <summary>1フレームの最大サイズ（不正な巨大フレームでメモリを消費させない）。</summary>
    public const int MaxFrameBytes = 1 << 20;

    /// <summary>フレームを書き込む。</summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException($"フレームが大きすぎます: {payload.Length} bytes");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>フレームを1つ読み込む。相手が正常に切断した場合は null。</summary>
    /// <exception cref="InvalidDataException">長さが不正、またはフレーム途中で切断された場合。</exception>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        int read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }
        if (read < 4)
        {
            throw new InvalidDataException("フレームヘッダの途中で切断されました。");
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"不正なフレーム長です: {length}");
        }
        var payload = new byte[length];
        if (length > 0)
        {
            using var payloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            payloadCts.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.ReadExactlyAsync(payload, payloadCts.Token).ConfigureAwait(false);
        }
        return payload;
    }
}

/// <summary>
/// メッセージ単位の双方向通信接続（TCP・Bluetooth LE 等で共通に利用する）。
/// </summary>
public interface IMessageConnection : IAsyncDisposable
{
    /// <summary>接続ID。</summary>
    string Id { get; }

    /// <summary>切断済みか。</summary>
    bool IsClosed { get; }

    /// <summary>鍵交換トランスクリプトから導出した6桁の確認コード（SAS）。確立前は null。</summary>
    string? ShortAuthCode => null;

    /// <summary>メッセージを受信した。</summary>
    event Action<IMessageConnection, NetMessage>? MessageReceived;

    /// <summary>切断された（1回だけ呼ばれる。正常切断なら null）。</summary>
    event Action<IMessageConnection, Exception?>? Closed;

    /// <summary>送受信を開始する。</summary>
    void Start();

    /// <summary>メッセージを送信キューに積む（切断後は無視）。</summary>
    bool Send(NetMessage message);

    /// <summary>キューを送り切ってから切断する。</summary>
    Task CloseAsync(Exception? reason = null);
}

/// <summary>
/// 1本のTCP接続上でメッセージを送受信する。
/// <para>
/// すべてのメッセージは <see cref="SecureChannel"/> で暗号化・認証する（接続直後に鍵を交換し、以降のフレームは AES-GCM）。
/// 経路上で盗み見ても、手札・参加 PIN・再接続トークンは読めない。
/// </para>
/// <para>
/// 受信はソケットの非同期読み取りを待つイベント駆動（待機ループや定期確認はしない）。
/// 送信はチャネルに積んで単一の書き込みタスクが順に送るため、呼び出し側をブロックせず順序も保証される。
/// 相手の無応答は TCP キープアライブ（OS 機能）で検出する。
/// </para>
/// </summary>
public sealed class MessageConnection : IMessageConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<NetMessage> _outgoing = Channel.CreateBounded<NetMessage>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly SecureChannel _secure;
    private int _closed;
    private int _ctsDisposed;
    private Task? _readTask;
    private Task? _writeTask;

    /// <summary>接続をラップする（<see cref="Start"/> で送受信を開始）。</summary>
    /// <param name="client">TCP 接続。</param>
    /// <param name="id">接続ID。</param>
    /// <param name="role">暗号化の立場（接続した側＝ゲストは Initiator、受け付けた側＝ホストは Responder）。</param>
    public MessageConnection(TcpClient client, string id, SecureChannelRole role = SecureChannelRole.Initiator)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Id = id;
        _secure = new SecureChannel(role);
        ConfigureKeepAlive(client.Client);
        client.NoDelay = true;
        _stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    }

    /// <summary>接続ID。</summary>
    public string Id { get; }

    /// <summary>相手のアドレス。</summary>
    public IPEndPoint? RemoteEndPoint { get; }

    /// <summary>切断済みか。</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>暗号化の鍵交換が済んでいるか。</summary>
    public bool IsSecured => _secure.IsEstablished;

    /// <inheritdoc />
    public string? ShortAuthCode => _secure.ShortAuthCode;

    /// <summary>メッセージを受信した（スレッドプール上で呼ばれる）。</summary>
    public event Action<IMessageConnection, NetMessage>? MessageReceived;

    /// <summary>切断された（1回だけ呼ばれる。原因の例外は正常切断なら null）。</summary>
    public event Action<IMessageConnection, Exception?>? Closed;

    /// <summary>送受信を開始する。</summary>
    public void Start()
    {
        _readTask ??= Task.Run(ReadLoopAsync);
        _writeTask ??= Task.Run(WriteLoopAsync);
    }

    /// <summary>メッセージを送信キューに積む（切断後は無視）。</summary>
    public bool Send(NetMessage message) => !IsClosed && _outgoing.Writer.TryWrite(message);

    /// <summary>キューを送り切ってから切断する。</summary>
    public async Task CloseAsync(Exception? reason = null)
    {
        _outgoing.Writer.TryComplete();
        if (_writeTask is not null)
        {
            try
            {
                // 送信キューの吐き出しを最大2秒だけ待つ（一回限りのタイムアウト）
                await _writeTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        Shutdown(reason);
        if (_readTask is not null)
        {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Transport read wait: {ex.Message}"); }
        }
        if (_writeTask is not null)
        {
            try { await _writeTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Transport write wait: {ex.Message}"); }
        }
        if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
        {
            _cts.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            // ソケットからの受信を await するイベント駆動ループ（受信がない間は何もしない）
            while (!_cts.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break; // 相手が切断
                }
                if (!_secure.IsEstablished)
                {
                    // 最初のフレームは鍵交換（Hello）。平文のメッセージ（旧版）はここで例外になり、切断する
                    _secure.AcceptHello(frame);
                    continue;
                }
                var message = _secure.OpenMessage(frame);
                if (message is null)
                {
                    throw new InvalidDataException("解釈できないメッセージを受信しました。");
                }
                MessageReceived?.Invoke(this, message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Shutdown(error);
    }

    private async Task WriteLoopAsync()
    {
        Exception? error = null;
        try
        {
            // 鍵交換：ゲストが先に Hello を送り、ホストは相手の Hello を受け取ってから返す。済むまでメッセージは送らない
            if (_secure.Role == SecureChannelRole.Initiator)
            {
                await FrameCodec.WriteAsync(_stream, _secure.CreateHello(), _cts.Token).ConfigureAwait(false);
            }
            await _secure.Established.WaitAsync(SecureChannel.HandshakeTimeout, _cts.Token).ConfigureAwait(false);
            if (_secure.Role == SecureChannelRole.Responder)
            {
                await FrameCodec.WriteAsync(_stream, _secure.CreateHello(), _cts.Token).ConfigureAwait(false);
            }

            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await FrameCodec.WriteAsync(_stream, _secure.SealMessage(message), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
            Shutdown(error);
        }
    }

    private void Shutdown(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        _outgoing.Writer.TryComplete();
        _cts.Cancel();
        _secure.Abort(reason);
        try
        {
            _client.Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            System.Diagnostics.Debug.WriteLine($"Transport close: {ex.Message}");
        }
        _secure.Dispose();
        Closed?.Invoke(this, reason);
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Wi-Fi 省電力モード（PSM）の不要な起床を抑えるため 20 秒無通信で確認開始
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException)
        {
            // 一部の環境では詳細設定不可（OS 既定のキープアライブで動作）
        }
    }
}

/// <summary>TCP の待ち受け（ホスト側）。</summary>
public sealed class TcpMessageServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private int _counter;

    /// <summary>待ち受けを生成する。</summary>
    /// <param name="address">待ち受けアドレス（LAN 公開なら IPAddress.Any）。</param>
    /// <param name="port">ポート（0 なら空きポートを自動割り当て）。</param>
    public TcpMessageServer(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
    }

    /// <summary>実際に待ち受けているポート。</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>接続を受け付けた（受信開始前に呼ばれるので、ここでイベントを購読してから Start する）。</summary>
    public event Action<MessageConnection>? ConnectionAccepted;

    /// <summary>待ち受けを開始する。</summary>
    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            // 接続要求を await するイベント駆動ループ
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                var connection = new MessageConnection(client, "c" + Interlocked.Increment(ref _counter), SecureChannelRole.Responder);
                ConnectionAccepted?.Invoke(connection);
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
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            await _acceptTask.ConfigureAwait(false);
        }
        _cts.Dispose();
    }
}

/// <summary>
/// 処理を1本の論理スレッドで順に実行するディスパッチャ。GameEngine はスレッドセーフではないため、
/// 通信スレッドからの操作はすべてここを経由する（UI層では DispatcherQueue をラップした実装に差し替えてよい）。
/// </summary>
public interface ISerialDispatcher
{
    /// <summary>処理を順番待ちに積む。</summary>
    void Post(Action action);
}

/// <summary>チャネルによる直列実行キュー（待機中はCPUを使わない）。</summary>
public sealed class SerialActionQueue : ISerialDispatcher, IAsyncDisposable
{
    private readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;

    /// <summary>キューを生成し、実行を開始する。</summary>
    public SerialActionQueue()
    {
        _worker = Task.Run(async () =>
        {
            await foreach (var action in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Error?.Invoke(this, ex);
                }
            }
        });
    }

    /// <summary>実行中の処理で例外が発生した。</summary>
    public event EventHandler<Exception>? Error;

    /// <inheritdoc />
    public void Post(Action action) => _channel.Writer.TryWrite(action);

    /// <summary>積まれた処理をすべて実行し終えるのを待つ（テスト用）。</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.SetResult());
        return tcs.Task;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
