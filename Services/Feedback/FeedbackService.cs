using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Services.Database;
using DTTS.Services.Settings;

namespace DTTS.Services.Feedback;

/// <summary>
/// ネットワーク接続状態の監視（UI層で Windows の NetworkInformation.NetworkStatusChanged 等を使って実装する）。
/// </summary>
public interface IConnectivityMonitor
{
    /// <summary>インターネットに接続されているか。</summary>
    bool IsOnline { get; }

    /// <summary>接続状態が変化した。</summary>
    event EventHandler? ConnectivityChanged;
}

/// <summary>常にオンライン扱いの監視（テスト・接続状態を取得できない環境用）。</summary>
public sealed class AlwaysOnlineMonitor : IConnectivityMonitor
{
    /// <inheritdoc />
    public bool IsOnline => true;

    /// <inheritdoc />
    public event EventHandler? ConnectivityChanged { add { } remove { } }
}

/// <summary>送信処理の結果。</summary>
/// <param name="Sent">送信できた件数。</param>
/// <param name="Failed">失敗した件数。</param>
/// <param name="Skipped">送信しなかった理由（送信したなら null）。</param>
public sealed record FeedbackSendResult(int Sent, int Failed, string? Skipped = null);

/// <summary>Webhook へ送るJSON（Google Apps Script の doPost で受け取り、スプレッドシート/Driveへ保存する想定）。</summary>
public sealed record FeedbackPayload(
    string Id,
    string CreatedAt,
    string Category,
    string Message,
    string? AppVersion,
    string? Os,
    string Client);

/// <summary>ローカル共有用のエクスポート形式。</summary>
public sealed record FeedbackExport(string ExportedAt, string? AppVersion, IReadOnlyList<FeedbackPayload> Entries);

/// <summary>
/// フィードバックサービス：端末内（SQLite）への蓄積、ローカル共有（JSONエクスポート）、
/// Webhook（Google Apps Script → Google Drive 等）へのバックグラウンド送信。
/// <para>
/// 送信は「投稿時」「オンライン復帰時」「明示的な呼び出し（アプリ起動時など）」の契機でのみ行い、
/// 定期的な再試行（ポーリング）はしない。同時に走る送信処理は常に1つ。
/// </para>
/// <para>
/// 送信内容は本文・種類・日時・（設定で許可した場合）アプリ版数とOS名のみ。端末名・ユーザー名などは含めない。
/// </para>
/// </summary>
public sealed class FeedbackService : IDisposable
{
    /// <summary>1件あたりの最大文字数。</summary>
    public const int MaxMessageLength = 2000;

    private readonly FeedbackRepository _repository;
    private readonly Func<FeedbackOptions> _optionsProvider;
    private readonly HttpClient _http;
    private readonly IConnectivityMonitor _connectivity;
    private readonly string? _appVersion;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _rerunRequested;
    private bool _disposed;

    /// <summary>サービスを生成する。</summary>
    /// <param name="repository">保存先。</param>
    /// <param name="optionsProvider">現在の送信設定を返す関数（設定画面での変更を即時反映）。</param>
    /// <param name="httpClient">HTTPクライアント（テストではスタブのハンドラを注入）。</param>
    /// <param name="connectivity">接続状態の監視。</param>
    /// <param name="appVersion">アプリのバージョン文字列。</param>
    public FeedbackService(
        FeedbackRepository repository,
        Func<FeedbackOptions> optionsProvider,
        HttpClient httpClient,
        IConnectivityMonitor? connectivity = null,
        string? appVersion = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _connectivity = connectivity ?? new AlwaysOnlineMonitor();
        _appVersion = appVersion;
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
    }

    /// <summary>フィードバックが追加された（UIのチャット表示更新用）。</summary>
    public event EventHandler<FeedbackEntry>? EntryAdded;

    /// <summary>送信状態が変わった（送信済みマークの更新用）。</summary>
    public event EventHandler<FeedbackSendResult>? SendCompleted;

