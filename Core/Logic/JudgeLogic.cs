using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.Core.Logic;

/// <summary>
/// 役判定ロジックのインターフェース。GameEngine・NPC思考エンジンのテストでモック差し替えできるよう分離している。
/// </summary>
public interface IJudgeLogic
{
    /// <summary>参照中のルール設定。</summary>
    RuleSettings Rules { get; }

    /// <summary>カード集合を解析し、成立し得るすべての解釈を返す。</summary>
    IReadOnlyList<CardPlay> Analyze(IEnumerable<Card> cards);

    /// <summary>カード集合を場に出せるか判定する（解釈は自動選択）。</summary>
    PlayJudgement Judge(IEnumerable<Card> cards, FieldState field, bool isFinishingPlay = false);

    /// <summary>解釈を指定して場に出せるか判定する。</summary>
    PlayJudgement Judge(CardPlay play, FieldState field, bool isFinishingPlay = false);

    /// <summary>手札から出せるすべての合法手を列挙する。</summary>
    IReadOnlyList<PlayJudgement> GetLegalPlays(IReadOnlyCollection<Card> hand, FieldState field);
}

/// <summary>
/// 大富豪の役判定ロジック。
/// <list type="bullet">
/// <item>組み合わせ解析（単騎・複数枚・階段・エンペラー、ジョーカーの複数解釈）</item>
/// <item>場との強弱比較（革命 XOR 11バック の反転考慮）</item>
/// <item>縛り判定（スート縛り・数しば・激しば・両しば・縛り発生回数）</item>
/// <item>特殊効果の検出（全効果は <see cref="RuleSettings"/> の値で有効化・トリガー数字を決定）</item>
/// <item>禁止あがり判定</item>
/// </list>
/// <para>
/// 本クラスは状態を持たず <see cref="FieldState"/> を変更しない（判定結果を返すのみ）。
/// ルール設定はコンストラクタで受け取った参照を毎回読み取るため、設定画面での変更が即時反映される。
/// </para>
/// </summary>
public sealed class JudgeLogic : IJudgeLogic
{
    private readonly RuleSettings _rules;

