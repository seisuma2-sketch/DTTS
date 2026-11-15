using System;
using System.Collections.Generic;
using DTTS.Core.Ai;
using DTTS.Services.Audio;

namespace DTTS.Services.Presentation;

/// <summary>プレイヤーパネルの横に出す、ひとことの吹き出しのきっかけ。</summary>
public enum ReactionKind : byte
{
    /// <summary>パスした。</summary>
    Pass = 0,
    /// <summary>革命を起こした。</summary>
    Revolution,
    /// <summary>8切りで場を流した。</summary>
    EightCut,
    /// <summary>相手の8切りを4止めで止めた。</summary>
    FourStop,
    /// <summary>砂嵐を出した（流し役を止めた場合も）。</summary>
    Sandstorm,
    /// <summary>そのほかの流し役（99車・ろくろ首・エンペラー・スペ3返し）。</summary>
    OtherFlush,
    /// <summary>1位であがった。</summary>
    FinishFirst,
    /// <summary>あがった（2位以下）。</summary>
    Finish,
    /// <summary>都落ち・反則で脱落した。</summary>
    Fall,
}

/// <summary>
/// NPC の性格ごとの短いリアクション（吹き出し）の文言。人が操作している席（通信対戦のゲストなど）は性格なしの共通の文言。
/// 同じ場面でも何種類かを順に使い、毎回同じ言葉にならないようにする。
/// </summary>
public static class NpcReactions
{
    /// <summary>吹き出しを出しておく時間。</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(1600);

