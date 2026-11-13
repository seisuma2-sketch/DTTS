using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DTTS.Core.Models;

namespace DTTS.ViewModels;

/// <summary>ルール設定の ON/OFF 1項目。値は <see cref="RuleSettings"/> を直接読み書きする。</summary>
public sealed partial class RuleToggleViewModel : ObservableObject
{
    private readonly RuleSettings _rules;
    private readonly Func<RuleSettings, bool> _get;
    private readonly Action<RuleSettings, bool> _set;
    private readonly Action _changed;

    public RuleToggleViewModel(RuleSettings rules, string label, string hint, Func<RuleSettings, bool> get, Action<RuleSettings, bool> set, Action changed)
    {
        _rules = rules;
        Label = label;
        Hint = hint;
        _get = get;
        _set = set;
        _changed = changed;
    }

    public string Label { get; }

    /// <summary>1行の短い説明。</summary>
    public string Hint { get; }

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

    public ObservableCollection<RuleToggleViewModel> Items { get; } = new();

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
/// 設定画面に並べるルール項目の一覧（カテゴリ・表示名・説明）。
/// ルールの値そのものはすべて <see cref="RuleSettings"/> にあり、ここは見せ方だけを決める。
/// </summary>
public static class RuleCatalog
{
    private sealed record Item(string Label, string Hint, Func<RuleSettings, bool> Get, Action<RuleSettings, bool> Set);

    private sealed record Category(string Title, string Summary, Item[] Items);