    /// <summary>判定ロジックを生成する。</summary>
    /// <param name="rules">参照するルール設定。</param>
    public JudgeLogic(RuleSettings rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <inheritdoc />
    public RuleSettings Rules => _rules;

    /// <summary>階段・エンペラーで使用できる最大の基本強さ（2を含められるなら13、不可なら12＝A）。</summary>
    private int SequenceMaxBaseStrength => _rules.Sequence.AllowTwoInSequence ? Card.MaxNaturalStrength : Card.MaxNaturalStrength - 1;

    // ======================================================================
    // 1. 組み合わせ解析
    // ======================================================================

    /// <inheritdoc />
    public IReadOnlyList<CardPlay> Analyze(IEnumerable<Card> cards)
    {
        var result = new List<CardPlay>();
        if (cards is null)
        {
            return result;
        }
        var arr = cards.ToArray();
        if (arr.Length == 0 || arr.Any(c => c is null))
        {
            return result;
        }
        // 同一カードの重複は不正
        if (arr.Select(c => c.Id).Distinct().Count() != arr.Length)
        {
            return result;
        }

        var jokers = arr.Where(c => c.IsJoker).OrderBy(c => c.JokerIndex).ToList();
        var naturals = arr.Where(c => !c.IsJoker).OrderBy(c => c.BaseStrength).ThenBy(c => c.Suit).ToList();
        int n = arr.Length;

        // --- 単騎 ---
        if (n == 1)
        {
            result.Add(CreateSameRankPlay(HandType.Single, naturals, jokers));
            return result;
        }

        // --- 複数枚出し（自然札がすべて同じ数字、またはジョーカーのみ） ---
        if (naturals.Select(c => c.Rank).Distinct().Count() <= 1)
        {
            result.Add(CreateSameRankPlay(HandType.Multiple, naturals, jokers));
        }

        bool jokerAllowedInSequence = jokers.Count == 0 || _rules.Sequence.AllowJokerInSequence;

        // --- 階段（同スート連番） ---
        if (_rules.Sequence.Enabled
            && n >= _rules.Sequence.MinLength
            && naturals.Count > 0
            && jokerAllowedInSequence
            && naturals.Select(c => c.Suit).Distinct().Count() == 1)
        {
            AddConsecutiveInterpretations(result, HandType.Sequence, naturals, jokers);
        }

        // --- エンペラー（4スート1枚ずつの4枚連番） ---
        if (_rules.Sequence.EmperorEnabled
            && n == 4
            && naturals.Count >= 2
            && jokerAllowedInSequence
            && naturals.Select(c => c.Suit).Distinct().Count() == naturals.Count)
        {
            AddConsecutiveInterpretations(result, HandType.Emperor, naturals, jokers);
        }

        return result;
    }

    /// <summary>単騎・複数枚出しの解釈を生成する。</summary>
    private static CardPlay CreateSameRankPlay(HandType type, List<Card> naturals, List<Card> jokers)
    {
        Rank rank = naturals.Count > 0 ? naturals[0].Rank : Rank.Joker;
        int strength = Card.GetBaseStrength(rank);
        var cards = naturals.Concat(jokers).ToList();
        var suits = naturals.Select(c => c.Suit).OrderBy(s => s)
            .Concat(Enumerable.Repeat(Suit.Joker, jokers.Count))
            .ToList();
        var ranks = Enumerable.Repeat(rank, cards.Count).ToList();
        return new CardPlay(type, cards, ranks, suits, strength, strength);
    }

    /// <summary>
    /// 連番系（階段・エンペラー）の全解釈を追加する。
    /// ジョーカーは欠番の補完、および余りを両端に配置する。配置可能な開始位置ごとに1解釈となる。
    /// </summary>
    private void AddConsecutiveInterpretations(List<CardPlay> result, HandType type, List<Card> naturals, List<Card> jokers)
    {
        int n = naturals.Count + jokers.Count;
        var bases = naturals.Select(c => c.BaseStrength).ToList();
        if (bases.Distinct().Count() != bases.Count)
        {
            return; // 同じ数字が重複する連番は不成立
        }

        int min = bases.Min();
        int max = bases.Max();
        int maxTop = SequenceMaxBaseStrength;
        if (max > maxTop)
        {
            return;
        }
        if (max - min + 1 > n)
        {
            return; // 欠番がジョーカーで埋まらない
        }

        for (int start = max - n + 1; start <= min; start++)
        {
            int end = start + n - 1;
            if (start < Card.MinStrength || end > maxTop)
            {
                continue;
            }

            var cards = new List<Card>(n);
            var ranks = new List<Rank>(n);
            var suits = new List<Suit>(n);
            int jokerIndex = 0;
            Suit sequenceSuit = naturals[0].Suit;

            for (int b = start; b <= end; b++)
            {
                ranks.Add(Card.GetRankFromBaseStrength(b));
                var natural = naturals.FirstOrDefault(c => c.BaseStrength == b);
                if (natural is not null)
                {
                    cards.Add(natural);
                    suits.Add(natural.Suit);
                }
                else
                {
                    cards.Add(jokers[jokerIndex++]);
                    // 階段のジョーカーは階段のスートを代用、エンペラーはワイルド
                    suits.Add(type == HandType.Sequence ? sequenceSuit : Suit.Joker);
                }
            }

            result.Add(new CardPlay(type, cards, ranks, suits.OrderBy(s => s).ToList(), start, end));
        }
    }

    // ======================================================================
    // 2. 総合判定
    // ======================================================================

    /// <inheritdoc />
    public PlayJudgement Judge(IEnumerable<Card> cards, FieldState field, bool isFinishingPlay = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        var arr = cards?.ToArray() ?? Array.Empty<Card>();
        if (arr.Length == 0)
        {
            return PlayJudgement.Reject(PlayRejectReason.NoCards);
        }
        if (field.RequiredCard is { } required && !arr.Contains(required))
        {
            return PlayJudgement.Reject(PlayRejectReason.MissingRequiredCard);
        }

        var interpretations = Analyze(arr);
        if (interpretations.Count == 0)
        {
            return PlayJudgement.Reject(PlayRejectReason.InvalidCombination);
        }

        var legal = new List<(CardPlay Play, BeatMethod Method)>();
        PlayRejectReason firstReason = PlayRejectReason.None;
        foreach (var play in interpretations)
        {
            var reason = CheckAgainstField(play, field, out var method);
            if (reason == PlayRejectReason.None)
            {
                legal.Add((play, method));
            }
            else if (firstReason == PlayRejectReason.None)
            {
                firstReason = reason;
            }
        }

        if (legal.Count == 0)
        {
            return PlayJudgement.Reject(firstReason, interpretations[0]);
        }

        var ordered = OrderByPreference(legal, field);
        var alternatives = ordered.Select(x => x.Play).ToArray();

        // 禁止あがりで拒否される解釈があれば、拒否されない解釈を優先して採用する
        PlayJudgement? rejected = null;
        foreach (var (play, method) in ordered)
        {
            var judgement = BuildJudgement(play, method, field, isFinishingPlay, alternatives);
            if (judgement.IsLegal)
            {
                return judgement;
            }
            rejected ??= judgement;
        }
        return rejected!;
    }

    /// <inheritdoc />
    public PlayJudgement Judge(CardPlay play, FieldState field, bool isFinishingPlay = false)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(field);
        if (field.RequiredCard is { } required && !play.Cards.Contains(required))
        {
            return PlayJudgement.Reject(PlayRejectReason.MissingRequiredCard, play);
        }
        var reason = CheckAgainstField(play, field, out var method);
        if (reason != PlayRejectReason.None)
        {
            return PlayJudgement.Reject(reason, play);
        }
        return BuildJudgement(play, method, field, isFinishingPlay, new[] { play });
    }

