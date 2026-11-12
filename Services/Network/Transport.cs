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
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}

/// <summary>
/// 1本のTCP接続上でメッセージを送受信する。
/// <para>
/// 受信はソケットの非同期読み取りを待つイベント駆動（待機ループや定期確認はしない）。
/// 送信はチャネルに積んで単一の書き込みタスクが順に送るため、呼び出し側をブロックせず順序も保証される。
/// 相手の無応答は TCP キープアライブ（OS 機能）で検出する。
/// </para>
/// </summary>
public sealed class MessageConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Channel<NetMessage> _outgoing = Channel.CreateUnbounded<NetMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _closed;
    private Task? _readTask;
    private Task? _writeTask;

    /// <summary>接続をラップする（<see cref="Start"/> で送受信を開始）。</summary>
    public MessageConnection(TcpClient client, string id)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Id = id;
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

    /// <summary>メッセージを受信した（スレッドプール上で呼ばれる）。</summary>
    public event Action<MessageConnection, NetMessage>? MessageReceived;

    /// <summary>切断された（1回だけ呼ばれる。原因の例外は正常切断なら null）。</summary>
    public event Action<MessageConnection, Exception?>? Closed;

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
                var message = NetProtocol.Deserialize(frame);
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
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await FrameCodec.WriteAsync(_stream, NetProtocol.Serialize(message), _cts.Token).ConfigureAwait(false);
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
        try
        {
            _client.Close();
        }
        catch (Exception)
        {
        }
        Closed?.Invoke(this, reason);
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);      // 5秒無通信で確認開始
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);  // 2秒間隔
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); // 3回で切断
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
                var connection = new MessageConnection(client, "c" + Interlocked.Increment(ref _counter));
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
