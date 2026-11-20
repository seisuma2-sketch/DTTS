using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Network;
using DTTS.Services.Presentation;

namespace DTTS.ViewModels;

/// <summary>プレイ履歴の1行の種類。</summary>
public enum PlayHistoryKind : byte
{
    /// <summary>ラウンドの開始。</summary>
    RoundStart = 0,
    /// <summary>カードを出した。</summary>
    Play,
    /// <summary>パスした。</summary>
    Pass,
    /// <summary>特殊効果が起きた。</summary>
    Effect,
    /// <summary>場が流れた。</summary>
    Flush,
    /// <summary>あがり・脱落。</summary>
    Finish,
}

/// <summary>プレイ履歴（ログ）の1行。</summary>
/// <param name="Round">ラウンド番号。</param>
/// <param name="Kind">種類。</param>
/// <param name="PlayerId">関係するプレイヤー（ラウンド開始・場流しなどは null）。</param>
/// <param name="PlayerName">表示名。</param>
/// <param name="Text">1行の説明（例：「アカネ：♠5・♥5」）。</param>
/// <param name="Cards">出したカード（出し札以外は空）。</param>
public sealed record PlayHistoryEntry(int Round, PlayHistoryKind Kind, int? PlayerId, string PlayerName, string Text, IReadOnlyList<Card> Cards);

/// <summary>ラウンド結果の1行（順位表）。</summary>
/// <param name="Position">順位（1〜）。</param>
/// <param name="PlayerId">プレイヤー。</param>
/// <param name="PlayerName">表示名。</param>
/// <param name="Rank">このラウンドで決まった階級。</param>
/// <param name="RankTitle">階級の表示名（大富豪など）。</param>
/// <param name="Points">このラウンドの得点。</param>
/// <param name="TotalScore">合計点（ゲストには届かないため null）。</param>
/// <param name="FinishKind">あがり方（通常・反則・都落ちなど。分からなければ null）。</param>
/// <param name="IsMe">自分の行か。</param>
public sealed record RoundResultRow(
    int Position,
    int PlayerId,
    string PlayerName,
    PlayerRank Rank,
    string RankTitle,
    int Points,
    int? TotalScore,
    FinishKind? FinishKind,
    bool IsMe)
{
    /// <summary>得点の表示（+2pt / 0pt / -1pt）。</summary>
    public string PointsText => Points > 0 ? $"+{Points}pt" : $"{Points}pt";

    /// <summary>合計の表示（不明なら空）。</summary>
    public string TotalText => TotalScore is { } t ? (t > 0 ? $"+{t}pt" : $"{t}pt") : "";
}

/// <summary>ラウンド結果のまとめ（順位表と、都落ち・下剋上などの出来事）。</summary>
/// <param name="RoundNumber">ラウンド番号。</param>
/// <param name="IsExtraRound">延長戦か。</param>
/// <param name="IsGameOver">ゲーム終了か。</param>
/// <param name="Rows">順位表（1位から）。</param>
/// <param name="IsGekokujo">下剋上が起きたか。</param>
/// <param name="MiyakoOchiPlayerId">都落ちしたプレイヤー。</param>
public sealed record RoundResultSummary(
    int RoundNumber,
    bool IsExtraRound,
    bool IsGameOver,
    IReadOnlyList<RoundResultRow> Rows,
    bool IsGekokujo,
    int? MiyakoOchiPlayerId);

/// <summary>
/// 対戦画面の情報まわり：プレイ履歴（ログ）・「出す」が押せない理由・構造化したラウンド結果・定型リアクションの送信。
/// 1人用・ホストはエンジンのイベント、ゲストはホストからの通知から、同じ形のデータを作る。
/// </summary>
public sealed partial class GameTableViewModel
{
    // ------------------------------------------------------------------
    // プレイ履歴（ログ）
    // ------------------------------------------------------------------

    /// <summary>履歴に残す行数の上限（古いものから捨てる）。</summary>
    public const int MaxHistoryEntries = 300;

    /// <summary>プレイ履歴（古い順。新しい行は末尾に足す）。</summary>
    public ObservableCollection<PlayHistoryEntry> PlayHistory { get; } = new();

    private int _historyRound;

    /// <summary>履歴の行を足す（上限を超えたら古い行から捨てる）。</summary>
    private void AddHistory(PlayHistoryKind kind, int? playerId, string text, IReadOnlyList<Card>? cards = null)
    {
        string name = playerId is { } id && id >= 0 && id < 4 ? GetPlayerVm(id).Name : "";
        PlayHistory.Add(new PlayHistoryEntry(_historyRound, kind, playerId, name, text, cards ?? Array.Empty<Card>()));
        while (PlayHistory.Count > MaxHistoryEntries)
        {
            PlayHistory.RemoveAt(0);
        }
    }

