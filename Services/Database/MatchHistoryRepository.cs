using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using Microsoft.Data.Sqlite;

namespace DTTS.Services.Database;

/// <summary>対戦記録の参加者。</summary>
public sealed record MatchPlayerRecord(int PlayerId, string Name, PlayerKind Kind, string? NpcProfile, bool IsLocalHuman, int TotalScore, int Place);

/// <summary>対戦記録の1ラウンド・1プレイヤー分の成績。</summary>
public sealed record RoundResultRecord(int RoundNumber, bool IsExtra, bool IsGekokujo, int PlayerId, int Position, PlayerRank Rank, int Points, FinishKind FinishKind);

/// <summary>1ゲーム分の対戦記録。</summary>
public sealed record MatchRecord(
    long Id,
    DateTimeOffset PlayedAt,
    int RoundsPlayed,
    string RulesJson,
    bool IsNetwork,
    IReadOnlyList<MatchPlayerRecord> Players,
    IReadOnlyList<RoundResultRecord> Rounds)
{
    /// <summary>この端末の人間プレイヤーの記録（いなければ null）。</summary>
    public MatchPlayerRecord? LocalHuman => Players.FirstOrDefault(p => p.IsLocalHuman);

    /// <summary>
    /// 終了したゲームから記録を作成する。
    /// </summary>
    /// <param name="engine">終了したゲームのエンジン。</param>
    /// <param name="result">ゲーム結果。</param>
    /// <param name="localHumanPlayerId">この端末の人間プレイヤーID（観戦・全員NPCなら null）。</param>
    /// <param name="npcProfiles">NPC席のプロファイル（表示用に記録）。</param>
    /// <param name="isNetwork">通信対戦か。</param>
    public static MatchRecord FromGame(
        GameEngine engine,
        GameResult result,
        int? localHumanPlayerId,
        IReadOnlyDictionary<int, NpcProfile>? npcProfiles = null,
        bool isNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(result);
        var players = result.Standings
            .Select(s =>
            {
                var p = engine.GetPlayer(s.PlayerId);
                string? profile = npcProfiles is not null && npcProfiles.TryGetValue(s.PlayerId, out var prof) ? prof.DisplayName : null;
                return new MatchPlayerRecord(s.PlayerId, s.Name, p.Kind, profile, s.PlayerId == localHumanPlayerId, s.TotalScore, s.Place);
            })
            .OrderBy(p => p.PlayerId)
            .ToList();
        var rounds = result.Rounds
            .SelectMany(r => r.Standings.Select(s =>
                new RoundResultRecord(r.RoundNumber, r.IsExtraRound, r.IsGekokujo, s.PlayerId, s.Position, s.Rank, s.Points, s.Kind)))
            .ToList();
        return new MatchRecord(0, DateTimeOffset.UtcNow, result.RoundsPlayed, engine.Rules.ToJson(), isNetwork, players, rounds);
    }
}

/// <summary>この端末の人間プレイヤーの通算成績。</summary>
public sealed record PlayerStatistics(
    int TotalGames,
    int Wins,
    double AveragePlace,
    int TotalRounds,
    IReadOnlyDictionary<PlayerRank, int> RankCounts,
    int BestScore,
    int CurrentWinStreak)
{
    /// <summary>勝率（0〜1）。</summary>
    public double WinRate => TotalGames == 0 ? 0 : Wins / (double)TotalGames;

    /// <summary>記録なし。</summary>
    public static PlayerStatistics Empty { get; } = new(0, 0, 0, 0, new Dictionary<PlayerRank, int>(), 0, 0);
}

/// <summary>戦績の保存・取得。</summary>
public sealed class MatchHistoryRepository
{
    private readonly AppDatabaseContext _db;

