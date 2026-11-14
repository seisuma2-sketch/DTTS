using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using DTTS.Core.Enums;

namespace DTTS.Core.Models;

/// <summary>
/// ローカルルール設定の集約ルート（12カテゴリ）。
/// <para>
/// UI（設定画面）・DB（JSON列として保存）・通信（ホストから配布）で共通に扱う純粋なDTO。
/// 判定ロジックは本クラスの値のみを参照し、ルールをハードコードしない。
/// </para>
/// <para>
/// 各トリガー数字（8切りの「8」等）も設定値として保持し、変則ルールにも対応できるようにしている。
/// </para>
/// </summary>
public sealed class RuleSettings
{
    /// <summary>設定スキーマのバージョン（将来のマイグレーション用）。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>現在のスキーマバージョン。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>1. 革命系</summary>
    public RevolutionRules Revolution { get; set; } = new();

    /// <summary>2. 階段系</summary>
    public SequenceRules Sequence { get; set; } = new();

    /// <summary>3. 縛り系</summary>
    public LockRules Lock { get; set; } = new();

    /// <summary>4. 流し・切り系</summary>
    public FlowRules Flow { get; set; } = new();

    /// <summary>5. 11バック系</summary>
    public ElevenBackRules ElevenBack { get; set; } = new();

    /// <summary>6. スキップ・リバース系</summary>
    public SkipReverseRules SkipReverse { get; set; } = new();

    /// <summary>7. 特殊効果系</summary>
    public SpecialEffectRules Special { get; set; } = new();

    /// <summary>8. ジョーカー・切り返し</summary>
    public JokerRules Joker { get; set; } = new();

    /// <summary>9. 階級変動</summary>
    public RankChangeRules RankChange { get; set; } = new();

    /// <summary>10. 禁止あがり</summary>
    public ForbiddenFinishRules ForbiddenFinish { get; set; } = new();

    /// <summary>11. カード交換</summary>
    public ExchangeRules Exchange { get; set; } = new();

    /// <summary>12. 進行・配牌</summary>
    public ProgressRules Progress { get; set; } = new();

    // ------------------------------------------------------------------
    // 初期化・複製
    // ------------------------------------------------------------------

    /// <summary>既定値（一般的な大富豪ルール）で新しい設定を生成する。</summary>
    public static RuleSettings CreateDefault() => new();

    /// <summary>全カテゴリを既定値に戻す（設定画面の「初期化」ボタン用）。</summary>
    public void ResetToDefault()
    {
        SchemaVersion = CurrentSchemaVersion;
        Revolution = new();
        Sequence = new();
        Lock = new();
        Flow = new();
        ElevenBack = new();
        SkipReverse = new();
        Special = new();
        Joker = new();
        RankChange = new();
        ForbiddenFinish = new();
        Exchange = new();
        Progress = new();
    }

    /// <summary>ディープコピーを作成する（JSON往復による）。</summary>
    public RuleSettings Clone() => FromJson(ToJson());

    // ------------------------------------------------------------------
    // 永続化（System.Text.Json ソースジェネレーター使用：トリミング安全）
    // ------------------------------------------------------------------

    /// <summary>JSON文字列へシリアライズする。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, RuleSettingsJsonContext.Default.RuleSettings);

