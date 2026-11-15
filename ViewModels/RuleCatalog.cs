using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Enums;
using DTTS.Core.Models;

namespace DTTS.ViewModels;

/// <summary>設定画面の右側（ルール詳細ガイド）に出す、ルール1項目の詳しい説明。</summary>
/// <param name="Title">ルール名。</param>
/// <param name="Category">属するカテゴリ名。</param>
/// <param name="Detail">詳しい解説。</param>
/// <param name="Condition">発動条件。</param>
/// <param name="Tip">戦略ポイントや具体例。</param>
public sealed record RuleGuide(string Title, string Category, string Detail, string Condition, string Tip)
{
    /// <summary>何も選んでいないときの案内。</summary>
    public static RuleGuide Placeholder { get; } = new(
        "ルールを選んでください",
        "",
        "左のルールにマウスを重ねるか、クリック（キーボードなら Tab で移動）すると、ここに詳しい解説が表示されます。",
        "カテゴリの見出しをクリックすると、そのカテゴリの項目が開きます。",
        "迷ったら、プリセットの「初心者用」か「中級者用」から始めて、気になるルールを1つずつ足していくのがおすすめです。");
}

/// <summary>ルール詳細ガイドを持つ設定項目（ON/OFF・選択肢の共通部分）。</summary>
public interface IRuleGuideSource
{
    /// <summary>この項目の詳しい説明。</summary>
    RuleGuide Guide { get; }
}

/// <summary>ルール設定の ON/OFF 1項目。値は <see cref="RuleSettings"/> を直接読み書きする。</summary>
public sealed partial class RuleToggleViewModel : ObservableObject, IRuleGuideSource
{
    private readonly RuleSettings _rules;
    private readonly Func<RuleSettings, bool> _get;
    private readonly Action<RuleSettings, bool> _set;
    private readonly Action _changed;

    public RuleToggleViewModel(
        RuleSettings rules, string label, string hint, RuleGuide guide,
        Func<RuleSettings, bool> get, Action<RuleSettings, bool> set, Action changed)
    {
        _rules = rules;
        Label = label;
        Hint = hint;
        Guide = guide;
        _get = get;
        _set = set;
        _changed = changed;
    }

    public string Label { get; }

    /// <summary>1行の短い説明。</summary>
    public string Hint { get; }

    /// <inheritdoc />
    public RuleGuide Guide { get; }

    public bool IsOn
    {
        get => _get(_rules);
        set
        {
            if (_get(_rules) == value) return;
            _set(_rules, value);
            OnPropertyChanged();
            _changed();
        }
    }
}

/// <summary>
/// ルール設定の「いくつかの値から1つ選ぶ」項目（縛りの連続回数・階段の最小枚数など）。
/// 値は <see cref="RuleSettings"/> を直接読み書きする。
/// </summary>
public sealed partial class RuleChoiceViewModel : ObservableObject, IRuleGuideSource
{
    private readonly RuleSettings _rules;
    private readonly int[] _values;
    private readonly Func<RuleSettings, int> _get;
    private readonly Action<RuleSettings, int> _set;
    private readonly Action _changed;

    public RuleChoiceViewModel(
        RuleSettings rules, string label, string hint, RuleGuide guide,
        IReadOnlyList<(string Label, int Value)> options,
        Func<RuleSettings, int> get, Action<RuleSettings, int> set, Action changed)
    {
        _rules = rules;
        Label = label;
        Hint = hint;
        Guide = guide;
        OptionLabels = options.Select(o => o.Label).ToArray();
        _values = options.Select(o => o.Value).ToArray();
        _get = get;
        _set = set;
        _changed = changed;
    }

    public string Label { get; }

    /// <summary>1行の短い説明。</summary>
    public string Hint { get; }

    /// <inheritdoc />
    public RuleGuide Guide { get; }

    /// <summary>選択肢の表示名。</summary>
    public IReadOnlyList<string> OptionLabels { get; }

    /// <summary>
    /// 選んでいる選択肢の番号。設定値が選択肢にない場合（手で編集した設定など）は、いちばん近い選択肢を示す。
    /// </summary>
    public int SelectedIndex
    {
        get
        {
            int current = _get(_rules);
            int best = 0;
            for (int i = 1; i < _values.Length; i++)
            {
                if (Math.Abs(_values[i] - current) < Math.Abs(_values[best] - current))
                {
                    best = i;
                }
            }
            return best;
        }
        set
        {
            // コンボボックスは項目の読み込み中に -1 を書き戻すことがある。範囲外の値は無視する
            if (value < 0 || value >= _values.Length || _get(_rules) == _values[value]) return;
            _set(_rules, _values[value]);
            OnPropertyChanged();
            _changed();
        }
    }
}

/// <summary>ルール設定の1カテゴリ（折りたたみカード1枚分）。</summary>
public sealed partial class RuleCategoryViewModel : ObservableObject
{
    public RuleCategoryViewModel(int number, string title, string summary)
    {
        Number = number;
        Title = title;
        Summary = summary;
    }

    public int Number { get; }
    public string Title { get; }

    /// <summary>見出しの下に出す、カテゴリの説明。</summary>
    public string Summary { get; }

    public string Heading => $"{Number}. {Title}";

    /// <summary>ON/OFF の項目。</summary>
    public ObservableCollection<RuleToggleViewModel> Items { get; } = new();

    /// <summary>選択式の項目（縛りの連続回数・階段の最小枚数など）。ないカテゴリでは空。</summary>
    public ObservableCollection<RuleChoiceViewModel> Choices { get; } = new();

    /// <summary>選択式の項目があるか。</summary>
    public bool HasChoices => Choices.Count > 0;

    /// <summary>有効な項目の数（例：「4 / 8」）。</summary>
    [ObservableProperty]
    public partial string BadgeText { get; set; } = "";

    /// <summary>1つでも有効な項目があるか（バッジの強調に使う）。</summary>
    [ObservableProperty]
    public partial bool HasEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public void RefreshBadge()
    {
        int on = Items.Count(i => i.IsOn);
        BadgeText = $"{on} / {Items.Count}";
        HasEnabled = on > 0;
    }
}

/// <summary>
/// 設定画面に並べるルール項目の一覧（カテゴリ・表示名・説明・詳細ガイド）。
/// ルールの値そのものはすべて <see cref="RuleSettings"/> にあり、ここは見せ方だけを決める。
/// </summary>
public static class RuleCatalog
{
    /// <summary>ON/OFF の項目。Detail＝詳しい解説、Condition＝発動条件、Tip＝戦略ポイント・具体例。</summary>
    private sealed record Item(
        string Label, string Hint, Func<RuleSettings, bool> Get, Action<RuleSettings, bool> Set,
        string Detail, string Condition, string Tip);