    private static string CardsText(IEnumerable<Card> cards) => string.Join("・", cards.Select(c => c.ToString()));

    /// <summary>1人用・ホスト：エンジンのイベントから履歴を作る。</summary>
    private void RecordHistory(GameEvent ev)
    {
        switch (ev)
        {
            case RoundStartedEvent rs:
                if (rs.RoundNumber == 1) PlayHistory.Clear();
                _historyRound = rs.RoundNumber;
                AddHistory(PlayHistoryKind.RoundStart, null, rs.IsExtraRound ? $"延長戦（第 {rs.RoundNumber} ラウンド）開始" : $"第 {rs.RoundNumber} ラウンド開始");
                break;
            case CardsPlayedEvent cp when cp.Judgement.Play is { } play:
                AddHistory(PlayHistoryKind.Play, cp.PlayerId, $"{GetPlayerVm(cp.PlayerId).Name}：{CardsText(play.Cards)}", play.Cards.ToList());
                break;
            case PlayerPassedEvent pp:
                AddHistory(PlayHistoryKind.Pass, pp.PlayerId, pp.IsFourStopDecline
                    ? $"{GetPlayerVm(pp.PlayerId).Name}：見送り（パス）"
                    : $"{GetPlayerVm(pp.PlayerId).Name}：パス");
                break;
            case EffectAppliedEvent ea when ea.Applied && EffectLabel(ea.Effect.Type) is { } label:
                AddHistory(PlayHistoryKind.Effect, ea.PlayerId, $"{GetPlayerVm(ea.PlayerId).Name}：{label}");
                break;
            case FieldFlushedEvent ff:
                AddHistory(PlayHistoryKind.Flush, ff.NextLeaderId, ff.NextLeaderId is { } leader
                    ? $"場が流れました（次の親：{GetPlayerVm(leader).Name}）"
                    : "場が流れました");
                break;
            case PlayerFinishedEvent pf:
                AddHistory(PlayHistoryKind.Finish, pf.PlayerId, FinishText(pf.PlayerId, pf.Kind, pf.Place));
                break;
        }
    }

    /// <summary>ゲスト：ホストからの通知から履歴を作る。</summary>
    private void RecordHistory(NoticeDto notice)
    {
        switch (notice.Kind)
        {
            case "RoundStarted":
                if (notice.Count <= 1) PlayHistory.Clear();
                _historyRound = notice.Count;
                AddHistory(PlayHistoryKind.RoundStart, null, $"第 {notice.Count} ラウンド開始");
                break;
            case "CardsPlayed" when notice.PlayerId is { } p && notice.CardIds is { } ids:
                var cards = ids.Select(Card.FromId).ToList();
                AddHistory(PlayHistoryKind.Play, p, $"{GetPlayerVm(p).Name}：{CardsText(cards)}", cards);
                break;
            case "PlayerPassed" when notice.PlayerId is { } p:
                AddHistory(PlayHistoryKind.Pass, p, $"{GetPlayerVm(p).Name}：パス");
                break;
            case "FourStopDeclined" when notice.PlayerId is { } p:
                AddHistory(PlayHistoryKind.Pass, p, $"{GetPlayerVm(p).Name}：見送り（パス）");
                break;
            case "EffectApplied" when notice.Applied && notice.PlayerId is { } p && notice.Effect is { } e && EffectLabel(e) is { } label:
                AddHistory(PlayHistoryKind.Effect, p, $"{GetPlayerVm(p).Name}：{label}");
                break;
            case "FieldFlushed":
                AddHistory(PlayHistoryKind.Flush, notice.PlayerId, notice.PlayerId is { } leader
                    ? $"場が流れました（次の親：{GetPlayerVm(leader).Name}）"
                    : "場が流れました");
                break;
            case "PlayerFinished" when notice.PlayerId is { } p && notice.FinishKind is { } kind:
                AddHistory(PlayHistoryKind.Finish, p, FinishText(p, kind, notice.Count));
                break;
        }
    }

    private string FinishText(int playerId, FinishKind kind, int place)
    {
        string name = GetPlayerVm(playerId).Name;
        return kind switch
        {
            FinishKind.Foul => $"{name}：反則負け",
            FinishKind.MiyakoOchi => $"{name}：都落ち",
            FinishKind.LastRemaining => place > 0 ? $"{name}：{place}位で確定" : $"{name}：順位確定",
            _ => place > 0 ? $"{name}：{place}位であがり" : $"{name}：あがり",
        };
    }