    /// <summary>
    /// JSON文字列から復元する。欠落した項目は既定値で補完され、範囲外の値は <see cref="Normalize"/> で補正される。
    /// </summary>
    /// <exception cref="JsonException">JSONの形式が不正な場合。</exception>
    public static RuleSettings FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault();
        }
        var settings = JsonSerializer.Deserialize(json, RuleSettingsJsonContext.Default.RuleSettings) ?? CreateDefault();
        settings.Normalize();
        return settings;
    }

    // ------------------------------------------------------------------
    // 検証
    // ------------------------------------------------------------------

    /// <summary>
    /// 範囲外の数値やnullカテゴリを安全な値へ補正する（破損データ・旧バージョン対策）。
    /// </summary>
    public void Normalize()
    {
        Revolution ??= new();
        Sequence ??= new();
        Lock ??= new();
        Flow ??= new();
        ElevenBack ??= new();
        SkipReverse ??= new();
        Special ??= new();
        Joker ??= new();
        RankChange ??= new();
        ForbiddenFinish ??= new();
        Exchange ??= new();
        Progress ??= new();

        Revolution.MinCards = Math.Clamp(Revolution.MinCards, 2, 6);
        Revolution.SequenceRevolutionMinLength = Math.Clamp(Revolution.SequenceRevolutionMinLength, 3, 13);
        Revolution.CoupCount = Math.Clamp(Revolution.CoupCount, 2, 6);
        Revolution.OmenCount = Math.Clamp(Revolution.OmenCount, 2, 6);
        Revolution.NanasanCount = Math.Clamp(Revolution.NanasanCount, 2, 6);

        Sequence.MinLength = Math.Clamp(Sequence.MinLength, 2, 13);

        Lock.TriggerCount = Math.Clamp(Lock.TriggerCount, 1, 5);

        Flow.SandstormCount = Math.Clamp(Flow.SandstormCount, 2, 6);
        Flow.AmbulanceCount = Math.Clamp(Flow.AmbulanceCount, 2, 6);
        Flow.RokurokubiCount = Math.Clamp(Flow.RokurokubiCount, 2, 6);

        Joker.JokerCount = Math.Clamp(Joker.JokerCount, 0, Card.MaxJokerCount);

        Exchange.DaifugoExchangeCount = Math.Clamp(Exchange.DaifugoExchangeCount, 0, 5);
        Exchange.FugoExchangeCount = Math.Clamp(Exchange.FugoExchangeCount, 0, 5);
        Exchange.TenpenChiiMaxRank = ClampNatural(Exchange.TenpenChiiMaxRank, Rank.Ten);

        Progress.RoundCount = Math.Clamp(Progress.RoundCount, 1, 99);
        Progress.MaxExtraRounds = Math.Clamp(Progress.MaxExtraRounds, 0, 10);
        Progress.PassLimitPerRound = Math.Clamp(Progress.PassLimitPerRound, 0, 99);
        Progress.PointsDaifugo = Math.Clamp(Progress.PointsDaifugo, -99, 99);
        Progress.PointsFugo = Math.Clamp(Progress.PointsFugo, -99, 99);
        Progress.PointsHeimin = Math.Clamp(Progress.PointsHeimin, -99, 99);
        Progress.PointsHinmin = Math.Clamp(Progress.PointsHinmin, -99, 99);
        Progress.PointsDaihinmin = Math.Clamp(Progress.PointsDaihinmin, -99, 99);
        if (!Enum.IsDefined(Progress.LaterRoundLeader))
        {
            Progress.LaterRoundLeader = RoundLeaderRule.Daihinmin;
        }
        if (!Enum.IsDefined(ForbiddenFinish.Penalty))
        {
            ForbiddenFinish.Penalty = ForbiddenFinishPenalty.LastPlace;
        }

        // トリガー数字が Joker 等の不正値になっていたら既定値へ戻す
        Revolution.CoupRank = ClampNatural(Revolution.CoupRank, Rank.Nine);
        Revolution.OmenRank = ClampNatural(Revolution.OmenRank, Rank.Six);
        Revolution.NanasanRank = ClampNatural(Revolution.NanasanRank, Rank.Seven);
        Flow.EightCutRank = ClampNatural(Flow.EightCutRank, Rank.Eight);
        Flow.FourStopRank = ClampNatural(Flow.FourStopRank, Rank.Four);
        Flow.SandstormRank = ClampNatural(Flow.SandstormRank, Rank.Three);
        Flow.AmbulanceRank = ClampNatural(Flow.AmbulanceRank, Rank.Nine);
        Flow.RokurokubiRank = ClampNatural(Flow.RokurokubiRank, Rank.Six);
        ElevenBack.TriggerRank = ClampNatural(ElevenBack.TriggerRank, Rank.Jack);
        SkipReverse.FiveSkipRank = ClampNatural(SkipReverse.FiveSkipRank, Rank.Five);
        SkipReverse.ThirteenSkipRank = ClampNatural(SkipReverse.ThirteenSkipRank, Rank.King);
        SkipReverse.NineReverseRank = ClampNatural(SkipReverse.NineReverseRank, Rank.Nine);
        SkipReverse.TwelveReverseRank = ClampNatural(SkipReverse.TwelveReverseRank, Rank.Queen);
        Special.SevenPassRank = ClampNatural(Special.SevenPassRank, Rank.Seven);
        Special.TenDiscardRank = ClampNatural(Special.TenDiscardRank, Rank.Ten);
        Special.TwelveBomberRank = ClampNatural(Special.TwelveBomberRank, Rank.Queen);
        Joker.SpadeThreeReturnRank = ClampNatural(Joker.SpadeThreeReturnRank, Rank.Three);

        SchemaVersion = CurrentSchemaVersion;
    }

    private static Rank ClampNatural(Rank value, Rank fallback)
        => (int)value >= 1 && (int)value <= Card.RanksPerSuit ? value : fallback;
}

