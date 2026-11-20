using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DTTS.Services.Database;

/// <summary>フィードバックの種類。</summary>
public enum FeedbackCategory : byte
{
    /// <summary>不具合報告。</summary>
    Bug = 0,
    /// <summary>要望。</summary>
    Request,
    /// <summary>感想・その他。</summary>
    Comment,
}

/// <summary>フィードバックの送信状態。</summary>
public enum FeedbackStatus : byte
{
    /// <summary>未送信（端末内に蓄積中）。</summary>
    Pending = 0,
    /// <summary>送信済み。</summary>
    Sent,
}

/// <summary>メッセージの送り手（チャット風表示の左右振り分け用）。</summary>
public enum FeedbackSender : byte
{
    /// <summary>ユーザー。</summary>
    User = 0,
    /// <summary>アプリからの自動メッセージ（受付・送信完了通知など）。</summary>
    System,
}

/// <summary>フィードバック1件。</summary>
public sealed record FeedbackEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    FeedbackCategory Category,
    string Message,
    FeedbackSender Sender,
    string? AppVersion,
    FeedbackStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset? SentAt);

/// <summary>フィードバックの保存・取得。</summary>
public sealed class FeedbackRepository
{
    private readonly AppDatabaseContext _db;

    /// <summary>リポジトリを生成する。</summary>
    public FeedbackRepository(AppDatabaseContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>追加する。</summary>
    public async Task AddAsync(FeedbackEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feedback(id, created_at, category, message, sender, app_version, status, attempts, last_error, sent_at)
            VALUES ($id, $at, $cat, $msg, $sender, $ver, $status, $attempts, $err, $sent);
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$at", AppDatabaseContext.ToDbTime(entry.CreatedAt));
        cmd.Parameters.AddWithValue("$cat", entry.Category.ToString());
        cmd.Parameters.AddWithValue("$msg", entry.Message);
        cmd.Parameters.AddWithValue("$sender", entry.Sender.ToString());
        cmd.Parameters.AddWithValue("$ver", (object?)entry.AppVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("$attempts", entry.Attempts);
        cmd.Parameters.AddWithValue("$err", (object?)entry.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sent", entry.SentAt is { } s ? AppDatabaseContext.ToDbTime(s) : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全件を古い順に取得する（チャット風表示用）。</summary>
    public Task<IReadOnlyList<FeedbackEntry>> GetAllAsync(CancellationToken cancellationToken = default)
        => QueryAsync("SELECT * FROM feedback ORDER BY created_at, rowid;", null, cancellationToken);

    /// <summary>未送信のユーザー投稿を古い順に取得する。</summary>
    public Task<IReadOnlyList<FeedbackEntry>> GetPendingAsync(CancellationToken cancellationToken = default)
        => QueryAsync(
            "SELECT * FROM feedback WHERE status = $status AND sender = $sender ORDER BY created_at, rowid;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", FeedbackStatus.Pending.ToString());
                cmd.Parameters.AddWithValue("$sender", FeedbackSender.User.ToString());
            },
            cancellationToken);

    /// <summary>送信済みにする。</summary>
    public async Task MarkSentAsync(Guid id, DateTimeOffset sentAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE feedback SET status = $status, sent_at = $sent, attempts = attempts + 1, last_error = NULL WHERE id = $id;";
        cmd.Parameters.AddWithValue("$status", FeedbackStatus.Sent.ToString());
        cmd.Parameters.AddWithValue("$sent", AppDatabaseContext.ToDbTime(sentAt));
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>送信失敗を記録する（未送信のまま、次の契機で再送する）。</summary>
    public async Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE feedback SET attempts = attempts + 1, last_error = $err WHERE id = $id;";
        cmd.Parameters.AddWithValue("$err", error.Length > 500 ? error[..500] : error);
        cmd.Parameters.AddWithValue("$id", id.ToString("N"));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>すべて削除する。</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AppDatabaseContext.ExecuteAsync(connection, "DELETE FROM feedback;", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FeedbackEntry>> QueryAsync(string sql, Action<SqliteCommand>? bind, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        var list = new List<FeedbackEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rawId = reader.GetString(reader.GetOrdinal("id"));
            if (!Guid.TryParseExact(rawId, "N", out var entryId))
            {
                entryId = Guid.Empty;
            }
            list.Add(new FeedbackEntry(
                entryId,
                AppDatabaseContext.FromDbTime(reader.GetString(reader.GetOrdinal("created_at"))),
                Enum.TryParse<FeedbackCategory>(reader.GetString(reader.GetOrdinal("category")), out var cat) ? cat : FeedbackCategory.Comment,
                reader.GetString(reader.GetOrdinal("message")),
                Enum.TryParse<FeedbackSender>(reader.GetString(reader.GetOrdinal("sender")), out var sender) ? sender : FeedbackSender.User,
                reader.IsDBNull(reader.GetOrdinal("app_version")) ? null : reader.GetString(reader.GetOrdinal("app_version")),
                Enum.TryParse<FeedbackStatus>(reader.GetString(reader.GetOrdinal("status")), out var st) ? st : FeedbackStatus.Pending,
                reader.GetInt32(reader.GetOrdinal("attempts")),
                reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error")),
                reader.IsDBNull(reader.GetOrdinal("sent_at")) ? null : AppDatabaseContext.FromDbTime(reader.GetString(reader.GetOrdinal("sent_at")))));
        }
        return list;
    }
}