    /// <summary>履歴に残す特殊効果の名前（縛り・ダウンナンバーなど細かいものは残さない）。</summary>
    private static string? EffectLabel(SpecialEffectType type) => type switch
    {
        SpecialEffectType.Revolution or SpecialEffectType.SequenceRevolution => "革命",
        SpecialEffectType.Coup => "クーデター",
        SpecialEffectType.Omen => "オーメン",
        SpecialEffectType.Nanasan => "ナナサン革命",
        SpecialEffectType.Emperor => "エンペラー",
        SpecialEffectType.EightCut => "8切り",
        SpecialEffectType.FourStop => "4止め",
        SpecialEffectType.Sandstorm => "砂嵐",
        SpecialEffectType.Ambulance => "99車",
        SpecialEffectType.Rokurokubi => "ろくろ首",
        SpecialEffectType.ElevenBack => "11バック",
        SpecialEffectType.StrongElevenBack => "強化11バック",
        SpecialEffectType.FiveSkip => "5スキップ",
        SpecialEffectType.ThirteenSkip => "13スキップ",
        SpecialEffectType.NineReverse => "9リバース",
        SpecialEffectType.TwelveReverse => "12リバース",
        SpecialEffectType.SevenPass => "7渡し",
        SpecialEffectType.TenDiscard => "10捨て",
        SpecialEffectType.TwelveBomber => "12ボンバー",
        SpecialEffectType.SpadeThreeReturn => "スペ3返し",
        _ => null,
    };

    // ------------------------------------------------------------------
    // 「出す」が押せない理由
    // ------------------------------------------------------------------

    /// <summary>
    /// カードを選んでいるのに「出す」が押せない理由（押せるとき・何も選んでいないとき・自分の番でないときは空）。
    /// </summary>
    [ObservableProperty]
    public partial string PlayBlockedReason { get; set; } = "";

    /// <summary>出せない理由があるか。</summary>
    public bool HasPlayBlockedReason => PlayBlockedReason.Length > 0;

    partial void OnPlayBlockedReasonChanged(string value) => OnPropertyChanged(nameof(HasPlayBlockedReason));

    /// <summary>1人用・ホスト：判定ロジックで、選んだカードが出せない理由を求める。</summary>
    private void UpdatePlayBlockedReasonFromEngine(IReadOnlyList<Card> selected)
    {
        if (_engine is null || selected.Count == 0 || CanPlay || IsCardSelectionMode
            || _engine.PendingDecision is not { } d || d.PlayerId != _myPlayerId || !d.Kind.IsPlayDecision())
        {
            PlayBlockedReason = "";
            return;
        }
        var hand = _engine.GetPlayer(_myPlayerId).Hand;
        var judgement = _engine.Judge.Judge(selected, _engine.Field, selected.Count == hand.Count);
        PlayBlockedReason = judgement.IsLegal
            ? (judgement.IsForbiddenFinish ? PlayBlockedReasons.Describe(PlayRejectReason.ForbiddenFinish) : "")
            : PlayBlockedReasons.Describe(judgement.RejectReason);
    }

    /// <summary>
    /// ゲスト：判定に使う場の詳細は届かないので、ホストから届いた合法手と照らして理由を推し量る
    /// （出せる手がない・枚数が違う・それ以外）。
    /// </summary>
    private void UpdatePlayBlockedReasonForGuest(IReadOnlyList<Card> selected)
    {
        var decision = _client?.View?.Decision;
        if (selected.Count == 0 || CanPlay || IsCardSelectionMode || decision is null || decision.PlayerId != _myPlayerId || !decision.Kind.IsPlayDecision())
        {
            PlayBlockedReason = "";
            return;
        }
        if (_guestLegalPlays.Count == 0)
        {
            PlayBlockedReason = "出せるカードがありません（パスしてください）";
        }
        else if (_guestLegalPlays.All(lp => lp.Count != selected.Count))
        {
            PlayBlockedReason = PlayBlockedReasons.Describe(PlayRejectReason.CountMismatch);
        }
        else
        {
            PlayBlockedReason = "その組み合わせは今は出せません";
        }
    }

    // ------------------------------------------------------------------
    // 構造化したラウンド結果
    // ------------------------------------------------------------------

    /// <summary>最新のラウンド結果（順位表）。結果を表示していないときは null。</summary>
    [ObservableProperty]
    public partial RoundResultSummary? LastRoundResult { get; set; }