// ======================================================================
// 1. 革命系
// ======================================================================

/// <summary>1. 革命系ルール。</summary>
public sealed class RevolutionRules
{
    /// <summary>革命：同じ数字を <see cref="MinCards"/> 枚以上出すと強さが反転する。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>革命に必要な枚数（既定4）。</summary>
    public int MinCards { get; set; } = 4;

    /// <summary>階段革命：<see cref="SequenceRevolutionMinLength"/> 枚以上の階段で革命が起きる。</summary>
    public bool SequenceRevolutionEnabled { get; set; } = false;

    /// <summary>階段革命に必要な枚数（既定5）。</summary>
    public int SequenceRevolutionMinLength { get; set; } = 5;

    /// <summary>
    /// 革命返し：革命中にさらに革命を起こすと元に戻る。
    /// 無効時は、革命中に革命条件を満たしても反転は発生しない（出し札自体は合法）。
    /// </summary>
    public bool CounterRevolutionEnabled { get; set; } = true;

    /// <summary>
    /// ジョーカー含み革命：ジョーカーを革命・階段革命・クーデター等の枚数にカウントする。
    /// 無効時は自然札の枚数のみで判定する。
    /// </summary>
    public bool JokerCountsForRevolution { get; set; } = true;

    /// <summary>
    /// 革命選択：革命条件を満たしたとき、革命を起こすかどうかを出したプレイヤーが選べる。
    /// （判定結果の効果に <c>IsOptional=true</c> が付与され、適用可否はエンジンが問い合わせる）
    /// </summary>
    public bool RevolutionIsOptional { get; set; } = false;

    /// <summary>クーデター：<see cref="CoupRank"/> をちょうど <see cref="CoupCount"/> 枚出すと革命（既定：9を3枚）。</summary>
    public bool CoupEnabled { get; set; } = false;

    /// <summary>クーデターのトリガー数字。</summary>
    public Rank CoupRank { get; set; } = Rank.Nine;

    /// <summary>クーデターの枚数。</summary>
    public int CoupCount { get; set; } = 3;

    /// <summary>
    /// オーメン：<see cref="OmenRank"/> をちょうど <see cref="OmenCount"/> 枚出すと革命が起き、
    /// 以後そのラウンド中は革命（革命返し含む）が一切起きなくなる（既定：6を3枚）。
    /// </summary>
    public bool OmenEnabled { get; set; } = false;

    /// <summary>オーメンのトリガー数字。</summary>
    public Rank OmenRank { get; set; } = Rank.Six;

    /// <summary>オーメンの枚数。</summary>
    public int OmenCount { get; set; } = 3;

    /// <summary>ナナサン革命：<see cref="NanasanRank"/> をちょうど <see cref="NanasanCount"/> 枚出すと革命（既定：7を3枚）。</summary>
    public bool NanasanEnabled { get; set; } = false;

    /// <summary>ナナサン革命のトリガー数字。</summary>
    public Rank NanasanRank { get; set; } = Rank.Seven;

    /// <summary>ナナサン革命の枚数。</summary>
    public int NanasanCount { get; set; } = 3;
}

// ======================================================================
// 2. 階段系
// ======================================================================

/// <summary>2. 階段系ルール。</summary>
public sealed class SequenceRules
{
    /// <summary>階段（同スート連番）を出せる。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>階段の最小枚数（既定3。2にすると同スートの連番2枚から階段として出せる）。</summary>
    public int MinLength { get; set; } = 3;