    /// <summary>
    /// フィードバックを投稿する。端末内に保存し、自動送信が有効ならバックグラウンドで送信を試みる。
    /// </summary>
    /// <exception cref="ArgumentException">本文が空、または長すぎる場合。</exception>
    public async Task<FeedbackEntry> SubmitAsync(string message, FeedbackCategory category, CancellationToken cancellationToken = default)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("メッセージを入力してください。", nameof(message));
        }
        if (text.Length > MaxMessageLength)
        {
            throw new ArgumentException($"メッセージは{MaxMessageLength}文字以内で入力してください。", nameof(message));
        }

        var entry = new FeedbackEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, category, text, FeedbackSender.User,
            _appVersion, FeedbackStatus.Pending, 0, null, null);
        await _repository.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        EntryAdded?.Invoke(this, entry);

        if (_optionsProvider().AutoSend)
        {
            _ = SendInBackgroundAsync();
        }
        return entry;
    }

    /// <summary>全履歴（チャット風表示用）。</summary>
    public Task<IReadOnlyList<FeedbackEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(cancellationToken);

    /// <summary>
    /// 未送信のフィードバックを送信する。送信先未設定・オフライン・送信中の場合は何もしない。
    /// </summary>
    public async Task<FeedbackSendResult> SendPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return new FeedbackSendResult(0, 0, "disposed");
        }
        var options = _optionsProvider();
        if (!TryGetWebhookUri(options.WebhookUrl, out var uri))
        {
            return new FeedbackSendResult(0, 0, "送信先が未設定です");
        }
        if (!_connectivity.IsOnline)
        {
            return new FeedbackSendResult(0, 0, "オフラインです");
        }
        if (!await _sendLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // 送信中に新しい投稿があった：現在の送信が終わった後にもう一巡させる
            Volatile.Write(ref _rerunRequested, 1);
            return new FeedbackSendResult(0, 0, "送信中です");
        }

        int sent = 0, failed = 0;
        try
        {
            bool networkError = false;
            do
            {
                Volatile.Write(ref _rerunRequested, 0);
                var pending = await _repository.GetPendingAsync(cancellationToken).ConfigureAwait(false);
                foreach (var entry in pending)
                {
                    try
                    {
                        var payload = ToPayload(entry, options.IncludeDiagnostics);
                        using var content = new StringContent(
                            JsonSerializer.Serialize(payload, FeedbackJsonContext.Default.FeedbackPayload),
                            Encoding.UTF8,
                            "application/json");
                        using var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            await _repository.MarkSentAsync(entry.Id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                            sent++;
                        }
                        else
                        {
                            await _repository.MarkFailedAsync(entry.Id, $"HTTP {(int)response.StatusCode}", cancellationToken).ConfigureAwait(false);
                            failed++;
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        await _repository.MarkFailedAsync(entry.Id, ex.Message, cancellationToken).ConfigureAwait(false);
                        failed++;
                        networkError = true;
                        break; // 通信断の可能性が高い：残りは次の契機で送る
                    }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        await _repository.MarkFailedAsync(entry.Id, "タイムアウト", cancellationToken).ConfigureAwait(false);
                        failed++;
                        networkError = true;
                        break;
                    }
                }
            }
            while (!networkError && Volatile.Read(ref _rerunRequested) != 0); // 送信中に届いた投稿の分だけもう一巡
        }
        finally
        {
            _sendLock.Release();
        }

        var result = new FeedbackSendResult(sent, failed);
        SendCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>
    /// ローカル共有用に全履歴をJSONで書き出す（ファイル保存・共有シート・クリップボード等で使用）。
    /// </summary>
    public async Task ExportAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var export = new FeedbackExport(
            DateTimeOffset.UtcNow.ToString("O"),
            _appVersion,
            all.Where(e => e.Sender == FeedbackSender.User).Select(e => ToPayload(e, includeDiagnostics: true)).ToList());
        await JsonSerializer.SerializeAsync(destination, export, FeedbackJsonContext.Default.FeedbackExport, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Webhook URL として使用できるか（https、またはローカル確認用の http://localhost のみ許可）。</summary>
    public static bool TryGetWebhookUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }
        bool ok = parsed.Scheme == Uri.UriSchemeHttps
            || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);
        if (ok)
        {
            uri = parsed;
        }
        return ok;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _connectivity.ConnectivityChanged -= OnConnectivityChanged;
    }

    private void OnConnectivityChanged(object? sender, EventArgs e)
    {
        if (_connectivity.IsOnline && _optionsProvider().AutoSend)
        {
            _ = SendInBackgroundAsync();
        }
    }

    private async Task SendInBackgroundAsync()
    {
        try
        {
            await SendPendingAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // バックグラウンド送信の失敗はアプリを止めない（未送信のまま残り、次の契機で再送）
        }
    }

    private FeedbackPayload ToPayload(FeedbackEntry entry, bool includeDiagnostics)
        => new(
            entry.Id.ToString("N"),
            entry.CreatedAt.ToString("O"),
            entry.Category.ToString(),
            entry.Message,
            includeDiagnostics ? entry.AppVersion ?? _appVersion : null,
            includeDiagnostics ? System.Runtime.InteropServices.RuntimeInformation.OSDescription : null,
            "DTTS");
}

/// <summary>フィードバック用の System.Text.Json ソース生成コンテキスト。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FeedbackPayload))]
[JsonSerializable(typeof(FeedbackExport))]
internal sealed partial class FeedbackJsonContext : JsonSerializerContext
{
}
