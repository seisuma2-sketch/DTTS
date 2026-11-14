using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;

namespace DTTS.Services.Network;

// ======================================================================
// メッセージ定義（ホスト権威型：ゲーム進行はホストの GameEngine のみが行い、
// クライアントはコマンドを送って、自分視点の盤面（ClientView）を受け取る）
// ======================================================================

/// <summary>通信メッセージの基底。JSON の "$type" で種類を判別する。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(JoinRequest), "join")]
[JsonDerivedType(typeof(JoinAccepted), "accepted")]
[JsonDerivedType(typeof(JoinRejected), "rejected")]
[JsonDerivedType(typeof(LobbyUpdate), "lobby")]
[JsonDerivedType(typeof(GameStarted), "started")]
[JsonDerivedType(typeof(ViewUpdate), "view")]
[JsonDerivedType(typeof(GameNotice), "notice")]
[JsonDerivedType(typeof(CommandRequest), "command")]
[JsonDerivedType(typeof(CommandResponse), "response")]
[JsonDerivedType(typeof(LeaveMessage), "leave")]
public abstract record NetMessage;

/// <summary>参加要求（クライアント → ホスト）。</summary>
/// <param name="ProtocolVersion">プロトコルバージョン。</param>
/// <param name="PlayerName">表示名。</param>
/// <param name="ClientId">端末ごとのランダムID（再接続時に同じ席へ戻すため）。</param>
public sealed record JoinRequest(int ProtocolVersion, string PlayerName, string ClientId) : NetMessage;

/// <summary>参加承認（ホスト → クライアント）。</summary>
public sealed record JoinAccepted(int SeatId, string RoomName, bool IsRejoin) : NetMessage;

/// <summary>参加拒否。</summary>
public sealed record JoinRejected(string Reason) : NetMessage;

/// <summary>席の状況。</summary>
public sealed record SeatInfo(int SeatId, string Name, PlayerKind Kind, bool IsConnected, bool IsHost);

/// <summary>ロビー（席）の更新。</summary>
public sealed record LobbyUpdate(IReadOnlyList<SeatInfo> Seats) : NetMessage;

/// <summary>ゲーム開始。</summary>
public sealed record GameStarted(string RulesJson) : NetMessage;

/// <summary>自分視点の盤面の更新。</summary>
public sealed record ViewUpdate(ClientView View) : NetMessage;

/// <summary>演出用の出来事の通知（他人の非公開情報は含まない）。</summary>
public sealed record GameNotice(NoticeDto Notice) : NetMessage;

/// <summary>コマンド（クライアント → ホスト）。</summary>
public sealed record CommandRequest(int RequestId, CommandDto Command) : NetMessage;

/// <summary>コマンドの結果（ホスト → クライアント）。</summary>
public sealed record CommandResponse(int RequestId, CommandError Error, PlayRejectReason PlayReject) : NetMessage;

/// <summary>退出・切断の通知。</summary>
public sealed record LeaveMessage(string Reason) : NetMessage;

/// <summary>コマンドの種類。</summary>
public enum CommandKind : byte
{
    /// <summary>カードを出す。</summary>
    Play = 0,
    /// <summary>パス（4止め・砂嵐の割り込みの見送り）。</summary>
    Pass,
    /// <summary>カード選択（交換・7渡し・10捨て）。</summary>
    SelectCards,
    /// <summary>12ボンバーの数字宣言。</summary>
    DeclareRanks,
    /// <summary>革命選択。</summary>
    RevolutionChoice,
}

/// <summary>コマンドの内容。</summary>
public sealed record CommandDto(CommandKind Kind, int[]? CardIds = null, Rank[]? Ranks = null, bool Flag = false);

/// <summary>プレイヤーの公開情報。</summary>
public sealed record PlayerViewDto(int Id, string Name, PlayerKind Kind, int HandCount, PlayerRank Rank, int TotalScore, bool IsFinished);

/// <summary>場の公開情報。</summary>
public sealed record FieldViewDto(
    int[]? CurrentCardIds,
    HandType? CurrentType,
    int? LastPlayerId,
    bool IsRevolution,
    bool IsElevenBack,
    bool IsStrengthReversed,
    Suit[] LockedSuits,
    bool IsNumberLocked,
    PlayDirection Direction,
    bool IsEightCutPending,
    bool IsElevenBackPersistent = false,
    bool IsSandstormStopPending = false);