    /// <summary>階段にジョーカーを含められる（欠番・端の補完）。</summary>
    public bool AllowJokerInSequence { get; set; } = true;

    /// <summary>
    /// 階段に「2」を含められる（K-A-2 まで）。いずれの設定でも 2→3 の折り返しは不可。
    /// </summary>
    public bool AllowTwoInSequence { get; set; } = true;

    /// <summary>
    /// 階段中効果：階段に含まれる 8・J・5 等の数字効果を発動させる。
    /// 無効時は階段では数字効果が一切発動しない（革命系・縛りは別扱い）。
    /// </summary>
    public bool EffectsInSequence { get; set; } = false;

    /// <summary>
    /// エンペラー：4種のスートを1枚ずつ使った4枚の連番。
    /// 場が空のときに出せ、場が4枚階段のときは強さに関係なく勝つ。出すと場が流れる。
    /// </summary>
    public bool EmperorEnabled { get; set; } = false;

    /// <summary>エンペラーで革命も発生させる。</summary>
    public bool EmperorCausesRevolution { get; set; } = false;
}

// ======================================================================
// 3. 縛り系
// ======================================================================

/// <summary>3. 縛り系ルール。</summary>
public sealed class LockRules
{
    /// <summary>スート縛り：直前と同じスート構成で出すと、場が流れるまでそのスートしか出せなくなる。</summary>
    public bool SuitLockEnabled { get; set; } = true;

    /// <summary>数しば：直前の強さ＋1の数字を出すと、場が流れるまで「ちょうど＋1」しか出せなくなる。</summary>
    public bool NumberLockEnabled { get; set; } = false;

    /// <summary>
    /// 激しば：スート縛りと数しばの同時成立を許可する。
    /// 無効時に両方の条件が同時に満たされた場合はスート縛りのみ成立し、
    /// 既にどちらかが成立している場合はもう一方は成立しない。
    /// </summary>
    public bool StrictLockEnabled { get; set; } = false;

    /// <summary>
    /// 両しば（片縛り）：複数枚出しでスートの一部のみが直前と一致した場合も、一致したスート分だけ縛る。
    /// 例：♠♥ に ♠♦ を出すと「♠ ＋ 任意1枚」の縛り。
    /// ※地域差の大きいルールのため、本実装ではこの定義を採用。
    /// </summary>
    public bool PartialSuitLockEnabled { get; set; } = false;

    /// <summary>
    /// 縛り発生回数：縛りの成立に必要な連続一致回数（既定1＝1回一致で成立＝同スートが2回続いたら縛り）。
    /// 2にすると「3回連続で同スートが出たら縛り」となる。数しばにも同じ回数を使う。
    /// </summary>
    public int TriggerCount { get; set; } = 1;
}

// ======================================================================
// 4. 流し・切り系
// ======================================================================

/// <summary>4. 流し・切り系ルール。</summary>
public sealed class FlowRules
{
    /// <summary>8切り：<see cref="EightCutRank"/> を含む出し札で場を流し、出した人が親になる。</summary>
    public bool EightCutEnabled { get; set; } = true;

    /// <summary>8切りのトリガー数字。</summary>
    public Rank EightCutRank { get; set; } = Rank.Eight;

    /// <summary>
    /// 4止め：8切りが出た直後、他のプレイヤーが同枚数の <see cref="FourStopRank"/> を出すと8切りを無効化できる。
    /// （エンジンは8切り発生時に <c>FieldState.IsEightCutPending</c> を立てて割り込みを受け付ける）
    /// </summary>
    public bool FourStopEnabled { get; set; } = false;

    /// <summary>4止めのトリガー数字。</summary>
    public Rank FourStopRank { get; set; } = Rank.Four;

    /// <summary>
    /// 砂嵐：<see cref="SandstormRank"/> をちょうど <see cref="SandstormCount"/> 枚出すと、
    /// 同枚数のどんな組にも強さに関係なく勝ち、場を流す（既定：3を3枚）。縛りには従う。
    /// 有効時は、ろくろ首・99車が出た直後に砂嵐を割り込みで出して流しを止められる（枚数・縛りは問わない）。
    /// </summary>
    public bool SandstormEnabled { get; set; } = false;

    /// <summary>砂嵐のトリガー数字。</summary>
    public Rank SandstormRank { get; set; } = Rank.Three;