    private static readonly Category[] Categories =
    {
        new("革命系", "強さが逆転する役", new Item[]
        {
            new("革命", "同じ数字を4枚以上出すと強さが逆転", r => r.Revolution.Enabled, (r, v) => r.Revolution.Enabled = v),
            new("階段革命", "長い階段でも革命が起きる", r => r.Revolution.SequenceRevolutionEnabled, (r, v) => r.Revolution.SequenceRevolutionEnabled = v),
            new("革命返し", "革命中の革命で元に戻る", r => r.Revolution.CounterRevolutionEnabled, (r, v) => r.Revolution.CounterRevolutionEnabled = v),
            new("ジョーカーを含む革命", "ジョーカーを枚数に数える", r => r.Revolution.JokerCountsForRevolution, (r, v) => r.Revolution.JokerCountsForRevolution = v),
            new("革命の選択", "起こすかどうかを選べる", r => r.Revolution.RevolutionIsOptional, (r, v) => r.Revolution.RevolutionIsOptional = v),
            new("クーデター", "9を3枚で革命", r => r.Revolution.CoupEnabled, (r, v) => r.Revolution.CoupEnabled = v),
            new("オーメン", "6を3枚で革命", r => r.Revolution.OmenEnabled, (r, v) => r.Revolution.OmenEnabled = v),
            new("ナナサン革命", "7を3枚で革命", r => r.Revolution.NanasanEnabled, (r, v) => r.Revolution.NanasanEnabled = v),
        }),
        new("階段系", "同じスートの連番", new Item[]
        {
            new("階段", "同スート3枚以上の連番を出せる", r => r.Sequence.Enabled, (r, v) => r.Sequence.Enabled = v),
            new("階段にジョーカー", "欠けた数字をジョーカーで補える", r => r.Sequence.AllowJokerInSequence, (r, v) => r.Sequence.AllowJokerInSequence = v),
            new("階段に2を含める", "K・A・2 のつながりを認める", r => r.Sequence.AllowTwoInSequence, (r, v) => r.Sequence.AllowTwoInSequence = v),
            new("階段中の効果", "階段に含まれるカードの効果も発動", r => r.Sequence.EffectsInSequence, (r, v) => r.Sequence.EffectsInSequence = v),
            new("エンペラー", "4スート別の4枚連番が最強", r => r.Sequence.EmperorEnabled, (r, v) => r.Sequence.EmperorEnabled = v),
            new("エンペラーで革命", "エンペラーで革命も起きる", r => r.Sequence.EmperorCausesRevolution, (r, v) => r.Sequence.EmperorCausesRevolution = v),
        }),
        new("縛り系", "出せるカードの制限", new Item[]
        {
            new("スート縛り", "同じスートが続くとそのスートのみ", r => r.Lock.SuitLockEnabled, (r, v) => r.Lock.SuitLockEnabled = v),
            new("数縛り", "連番が続くと次の数字のみ", r => r.Lock.NumberLockEnabled, (r, v) => r.Lock.NumberLockEnabled = v),
            new("両しば", "一部のスートが一致した分だけ縛る", r => r.Lock.PartialSuitLockEnabled, (r, v) => r.Lock.PartialSuitLockEnabled = v),
            new("激しば", "スート縛りと数縛りが同時に成立", r => r.Lock.StrictLockEnabled, (r, v) => r.Lock.StrictLockEnabled = v),
        }),
        new("流し・切り系", "場を流す役", new Item[]
        {
            new("8切り", "8を出すと場が流れる", r => r.Flow.EightCutEnabled, (r, v) => r.Flow.EightCutEnabled = v),
            new("4止め", "8切りを4で止められる", r => r.Flow.FourStopEnabled, (r, v) => r.Flow.FourStopEnabled = v),
            new("砂嵐", "3を3枚で場が流れる", r => r.Flow.SandstormEnabled, (r, v) => r.Flow.SandstormEnabled = v),
            new("99車", "9を2枚で場が流れる", r => r.Flow.AmbulanceEnabled, (r, v) => r.Flow.AmbulanceEnabled = v),
            new("ろくろ首", "6を2枚で場が流れる", r => r.Flow.RokurokubiEnabled, (r, v) => r.Flow.RokurokubiEnabled = v),
        }),
        new("11バック系", "一時的な強さの逆転", new Item[]
        {
            new("11バック", "Jを出すと場が流れるまで逆転", r => r.ElevenBack.Enabled, (r, v) => r.ElevenBack.Enabled = v),
            new("強化11バック", "場が流れても逆転が続く", r => r.ElevenBack.StrongElevenBackEnabled, (r, v) => r.ElevenBack.StrongElevenBackEnabled = v),
        }),
        new("スキップ・リバース系", "手番の順番を変える", new Item[]
        {
            new("5スキップ", "出した枚数だけ次の人を飛ばす", r => r.SkipReverse.FiveSkipEnabled, (r, v) => r.SkipReverse.FiveSkipEnabled = v),
            new("13スキップ", "Kで次の人を飛ばす", r => r.SkipReverse.ThirteenSkipEnabled, (r, v) => r.SkipReverse.ThirteenSkipEnabled = v),
            new("9リバース", "9で回る向きが逆になる", r => r.SkipReverse.NineReverseEnabled, (r, v) => r.SkipReverse.NineReverseEnabled = v),
            new("12リバース", "Qで回る向きが逆になる", r => r.SkipReverse.TwelveReverseEnabled, (r, v) => r.SkipReverse.TwelveReverseEnabled = v),
        }),
        new("特殊効果系", "カードを渡す・捨てる", new Item[]
        {
            new("7渡し", "出した枚数だけ次の人へ渡す", r => r.Special.SevenPassEnabled, (r, v) => r.Special.SevenPassEnabled = v),
            new("10捨て", "出した枚数まで好きに捨てる", r => r.Special.TenDiscardEnabled, (r, v) => r.Special.TenDiscardEnabled = v),
            new("12ボンバー", "宣言した数字を全員が捨てる", r => r.Special.TwelveBomberEnabled, (r, v) => r.Special.TwelveBomberEnabled = v),
            new("ダウンナンバー", "同じスートでちょうど1つ下の数字を出せる", r => r.Special.DownNumberEnabled, (r, v) => r.Special.DownNumberEnabled = v),
        }),
        new("ジョーカー", "ジョーカーまわり", new Item[]
        {
            new("スペ3返し", "単体ジョーカーに♠3で勝つ", r => r.Joker.SpadeThreeReturnEnabled, (r, v) => r.Joker.SpadeThreeReturnEnabled = v),
            new("ジョーカーで効果発動", "代わりにした数字の効果も起きる", r => r.Joker.JokerTriggersEffects, (r, v) => r.Joker.JokerTriggersEffects = v),
        }),
        new("階級変動", "ラウンドをまたぐ逆転", new Item[]
        {
            new("都落ち", "大富豪が1位を逃すと大貧民", r => r.RankChange.MiyakoOchiEnabled, (r, v) => r.RankChange.MiyakoOchiEnabled = v),
            new("下剋上", "大貧民が1位なら階級が反転", r => r.RankChange.GekokujoEnabled, (r, v) => r.RankChange.GekokujoEnabled = v),
        }),
        new("禁止あがり", "最後の1手の制限", new Item[]
        {
            new("禁止あがり", "以下の出し方ではあがれない", r => r.ForbiddenFinish.Enabled, (r, v) => r.ForbiddenFinish.Enabled = v),
            new("2あがり禁止", "最強の数字であがれない", r => r.ForbiddenFinish.ForbidStrongestNumber, (r, v) => r.ForbiddenFinish.ForbidStrongestNumber = v),
            new("最強数字は逆転に追従", "革命・11バック中は3あがりを禁止", r => r.ForbiddenFinish.StrongestFollowsReversal, (r, v) => r.ForbiddenFinish.StrongestFollowsReversal = v),
            new("8切りあがり禁止", "8切りであがれない", r => r.ForbiddenFinish.ForbidEightCut, (r, v) => r.ForbiddenFinish.ForbidEightCut = v),
            new("ジョーカーあがり禁止", "ジョーカーであがれない", r => r.ForbiddenFinish.ForbidJoker, (r, v) => r.ForbiddenFinish.ForbidJoker = v),
            new("スペ3あがり禁止", "スペ3返しであがれない", r => r.ForbiddenFinish.ForbidSpadeThree, (r, v) => r.ForbiddenFinish.ForbidSpadeThree = v),
            new("12ボンバーあがり禁止", "12ボンバーであがれない", r => r.ForbiddenFinish.ForbidTwelveBomber, (r, v) => r.ForbiddenFinish.ForbidTwelveBomber = v),
            new("11バックあがり禁止", "11バックであがれない", r => r.ForbiddenFinish.ForbidElevenBack, (r, v) => r.ForbiddenFinish.ForbidElevenBack = v),
            new("流し役あがり禁止", "砂嵐・99車などであがれない", r => r.ForbiddenFinish.ForbidOtherFlushEffects, (r, v) => r.ForbiddenFinish.ForbidOtherFlushEffects = v),
            new("革命あがり禁止", "革命を起こしてあがれない", r => r.ForbiddenFinish.ForbidRevolution, (r, v) => r.ForbiddenFinish.ForbidRevolution = v),
        }),
        new("カード交換", "ラウンド開始時の交換", new Item[]
        {
            new("カード交換", "階級に応じてカードを交換", r => r.Exchange.Enabled, (r, v) => r.Exchange.Enabled = v),
            new("交換を都落ちと連動", "都落ち後の階級で交換相手を決める", r => r.Exchange.LinkedToMiyakoOchi, (r, v) => r.Exchange.LinkedToMiyakoOchi = v),
            new("貧民が選んで渡す", "最強カードの自動提出をやめ、自分で選ぶ", r => r.Exchange.PoorCanChooseCards, (r, v) => r.Exchange.PoorCanChooseCards = v),
            new("ジョーカー保護", "ジョーカーは渡さなくてよい", r => r.Exchange.JokerProtected, (r, v) => r.Exchange.JokerProtected = v),
            new("天変地異", "大貧民の手札が弱すぎると大富豪と全交換", r => r.Exchange.TenpenChiiEnabled, (r, v) => r.Exchange.TenpenChiiEnabled = v),
        }),
        new("進行", "対戦の進め方", new Item[]
        {
            new("ダイヤ3スタート", "♦3を持つ人が最初の親", r => r.Progress.DiamondThreeStarts, (r, v) => r.Progress.DiamondThreeStarts = v),
            new("席替え", "ラウンドごとに席順を入れ替える", r => r.Progress.SeatShuffleEachRound, (r, v) => r.Progress.SeatShuffleEachRound = v),
            new("パス固定", "一度パスすると場が流れるまで出せない", r => r.Progress.PassLocksUntilFlush, (r, v) => r.Progress.PassLocksUntilFlush = v),
            new("同点なら延長戦", "最終ラウンドで1位が同点なら延長", r => r.Progress.ExtraRoundOnTie, (r, v) => r.Progress.ExtraRoundOnTie = v),
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
            foreach (var item in c.Items)
            {
                vm.Items.Add(new RuleToggleViewModel(rules, item.Label, item.Hint, item.Get, item.Set, () =>
                {
                    vm.RefreshBadge();
                    changed();
                }));
            }
            vm.RefreshBadge();
            result.Add(vm);
        }
        return result;
    }
}