/// <summary>判断要求（誰が何を判断中か。自分宛てなら詳細込み）。</summary>
public sealed record DecisionViewDto(
    DecisionKind Kind,
    int PlayerId,
    CardSelectionPurpose? Purpose,
    int MinCount,
    int MaxCount,
    int? TargetPlayerId,
    SpecialEffectType? Effect,
    bool CanPass);

/// <summary>
/// クライアント（1人のプレイヤー）視点の盤面。自分の手札のみ実カードを含み、他人は枚数のみ。
/// </summary>
public sealed record ClientView(
    long Sequence,
    GamePhase Phase,
    int RoundNumber,
    bool IsExtraRound,
    int RoundCount,
    int[] SeatOrder,
    int MyPlayerId,
    int[] MyHand,
    PlayerViewDto[] Players,
    FieldViewDto Field,
    DecisionViewDto? Decision,
    int[][]? LegalPlays)
{
    /// <summary>自分の手札（カード）。</summary>
    [JsonIgnore]
    public IReadOnlyList<Card> MyHandCards => MyHand.Select(Card.FromId).ToList();

    /// <summary>自分の判断待ちか。</summary>
    [JsonIgnore]
    public bool IsMyDecision => Decision is not null && Decision.PlayerId == MyPlayerId;
}

/// <summary>出来事の通知。</summary>
/// <param name="Kind">出来事の種類（GameEvent の型名から "Event" を除いたもの。例: "CardsPlayed"）。</param>
/// <param name="PlayerId">主体のプレイヤー。</param>
/// <param name="OtherPlayerId">相手のプレイヤー（カード移動の受け手など）。</param>
/// <param name="CardIds">公開されたカード（非公開なら null）。</param>
/// <param name="Count">枚数など（RoundStarted・RoundEnded はラウンド番号、PlayerFinished はその時点の順位）。</param>
/// <param name="Effect">特殊効果。</param>
/// <param name="FinishKind">あがり方。</param>
/// <param name="Applied">効果が適用されたか。</param>
/// <param name="Standings">ラウンド/ゲームの順位（プレイヤーID順位、点）。</param>
/// <param name="Ranks">宣言された数字（12ボンバー）。</param>
/// <param name="DiscardReason">カードが捨てられた理由（CardsDiscarded）。</param>
public sealed record NoticeDto(
    string Kind,
    int? PlayerId = null,
    int? OtherPlayerId = null,
    int[]? CardIds = null,
    int Count = 0,
    SpecialEffectType? Effect = null,
    FinishKind? FinishKind = null,
    bool Applied = true,
    StandingDto[]? Standings = null,
    Rank[]? Ranks = null,
    CardDiscardReason? DiscardReason = null);

/// <summary>順位情報。</summary>
public sealed record StandingDto(int PlayerId, int Position, PlayerRank Rank, int Points, int TotalScore);

/// <summary>プロトコル定数とシリアライズ。</summary>
public static class NetProtocol
{
    /// <summary>プロトコルバージョン（非互換の変更時に上げる）。</summary>
    public const int Version = 2;

    /// <summary>既定のTCPポート。</summary>
    public const int DefaultTcpPort = 47820;

    /// <summary>既定のUDP探索ポート。</summary>
    public const int DefaultDiscoveryPort = 47821;

    /// <summary>メッセージをUTF-8 JSONにする。</summary>
    public static byte[] Serialize(NetMessage message)
        => JsonSerializer.SerializeToUtf8Bytes(message, NetJsonContext.Default.NetMessage);

    /// <summary>UTF-8 JSONからメッセージを復元する（不正なら null）。</summary>
    public static NetMessage? Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, NetJsonContext.Default.NetMessage);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// エンジンの状態・イベントから、各プレイヤー向けの情報を作る（非公開情報のフィルタリングを一手に担う）。
/// </summary>
public static class ClientViewBuilder
{
    /// <summary>指定プレイヤー視点の盤面を作る。</summary>
    public static ClientView Build(GameEngine engine, int playerId, long sequence)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var field = engine.Field;
        var d = engine.PendingDecision;
        bool mine = d is not null && d.PlayerId == playerId;

        int[][]? legal = null;
        if (mine && d!.Kind.IsPlayDecision())
        {
            legal = engine.GetLegalPlays(playerId).Select(j => j.Play!.Cards.Select(c => c.Id).ToArray()).ToArray();
        }