    /// <summary>砂嵐の枚数。</summary>
    public int SandstormCount { get; set; } = 3;

    /// <summary>
    /// 99車（救急車）：<see cref="AmbulanceRank"/> をちょうど <see cref="AmbulanceCount"/> 枚出すと、
    /// 同枚数のどんな組にも勝ち、場を流す（既定：9を2枚）。縛りには従う。
    /// </summary>
    public bool AmbulanceEnabled { get; set; } = false;

    /// <summary>99車のトリガー数字。</summary>
    public Rank AmbulanceRank { get; set; } = Rank.Nine;

    /// <summary>99車の枚数。</summary>
    public int AmbulanceCount { get; set; } = 2;

    /// <summary>
    /// ろくろ首：<see cref="RokurokubiRank"/> をちょうど <see cref="RokurokubiCount"/> 枚出すと場を流す（既定：6を2枚）。
    /// 砂嵐・99車と異なり、出すためには通常の強さ比較に勝つ必要がある。
    /// ※地域差の大きいルールのため、本実装ではこの定義を採用。
    /// </summary>
    public bool RokurokubiEnabled { get; set; } = false;

    /// <summary>ろくろ首のトリガー数字。</summary>
    public Rank RokurokubiRank { get; set; } = Rank.Six;

    /// <summary>ろくろ首の枚数。</summary>
    public int RokurokubiCount { get; set; } = 2;
}

// ======================================================================
// 5. 11バック系
// ======================================================================

/// <summary>5. 11バック系ルール。</summary>
public sealed class ElevenBackRules
{
    /// <summary>11バック：<see cref="TriggerRank"/> を含む出し札で、場が流れるまで強さが反転する。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>11バックのトリガー数字。</summary>
    public Rank TriggerRank { get; set; } = Rank.Jack;

    /// <summary>
    /// 強化11バック：反転が場流れ後も持続し、次に11バックが出るまで解除されない。
    /// ※地域差の大きいルールのため、本実装ではこの定義を採用。
    /// </summary>
    public bool StrongElevenBackEnabled { get; set; } = false;
}

// ======================================================================
// 6. スキップ・リバース系
// ======================================================================

/// <summary>6. スキップ・リバース系ルール。効果量は出した該当カードの枚数。</summary>
public sealed class SkipReverseRules
{
    /// <summary>5スキップ：出した枚数分、後続プレイヤーの手番を飛ばす。</summary>
    public bool FiveSkipEnabled { get; set; } = false;

    /// <summary>5スキップのトリガー数字。</summary>
    public Rank FiveSkipRank { get; set; } = Rank.Five;

    /// <summary>13スキップ（Kスキップ）。</summary>
    public bool ThirteenSkipEnabled { get; set; } = false;

    /// <summary>13スキップのトリガー数字。</summary>
    public Rank ThirteenSkipRank { get; set; } = Rank.King;

    /// <summary>9リバース：手番の進行方向を反転する（枚数が偶数なら元に戻る扱いはエンジン側で処理）。</summary>
    public bool NineReverseEnabled { get; set; } = false;

    /// <summary>9リバースのトリガー数字。</summary>
    public Rank NineReverseRank { get; set; } = Rank.Nine;

    /// <summary>12リバース（Qリバース）。</summary>
    public bool TwelveReverseEnabled { get; set; } = false;

    /// <summary>12リバースのトリガー数字。</summary>
    public Rank TwelveReverseRank { get; set; } = Rank.Queen;
}

// ======================================================================
// 7. 特殊効果系
// ======================================================================

/// <summary>7. 特殊効果系ルール。</summary>
public sealed class SpecialEffectRules
{
    /// <summary>7渡し：出した枚数分、次のプレイヤーへ好きな手札を渡す。</summary>
    public bool SevenPassEnabled { get; set; } = false;

    /// <summary>7渡しのトリガー数字。</summary>
    public Rank SevenPassRank { get; set; } = Rank.Seven;

    /// <summary>10捨て：出した枚数分、好きな手札を捨てる。</summary>
    public bool TenDiscardEnabled { get; set; } = false;

    /// <summary>10捨てのトリガー数字。</summary>
    public Rank TenDiscardRank { get; set; } = Rank.Ten;