    /// <summary>選択式の項目。</summary>
    private sealed record Choice(
        string Label, string Hint, (string Label, int Value)[] Options, Func<RuleSettings, int> Get, Action<RuleSettings, int> Set,
        string Detail, string Condition, string Tip);

    private sealed record Category(string Title, string Summary, Item[] Items, Choice[]? Choices = null);

    /// <summary>得点配分の組み合わせ（大富豪・富豪・平民・貧民・大貧民）。</summary>
    private sealed record PointPreset(string Label, int Daifugo, int Fugo, int Heimin, int Hinmin, int Daihinmin)
    {
        public bool Matches(ProgressRules p)
            => p.PointsDaifugo == Daifugo && p.PointsFugo == Fugo && p.PointsHeimin == Heimin
                && p.PointsHinmin == Hinmin && p.PointsDaihinmin == Daihinmin;

        public void Apply(ProgressRules p)
        {
            p.PointsDaifugo = Daifugo;
            p.PointsFugo = Fugo;
            p.PointsHeimin = Heimin;
            p.PointsHinmin = Hinmin;
            p.PointsDaihinmin = Daihinmin;
        }
    }

    // Categories より前に置く（静的フィールドは書いた順に初期化されるため）
    private static readonly PointPreset[] PointPresets =
    {
        new("3・2・1・0（標準）", 3, 2, 1, 1, 0),
        new("差を広げる 5・3・1・0", 5, 3, 2, 1, 0),
        new("上位だけ 2・1・0・0", 2, 1, 0, 0, 0),
        new("2・1・-1・-2（マイナスあり）", 2, 1, 0, -1, -2),
    };