    /// <summary>手札でパス以外に出せる手があるか。</summary>
    public bool HasAnyLegalPlay(IReadOnlyCollection<Card> hand, FieldState field) => GetLegalPlays(hand, field).Count > 0;

    private PlayJudgement BuildJudgement(
        CardPlay play, BeatMethod method, FieldState field, bool isFinishingPlay, IReadOnlyList<CardPlay> alternatives)
    {
        // 砂嵐の割り込みは枚数の違う場札の上に出すため、縛りは発生させない（直後に場が流れる）
        var nextLock = method == BeatMethod.SandstormStop ? LockState.None : ComputeNextLock(play, field);
        var effects = DetectEffects(play, field, method, nextLock);
        var forbidden = isFinishingPlay ? GetForbiddenFinishReasons(play, field, effects) : ForbiddenFinishReason.None;

        if (forbidden != ForbiddenFinishReason.None && _rules.ForbiddenFinish.Penalty == ForbiddenFinishPenalty.RejectPlay)
        {
            return PlayJudgement.Reject(PlayRejectReason.ForbiddenFinish, play, forbidden);
        }
        return PlayJudgement.Legal(play, method, alternatives, effects, nextLock, forbidden);
    }

    /// <summary>
    /// 複数の合法解釈の優先順位を決める。
    /// 親出し時は「単騎・複数枚 ＞ 階段 ＞ エンペラー」、同種別内では強い解釈（相手が返しにくい）を優先する。
    /// </summary>
    private static List<(CardPlay Play, BeatMethod Method)> OrderByPreference(
        List<(CardPlay Play, BeatMethod Method)> legal, FieldState field)
    {
        bool reversed = field.IsStrengthReversed;
        return legal
            .OrderBy(x => TypePriority(x.Play.Type))
            .ThenByDescending(x => x.Play.GetStrengthRange(reversed).High)
            .ThenByDescending(x => x.Play.GetStrengthRange(reversed).Low)
            .ToList();
    }

    private static int TypePriority(HandType type) => type switch
    {
        HandType.Single => 0,
        HandType.Multiple => 1,
        HandType.Sequence => 2,
        HandType.Emperor => 3,
        _ => 9,
    };

    // ======================================================================
    // 3. 場との比較（強弱・縛り・特殊勝ち）
    // ======================================================================

    /// <summary>
    /// 解釈済みの出し札が現在の場に出せるかを判定する。
    /// </summary>
    /// <param name="play">出し札。</param>
    /// <param name="field">場の状態。</param>
    /// <param name="method">出せる場合、その根拠。</param>
    /// <returns>不正理由。合法なら <see cref="PlayRejectReason.None"/>。</returns>
    public PlayRejectReason CheckAgainstField(CardPlay play, FieldState field, out BeatMethod method)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(field);
        method = BeatMethod.Lead;
        var current = field.CurrentPlay;
        bool reversed = field.IsStrengthReversed;

        // --- 8切り直後の4止め受付中 ---
        if (field.IsEightCutPending)
        {
            if (current is not null && IsFourStop(play, current))
            {
                method = BeatMethod.FourStop;
                return PlayRejectReason.None;
            }
            return PlayRejectReason.FourStopOnly;
        }

        // --- ろくろ首・99車の直後の砂嵐受付中（枚数・種別・縛りは問わず、砂嵐だけが出せる） ---
        if (field.IsSandstormStopPending)
        {
            if (current is not null && IsSandstorm(play))
            {
                method = BeatMethod.SandstormStop;
                return PlayRejectReason.None;
            }
            return PlayRejectReason.SandstormStopOnly;
        }

        // --- 親出し ---
        if (current is null)
        {
            return PlayRejectReason.None;
        }

        // --- スペ3返し（縛りを無視） ---
        if (IsSpadeThreeReturn(play, current))
        {
            method = BeatMethod.SpadeThreeReturn;
            return PlayRejectReason.None;
        }

        // --- 種別・枚数 ---
        bool typeMatches = play.IsSequenceLike ? current.IsSequenceLike : play.Type == current.Type;
        if (!typeMatches)
        {
            return PlayRejectReason.TypeMismatch;
        }
        if (play.Count != current.Count)
        {
            return PlayRejectReason.CountMismatch;
        }

        // --- エンペラー（4枚階段に無条件勝ち・縛り無視） ---
        if (play.Type == HandType.Emperor && current.Type == HandType.Sequence)
        {
            method = BeatMethod.Emperor;
            return PlayRejectReason.None;
        }

        // --- スート縛り ---
        if (field.Lock.IsSuitLocked && !MatchesSuitLock(play, field.Lock))
        {
            return PlayRejectReason.SuitLockViolation;
        }

        var (playLow, _) = play.GetStrengthRange(reversed);
        var (currentLow, currentHigh) = current.GetStrengthRange(reversed);