    /// <summary>12ボンバー：出した枚数分の数字（ジョーカーも可）を宣言し、全員がそのカードを手札から捨てる。</summary>
    public bool TwelveBomberEnabled { get; set; } = false;

    /// <summary>12ボンバーのトリガー数字。</summary>
    public Rank TwelveBomberRank { get; set; } = Rank.Queen;

    /// <summary>
    /// ダウンナンバー：場と同じスート構成で、強さがちょうど1つ下の数字を出せる（単騎・複数枚出しのみ、ジョーカー不可）。
    /// 数しば中は使用できない。
    /// ※地域差の大きいルールのため、本実装ではこの定義を採用。
    /// </summary>
    public bool DownNumberEnabled { get; set; } = false;
}

// ======================================================================
// 8. ジョーカー・切り返し
// ======================================================================

/// <summary>8. ジョーカー・切り返しルール。</summary>
public sealed class JokerRules
{
    /// <summary>デッキに入れるジョーカー枚数（0〜2）。</summary>
    public int JokerCount { get; set; } = 1;

    /// <summary>スペ3返し：単体ジョーカーに対して ♠<see cref="SpadeThreeReturnRank"/> 単騎で勝ち、場を流す。縛りは無視する。</summary>
    public bool SpadeThreeReturnEnabled { get; set; } = true;

    /// <summary>スペ3返しに使う数字（通常は3）。</summary>
    public Rank SpadeThreeReturnRank { get; set; } = Rank.Three;

    /// <summary>
    /// ジョーカーが代用した数字の効果を発動させる（例：8の代わりのジョーカーで8切り）。
    /// 砂嵐・99車などの「特定数字×枚数」役にも適用される。
    /// </summary>
    public bool JokerTriggersEffects { get; set; } = false;
}

// ======================================================================
// 9. 階級変動
// ======================================================================

/// <summary>9. 階級変動ルール。</summary>
public sealed class RankChangeRules
{
    /// <summary>都落ち：前ラウンドの大富豪が1位であがれなかった場合、即座に大貧民になる。</summary>
    public bool MiyakoOchiEnabled { get; set; } = true;

    /// <summary>下剋上：大貧民が1位であがった場合、全員の階級が逆転する。</summary>
    public bool GekokujoEnabled { get; set; } = false;
}

// ======================================================================
// 10. 禁止あがり
// ======================================================================

/// <summary>
/// 10. 禁止あがり（厳格判定）ルール。
/// 判定は「出す直前の場の状態」で行う（革命を起こしてあがる場合も、出す前の状態で最強数字を判断）。
/// </summary>
public sealed class ForbiddenFinishRules
{
    /// <summary>禁止あがり判定を行う。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>禁止あがり時のペナルティ。</summary>
    public ForbiddenFinishPenalty Penalty { get; set; } = ForbiddenFinishPenalty.LastPlace;

    /// <summary>最強数字（2）を含む出し札であがれない。</summary>
    public bool ForbidStrongestNumber { get; set; } = true;

    /// <summary>
    /// 最強数字を反転状態に追従させる（革命・11バック中は3が禁止）。
    /// 無効時は常に「2」が禁止。
    /// </summary>
    public bool StrongestFollowsReversal { get; set; } = true;

    /// <summary>8切りが発動する出し札であがれない。</summary>
    public bool ForbidEightCut { get; set; } = true;

    /// <summary>ジョーカーを含む出し札であがれない。</summary>
    public bool ForbidJoker { get; set; } = true;

    /// <summary>スペ3返しであがれない。</summary>
    public bool ForbidSpadeThree { get; set; } = true;

    /// <summary>12ボンバーが発動する出し札であがれない。</summary>
    public bool ForbidTwelveBomber { get; set; } = false;

    /// <summary>11バックが発動する出し札であがれない。</summary>
    public bool ForbidElevenBack { get; set; } = false;

    /// <summary>砂嵐・99車・ろくろ首・エンペラーで場を流してあがれない。</summary>
    public bool ForbidOtherFlushEffects { get; set; } = false;

    /// <summary>革命を起こす出し札であがれない。</summary>
    public bool ForbidRevolution { get; set; } = false;
}

// ======================================================================
// 11. カード交換
// ======================================================================

