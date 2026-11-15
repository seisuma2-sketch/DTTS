using System.Collections.Generic;
using DTTS.Core.Enums;

namespace DTTS.Services.Settings;

/// <summary>
/// 対戦画面に出す操作ガイド（ヒント）を、ガイドのレベルに合わせて選ぶ。
/// <list type="bullet">
/// <item>初心者用：自分の手番ごとの操作ヒントと、特殊効果が出るたびの説明。</item>
/// <item>中級者用：特殊効果・カード選択の説明を、その対戦で初めて起きたときだけ。</item>
/// <item>上級者用：何も出さない。</item>
/// </list>
/// 画面に依存しない純粋な判定なので、テストから直接確かめられる。
/// </summary>
public sealed class GuideHints
{
    private readonly HashSet<string> _seen = new();

    /// <summary>ガイドを作る。</summary>
    public GuideHints(GuideLevel level)
    {
        Level = level;
    }

    /// <summary>ガイドのレベル。</summary>
    public GuideLevel Level { get; }

    /// <summary>新しい対戦の始まり：「初めて」の記録を消す。</summary>
    public void Reset() => _seen.Clear();

    /// <summary>特殊効果が起きたときのヒント（出さないなら null）。</summary>
    /// <param name="effect">起きた効果。</param>
    /// <param name="applied">実際に効果が成立したか（革命を選ばなかった等は false）。</param>
    public string? ForEffect(SpecialEffectType effect, bool applied = true)
    {
        if (!applied || Level == GuideLevel.Expert)
        {
            return null;
        }
        string? text = EffectText(effect);
        if (text is null)
        {
            return null;
        }
        // 中級者用は、その対戦で初めて起きたときだけ（革命系はまとめて1回）
        string key = "effect:" + (effect.IsRevolutionTrigger() ? "revolution" : effect.ToString());
        return Level == GuideLevel.Beginner || _seen.Add(key) ? text : null;
    }

    /// <summary>カード選択（交換・7渡し・10捨て）を求められたときのヒント（出さないなら null）。</summary>
    public string? ForCardSelection(CardSelectionPurpose purpose)
    {
        if (Level == GuideLevel.Expert)
        {
            return null;
        }
        string text = purpose switch
        {
            CardSelectionPurpose.ExchangeGive => "ヒント：カード交換では、階級に応じた枚数を相手に渡します。手札のカードをクリックして選び、「決定」を押してください。",
            CardSelectionPurpose.SevenPass => "ヒント：7渡しでは、出した7の枚数だけ次の人へカードを渡せます。いらないカードを選んで「決定」を押してください。",
            CardSelectionPurpose.TenDiscard => "ヒント：10捨てでは、出した10の枚数まで手札を捨てられます（0枚でもかまいません）。",
            _ => "ヒント：手札のカードをクリックして選び、「決定」を押してください。",
        };
        return Level == GuideLevel.Beginner || _seen.Add("select:" + purpose) ? text : null;
    }

    /// <summary>自分の手番のヒント（初心者用だけ。出さないなら null）。</summary>
    /// <param name="isLead">場が空（親番）か。</param>
    /// <param name="hasLegalPlay">出せる手があるか。</param>
    /// <param name="reversed">強さが逆転中か（革命・11バック）。</param>
    /// <param name="suitLocked">スート縛り中か。</param>
    /// <param name="numberLocked">数しば中か。</param>
    public string? ForTurn(bool isLead, bool hasLegalPlay, bool reversed = false, bool suitLocked = false, bool numberLocked = false)
    {
        if (Level != GuideLevel.Beginner)
        {
            return null;
        }
        if (isLead)
        {
            return "ヒント：あなたが親です。好きなカードを出せます（1枚・同じ数字の組・階段）。カードを選んで「出す」[Enter]。";
        }
        if (!hasLegalPlay)
        {
            return "ヒント：場に出せるカードがありません。「パス」[BS] を押してください。場が流れると、また出せるようになります。";
        }
        var notes = new List<string>();
        if (reversed)
        {
            notes.Add("今は強さが逆転していて、3 が強く 2 が弱くなっています");
        }
        if (suitLocked)
        {
            notes.Add("スート縛り中なので、場と同じマークのカードしか出せません");
        }
        if (numberLocked)
        {
            notes.Add("数しば中なので、場のカードのちょうど1つ上の数字しか出せません");
        }
        string extra = notes.Count > 0 ? "（" + string.Join("。", notes) + "）" : "";
        return $"ヒント：場と同じ枚数で、より強いカードを出すか「パス」[BS] します{extra}。出せるカードは明るく表示されています。";
    }

    /// <summary>特殊効果の短い説明（説明しない効果は null）。</summary>
    public static string? EffectText(SpecialEffectType effect) => effect switch
    {
        SpecialEffectType.Revolution or SpecialEffectType.SequenceRevolution or SpecialEffectType.Coup
            or SpecialEffectType.Nanasan => "ヒント：革命！カードの強さが逆になりました（3 がいちばん強く、2 がいちばん弱い）。もう一度革命が起きると元に戻ります。",
        SpecialEffectType.Omen => "ヒント：オーメン！強さが逆になり、このラウンドではもう革命が起きません。",
        SpecialEffectType.Emperor => "ヒント：エンペラー（4種のマークの4枚連番）。4枚の階段に必ず勝てます。",
        SpecialEffectType.SuitLock => "ヒント：スート縛り！場が流れるまで、同じマークのカードしか出せません。",
        SpecialEffectType.NumberLock => "ヒント：数しば！場が流れるまで、ちょうど1つ上の数字しか出せません。",
        SpecialEffectType.EightCut => "ヒント：8切り！場が流れて、8を出した人がもう一度親になります。",
        SpecialEffectType.FourStop => "ヒント：4止め！8切りが止められ、場は流れずに続きます。",
        SpecialEffectType.Sandstorm => "ヒント：砂嵐！同じ枚数なら何にでも勝ち、すぐに場が流れます。誰にも止められません。",
        SpecialEffectType.Ambulance => "ヒント：99車！同じ枚数なら何にでも勝ち、場が流れます（砂嵐でだけ止められます）。",
        SpecialEffectType.Rokurokubi => "ヒント：ろくろ首！場が流れます（砂嵐でだけ止められます）。",
        SpecialEffectType.ElevenBack or SpecialEffectType.StrongElevenBack => "ヒント：11バック！しばらくカードの強さが逆になります。",
        SpecialEffectType.FiveSkip or SpecialEffectType.ThirteenSkip => "ヒント：スキップ！出した枚数の人数だけ、後の人の手番が飛ばされます。",
        SpecialEffectType.NineReverse or SpecialEffectType.TwelveReverse => "ヒント：リバース！手番の回る向きが逆になりました。",
        SpecialEffectType.SevenPass => "ヒント：7渡し！出した7の枚数だけ、次の人へ手札を渡せます。",
        SpecialEffectType.TenDiscard => "ヒント：10捨て！出した10の枚数まで、手札を捨てられます。",
        SpecialEffectType.TwelveBomber => "ヒント：12ボンバー！宣言した数字のカードを、全員が手札から捨てます。",
        SpecialEffectType.DownNumber => "ヒント：ダウンナンバー！同じマークでちょうど1つ下の数字を出して、場を続けました。",
        SpecialEffectType.SpadeThreeReturn => "ヒント：スペ3返し！ジョーカー1枚には♠3で勝てて、場が流れます。",
        _ => null,
    };
}