    private static readonly Category[] Categories =
    {
        new("革命系", "強さが逆転する役", new Item[]
        {
            new("革命", "同じ数字をまとめて出すと強さが逆転", r => r.Revolution.Enabled, (r, v) => r.Revolution.Enabled = v,
                "同じ数字を決まった枚数（標準は4枚）以上まとめて出すと、カードの強さが逆転します（3が最強、2が最弱。ジョーカーは最強のまま）。次の革命が起きるまで、ラウンドの終わりまで続きます。",
                "同じ数字を「革命に必要な枚数」以上出す。ジョーカーを枚数に数えるかは「ジョーカーを含む革命」で決まります。",
                "弱いカードばかりの手札なら狙い目です。3や4を温存しておくと、革命後に一気に有利になります。"),
            new("階段革命", "長い階段でも革命が起きる", r => r.Revolution.SequenceRevolutionEnabled, (r, v) => r.Revolution.SequenceRevolutionEnabled = v,
                "決まった枚数（標準は5枚）以上の階段を出したときにも革命が起きます。必要な枚数は下の「階段革命の必要枚数」で選べます。",
                "同じスートの連番を「階段革命の必要枚数」以上出す。",
                "長い階段は返されにくいので、革命と同時に親も取りやすい強力な一手です。"),
            new("革命返し", "革命中の革命で元に戻る", r => r.Revolution.CounterRevolutionEnabled, (r, v) => r.Revolution.CounterRevolutionEnabled = v,
                "革命中にもう一度革命を起こすと、強さが元に戻ります。オフにすると、革命中は革命の条件を満たしても逆転は起きません（出すこと自体はできます）。",
                "革命中に、革命の条件を満たす出し方をする。",
                "相手の革命で不利になったときの切り札です。4枚組は安易に崩さず持っておくと保険になります。"),
            new("ジョーカーを含む革命", "ジョーカーを枚数に数える", r => r.Revolution.JokerCountsForRevolution, (r, v) => r.Revolution.JokerCountsForRevolution = v,
                "革命・階段革命・クーデターなどの枚数に、ジョーカーを含めて数えます。オフにすると、ジョーカー以外の枚数だけで判定します。",
                "例：同じ数字3枚＋ジョーカー1枚 → オンなら革命、オフなら革命になりません。",
                "オンにすると革命が起きやすくなります。荒れた展開が好きならオンがおすすめです。"),
            new("革命の選択", "起こすかどうかを選べる", r => r.Revolution.RevolutionIsOptional, (r, v) => r.Revolution.RevolutionIsOptional = v,
                "革命の条件を満たしたとき、革命を起こすかどうかを出した人が選べます。",
                "革命の条件を満たす出し方をすると、「起こす／起こさない」の確認が表示されます。",
                "4枚をまとめて処理したいだけで逆転は困る、というときに「起こさない」を選べます。"),
            new("クーデター", "9を3枚で革命", r => r.Revolution.CoupEnabled, (r, v) => r.Revolution.CoupEnabled = v,
                "9をちょうど3枚出すと革命が起きます。",
                "9を3枚（ちょうど）出す。",
                "4枚そろわなくても革命を起こせる近道です。9リバースや99車も有効だと、9の価値が一気に上がります。"),
            new("オーメン", "6を3枚で革命", r => r.Revolution.OmenEnabled, (r, v) => r.Revolution.OmenEnabled = v,
                "6をちょうど3枚出すと革命が起き、さらにそのラウンド中は以後の革命（革命返しを含む）が一切起きなくなります。",
                "6を3枚（ちょうど）出す。",
                "逆転した状態を固定できます。弱い手札のときに決まれば、そのラウンドの主導権をほぼ握れます。"),
            new("ナナサン革命", "7を3枚で革命", r => r.Revolution.NanasanEnabled, (r, v) => r.Revolution.NanasanEnabled = v,
                "7をちょうど3枚出すと革命が起きます。",
                "7を3枚（ちょうど）出す。",
                "7渡しも有効なら、革命と同時に3枚を次の人へ押し付けられます。"),
        }, new Choice[]
        {
            new("革命に必要な枚数", "同じ数字を何枚出すと革命か",
                new[] { ("4枚以上（標準）", 4), ("5枚以上", 5), ("3枚以上", 3) },
                r => r.Revolution.MinCards, (r, v) => r.Revolution.MinCards = v,
                "革命が起きるのに必要な、同じ数字の枚数です。5枚以上はジョーカーを含めないと出せないため、革命はめったに起きなくなります。3枚以上にすると、3枚組を出すたびに革命が起きます。",
                "「革命」がオンのとき、同じ数字をこの枚数以上まとめて出す。ジョーカーを数えるかは「ジョーカーを含む革命」に従います。",
                "3枚以上は荒れた展開に、5枚以上は革命の少ない落ち着いた勝負になります。クーデターなど「ちょうど3枚」の役とは別に判定されます。"),
            new("階段革命の必要枚数", "何枚の階段で革命か",
                new[] { ("5枚以上（標準）", 5), ("4枚以上", 4), ("6枚以上", 6) },
                r => r.Revolution.SequenceRevolutionMinLength, (r, v) => r.Revolution.SequenceRevolutionMinLength = v,
                "階段革命が起きるのに必要な、階段の枚数です。",
                "「階段革命」がオンのとき、同じスートの連番をこの枚数以上出す。",
                "4枚以上にすると階段革命がぐっと起きやすくなります。ジョーカーで欠けた数字を補えば、さらに狙いやすくなります。"),
        }),
        new("階段系", "同じスートの連番", new Item[]
        {
            new("階段", "同じスートの連番をまとめて出せる", r => r.Sequence.Enabled, (r, v) => r.Sequence.Enabled = v,
                "同じスートで数字が連続したカードを、まとめて出せます。場に階段が出ているときは、同じ枚数でより強い階段だけが出せます。",
                "同じスートの連番を出す。最小枚数は下の「階段の最小枚数」で、3枚から／2枚から を選べます。",
                "単騎では弱いカードも、階段にすれば一度に処理できます。階段は返されにくく、親を取りやすい形です。"),
            new("階段にジョーカー", "欠けた数字をジョーカーで補える", r => r.Sequence.AllowJokerInSequence, (r, v) => r.Sequence.AllowJokerInSequence = v,
                "階段の欠けた数字や端を、ジョーカーで補えます。",
                "例：♠3・♠5＋ジョーカー → ♠3-4-5 の階段。",
                "あと1枚で階段になる手札なら、ジョーカーを階段に回すと手数を大きく減らせます。"),
            new("階段に2を含める", "K・A・2 のつながりを認める", r => r.Sequence.AllowTwoInSequence, (r, v) => r.Sequence.AllowTwoInSequence = v,
                "K・A・2 とつながる階段を認めます。どちらの設定でも、2から3へ折り返す階段（A-2-3 など）は出せません。",
                "例：♥K-A-2。オフにすると、階段に使えるのは A までです。",
                "最強クラスの階段になります。ただし2であがるのは禁止あがりの設定に注意してください。"),
            new("階段中の効果", "階段に含まれるカードの効果も発動", r => r.Sequence.EffectsInSequence, (r, v) => r.Sequence.EffectsInSequence = v,
                "階段に含まれる 8・J・5 などの数字の効果を発動させます。オフにすると、階段ではこれらの効果は起きません（革命・縛りは別扱いです）。",
                "例：♥7-8-9 を出す → オンなら8切りで場が流れます。",
                "オンにすると、階段が「まとめて処理＋場流し」の強力な手になります。"),
            new("エンペラー", "4スート別の4枚連番が最強", r => r.Sequence.EmperorEnabled, (r, v) => r.Sequence.EmperorEnabled = v,
                "4種類のスートを1枚ずつ使った4枚の連番です。場が空のとき、または場が4枚の階段のときに出せ、4枚階段には強さに関係なく勝ちます。出すと場が流れます。",
                "例：♠4・♥5・♦6・♣7（スートがすべて違う4枚の連番）。",
                "スートがばらばらで階段にならない連番を、一度に処理できる珍しい役です。"),
            new("エンペラーで革命", "エンペラーで革命も起きる", r => r.Sequence.EmperorCausesRevolution, (r, v) => r.Sequence.EmperorCausesRevolution = v,
                "エンペラーを出したとき、同時に革命も起きるようにします。",
                "「エンペラー」がオンのときに、エンペラーを出す。",
                "場を流して親を取りつつ強さも逆転させる、1手で流れを変える組み合わせです。"),
        }, new Choice[]
        {
            new("階段の最小枚数", "何枚の連番から階段として出せるか",
                new[] { ("3枚から", 3), ("2枚から", 2) },
                r => r.Sequence.MinLength, (r, v) => r.Sequence.MinLength = v,
                "階段として出せる最小の枚数です。「2枚から」にすると、同じスートの連番2枚（例：♠5・♠6）も階段として出せます。",
                "2枚の階段には、2枚の階段でしか返せません（同じ数字2枚のペアとは別の役です）。",
                "「2枚から」にすると出せる手が大きく増え、展開が速くなります。ジョーカー＋1枚は、親のときはペア、階段の場では階段として出せます。"),
        }),
        new("縛り系", "出せるカードの制限", new Item[]
        {
            new("スート縛り", "同じスートが続くとそのスートのみ", r => r.Lock.SuitLockEnabled, (r, v) => r.Lock.SuitLockEnabled = v,
                "直前と同じスート（マーク）のカードが続くと、場が流れるまでそのスートしか出せなくなります。ジョーカーはどのスートの代わりにもなります。",
                "同じスートの出し方が連続する。必要な連続回数は下の「縛りが成立する連続回数」で、2枚連続／3枚連続 を選べます。",
                "相手が持っていなさそうなスートで縛れば、そのまま親を取りやすくなります。"),
            new("数縛り", "連番が続くと次の数字のみ", r => r.Lock.NumberLockEnabled, (r, v) => r.Lock.NumberLockEnabled = v,
                "直前よりちょうど1つ強い数字が続くと、場が流れるまで「ちょうど1つ上」の数字しか出せなくなります。",
                "例：5 → 6 と続く（3枚連続の設定なら 5 → 6 → 7 で成立）。",
                "連番を持っていれば続けて出せ、相手は続きにくくなります。途切れたところで場が流れます。"),
            new("両しば", "一部のスートが一致した分だけ縛る", r => r.Lock.PartialSuitLockEnabled, (r, v) => r.Lock.PartialSuitLockEnabled = v,
                "複数枚出しで、スートの一部だけが直前と一致した場合にも、一致したスートの分だけ縛ります。",
                "例：♠♥ のペアに ♠♦ のペアを出す → 「♠＋何でも1枚」の縛り。",
                "ペア以上の場でも縛りが起きやすくなります。スートのそろったペアが強くなります。"),
            new("激しば", "スート縛りと数縛りが同時に成立", r => r.Lock.StrictLockEnabled, (r, v) => r.Lock.StrictLockEnabled = v,
                "スート縛りと数縛りが同時に成立することを認めます。オフのときは、両方の条件を同時に満たしてもスート縛りだけが成立します。",
                "例：♠5 → ♠6 と続く → 「♠で、かつ7」しか出せなくなります。",
                "出せるカードがほぼ1枚に絞られます。決まれば、ほぼ確実に親を取れます。"),
        }, new Choice[]
        {
            new("縛りが成立する連続回数", "何枚続いたら縛りになるか",
                new[] { ("2枚連続", 1), ("3枚連続", 2) },
                r => r.Lock.TriggerCount, (r, v) => r.Lock.TriggerCount = v,
                "縛り（スート縛り・数縛り）が成立するまでに、条件を満たす出し方が何枚続く必要があるかを決めます。",
                "2枚連続：♠ → ♠ で成立。3枚連続：♠ → ♠ → ♠ と3回続いて成立。途中で違うスートが出ると数え直しです。",
                "「3枚連続」にすると縛りが起きにくくなり、自由に出せる場面が増えます。縛りに振り回されたくないときに。"),
        }),
        new("流し・切り系", "場を流す役", new Item[]
        {
            new("8切り", "8を出すと場が流れる", r => r.Flow.EightCutEnabled, (r, v) => r.Flow.EightCutEnabled = v,
                "8を含むカードを出すと、その場で場が流れ、出した人が次の親になります。",
                "8を出す（ペアや3枚でも可）。",
                "弱いカードの直後に8で切って親を取り、好きな形で出し直すのが基本です。8であがるのは禁止あがりの設定に注意してください。"),
            new("4止め", "8切りを4で止められる", r => r.Flow.FourStopEnabled, (r, v) => r.Flow.FourStopEnabled = v,
                "8切りが出た直後、4を出すと8切りを止められます。標準では8の枚数の2倍の4が必要です（8が1枚なら4を2枚、8が2枚なら4を4枚）。止めると場は流れず、4が場に残って次の人へ続きます。必要な枚数は下の「4止めに必要な枚数」で選べます。",
                "8切りの直後、必要な枚数の4を持つ人に「止める／見送る」の確認が出ます。階段に含まれる8の8切りは止められません。",
                "相手に親を渡したくないときに。4を4枚で止めると、革命も同時に起きます（「革命」がオンの場合）。"),
            new("砂嵐", "3を3枚で場が流れる・流しを止められる", r => r.Flow.SandstormEnabled, (r, v) => r.Flow.SandstormEnabled = v,
                "3をちょうど3枚出すと、同じ枚数のどんな組にも強さに関係なく勝ち、場が流れます。さらに、ろくろ首・99車が出た直後には、枚数が違っても割り込んで流しを止められます。",
                "3を3枚（ちょうど）出す。ろくろ首・99車の直後は、砂嵐を持っているかどうかに関わらず全員に「砂嵐で止める／パス（流す）」の確認が出ます（持っていない人は「パス（流す）」だけ押せます）。",
                "全員に確認が出るので、確認の有無から砂嵐の持ち主はわかりません。止めると砂嵐で場が流れ、止めた人が親になります。最弱の3が3枚そろったら、切り札として温存しましょう。"),
            new("99車", "9を2枚で場が流れる", r => r.Flow.AmbulanceEnabled, (r, v) => r.Flow.AmbulanceEnabled = v,
                "9をちょうど2枚出すと、同じ枚数のどんな組にも強さに関係なく勝ち、場が流れます（救急車とも呼びます）。",
                "9を2枚（ちょうど）出す。スート縛り・数縛りには従います。砂嵐が有効なら、直後に砂嵐で止められることがあります。",
                "強いペアに対する切り返しに。2のペアにも勝てます。"),
            new("ろくろ首", "6を2枚で場が流れる", r => r.Flow.RokurokubiEnabled, (r, v) => r.Flow.RokurokubiEnabled = v,
                "6をちょうど2枚出すと場が流れます。砂嵐・99車と違い、出すためには通常どおり場のカードより強い必要があります。",
                "6を2枚（ちょうど）出す。砂嵐が有効なら、直後に砂嵐で止められることがあります。",
                "親のときや弱いペアの後に出して、そのまま親を続けるのに使えます。"),
        }, new Choice[]
        {
            new("4止めに必要な枚数", "8切りを止めるのに4が何枚いるか",
                new[]
                {
                    ("8の2倍（標準）", (int)FourStopCountRule.DoubleEight),
                    ("8と同じ枚数", (int)FourStopCountRule.SameAsEight),
                },
                r => (int)r.Flow.FourStopCount, (r, v) => r.Flow.FourStopCount = (FourStopCountRule)v,
                "8の2倍：8が1枚なら4を2枚（ペア）、8が2枚なら4を4枚出すと止められます。\n8と同じ枚数：8が1枚なら4を1枚、8が2枚なら4を2枚で止められます。",
                "「4止め」がオンのとき、8切りの直後。",
                "「8の2倍」は止めるのが難しく、8切りの価値が高いままです。気軽に止め合いたいなら「8と同じ枚数」に。"),
        }),
        new("11バック系", "一時的な強さの逆転", new Item[]
        {
            new("11バック", "Jを出すと強さが逆転", r => r.ElevenBack.Enabled, (r, v) => r.ElevenBack.Enabled = v,
                "J（11）を含むカードを出すと、カードの強さが逆転します。逆転がいつまで続くかは、下の「11バックの持続」で選べます（標準は場が流れるまで）。革命中に出すと打ち消し合って、通常の強さになります。",
                "Jを出す。逆転中にもう一度Jが出ると、その時点で解除されます（「次の手番だけ」の設定では、改めて次の人だけが逆転します）。",
                "Jの直後は弱いカードが強くなります。3や4を処理する好機です。対戦画面の「11バック」バッジで確認できます。"),
        }, new Choice[]
        {
            new("11バックの持続", "逆転がいつまで続くか",
                new[]
                {
                    ("場が流れるまで（標準）", (int)ElevenBackDuration.UntilFlush),
                    ("次の手番だけ", (int)ElevenBackDuration.NextTurnOnly),
                    ("永続（強化11バック）", (int)ElevenBackDuration.Persistent),
                },
                r => (int)r.ElevenBack.Duration, (r, v) => r.ElevenBack.Duration = (ElevenBackDuration)v,
                "場が流れるまで：場が流れた時点で必ず元に戻ります。\n次の手番だけ：Jを出した人の次の1人だけが逆転した強さで出し、その人が出すかパスした時点で元に戻ります（場が流れた場合も戻ります）。\n永続（強化11バック）：場が流れても続き、次に誰かがJを出すまで解除されません。",
                "「11バック」がオンのときにJを出す。対戦画面のバッジは「11バック」「11バック（次の手番まで）」「強化11バック（永続）」と表示が変わります。どの設定でも、新しいラウンドでは必ず元に戻ります。",
                "「次の手番だけ」は、Jを出した人ではなく次の人だけが得をする設定です。次の人に弱いカードを出させたくないときは、Jの使いどころに注意しましょう。「永続」は「完全上級者」プリセットに含まれます。"),
        }),
        new("スキップ・リバース系", "手番の順番を変える", new Item[]
        {
            new("5スキップ", "出した枚数だけ次の人を飛ばす", r => r.SkipReverse.FiveSkipEnabled, (r, v) => r.SkipReverse.FiveSkipEnabled = v,
                "5を出すと、出した枚数分だけ、次に手番が来る人を順に飛ばします（あがった人・パスで抜けている人は数えません）。",
                "5を出す（1枚なら1人、2枚なら2人飛ばし）。残っている全員を飛ばした場合は場が流れ、5を出した人が親になります。",
                "あがりそうな次の人を飛ばして時間を稼げます。飛ばされた人も、次に誰かがカードを出せば再び出せます。"),
            new("13スキップ", "Kで次の人を飛ばす", r => r.SkipReverse.ThirteenSkipEnabled, (r, v) => r.SkipReverse.ThirteenSkipEnabled = v,
                "K（13）を出すと、出した枚数分だけ後ろの人の手番を飛ばします。",
                "Kを出す（2枚なら2人飛ばし）。",
                "強いKに追加の効果が付きます。Kで場を取りながら、次の人の反撃も封じられます。"),
            new("9リバース", "9で回る向きが逆になる", r => r.SkipReverse.NineReverseEnabled, (r, v) => r.SkipReverse.NineReverseEnabled = v,
                "9を出すと、手番の回る向きが逆になります。偶数枚出すと、向きは元のままです。",
                "9を奇数枚出す。",
                "あがりそうな次の人に手番を渡さず、反対側へ回せます。"),
            new("12リバース", "Qで回る向きが逆になる", r => r.SkipReverse.TwelveReverseEnabled, (r, v) => r.SkipReverse.TwelveReverseEnabled = v,
                "Q（12）を出すと、手番の回る向きが逆になります。偶数枚出すと、向きは元のままです。",
                "Qを奇数枚出す。",
                "12ボンバーも有効なら、Q1枚で「数字を消す＋向きを変える」が同時に起きます。"),
        }),
        new("特殊効果系", "カードを渡す・捨てる", new Item[]
        {
            new("7渡し", "出した枚数だけ次の人へ渡す", r => r.Special.SevenPassEnabled, (r, v) => r.Special.SevenPassEnabled = v,
                "7を出すと、出した枚数分だけ、好きな手札を次の人へ渡します。",
                "7を出す（2枚なら2枚渡す）。渡した結果、手札が0枚になればあがりです。",
                "いらない弱いカードを押し付けられます。最後の1枚を渡してあがる動きも強力です。"),
            new("10捨て", "出した枚数まで好きに捨てる", r => r.Special.TenDiscardEnabled, (r, v) => r.Special.TenDiscardEnabled = v,
                "10を出すと、出した枚数までの好きな手札を捨てられます（捨てなくてもかまいません）。",
                "10を出す（2枚なら2枚まで捨てられる）。捨てた結果、手札が0枚になればあがりです。",
                "処理しにくい孤立した弱いカードを捨てるのに最適です。"),
            new("12ボンバー", "宣言した数字を全員が捨てる", r => r.Special.TwelveBomberEnabled, (r, v) => r.Special.TwelveBomberEnabled = v,
                "Q（12）を出すと、出した枚数分の数字を宣言し、全員（自分も含む）がその数字のカードをすべて捨てます。数字のほかに、ジョーカー（JK）も宣言できます。消えた数字は画面中央に弱い順で表示されます。",
                "Qを出す（2枚なら2種類を宣言）。捨てた結果、手札が0枚になった人はあがりです。",
                "自分が多く持つ弱い数字を宣言すれば、手札を一気に減らせます。相手のジョーカーが残っていそうなら、JK を宣言して最強札を消すのも有効です。"),
            new("ダウンナンバー", "同じスートでちょうど1つ下の数字を出せる", r => r.Special.DownNumberEnabled, (r, v) => r.Special.DownNumberEnabled = v,
                "場と同じスートの組み合わせで、強さがちょうど1つ下の数字を出せます（単騎・複数枚出しのみ。ジョーカーは不可）。数縛り中は使えません。",
                "例：場が♥9のとき、♥8を出せる。",
                "強いカードがなくても場に続けられます。同じスートの連番を持っていると有利です。"),
        }),
        new("ジョーカー", "ジョーカーまわり", new Item[]
        {
            new("スペ3返し", "単体ジョーカーに♠3で勝つ", r => r.Joker.SpadeThreeReturnEnabled, (r, v) => r.Joker.SpadeThreeReturnEnabled = v,
                "1枚で出されたジョーカーに対して、♠3を1枚出すと勝てます。出すと場が流れ、♠3を出した人が親になります。縛りは無視します。",
                "場が単体のジョーカーのとき、♠3を1枚出す。",
                "最弱の♠3が、最強のジョーカーへの唯一の対抗手段になります。♠3を持っている間は、ジョーカーを単騎で出しにくくなります。"),
            new("ジョーカーで効果発動", "代わりにした数字の効果も起きる", r => r.Joker.JokerTriggersEffects, (r, v) => r.Joker.JokerTriggersEffects = v,
                "ジョーカーを数字の代わりに使ったとき、その数字の効果も発動します。砂嵐・99車などの「特定の数字×枚数」の役にも使えます。",
                "例：8＋ジョーカーのペア → 8が2枚ぶんの8切り。3が2枚＋ジョーカー → 砂嵐。",
                "ジョーカーの使い道が大きく広がります。足りない1枚を補って役を完成させられます。"),
        }, new Choice[]
        {
            new("ジョーカーの枚数", "山札に入れるジョーカーの枚数",
                new[] { ("1枚（標準）", 1), ("2枚", 2), ("なし", 0) },
                r => r.Joker.JokerCount, (r, v) => r.Joker.JokerCount = v,
                "山札に入れるジョーカーの枚数です。1枚のときは1枚目のジョーカーだけ、2枚のときは絵柄の違う2枚のジョーカーが登場します。なしにすると、ジョーカーを使うルールは実質的に起きなくなります。",
                "ラウンドの開始時（カードを配るとき）に反映されます。",
                "2枚にすると、ジョーカーを持つ人が増えて逆転が起きやすくなります。スペ3返しや12ボンバーの「JK」宣言の価値も上がります。"),
        }),
        new("階級変動", "ラウンドをまたぐ逆転", new Item[]
        {
            new("都落ち", "大富豪が1位を逃すと大貧民", r => r.RankChange.MiyakoOchiEnabled, (r, v) => r.RankChange.MiyakoOchiEnabled = v,
                "前のラウンドの大富豪が1位であがれなかった場合、その時点で大貧民に転落します（残りの手札は没収されます）。",
                "大富豪以外の誰かが、最初にあがる。",
                "大富豪でも油断できなくなります。大富豪より先にあがることを全員が狙う展開になります。"),
            new("下剋上", "大貧民が1位なら階級が反転", r => r.RankChange.GekokujoEnabled, (r, v) => r.RankChange.GekokujoEnabled = v,
                "前のラウンドの大貧民が1位であがると、全員の階級が上下反転し、その場でラウンドが終わります（大貧民 → 大富豪、貧民 → 富豪）。",
                "大貧民が、最初にあがる。",
                "カード交換で不利な大貧民に、一発逆転の目が生まれます。"),
        }),
        new("禁止あがり", "最後の1手の制限", new Item[]
        {
            new("禁止あがり", "以下の出し方ではあがれない", r => r.ForbiddenFinish.Enabled, (r, v) => r.ForbiddenFinish.Enabled = v,
                "下の項目で指定した出し方で最後の1手を出すと反則になり、「禁止あがりをしたとき」で選んだ扱いになります（標準は最下位）。オフにすると、どんな出し方でもあがれます。",
                "最後の手札を、禁止されている出し方で出し切る。",
                "強いカードは最後まで残さず、途中で使い切る計画が必要になります。大富豪らしい駆け引きの核になるルールです。"),
            new("2あがり禁止", "最強の数字であがれない", r => r.ForbiddenFinish.ForbidStrongestNumber, (r, v) => r.ForbiddenFinish.ForbidStrongestNumber = v,
                "最強の数字（通常は2）を含む出し方ではあがれません。",
                "最後の1手に2が含まれている。",
                "2は場を取るために途中で使い、最後は弱めのカードであがる形を作りましょう。"),
            new("最強数字は逆転に追従", "革命・11バック中は3あがりを禁止", r => r.ForbiddenFinish.StrongestFollowsReversal, (r, v) => r.ForbiddenFinish.StrongestFollowsReversal = v,
                "強さが逆転している間（革命・11バック中）は、最強になっている3であがることを禁止します。オフにすると、常に2だけが禁止です。",
                "逆転中に、最後の1手に3が含まれている。",
                "「そのとき最強の数字であがれない」という一貫したルールになります。"),
            new("8切りあがり禁止", "8切りであがれない", r => r.ForbiddenFinish.ForbidEightCut, (r, v) => r.ForbiddenFinish.ForbidEightCut = v,
                "8切りが発動する出し方ではあがれません。",
                "最後の1手で8切りが起きる。",
                "8は親を取るために使い、最後の1枚には残さないようにしましょう。"),
            new("ジョーカーあがり禁止", "ジョーカーであがれない", r => r.ForbiddenFinish.ForbidJoker, (r, v) => r.ForbiddenFinish.ForbidJoker = v,
                "ジョーカーを含む出し方ではあがれません。",
                "最後の1手にジョーカーが含まれている。",
                "ジョーカーは最後の1つ手前までに使い切りましょう。"),
            new("スペ3あがり禁止", "スペ3返しであがれない", r => r.ForbiddenFinish.ForbidSpadeThree, (r, v) => r.ForbiddenFinish.ForbidSpadeThree = v,
                "スペ3返し（ジョーカーに♠3で勝つ）ではあがれません。",
                "最後の1手がスペ3返しになる。",
                "♠3を最後まで抱えると、あがれない場面が出てきます。"),
            new("12ボンバーあがり禁止", "12ボンバーであがれない", r => r.ForbiddenFinish.ForbidTwelveBomber, (r, v) => r.ForbiddenFinish.ForbidTwelveBomber = v,
                "12ボンバーが発動する出し方ではあがれません。",
                "最後の1手で12ボンバーが起きる。",
                "Qは早めに使って、数字を消す効果を活かしましょう。"),
            new("11バックあがり禁止", "11バックであがれない", r => r.ForbiddenFinish.ForbidElevenBack, (r, v) => r.ForbiddenFinish.ForbidElevenBack = v,
                "11バックが発動する出し方ではあがれません。",
                "最後の1手で11バックが起きる。",
                "Jを最後に残さないよう、手順を組み立てましょう。"),
            new("流し役あがり禁止", "砂嵐・99車などであがれない", r => r.ForbiddenFinish.ForbidOtherFlushEffects, (r, v) => r.ForbiddenFinish.ForbidOtherFlushEffects = v,
                "砂嵐・99車・ろくろ首・エンペラーで場を流してあがることを禁止します。",
                "最後の1手が、これらの場を流す役になる。",
                "場を流す役は「親を取るための手」と割り切って使いましょう。"),
            new("革命あがり禁止", "革命を起こしてあがれない", r => r.ForbiddenFinish.ForbidRevolution, (r, v) => r.ForbiddenFinish.ForbidRevolution = v,
                "革命を起こす出し方ではあがれません。",
                "最後の1手で革命が起きる。",
                "4枚組を最後に残すと、あがれなくなることがあります。"),
        }, new Choice[]
        {
            new("禁止あがりをしたとき", "反則のあがりの扱い",
                new[]
                {
                    ("最下位になる（標準）", (int)ForbiddenFinishPenalty.LastPlace),
                    ("その手では出せない", (int)ForbiddenFinishPenalty.RejectPlay),
                },
                r => (int)r.ForbiddenFinish.Penalty, (r, v) => r.ForbiddenFinish.Penalty = (ForbiddenFinishPenalty)v,
                "最下位になる：禁止された出し方でも出せますが、手札がなくなった時点で反則あがりとなり、そのラウンドは最下位（大貧民）の扱いになります。\nその手では出せない：最後の1手として禁止された出し方は、そもそも出せません。別の手を出すかパスします。",
                "「禁止あがり」がオンで、最後の手札を禁止された出し方で出そうとしたとき。",
                "「その手では出せない」はうっかり反則負けを防げる、初心者に優しい設定です。親番で出せる手が禁止の手しか残っていないときは、反則として手札を没収されます。"),
        }),
        new("カード交換", "ラウンド開始時の交換", new Item[]
        {
            new("カード交換", "階級に応じてカードを交換", r => r.Exchange.Enabled, (r, v) => r.Exchange.Enabled = v,
                "2ラウンド目以降、配られた直後に階級に応じてカードを交換します。標準では大貧民は大富豪へ2枚、貧民は富豪へ1枚渡し、同じ枚数を受け取ります（枚数は下で変えられます）。",
                "2ラウンド目以降の開始時に自動で行われます。",
                "上の階級ほど有利になり、連勝しやすくなります。オフにすると、毎ラウンド平等な勝負になります。"),
            new("交換を都落ちと連動", "都落ち後の階級で交換相手を決める", r => r.Exchange.LinkedToMiyakoOchi, (r, v) => r.Exchange.LinkedToMiyakoOchi = v,
                "都落ちが起きた場合、交換の相手を都落ち後の確定した階級で決めます。オフにすると、都落ちした人は次のラウンドの交換に参加しません。",
                "前のラウンドで都落ちが起きている。",
                "オンでは、都落ちした元大富豪も大貧民として強いカードを渡すことになります。"),
            new("貧民が選んで渡す", "最強カードの自動提出をやめ、自分で選ぶ", r => r.Exchange.PoorCanChooseCards, (r, v) => r.Exchange.PoorCanChooseCards = v,
                "貧民・大貧民が、渡すカードを自分で選べます。オフのときは、手札の最も強いカードから自動で渡されます。",
                "カード交換のとき、貧民・大貧民に選択の画面が出ます。",
                "下の階級の不利がやわらぎます。初心者が多い卓や、差を詰めたいときに。"),
            new("ジョーカー保護", "ジョーカーは渡さなくてよい", r => r.Exchange.JokerProtected, (r, v) => r.Exchange.JokerProtected = v,
                "貧民・大貧民は、交換でジョーカーを渡さなくてよくなります（自動で渡す場合もジョーカーを避けます）。",
                "カード交換のとき、貧民・大貧民の手札にジョーカーがある。",
                "下の階級にも切り札が残り、逆転の可能性が高まります。"),
            new("天変地異", "大貧民の手札が弱すぎると大富豪と全交換", r => r.Exchange.TenpenChiiEnabled, (r, v) => r.Exchange.TenpenChiiEnabled = v,
                "配られた直後、大貧民の手札がすべて基準の数字（標準は10）以下でジョーカーもない場合、大富豪と手札をすべて交換します。このとき通常のカード交換は行いません。",
                "大貧民の手札が、すべて「天変地異の基準」以下の強さで、ジョーカーもない。",
                "めったに起きませんが、起きれば立場が完全に入れ替わる救済ルールです。"),
        }, new Choice[]
        {
            new("大富豪と大貧民の交換枚数", "大貧民が渡し、大富豪が返す枚数",
                new[] { ("2枚（標準）", 2), ("1枚", 1), ("3枚", 3) },
                r => r.Exchange.DaifugoExchangeCount, (r, v) => r.Exchange.DaifugoExchangeCount = v,
                "大貧民が大富豪へ渡す枚数と、大富豪が大貧民へ返す枚数です。",
                "「カード交換」がオンで、2ラウンド目以降の開始時。",
                "枚数を増やすほど上の階級が有利になり、階級が固定されやすくなります。"),
            new("富豪と貧民の交換枚数", "貧民が渡し、富豪が返す枚数",
                new[] { ("1枚（標準）", 1), ("2枚", 2), ("交換しない", 0) },
                r => r.Exchange.FugoExchangeCount, (r, v) => r.Exchange.FugoExchangeCount = v,
                "貧民が富豪へ渡す枚数と、富豪が貧民へ返す枚数です。「交換しない」にすると、交換は大富豪と大貧民の間だけになります。",
                "「カード交換」がオンで、2ラウンド目以降の開始時。",
                "大富豪・大貧民の交換枚数より多くすると不自然になるので、同じか少なめがおすすめです。"),
            new("天変地異の基準", "大貧民の手札がこの数字以下なら発動",
                new[] { ("10以下（標準）", (int)Rank.Ten), ("9以下", (int)Rank.Nine), ("J以下", (int)Rank.Jack) },
                r => (int)r.Exchange.TenpenChiiMaxRank, (r, v) => r.Exchange.TenpenChiiMaxRank = (Rank)v,
                "天変地異が起きる条件となる、大貧民の手札の強さの上限です（3が最弱、2が最強の通常の強さで比べます）。",
                "「天変地異」がオンで、大貧民の手札がすべてこの数字以下の強さで、ジョーカーもない。",
                "J以下にすると少しだけ起きやすくなります。それでも珍しい現象なので、起きたら大逆転のチャンスです。"),
        }),
        new("進行", "対戦の進め方", new Item[]
        {
            new("ダイヤ3スタート", "♦3を持つ人が最初の親", r => r.Progress.DiamondThreeStarts, (r, v) => r.Progress.DiamondThreeStarts = v,
                "第1ラウンドは♦3を持つ人が親になり、最初の1手に♦3を含めなければなりません。オフにすると、最初の親はランダムです。",
                "第1ラウンドの開始時。",
                "♦3を含むペアや階段でも出せます。最初の1手で弱いカードをまとめて処理できると有利です。"),
            new("席替え", "ラウンドごとに席順を入れ替える", r => r.Progress.SeatShuffleEachRound, (r, v) => r.Progress.SeatShuffleEachRound = v,
                "ラウンドごとに、手番の回る順番（席順）をシャッフルします。対戦画面の左・上・右の席も新しい席順に合わせて並び替わり、手番はいつも画面上の時計回り（自分 → 左 → 上 → 右）に進みます（リバース中は逆回り）。",
                "各ラウンドの開始時。",
                "同じ人の後ろで出し続ける有利・不利が固定されなくなります。"),
            new("あがった人のカードが残る", "あがった後も、そのカードに続けて出せる", r => r.Progress.KeepFieldOnPlayerFinish, (r, v) => r.Progress.KeepFieldOnPlayerFinish = v,
                "誰かがあがったとき、その人が最後に出したカードを場に残します。次の人はそのカードより強いカードを出して続けられます。オフ（あがり流し）にすると、あがった瞬間に場が流れ、次の人が親として好きなカードを出せます。",
                "誰かが手札を出し切ってあがる。オン：場はそのまま、全員パスなら、あがった人の次の人が親。オフ：その場で場が流れ、あがった人の次の人が親。",
                "オンのときは、あがり際の強いカードに続けにくく、次の人が不利になりがちです。オフにすると、あがった人の次の席が親を取れるので、席順の有利・不利が変わります。"),
            new("パス固定", "一度パスすると場が流れるまで出せない", r => r.Progress.PassLocksUntilFlush, (r, v) => r.Progress.PassLocksUntilFlush = v,
                "一度パスすると、その場が流れるまで再び出すことはできません。オフのときは、パスの後でも手番が回ってくれば出せます。",
                "パスをする。",
                "様子見のパスができなくなり、出すかどうかの判断が重くなります。"),
            new("同点なら延長戦", "最終ラウンドで1位が同点なら延長", r => r.Progress.ExtraRoundOnTie, (r, v) => r.Progress.ExtraRoundOnTie = v,
                "最終ラウンドが終わったとき、合計点の1位が同点なら延長戦を行います（最大ラウンド数は下の「延長戦の上限」で選べます）。オフにすると、同点の人は全員が1位です。",
                "規定のラウンドを終えて、1位が同点になる。",
                "はっきり1人の勝者を決めたいときにオンにします。"),
        }, new Choice[]
        {
            new("2ラウンド目以降の親", "ラウンドの最初に出す人",
                new[]
                {
                    ("前回の大貧民（標準）", (int)RoundLeaderRule.Daihinmin),
                    ("前回の大富豪", (int)RoundLeaderRule.Daifugo),
                    ("♦3を持つ人", (int)RoundLeaderRule.DiamondThreeHolder),
                    ("ランダム", (int)RoundLeaderRule.Random),
                },
                r => (int)r.Progress.LaterRoundLeader, (r, v) => r.Progress.LaterRoundLeader = (RoundLeaderRule)v,
                "2ラウンド目以降（延長戦を含む）で、最初に出す親を誰にするかを決めます。第1ラウンドは「ダイヤ3スタート」に従います。",
                "2ラウンド目以降の開始時（カード交換の後）。「♦3を持つ人」でも、最初の1手に♦3を含める必要はありません。",
                "「前回の大貧民」は下の階級の救済になり、「前回の大富豪」は勝っている人がさらに有利になります。"),
            new("パスの回数制限", "1ラウンドでパスできる回数",
                new[] { ("無制限（標準）", 0), ("3回まで", 3), ("5回まで", 5) },
                r => r.Progress.PassLimitPerRound, (r, v) => r.Progress.PassLimitPerRound = v,
                "1ラウンドの中でパスできる回数の上限です。上限に達すると、出せるカードがある限り必ず出さなければなりません。",
                "その人がこのラウンドでパスした回数が上限に達している。出せる手が1つもないときは、上限を超えてもパスできます。",
                "様子見ができなくなり、強いカードを温存しにくくなります。テンポの速い勝負にしたいときに。"),
            new("延長戦の上限", "1位が同点のときの最大ラウンド数",
                new[] { ("3ラウンド（標準）", 3), ("1ラウンド", 1), ("5ラウンド", 5) },
                r => r.Progress.MaxExtraRounds, (r, v) => r.Progress.MaxExtraRounds = v,
                "「同点なら延長戦」がオンのとき、延長戦を最大で何ラウンドまで行うかを決めます。上限まで行っても同点なら、同点の人は全員が1位です。",
                "規定のラウンドを終えて、1位が同点になる。",
                "短く終わらせたいなら1ラウンド、決着にこだわるなら5ラウンドに。"),
            new("得点の配分", "階級ごとにもらえる点数（大富豪・富豪・貧民・大貧民）",
                PointPresets.Select((p, i) => (p.Label, i)).ToArray(),
                r => Array.FindIndex(PointPresets, p => p.Matches(r.Progress)),
                (r, v) => PointPresets[v].Apply(r.Progress),
                "ラウンドが終わるたびに、階級に応じてもらえる点数です。対戦の総合順位は、この点数の合計で決まります。",
                "各ラウンドの終了時。",
                "「差を広げる」にすると1位の価値が上がり、1回の大富豪で大きく抜け出せます。「上位だけ」は貧民以下が0点になる厳しい設定です。「2・1・-1・-2」は大富豪+2・富豪+1・貧民-1・大貧民-2（平民は0）で、負けると合計点が減ります（マイナスにもなります）。"),
        }),
    };