/// <summary>11. カード交換ルール（2ラウンド目以降の配牌後に実施）。</summary>
public sealed class ExchangeRules
{
    /// <summary>カード交換を行う。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>大富豪⇔大貧民の交換枚数。</summary>
    public int DaifugoExchangeCount { get; set; } = 2;

    /// <summary>富豪⇔貧民の交換枚数。</summary>
    public int FugoExchangeCount { get; set; } = 1;

    /// <summary>
    /// 都落ち連動：都落ちが発生した場合、交換相手を都落ち後の確定階級で決定する。
    /// 無効時は都落ちしたプレイヤーは次ラウンドの交換に参加しない。
    /// </summary>
    public bool LinkedToMiyakoOchi { get; set; } = true;

    /// <summary>
    /// 貧民選択：貧民・大貧民が渡すカードを自分で選べる。
    /// 無効時は手札の最強カードから自動的に渡す。
    /// </summary>
    public bool PoorCanChooseCards { get; set; } = false;

    /// <summary>ジョーカー保護：貧民・大貧民はジョーカーを交換に出さなくてよい。</summary>
    public bool JokerProtected { get; set; } = false;

    /// <summary>
    /// 天変地異：配牌後、大貧民の手札がすべて <see cref="TenpenChiiMaxRank"/> 以下（ジョーカーなし）なら、
    /// 大富豪と手札をすべて交換する（通常の交換は行わない）。
    /// </summary>
    public bool TenpenChiiEnabled { get; set; } = false;

    /// <summary>天変地異の判定基準数字（既定10：手札が10以下のみ）。</summary>
    public Rank TenpenChiiMaxRank { get; set; } = Rank.Ten;
}

// ======================================================================
// 12. 進行・配牌
// ======================================================================

/// <summary>12. 進行・配牌ルール。</summary>
public sealed class ProgressRules
{
    /// <summary>ダイヤ3初手：第1ラウンドは♦3を持つプレイヤーが親となり、初手に♦3を含めなければならない。</summary>
    public bool DiamondThreeStarts { get; set; } = true;

    /// <summary>席替え：ラウンドごとに座席順をシャッフルする。</summary>
    public bool SeatShuffleEachRound { get; set; } = false;

    /// <summary>
    /// パス制限：1ラウンドでパスできる回数の上限（0＝無制限）。
    /// 上限に達していても、出せる手が1つもない場合はパスできる。
    /// </summary>
    public int PassLimitPerRound { get; set; } = 0;

    /// <summary>
    /// パスしたら場が流れるまで出せない（一度パスした場には再参加できない）。
    /// 無効時は、他の全員がパスして手番が一周しない限り、パス後も再び出せる。
    /// </summary>
    public bool PassLocksUntilFlush { get; set; } = false;

    /// <summary>2ラウンド目以降（延長戦を含む）の親の決め方。</summary>
    public RoundLeaderRule LaterRoundLeader { get; set; } = RoundLeaderRule.Daihinmin;

    /// <summary>大富豪の獲得点。</summary>
    public int PointsDaifugo { get; set; } = 3;

    /// <summary>富豪の獲得点。</summary>
    public int PointsFugo { get; set; } = 2;

    /// <summary>平民の獲得点（5人以上・3人戦用）。</summary>
    public int PointsHeimin { get; set; } = 1;

    /// <summary>貧民の獲得点。</summary>
    public int PointsHinmin { get; set; } = 1;

    /// <summary>大貧民の獲得点。</summary>
    public int PointsDaihinmin { get; set; } = 0;

    /// <summary>1ゲームのラウンド数（既定5R）。</summary>
    public int RoundCount { get; set; } = 5;

    /// <summary>最終ラウンド終了時に総合1位が同点なら延長戦を行う。</summary>
    public bool ExtraRoundOnTie { get; set; } = true;

    /// <summary>延長戦の最大ラウンド数。</summary>
    public int MaxExtraRounds { get; set; } = 3;
}

/// <summary>
/// <see cref="RuleSettings"/> 用の System.Text.Json ソース生成コンテキスト。
/// リフレクションを使わないため Release ビルドのトリミング有効時も安全に動作する。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RuleSettings))]
internal sealed partial class RuleSettingsJsonContext : JsonSerializerContext
{
}