    private static readonly Dictionary<(NpcPersonality?, ReactionKind), string[]> Lines = new()
    {
        // 堅実：落ち着いた、控えめな言葉
        [(NpcPersonality.Steady, ReactionKind.Pass)] = new[] { "ここは見送るか", "パス", "まだ様子見" },
        [(NpcPersonality.Steady, ReactionKind.Revolution)] = new[] { "流れを変えておこう", "革命、いきます" },
        [(NpcPersonality.Steady, ReactionKind.EightCut)] = new[] { "一度切っておく", "8切りで" },
        [(NpcPersonality.Steady, ReactionKind.FourStop)] = new[] { "それは止めておくよ" },
        [(NpcPersonality.Steady, ReactionKind.Sandstorm)] = new[] { "ここで砂嵐を" },
        [(NpcPersonality.Steady, ReactionKind.OtherFlush)] = new[] { "流します" },
        [(NpcPersonality.Steady, ReactionKind.FinishFirst)] = new[] { "予定どおり", "上がりです" },
        [(NpcPersonality.Steady, ReactionKind.Finish)] = new[] { "上がりです" },
        [(NpcPersonality.Steady, ReactionKind.Fall)] = new[] { "うーん、残念" },

        // 革命狙い：ひっくり返すのが好き
        [(NpcPersonality.RevolutionSeeker, ReactionKind.Pass)] = new[] { "まだ溜めておく", "パス！", "その時を待つ" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.Revolution)] = new[] { "よし、ひっくり返す！", "革命だ！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.EightCut)] = new[] { "切っちゃえ！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.FourStop)] = new[] { "させないよ！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.Sandstorm)] = new[] { "砂嵐、吹き荒れろ！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.OtherFlush)] = new[] { "流しちゃおう！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.FinishFirst)] = new[] { "大逆転！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.Finish)] = new[] { "上がり！" },
        [(NpcPersonality.RevolutionSeeker, ReactionKind.Fall)] = new[] { "くっ、ひっくり返された…" },

        // 温存：慎重で、強いカードを取っておく
        [(NpcPersonality.Conserver, ReactionKind.Pass)] = new[] { "温存しておきます", "まだ出さない", "パス" },
        [(NpcPersonality.Conserver, ReactionKind.Revolution)] = new[] { "…ここで革命" },
        [(NpcPersonality.Conserver, ReactionKind.EightCut)] = new[] { "やむを得ず8切り" },
        [(NpcPersonality.Conserver, ReactionKind.FourStop)] = new[] { "取っておいた4です" },
        [(NpcPersonality.Conserver, ReactionKind.Sandstorm)] = new[] { "とっておきの砂嵐" },
        [(NpcPersonality.Conserver, ReactionKind.OtherFlush)] = new[] { "流しておきます" },
        [(NpcPersonality.Conserver, ReactionKind.FinishFirst)] = new[] { "最後まで取っておいた甲斐が" },
        [(NpcPersonality.Conserver, ReactionKind.Finish)] = new[] { "なんとか上がり" },
        [(NpcPersonality.Conserver, ReactionKind.Fall)] = new[] { "温存しすぎた…" },

        // アグレッシブ：強気
        [(NpcPersonality.Aggressive, ReactionKind.Pass)] = new[] { "ちっ、パス！", "今は出せない", "次で取り返す" },
        [(NpcPersonality.Aggressive, ReactionKind.Revolution)] = new[] { "全部ひっくり返す！" },
        [(NpcPersonality.Aggressive, ReactionKind.EightCut)] = new[] { "8切り！主導権はもらう", "切る！" },
        [(NpcPersonality.Aggressive, ReactionKind.FourStop)] = new[] { "止めてやる！" },
        [(NpcPersonality.Aggressive, ReactionKind.Sandstorm)] = new[] { "砂嵐で吹き飛ばす！" },
        [(NpcPersonality.Aggressive, ReactionKind.OtherFlush)] = new[] { "流す！" },
        [(NpcPersonality.Aggressive, ReactionKind.FinishFirst)] = new[] { "一番乗り！" },
        [(NpcPersonality.Aggressive, ReactionKind.Finish)] = new[] { "上がりだ！" },
        [(NpcPersonality.Aggressive, ReactionKind.Fall)] = new[] { "攻めすぎたか…" },

        // 人（性格なし）
        [(null, ReactionKind.Pass)] = new[] { "パス" },
        [(null, ReactionKind.Revolution)] = new[] { "革命！" },
        [(null, ReactionKind.EightCut)] = new[] { "8切り！" },
        [(null, ReactionKind.FourStop)] = new[] { "4止め！" },
        [(null, ReactionKind.Sandstorm)] = new[] { "砂嵐！" },
        [(null, ReactionKind.OtherFlush)] = new[] { "流し！" },
        [(null, ReactionKind.FinishFirst)] = new[] { "1位！" },
        [(null, ReactionKind.Finish)] = new[] { "上がり！" },
        [(null, ReactionKind.Fall)] = new[] { "あっ…" },
    };

    /// <summary>リアクションの文言。</summary>
    /// <param name="personality">NPC の性格（人の席は null）。</param>
    /// <param name="kind">きっかけ。</param>
    /// <param name="variant">何回目か（言い回しを順に変える）。</param>
    public static string For(NpcPersonality? personality, ReactionKind kind, int variant)
    {
        if (!Lines.TryGetValue((personality, kind), out var lines) && !Lines.TryGetValue((null, kind), out lines))
        {
            return "";
        }
        int i = ((variant % lines.Length) + lines.Length) % lines.Length;
        return lines[i];
    }

    /// <summary>性格を表すアイコン（Segoe Fluent Icons の線画）。人の席は人物のアイコン。</summary>
    public static string AvatarGlyph(NpcPersonality? personality) => personality switch
    {
        NpcPersonality.Steady => "",            // 盾（堅実）
        NpcPersonality.RevolutionSeeker => "",  // 回転する矢印（ひっくり返す）
        NpcPersonality.Conserver => "",         // ストップウォッチ（じっくり温存）
        NpcPersonality.Aggressive => "",        // 稲妻（攻め）
        _ => "",                                // 人物
    };

    /// <summary>性格が分からない NPC の席のアイコン（ゲストの画面など）。</summary>
    public const string NpcGlyph = "\uE99A"; // ロボット

        /// <summary>性格の表示名（人の席は「プレイヤー」）。</summary>
    public static string PersonalityName(NpcPersonality? personality) => personality switch
    {
        NpcPersonality.Steady => "堅実",
        NpcPersonality.RevolutionSeeker => "革命狙い",
        NpcPersonality.Conserver => "温存",
        NpcPersonality.Aggressive => "アグレッシブ",
        _ => "プレイヤー",
    };
}

/// <summary>BGM の選び方（対戦の状況から、通常のラウンジか緊張感のある曲かを決める）。</summary>
public static class BgmPolicy
{
    /// <summary>
    /// 対戦中の BGM。革命中、または自分がまだ残っているのに相手の誰かがあと2枚以下（あがりが迫った）なら緊張の曲。
    /// </summary>
    /// <param name="isRevolution">革命中か。</param>
    /// <param name="myRemaining">自分の残り枚数（あがった後は 0）。</param>
    /// <param name="fewestOpponentCards">まだあがっていない相手の、最も少ない残り枚数（相手がいなければ null）。</param>
    public static BgmTrack ForTable(bool isRevolution, int myRemaining, int? fewestOpponentCards)
    {
        if (isRevolution)
        {
            return BgmTrack.Tension;
        }
        return myRemaining > 0 && fewestOpponentCards is { } n && n > 0 && n <= PinchThreshold ? BgmTrack.Tension : BgmTrack.Lounge;
    }

    /// <summary>相手の残り枚数がこの枚数以下になったら「ピンチ」とみなす。</summary>
    public const int PinchThreshold = 2;
}
