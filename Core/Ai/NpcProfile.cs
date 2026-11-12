using System;

namespace DTTS.Core.Ai;

/// <summary>NPCの強さ。</summary>
public enum NpcDifficulty : byte
{
    /// <summary>よわい：判断にばらつきが大きく、ときどき適当に出す。手札の構成や先読みを考えない。</summary>
    Easy = 0,
    /// <summary>ふつう：手札の構成（ペア・階段）を崩さないよう考え、禁止あがりを避ける。</summary>
    Normal = 1,
    /// <summary>つよい：ばらつきなし。場に出たカードを記憶し、誰にも返されない手を見極めてあがりまでを読む。</summary>
    Hard = 2,
}

/// <summary>NPCの性格。</summary>
public enum NpcPersonality : byte
{
    /// <summary>堅実：バランス型。弱いカードから順に処理し、強いカードは勝負所まで取っておく。</summary>
    Steady = 0,
    /// <summary>革命狙い：革命で手札が強くなるなら積極的に起こし、革命の材料（同じ数字3枚以上）を崩さない。</summary>
    RevolutionSeeker = 1,
    /// <summary>温存：強いカードとジョーカーを最後まで温存し、無理に返さずパスしがち。</summary>
    Conserver = 2,
    /// <summary>アグレッシブ：強いカードも惜しまず場を取りに行き、8切り等で主導権を握る。ほとんどパスしない。</summary>
    Aggressive = 3,
}

/// <summary>NPCの設定（強さ × 性格）。</summary>
/// <param name="Difficulty">強さ。</param>
/// <param name="Personality">性格。</param>
public sealed record NpcProfile(NpcDifficulty Difficulty, NpcPersonality Personality)
{
    /// <summary>既定のNPC設定（ふつう・堅実）。</summary>
    public static NpcProfile Default { get; } = new(NpcDifficulty.Normal, NpcPersonality.Steady);

    /// <summary>評価関数の重み。</summary>
    public NpcWeights Weights => NpcWeights.For(this);

    /// <summary>表示名（例：「つよい・革命狙い」）。</summary>
    public string DisplayName => $"{DifficultyName(Difficulty)}・{PersonalityName(Personality)}";

    /// <summary>強さの表示名。</summary>
    public static string DifficultyName(NpcDifficulty d) => d switch
    {
        NpcDifficulty.Easy => "よわい",
        NpcDifficulty.Normal => "ふつう",
        _ => "つよい",
    };

    /// <summary>性格の表示名。</summary>
    public static string PersonalityName(NpcPersonality p) => p switch
    {
        NpcPersonality.Steady => "堅実",
        NpcPersonality.RevolutionSeeker => "革命狙い",
        NpcPersonality.Conserver => "温存",
        _ => "アグレッシブ",
    };
}

/// <summary>
/// 評価関数の重み。性格で傾向を、強さでばらつき（ノイズ）と考慮範囲を決める。
/// </summary>
public sealed record NpcWeights
{
    /// <summary>手札を1枚減らすことの価値。</summary>
    public double CardShed { get; init; } = 3.0;

    /// <summary>強いカードを使うコスト（強さの2乗に掛かる）。</summary>
    public double StrongCardCost { get; init; } = 1.0;

    /// <summary>ジョーカーを使うコスト（追加分）。</summary>
    public double JokerHold { get; init; } = 1.0;

    /// <summary>ペア・階段を崩すペナルティ。</summary>
    public double BreakPenalty { get; init; } = 4.0;

    /// <summary>革命による手札価値変化の重み。</summary>
    public double Revolution { get; init; } = 1.0;

    /// <summary>革命選択で革命を起こす方へ寄せる補正値。</summary>
    public double RevolutionBias { get; init; }

    /// <summary>場を流して親を取ることの価値。</summary>
    public double Flush { get; init; } = 1.0;

    /// <summary>パスの基準点（高いほどパスしやすい）。</summary>
    public double PassBaseline { get; init; } = -1.0;

    /// <summary>判断に加えるノイズの幅（±）。</summary>
    public double Noise { get; init; }

    /// <summary>完全にランダムな手を選ぶ確率（%）。</summary>
    public int RandomMovePercent { get; init; }

    /// <summary>手札の構成（ペア・階段の温存）を考慮するか。</summary>
    public bool ConsidersStructure { get; init; } = true;

    /// <summary>禁止カードだけが残る事態を避けるか。</summary>
    public bool AvoidsForbiddenFinish { get; init; } = true;

    /// <summary>場に出たカードを記憶し、返されない手を読むか。</summary>
    public bool CountsCards { get; init; }

    /// <summary>プロファイルから重みを生成する。</summary>
    public static NpcWeights For(NpcProfile profile)
    {
        var w = profile.Personality switch
        {
            NpcPersonality.RevolutionSeeker => new NpcWeights
            {
                Revolution = 3.0,
                RevolutionBias = 3.0,
                PassBaseline = -1.0,
            },
            NpcPersonality.Conserver => new NpcWeights
            {
                StrongCardCost = 1.6,
                JokerHold = 2.5,
                PassBaseline = 1.0,
                Flush = 0.8,
            },
            NpcPersonality.Aggressive => new NpcWeights
            {
                CardShed = 3.5,
                StrongCardCost = 0.45,
                JokerHold = 0.4,
                Flush = 2.5,
                PassBaseline = -5.0,
                BreakPenalty = 3.0,
            },
            _ => new NpcWeights(),
        };

        return profile.Difficulty switch
        {
            NpcDifficulty.Easy => w with
            {
                Noise = 5.0,
                RandomMovePercent = 25,
                ConsidersStructure = false,
                AvoidsForbiddenFinish = false,
                CountsCards = false,
            },
            NpcDifficulty.Normal => w with { Noise = 1.5, CountsCards = false },
            NpcDifficulty.Hard => w with { Noise = 0.0, CountsCards = true },
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }
}