    /// <summary>ラウンド結果の順位表（画面の一覧に並べる用。<see cref="LastRoundResult"/> と同じ内容）。</summary>
    public ObservableCollection<RoundResultRow> RoundResultRows { get; } = new();

    /// <summary>1人用・ホスト：エンジンのラウンド結果から順位表を作る。</summary>
    private void BuildRoundResult(RoundResult result, bool isGameOver)
    {
        var rows = result.Standings
            .OrderBy(s => s.Position)
            .Select(s => new RoundResultRow(
                s.Position,
                s.PlayerId,
                GetPlayerVm(s.PlayerId).Name,
                s.Rank,
                RankTitle(s.Rank),
                s.Points,
                _engine?.GetPlayer(s.PlayerId).TotalScore,
                s.Kind,
                s.PlayerId == _myPlayerId))
            .ToList();
        SetRoundResult(new RoundResultSummary(result.RoundNumber, result.IsExtraRound, isGameOver, rows, result.IsGekokujo, result.MiyakoOchiPlayerId));
    }

    /// <summary>ゲスト：ホストからのラウンド終了の通知から順位表を作る。</summary>
    private void BuildRoundResult(NoticeDto notice, IEnumerable<StandingDto> standings)
    {
        var rows = standings
            .OrderBy(s => s.Position)
            .Select(s => new RoundResultRow(
                s.Position,
                s.PlayerId,
                GetPlayerVm(s.PlayerId).Name,
                s.Rank,
                RankTitle(s.Rank),
                s.Points,
                GetPlayerVm(s.PlayerId).TotalScore,
                s.FinishKind,
                s.PlayerId == _myPlayerId))
            .ToList();
        SetRoundResult(new RoundResultSummary(notice.Count, notice.IsExtraRound, notice.Applied, rows, notice.IsGekokujo, notice.OtherPlayerId));
    }

    private void SetRoundResult(RoundResultSummary summary)
    {
        LastRoundResult = summary;
        RoundResultRows.Clear();
        foreach (var row in summary.Rows)
        {
            RoundResultRows.Add(row);
        }
    }

    // ------------------------------------------------------------------
    // 定型リアクション
    // ------------------------------------------------------------------

    /// <summary>送れる定型リアクション（ボタンに並べる）。</summary>
    public IReadOnlyList<QuickReaction> QuickReactionOptions => QuickReactions.All;

    /// <summary>自由入力のひとことテキスト。</summary>
    [ObservableProperty]
    public partial string CustomReactionText { get; set; } = "";

    private DateTime _lastOwnReaction = DateTime.MinValue;

    /// <summary>連打の間隔（ホスト側の間引きと同じ）。</summary>
    private static readonly TimeSpan OwnReactionInterval = TimeSpan.FromMilliseconds(1200);

    /// <summary>自由入力のひとことを送る。</summary>
    [RelayCommand]
    private void SendCustomReaction()
    {
        if (!QuickReactions.TryEncodeCustom(CustomReactionText, out var payload)) return;
        CustomReactionText = "";
        SendReaction(payload);
    }

    /// <summary>
    /// リアクション（定型コードまたは自由入力ペイロード）を送る。
    /// 通信対戦では全員に届き、1人用では自分の欄に吹き出しを出す。
    /// </summary>
    [RelayCommand]
    private void SendReaction(string? code)
    {
        if (_isShutDown || !QuickReactions.IsValidPayload(code)) return;
        var now = DateTime.UtcNow;
        if (now - _lastOwnReaction < OwnReactionInterval) return;
        _lastOwnReaction = now;

        if (_client is not null)
        {
            _client.SendReaction(code!);
        }
        else if (_hostSession is not null)
        {
            _hostSession.SendReaction(code!);
        }
        else
        {
            ShowReactionBubble(_myPlayerId, code);
        }
    }

    /// <summary>リアクションの吹き出しを、その人の欄に出す（自分の欄も含む）。</summary>
    private void ShowReactionBubble(int playerId, string? code)
    {
        if (_isShutDown || QuickReactions.DisplayTextOf(code) is not { } text) return;
        ReactionTriggered?.Invoke(this, new ReactionEventArgs(playerId, text));
        AddHistory(PlayHistoryKind.Effect, playerId, $"{GetPlayerVm(playerId).Name}「{text}」");
    }

    /// <summary>ホスト：参加者・自分のリアクションが届いた（通信スレッドから来ることがあるので UI スレッドへ移す）。</summary>
    private void OnHostReactionReceived(object? sender, (int SeatId, string Code) reaction)
    {
        _dispatcherQueue.TryEnqueue(() => ShowReactionBubble(reaction.SeatId, reaction.Code));
    }
}
