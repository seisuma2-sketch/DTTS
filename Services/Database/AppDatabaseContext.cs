using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DTTS.Services.Database;

/// <summary>
/// アプリのSQLiteデータベース（戦績・ルール設定・テーマ・NPC設定・フィードバック）。
/// <para>
/// 操作ごとに接続を開閉する（Microsoft.Data.Sqlite の接続プールにより低コスト）。
/// スキーマは <c>PRAGMA user_version</c> で管理し、<see cref="InitializeAsync"/> で前方マイグレーションを適用する。
/// </para>
/// </summary>
public sealed class AppDatabaseContext
{
    /// <summary>既定のファイル名。</summary>
    public const string DefaultFileName = "dtts.db";

    /// <summary>スキーマのマイグレーション（インデックス＋1 がバージョン番号）。既存の要素は変更せず、末尾に追加すること。</summary>
    private static readonly string[] Migrations =
    {
        // v1: 初期スキーマ
        """
        CREATE TABLE app_settings (
            key         TEXT PRIMARY KEY,
            value       TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE TABLE match_history (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            played_at       TEXT    NOT NULL,
            rounds_played   INTEGER NOT NULL,
            rules_json      TEXT    NOT NULL,
            is_network      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE match_players (
            match_id        INTEGER NOT NULL REFERENCES match_history(id) ON DELETE CASCADE,
            player_id       INTEGER NOT NULL,
            name            TEXT    NOT NULL,
            kind            TEXT    NOT NULL,
            npc_profile     TEXT,
            is_local_human  INTEGER NOT NULL DEFAULT 0,
            total_score     INTEGER NOT NULL,
            place           INTEGER NOT NULL,
            PRIMARY KEY (match_id, player_id)
        );

        CREATE TABLE round_results (
            match_id        INTEGER NOT NULL REFERENCES match_history(id) ON DELETE CASCADE,
            round_number    INTEGER NOT NULL,
            is_extra        INTEGER NOT NULL,
            is_gekokujo     INTEGER NOT NULL,
            player_id       INTEGER NOT NULL,
            position        INTEGER NOT NULL,
            rank            TEXT    NOT NULL,
            points          INTEGER NOT NULL,
            finish_kind     TEXT    NOT NULL,
            PRIMARY KEY (match_id, round_number, player_id)
        );

        CREATE INDEX ix_match_history_played_at ON match_history(played_at DESC);
        CREATE INDEX ix_match_players_human ON match_players(is_local_human, match_id);

        CREATE TABLE feedback (
            id          TEXT PRIMARY KEY,
            created_at  TEXT    NOT NULL,
            category    TEXT    NOT NULL,
            message     TEXT    NOT NULL,
            sender      TEXT    NOT NULL,
            app_version TEXT,
            status      TEXT    NOT NULL,
            attempts    INTEGER NOT NULL DEFAULT 0,
            last_error  TEXT,
            sent_at     TEXT
        );

        CREATE INDEX ix_feedback_status ON feedback(status, created_at);
        """,
    };

    /// <summary>データベースコンテキストを生成する。</summary>
    /// <param name="databasePath">データベースファイルのパス。</param>
    public AppDatabaseContext(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("データベースのパスを指定してください。", nameof(databasePath));
        }
        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    /// <summary>データベースファイルのフルパス。</summary>
    public string DatabasePath { get; }

    /// <summary>接続文字列。</summary>
    public string ConnectionString { get; }

    /// <summary>現在のスキーマバージョン。</summary>
    public static int LatestSchemaVersion => Migrations.Length;

    /// <summary>既定の保存先（アプリのローカルフォルダ直下）のパスを返す。</summary>
    public static string GetDefaultPath(string localFolder) => Path.Combine(localFolder, DefaultFileName);

    /// <summary>
    /// データベースを作成し、未適用のマイグレーションを順に適用する。アプリ起動時に1回呼ぶ。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL：読み取りと書き込みの並行性向上、クラッシュ耐性
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

        int version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false));
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"データベースのバージョン（{version}）がアプリの対応バージョン（{Migrations.Length}）より新しいため開けません。");
        }

        for (int v = version; v < Migrations.Length; v++)
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Migrations[v];
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version = {v + 1};";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>接続を開く（外部キー制約を有効化済み）。</summary>
    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>日時をDB保存用の文字列（ISO 8601, UTC）に変換する。</summary>
    internal static string ToDbTime(DateTimeOffset time) => time.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>DB保存用の文字列から日時を復元する。</summary>
    internal static DateTimeOffset FromDbTime(string value)
        => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