    /// <summary>リポジトリを生成する。</summary>
    public MatchHistoryRepository(AppDatabaseContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>対戦記録を保存し、採番されたIDを返す。</summary>
    public async Task<long> SaveAsync(MatchRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long id;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO match_history(played_at, rounds_played, rules_json, is_network)
                VALUES ($at, $rounds, $rules, $net);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$at", AppDatabaseContext.ToDbTime(record.PlayedAt));
            cmd.Parameters.AddWithValue("$rounds", record.RoundsPlayed);
            cmd.Parameters.AddWithValue("$rules", record.RulesJson);
            cmd.Parameters.AddWithValue("$net", record.IsNetwork ? 1 : 0);
            id = (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        foreach (var p in record.Players)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO match_players(match_id, player_id, name, kind, npc_profile, is_local_human, total_score, place)
                VALUES ($m, $p, $name, $kind, $npc, $human, $score, $place);
                """;
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$p", p.PlayerId);
            cmd.Parameters.AddWithValue("$name", p.Name);
            cmd.Parameters.AddWithValue("$kind", p.Kind.ToString());
            cmd.Parameters.AddWithValue("$npc", (object?)p.NpcProfile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$human", p.IsLocalHuman ? 1 : 0);
            cmd.Parameters.AddWithValue("$score", p.TotalScore);
            cmd.Parameters.AddWithValue("$place", p.Place);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var r in record.Rounds)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO round_results(match_id, round_number, is_extra, is_gekokujo, player_id, position, rank, points, finish_kind)
                VALUES ($m, $r, $extra, $geko, $p, $pos, $rank, $pts, $kind);
                """;
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$r", r.RoundNumber);
            cmd.Parameters.AddWithValue("$extra", r.IsExtra ? 1 : 0);
            cmd.Parameters.AddWithValue("$geko", r.IsGekokujo ? 1 : 0);
            cmd.Parameters.AddWithValue("$p", r.PlayerId);
            cmd.Parameters.AddWithValue("$pos", r.Position);
            cmd.Parameters.AddWithValue("$rank", r.Rank.ToString());
            cmd.Parameters.AddWithValue("$pts", r.Points);
            cmd.Parameters.AddWithValue("$kind", r.FinishKind.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>新しい順に対戦記録を取得する。</summary>
    public async Task<IReadOnlyList<MatchRecord>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var headers = new List<(long Id, DateTimeOffset At, int Rounds, string Rules, bool Net)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, played_at, rounds_played, rules_json, is_network FROM match_history
                ORDER BY played_at DESC, id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                headers.Add((reader.GetInt64(0), AppDatabaseContext.FromDbTime(reader.GetString(1)), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4) != 0));
            }
        }

        var result = new List<MatchRecord>(headers.Count);
        foreach (var h in headers)
        {
            var players = new List<MatchPlayerRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT player_id, name, kind, npc_profile, is_local_human, total_score, place
                    FROM match_players WHERE match_id = $m ORDER BY player_id;
                    """;
                cmd.Parameters.AddWithValue("$m", h.Id);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    players.Add(new MatchPlayerRecord(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        ParseEnum(reader.GetString(2), PlayerKind.Npc),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetInt32(4) != 0,
                        reader.GetInt32(5),
                        reader.GetInt32(6)));
                }
            }

            var rounds = new List<RoundResultRecord>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT round_number, is_extra, is_gekokujo, player_id, position, rank, points, finish_kind
                    FROM round_results WHERE match_id = $m ORDER BY round_number, position;
                    """;
                cmd.Parameters.AddWithValue("$m", h.Id);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rounds.Add(new RoundResultRecord(
                        reader.GetInt32(0),
                        reader.GetInt32(1) != 0,
                        reader.GetInt32(2) != 0,
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        ParseEnum(reader.GetString(5), PlayerRank.Heimin),
                        reader.GetInt32(6),
                        ParseEnum(reader.GetString(7), FinishKind.Normal)));
                }
            }

            result.Add(new MatchRecord(h.Id, h.At, h.Rounds, h.Rules, h.Net, players, rounds));
        }
        return result;
    }

    /// <summary>この端末の人間プレイヤーの通算成績を集計する。</summary>
    public async Task<PlayerStatistics> GetLocalHumanStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);

        int games = 0, wins = 0, best = 0;
        double avgPlace = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN place = 1 THEN 1 ELSE 0 END), 0),
                       COALESCE(AVG(place), 0), COALESCE(MAX(total_score), 0)
                FROM match_players WHERE is_local_human = 1;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                games = reader.GetInt32(0);
                wins = reader.GetInt32(1);
                avgPlace = reader.GetDouble(2);
                best = reader.GetInt32(3);
            }
        }
        if (games == 0)
        {
            return PlayerStatistics.Empty;
        }

        var rankCounts = new Dictionary<PlayerRank, int>();
        int totalRounds = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.rank, COUNT(*) FROM round_results r
                JOIN match_players p ON p.match_id = r.match_id AND p.player_id = r.player_id
                WHERE p.is_local_human = 1 GROUP BY r.rank;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var rank = ParseEnum(reader.GetString(0), PlayerRank.Heimin);
                int n = reader.GetInt32(1);
                rankCounts[rank] = rankCounts.TryGetValue(rank, out var c) ? c + n : n;
                totalRounds += n;
            }
        }

        int streak = 0;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT p.place FROM match_players p JOIN match_history m ON m.id = p.match_id
                WHERE p.is_local_human = 1 ORDER BY m.played_at DESC, m.id DESC;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt32(0) == 1)
            {
                streak++;
            }
        }

        return new PlayerStatistics(games, wins, avgPlace, totalRounds, rankCounts, best, streak);
    }

    /// <summary>すべての戦績を削除する。</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AppDatabaseContext.ExecuteAsync(connection, "DELETE FROM match_history;", cancellationToken).ConfigureAwait(false);
    }

    private static T ParseEnum<T>(string value, T fallback)
        where T : struct, Enum
        => Enum.TryParse<T>(value, out var parsed) ? parsed : fallback;
}