        return new ClientView(
            sequence,
            engine.Phase,
            engine.RoundNumber,
            engine.IsExtraRound,
            engine.Rules.Progress.RoundCount,
            engine.SeatOrder.ToArray(),
            playerId,
            engine.GetPlayer(playerId).Hand.Select(c => c.Id).ToArray(),
            engine.Players.Select(p => new PlayerViewDto(p.Id, p.Name, p.Kind, p.HandCount, p.Rank, p.TotalScore, p.IsFinished)).ToArray(),
            new FieldViewDto(
                field.CurrentPlay?.Cards.Select(c => c.Id).ToArray(),
                field.CurrentPlay?.Type,
                field.LastPlayerId,
                field.IsRevolution,
                field.IsElevenBack,
                field.IsStrengthReversed,
                field.Lock.LockedSuits.ToArray(),
                field.Lock.IsNumberLocked,
                field.Direction,
                field.IsEightCutPending,
                field.IsElevenBackPersistent,
                field.IsSandstormStopPending),
            d is null ? null : new DecisionViewDto(
                d.Kind,
                d.PlayerId,
                mine ? d.Purpose : null,
                mine ? d.MinCount : 0,
                mine ? d.MaxCount : 0,
                mine || d.Kind.IsInterruptChance() ? d.TargetPlayerId : null,
                d.Effect,
                mine && d.CanPass),
            legal);
    }

    /// <summary>
    /// ゲームイベントを指定プレイヤー向けの通知に変換する（通知不要なイベントは null）。
    /// 交換・7渡し・天変地異のカードは当事者にのみ公開し、他のプレイヤーには枚数だけを伝える。
    /// </summary>
    public static NoticeDto? ToNotice(GameEvent e, int viewerId)
    {
        static int[] Ids(IEnumerable<Card> cards) => cards.Select(c => c.Id).ToArray();
        return e switch
        {
            RoundStartedEvent rs => new NoticeDto("RoundStarted", Count: rs.RoundNumber),
            CardsPlayedEvent cp => new NoticeDto("CardsPlayed", cp.PlayerId, CardIds: Ids(cp.Judgement.Play!.Cards), Count: cp.Judgement.Play!.Count),
            PlayerPassedEvent pp => new NoticeDto(pp.IsFourStopDecline ? "FourStopDeclined" : "PlayerPassed", pp.PlayerId),
            EffectAppliedEvent ea => new NoticeDto("EffectApplied", ea.PlayerId, Count: ea.Effect.Magnitude, Effect: ea.Effect.Type, Applied: ea.Applied),
            PlayersSkippedEvent ps => new NoticeDto("PlayersSkipped", ps.ByPlayerId, CardIds: null, Count: ps.SkippedPlayerIds.Count),
            CardsTransferredEvent ct => new NoticeDto(
                "CardsTransferred",
                ct.FromPlayerId,
                ct.ToPlayerId,
                viewerId == ct.FromPlayerId || viewerId == ct.ToPlayerId ? Ids(ct.Cards) : null,
                ct.Cards.Count),
            CardsDiscardedEvent cd => new NoticeDto("CardsDiscarded", cd.PlayerId, CardIds: Ids(cd.Cards), Count: cd.Cards.Count, DiscardReason: cd.Reason),
            RanksDeclaredEvent rd => new NoticeDto("RanksDeclared", rd.PlayerId, Count: rd.Ranks.Count, Ranks: rd.Ranks.ToArray()),
            FieldFlushedEvent ff => new NoticeDto("FieldFlushed", ff.NextLeaderId, Effect: ff.Cause),
            PlayerFinishedEvent pf => new NoticeDto("PlayerFinished", pf.PlayerId, Count: pf.Place, FinishKind: pf.Kind),
            RoundEndedEvent re => new NoticeDto("RoundEnded", Count: re.Result.RoundNumber, Applied: re.IsGameOver,
                Standings: re.Result.Standings.Select(s => new StandingDto(s.PlayerId, s.Position, s.Rank, s.Points, 0)).ToArray()),
            GameEndedEvent ge => new NoticeDto("GameEnded", Count: ge.Result.RoundsPlayed,
                Standings: ge.Result.Standings.Select(s => new StandingDto(s.PlayerId, s.Place, PlayerRank.None, 0, s.TotalScore)).ToArray()),
            _ => null,
        };
    }
}

/// <summary>通信用の System.Text.Json ソース生成コンテキスト（トリミング安全）。</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NetMessage))]
[JsonSerializable(typeof(RoomInfo))]
internal sealed partial class NetJsonContext : JsonSerializerContext
{
}