        // --- 数しば（ちょうど＋1のみ。砂嵐・99車も数しばには従う） ---
        if (field.Lock.IsNumberLocked)
        {
            if (!play.IsJokerOnly && !current.IsJokerOnly && playLow == currentHigh + 1)
            {
                method = BeatMethod.NumberLockFollow;
                return PlayRejectReason.None;
            }
            return PlayRejectReason.NumberLockViolation;
        }

        // --- 砂嵐・99車（同枚数に無条件勝ち。スート縛り・数しばには従う） ---
        if (IsSandstorm(play))
        {
            method = BeatMethod.Sandstorm;
            return PlayRejectReason.None;
        }
        if (IsAmbulance(play))
        {
            method = BeatMethod.Ambulance;
            return PlayRejectReason.None;
        }

        // --- 通常の強さ比較 ---
        if (playLow > currentHigh)
        {
            method = BeatMethod.Stronger;
            return PlayRejectReason.None;
        }

        // --- ダウンナンバー（同スート構成でちょうど1つ下） ---
        if (IsDownNumber(play, current, playLow, currentLow))
        {
            method = BeatMethod.DownNumber;
            return PlayRejectReason.None;
        }

        return PlayRejectReason.NotStronger;
    }

    /// <summary>出し札がスート縛りに合致するか（ジョーカーは任意スート、縛り側のワイルド枠は任意の自然札を受け入れる）。</summary>
    public static bool MatchesSuitLock(CardPlay play, LockState lockState)
    {
        if (!lockState.IsSuitLocked)
        {
            return true;
        }
        if (play.Count != lockState.LockedSuits.Count)
        {
            return false;
        }
        var remaining = lockState.LockedSuits.ToList();
        foreach (var suit in play.NaturalSuits)
        {
            // 同スート枠を優先的に消費し、なければワイルド枠を使う
            if (!remaining.Remove(suit) && !remaining.Remove(Suit.Joker))
            {
                return false;
            }
        }
        return true;
    }

    private bool IsSpadeThreeReturn(CardPlay play, CardPlay current)
    {
        var j = _rules.Joker;
        return j.SpadeThreeReturnEnabled
            && current.Type == HandType.Single && current.IsJokerOnly
            && play.Type == HandType.Single && !play.IsJokerOnly
            && play.Cards[0].Suit == Suit.Spade && play.Cards[0].Rank == j.SpadeThreeReturnRank;
    }

    /// <summary>4止めに必要な4の枚数（標準は8の枚数の2倍）。</summary>
    public int FourStopRequiredCount(CardPlay eightPlay)
    {
        ArgumentNullException.ThrowIfNull(eightPlay);
        return _rules.Flow.FourStopCount == FourStopCountRule.SameAsEight ? eightPlay.Count : eightPlay.Count * 2;
    }

    private bool IsFourStop(CardPlay play, CardPlay eightPlay)
    {
        var f = _rules.Flow;
        // 4は単騎・同じ数字の組（8が1枚でも4は2枚のペアで出す）。階段の8切りは止められない
        return f.FourStopEnabled
            && !eightPlay.IsSequenceLike
            && play.Type is HandType.Single or HandType.Multiple
            && !play.IsJokerOnly
            && play.Count == FourStopRequiredCount(eightPlay)
            && play.Rank == f.FourStopRank
            && (play.JokerCount == 0 || _rules.Joker.JokerTriggersEffects);
    }

    private bool IsSandstorm(CardPlay play)
        => _rules.Flow.SandstormEnabled && IsExactCombination(play, _rules.Flow.SandstormRank, _rules.Flow.SandstormCount);

    private bool IsAmbulance(CardPlay play)
        => _rules.Flow.AmbulanceEnabled && IsExactCombination(play, _rules.Flow.AmbulanceRank, _rules.Flow.AmbulanceCount);

    private bool IsRokurokubi(CardPlay play)
        => _rules.Flow.RokurokubiEnabled && IsExactCombination(play, _rules.Flow.RokurokubiRank, _rules.Flow.RokurokubiCount);

    /// <summary>「特定数字をちょうどN枚」の役か（ジョーカー代用は JokerTriggersEffects 有効時のみ）。</summary>
    private bool IsExactCombination(CardPlay play, Rank rank, int count)
        => play.Type == HandType.Multiple
            && !play.IsJokerOnly
            && play.Rank == rank
            && play.Count == count
            && (play.JokerCount == 0 || _rules.Joker.JokerTriggersEffects);

    private bool IsDownNumber(CardPlay play, CardPlay current, int playStrength, int currentStrength)
    {
        if (!_rules.Special.DownNumberEnabled)
        {
            return false;
        }
        if (play.IsSequenceLike || play.JokerCount > 0 || current.JokerCount > 0)
        {
            return false;
        }
        return playStrength == currentStrength - 1 && play.NaturalSuits.SequenceEqual(current.NaturalSuits);
    }

    // ======================================================================
    // 4. 縛り
    // ======================================================================

    /// <summary>
    /// 出し札を場に置いた後の縛り状態を算出する（場の状態は変更しない）。
    /// 連番判定は出す直前の反転状態で行う。
    /// </summary>
    public LockState ComputeNextLock(CardPlay play, FieldState field)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(field);
        var current = field.CurrentPlay;
        if (current is null)
        {
            return LockState.None; // 場の最初の1手では縛りは発生しない
        }

        var rules = _rules.Lock;
        var previous = field.Lock;

        // --- スート一致 ---
        IReadOnlyList<Suit>? matchedSuits = null;
        int suitStreak = 0;
        if (rules.SuitLockEnabled && play.Count == current.Count)
        {
            if (TryMatchSuits(current, play, out var full))
            {
                matchedSuits = full;
            }
            else if (rules.PartialSuitLockEnabled && play.Count >= 2 && TryMatchPartialSuits(current, play, out var partial))
            {
                matchedSuits = partial;
            }
            if (matchedSuits is not null)
            {
                suitStreak = previous.SuitMatchStreak + 1;
            }
        }

        // --- 連番（＋1） ---
        int numberStreak = 0;
        if (rules.NumberLockEnabled && IsNumberConsecutive(current, play, field.IsStrengthReversed))
        {
            numberStreak = previous.NumberMatchStreak + 1;
        }

        bool newSuitLock = !previous.IsSuitLocked && matchedSuits is not null && suitStreak >= rules.TriggerCount;
        bool newNumberLock = !previous.IsNumberLocked && rules.NumberLockEnabled && numberStreak >= rules.TriggerCount;

        // 激しば無効時：スート縛りと数しばは同時に成立させない（同時成立時はスート縛りを優先）
        if (!rules.StrictLockEnabled)
        {
            if (newSuitLock && newNumberLock)
            {
                newNumberLock = false;
            }
            if (previous.IsSuitLocked)
            {
                newNumberLock = false;
            }
            if (previous.IsNumberLocked)
            {
                newSuitLock = false;
            }
        }

        return new LockState
        {
            LockedSuits = newSuitLock ? matchedSuits! : previous.LockedSuits,
            IsNumberLocked = previous.IsNumberLocked || newNumberLock,
            SuitMatchStreak = suitStreak,
            NumberMatchStreak = numberStreak,
        };
    }

    /// <summary>
    /// 2つの出し札のスート構成が一致するか判定し、一致すれば縛るスート構成を返す。
    /// ジョーカーはワイルドとして相手の自然札と対応付けられるが、
    /// 少なくとも1組は自然札同士でスートが一致している必要がある（ジョーカーだけでは縛りは発生しない）。
    /// </summary>
    public static bool TryMatchSuits(CardPlay previous, CardPlay next, out IReadOnlyList<Suit> lockedSuits)
    {
        lockedSuits = Array.Empty<Suit>();
        if (previous.Count != next.Count)
        {
            return false;
        }
        var common = new List<Suit>();
        var nextRest = next.NaturalSuits.ToList();
        var previousRest = new List<Suit>();
        foreach (var suit in previous.NaturalSuits)
        {
            if (nextRest.Remove(suit))
            {
                common.Add(suit);
            }
            else
            {
                previousRest.Add(suit);
            }
        }

        if (common.Count == 0)
        {
            return false;
        }
        // 対応の取れない自然札は、相手側のジョーカーで吸収できなければ不一致
        if (previousRest.Count > next.JokerCount || nextRest.Count > previous.JokerCount)
        {
            return false;
        }

        var result = common.Concat(previousRest).Concat(nextRest).ToList();
        while (result.Count < next.Count)
        {
            result.Add(Suit.Joker); // 双方ジョーカーの枠はワイルド
        }
        lockedSuits = result.OrderBy(s => s).ToArray();
        return true;
    }

    /// <summary>
    /// 両しば（片縛り）用：一部のスートのみ一致する場合、一致したスートと残りワイルドの構成を返す。
    /// </summary>
    public static bool TryMatchPartialSuits(CardPlay previous, CardPlay next, out IReadOnlyList<Suit> lockedSuits)
    {
        lockedSuits = Array.Empty<Suit>();
        if (previous.Count != next.Count)
        {
            return false;
        }
        var common = new List<Suit>();
        var nextRest = next.NaturalSuits.ToList();
        foreach (var suit in previous.NaturalSuits)
        {
            if (nextRest.Remove(suit))
            {
                common.Add(suit);
            }
        }
        if (common.Count == 0)
        {
            return false;
        }
        while (common.Count < next.Count)
        {
            common.Add(Suit.Joker);
        }
        lockedSuits = common.OrderBy(s => s).ToArray();
        return true;
    }

    /// <summary>
    /// 直前の出し札に対して、強さがちょうど＋1で続いているか（数しばの成立条件）。
    /// 階段の場合は「新しい階段の最弱 ＝ 直前の階段の最強＋1」。
    /// </summary>
    public static bool IsNumberConsecutive(CardPlay previous, CardPlay next, bool reversed)
    {
        if (previous.IsJokerOnly || next.IsJokerOnly)
        {
            return false;
        }
        return next.GetStrengthRange(reversed).Low == previous.GetStrengthRange(reversed).High + 1;
    }

    // ======================================================================
    // 5. 特殊効果検出
    // ======================================================================

    /// <summary>
    /// 出し札で発動する特殊効果を列挙する。並び順はエンジンでの推奨適用順
    /// （割り込み → 革命 → 11バック → 縛り → 手札操作 → 手番操作 → 場流し）。
    /// </summary>
    public IReadOnlyList<TriggeredEffect> DetectEffects(CardPlay play, FieldState field, BeatMethod method, LockState nextLock)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(nextLock);
        var effects = new List<TriggeredEffect>();

        // --- 割り込み・特殊勝ち（場を流さないもの） ---
        if (method == BeatMethod.FourStop)
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.FourStop));
        }
        if (method == BeatMethod.DownNumber)
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.DownNumber));
        }

        // --- 革命系 ---
        var revolution = DetectRevolution(play);
        if (revolution is { } revType && CanRevolutionOccur(field))
        {
            effects.Add(new TriggeredEffect(revType, 1, _rules.Revolution.RevolutionIsOptional));
        }

        // --- 11バック ---
        var eb = _rules.ElevenBack;
        if (eb.Enabled && EffectCount(play, eb.TriggerRank) > 0)
        {
            effects.Add(new TriggeredEffect(eb.StrongElevenBackEnabled ? SpecialEffectType.StrongElevenBack : SpecialEffectType.ElevenBack));
        }

        // --- 縛りの新規成立 ---
        if (!field.Lock.IsSuitLocked && nextLock.IsSuitLocked)
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.SuitLock));
        }
        if (!field.Lock.IsNumberLocked && nextLock.IsNumberLocked)
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.NumberLock));
        }

        // --- 手札操作系 ---
        var sp = _rules.Special;
        AddCountEffect(effects, play, sp.SevenPassEnabled, sp.SevenPassRank, SpecialEffectType.SevenPass);
        AddCountEffect(effects, play, sp.TenDiscardEnabled, sp.TenDiscardRank, SpecialEffectType.TenDiscard);
        AddCountEffect(effects, play, sp.TwelveBomberEnabled, sp.TwelveBomberRank, SpecialEffectType.TwelveBomber);

        // --- 手番操作系 ---
        var sr = _rules.SkipReverse;
        AddCountEffect(effects, play, sr.FiveSkipEnabled, sr.FiveSkipRank, SpecialEffectType.FiveSkip);
        AddCountEffect(effects, play, sr.ThirteenSkipEnabled, sr.ThirteenSkipRank, SpecialEffectType.ThirteenSkip);
        AddCountEffect(effects, play, sr.NineReverseEnabled, sr.NineReverseRank, SpecialEffectType.NineReverse);
        AddCountEffect(effects, play, sr.TwelveReverseEnabled, sr.TwelveReverseRank, SpecialEffectType.TwelveReverse);

        // --- 場流し系（最後に適用） ---
        var fl = _rules.Flow;
        AddCountEffect(effects, play, fl.EightCutEnabled, fl.EightCutRank, SpecialEffectType.EightCut);
        if (IsSandstorm(play))
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.Sandstorm));
        }
        if (IsAmbulance(play))
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.Ambulance));
        }
        if (IsRokurokubi(play))
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.Rokurokubi));
        }
        if (play.Type == HandType.Emperor)
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.Emperor));
        }
        if (method == BeatMethod.SpadeThreeReturn)
        {
            effects.Add(new TriggeredEffect(SpecialEffectType.SpadeThreeReturn));
        }

        return effects;
    }

    /// <summary>
    /// 出し札が満たす革命条件を返す（複数該当時は オーメン ＞ クーデター ＞ ナナサン ＞ 通常革命 の優先順で1つ）。
    /// 場の状態（革命返し可否・オーメンによる封印）はここでは考慮しない。
    /// </summary>
    public SpecialEffectType? DetectRevolution(CardPlay play)
    {
        var r = _rules.Revolution;
        int count = r.JokerCountsForRevolution ? play.Count : play.NaturalCards.Count;

        if (play.Type == HandType.Multiple && !play.IsJokerOnly)
        {
            if (r.OmenEnabled && play.Rank == r.OmenRank && count == r.OmenCount)
            {
                return SpecialEffectType.Omen;
            }
            if (r.CoupEnabled && play.Rank == r.CoupRank && count == r.CoupCount)
            {
                return SpecialEffectType.Coup;
            }
            if (r.NanasanEnabled && play.Rank == r.NanasanRank && count == r.NanasanCount)
            {
                return SpecialEffectType.Nanasan;
            }
            if (r.Enabled && count >= r.MinCards)
            {
                return SpecialEffectType.Revolution;
            }
        }
        else if (play.Type == HandType.Sequence)
        {
            if (r.SequenceRevolutionEnabled && count >= r.SequenceRevolutionMinLength)
            {
                return SpecialEffectType.SequenceRevolution;
            }
        }
        else if (play.Type == HandType.Emperor && _rules.Sequence.EmperorCausesRevolution)
        {
            return SpecialEffectType.Revolution;
        }
        return null;
    }

    /// <summary>現在の場で革命（反転）が起こり得るか（オーメンによる封印・革命返し可否）。</summary>
    public bool CanRevolutionOccur(FieldState field)
    {
        if (field.IsRevolutionLocked)
        {
            return false;
        }
        return !field.IsRevolution || _rules.Revolution.CounterRevolutionEnabled;
    }

    /// <summary>
    /// 数字効果の発動枚数を数える。階段は「階段中効果」有効時のみ、
    /// ジョーカー代用分は「ジョーカー効果発動」有効時のみカウントする。
    /// </summary>
    private int EffectCount(CardPlay play, Rank rank)
    {
        if (play.IsJokerOnly)
        {
            return 0;
        }
        if (play.IsSequenceLike && !_rules.Sequence.EffectsInSequence)
        {
            return 0;
        }
        return play.CountRank(rank, _rules.Joker.JokerTriggersEffects);
    }

    private void AddCountEffect(List<TriggeredEffect> effects, CardPlay play, bool enabled, Rank rank, SpecialEffectType type)
    {
        if (!enabled)
        {
            return;
        }
        int count = EffectCount(play, rank);
        if (count > 0)
        {
            effects.Add(new TriggeredEffect(type, count));
        }
    }

    // ======================================================================
    // 6. 禁止あがり
    // ======================================================================

    /// <summary>
    /// あがり手として出した場合の禁止あがり該当理由を返す（出す直前の場の状態で判定）。
    /// </summary>
    public ForbiddenFinishReason GetForbiddenFinishReasons(CardPlay play, FieldState field, IReadOnlyList<TriggeredEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(play);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(effects);
        var f = _rules.ForbiddenFinish;
        if (!f.Enabled)
        {
            return ForbiddenFinishReason.None;
        }

        var reasons = ForbiddenFinishReason.None;

        if (f.ForbidStrongestNumber)
        {
            bool reversed = f.StrongestFollowsReversal && field.IsStrengthReversed;
            // 反転中は基本強さ最小の数字（3）、通常時は最大の数字（2）が最強
            Rank strongest = Card.GetRankFromBaseStrength(reversed ? Card.MinStrength : Card.MaxNaturalStrength);
            if (play.NaturalCards.Any(c => c.Rank == strongest))
            {
                reasons |= ForbiddenFinishReason.StrongestNumber;
            }
        }
        if (f.ForbidJoker && play.JokerCount > 0)
        {
            reasons |= ForbiddenFinishReason.Joker;
        }
        if (f.ForbidEightCut && effects.Any(e => e.Type == SpecialEffectType.EightCut))
        {
            reasons |= ForbiddenFinishReason.EightCut;
        }
        if (f.ForbidSpadeThree && effects.Any(e => e.Type == SpecialEffectType.SpadeThreeReturn))
        {
            reasons |= ForbiddenFinishReason.SpadeThree;
        }
        if (f.ForbidTwelveBomber && effects.Any(e => e.Type == SpecialEffectType.TwelveBomber))
        {
            reasons |= ForbiddenFinishReason.TwelveBomber;
        }
        if (f.ForbidElevenBack && effects.Any(e => e.Type is SpecialEffectType.ElevenBack or SpecialEffectType.StrongElevenBack))
        {
            reasons |= ForbiddenFinishReason.ElevenBack;
        }
        if (f.ForbidOtherFlushEffects && effects.Any(e => e.Type is
                SpecialEffectType.Sandstorm or SpecialEffectType.Ambulance or
                SpecialEffectType.Rokurokubi or SpecialEffectType.Emperor))
        {
            reasons |= ForbiddenFinishReason.FlushEffect;
        }
        if (f.ForbidRevolution && effects.Any(e => e.IsRevolutionTrigger))
        {
            reasons |= ForbiddenFinishReason.Revolution;
        }
        return reasons;
    }

    // ======================================================================
    // 7. 合法手列挙（NPC思考・パス判定・UIの出せるカード強調表示用）
    // ======================================================================

    /// <inheritdoc />
    public IReadOnlyList<PlayJudgement> GetLegalPlays(IReadOnlyCollection<Card> hand, FieldState field)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(field);
        var results = new List<PlayJudgement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 砂嵐の割り込み受付中は、場札と枚数が違っても砂嵐を出せる。4止めは8の枚数で必要枚数が決まる
        int? requiredCount = field.IsSandstormStopPending
            ? null
            : field.IsEightCutPending && field.CurrentPlay is { } eight
                ? FourStopRequiredCount(eight)
                : field.CurrentPlay?.Count;

        foreach (var candidate in EnumerateCandidates(hand))
        {
            // 場があり、スペ3返し（単騎）以外で枚数が合わない候補は早期に除外
            if (requiredCount is { } rc && candidate.Count != rc)
            {
                continue;
            }
            string key = string.Join(",", candidate.Select(c => c.Id).OrderBy(id => id));
            if (!seen.Add(key))
            {
                continue;
            }
            var judgement = Judge(candidate, field, candidate.Count == hand.Count);
            if (judgement.IsLegal)
            {
                results.Add(judgement);
            }
        }
        return results;
    }

    /// <summary>
    /// 手札から構成し得る候補カード集合を列挙する（合法性は未検証）。
    /// ジョーカーは同一視し、若い番号から順に使用する。
    /// </summary>
    private IEnumerable<IReadOnlyList<Card>> EnumerateCandidates(IReadOnlyCollection<Card> hand)
    {
        var jokers = hand.Where(c => c.IsJoker).OrderBy(c => c.JokerIndex).ToList();
        var naturals = hand.Where(c => !c.IsJoker).ToList();

        // --- 単騎 ---
        foreach (var card in hand)
        {
            yield return new[] { card };
        }

        // --- 複数枚出し（同数字の部分集合 × ジョーカー枚数） ---
        foreach (var group in naturals.GroupBy(c => c.Rank))
        {
            var cards = group.OrderBy(c => c.Suit).ToList();
            int subsetCount = 1 << cards.Count;
            for (int mask = 1; mask < subsetCount; mask++)
            {
                var subset = new List<Card>();
                for (int i = 0; i < cards.Count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        subset.Add(cards[i]);
                    }
                }
                for (int j = 0; j <= jokers.Count; j++)
                {
                    if (subset.Count + j >= 2)
                    {
                        yield return subset.Concat(jokers.Take(j)).ToList();
                    }
                }
            }
        }
        for (int j = 2; j <= jokers.Count; j++)
        {
            yield return jokers.Take(j).ToList();
        }

        int maxTop = SequenceMaxBaseStrength;
        bool jokerInSequence = _rules.Sequence.AllowJokerInSequence;
        int usableJokers = jokerInSequence ? jokers.Count : 0;

        // --- 階段 ---
        if (_rules.Sequence.Enabled)
        {
            foreach (var suitGroup in naturals.GroupBy(c => c.Suit))
            {
                var byStrength = suitGroup.ToDictionary(c => c.BaseStrength);
                for (int start = Card.MinStrength; start <= maxTop; start++)
                {
                    var seq = new List<Card>();
                    int jokersUsed = 0;
                    int naturalCount = 0;
                    for (int b = start; b <= maxTop; b++)
                    {
                        if (byStrength.TryGetValue(b, out var natural))
                        {
                            seq.Add(natural);
                            naturalCount++;
                        }
                        else if (jokersUsed < usableJokers)
                        {
                            seq.Add(jokers[jokersUsed++]);
                        }
                        else
                        {
                            break;
                        }
                        if (seq.Count >= _rules.Sequence.MinLength && naturalCount > 0)
                        {
                            yield return seq.ToList();
                        }
                    }
                }
            }
        }

        // --- エンペラー ---
        if (_rules.Sequence.EmperorEnabled)
        {
            const int emperorLength = 4;
            for (int start = Card.MinStrength; start + emperorLength - 1 <= maxTop; start++)
            {
                foreach (var combo in EnumerateEmperor(naturals, jokers, usableJokers, start, 0, new List<Card>(), new HashSet<Suit>(), 0, emperorLength))
                {
                    yield return combo;
                }
            }
        }
    }

    private static IEnumerable<IReadOnlyList<Card>> EnumerateEmperor(
        List<Card> naturals, List<Card> jokers, int usableJokers, int start, int position,
        List<Card> current, HashSet<Suit> usedSuits, int jokersUsed, int length)
    {
        if (position == length)
        {
            if (current.Count(c => !c.IsJoker) >= 2)
            {
                yield return current.ToList();
            }
            yield break;
        }
        int b = start + position;
        foreach (var card in naturals.Where(c => c.BaseStrength == b && !usedSuits.Contains(c.Suit)))
        {
            current.Add(card);
            usedSuits.Add(card.Suit);
            foreach (var r in EnumerateEmperor(naturals, jokers, usableJokers, start, position + 1, current, usedSuits, jokersUsed, length))
            {
                yield return r;
            }
            usedSuits.Remove(card.Suit);
            current.RemoveAt(current.Count - 1);
        }
        if (jokersUsed < usableJokers)
        {
            current.Add(jokers[jokersUsed]);
            foreach (var r in EnumerateEmperor(naturals, jokers, usableJokers, start, position + 1, current, usedSuits, jokersUsed + 1, length))
            {
                yield return r;
            }
            current.RemoveAt(current.Count - 1);
        }
    }
}