    /// <summary>ルール設定からカテゴリ一覧を作る（各項目は <paramref name="rules"/> を直接読み書きする）。</summary>
    public static IReadOnlyList<RuleCategoryViewModel> Build(RuleSettings rules, Action changed)
    {
        var result = new List<RuleCategoryViewModel>();
        for (int i = 0; i < Categories.Length; i++)
        {
            var c = Categories[i];
            var vm = new RuleCategoryViewModel(i + 1, c.Title, c.Summary);
            Action onChanged = () =>
            {
                vm.RefreshBadge();
                changed();
            };
            foreach (var item in c.Items)
            {
                var guide = new RuleGuide(item.Label, c.Title, item.Detail, item.Condition, item.Tip);
                vm.Items.Add(new RuleToggleViewModel(rules, item.Label, item.Hint, guide, item.Get, item.Set, onChanged));
            }
            foreach (var choice in c.Choices ?? Array.Empty<Choice>())
            {
                var guide = new RuleGuide(choice.Label, c.Title, choice.Detail, choice.Condition, choice.Tip);
                vm.Choices.Add(new RuleChoiceViewModel(rules, choice.Label, choice.Hint, guide, choice.Options, choice.Get, choice.Set, onChanged));
            }
            vm.RefreshBadge();
            result.Add(vm);
        }
        return result;
    }

    /// <summary>有効な ON/OFF 項目の数と総数（マイプリセットの一覧に出す要約用）。</summary>
    public static (int Enabled, int Total) CountEnabled(RuleSettings rules)
    {
        int enabled = 0, total = 0;
        foreach (var c in Categories)
        {
            foreach (var item in c.Items)
            {
                total++;
                if (item.Get(rules))
                {
                    enabled++;
                }
            }
        }
        return (enabled, total);
    }
}
