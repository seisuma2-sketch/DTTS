using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;

namespace DTTS.ViewModels;

public sealed record RuleHelpCategory(string Name, IReadOnlyList<RuleHelpItem> Items);

public sealed record RuleHelpItem(string Title, string Summary, string Detail);

public sealed partial class HelpViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "";

    public ObservableCollection<RuleHelpCategory> FilteredCategories { get; } = new();

    private readonly List<RuleHelpCategory> _allCategories = BuildCategories();

    public HelpViewModel(INavigationService navigation)
    {
        _navigation = navigation;
        ApplyFilter();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredCategories.Clear();
        string q = SearchQuery.Trim().ToLowerInvariant();

        foreach (var cat in _allCategories)
        {
            var matchedItems = string.IsNullOrEmpty(q)
                ? cat.Items
                : cat.Items.Where(i => i.Title.ToLowerInvariant().Contains(q) ||
                                       i.Summary.ToLowerInvariant().Contains(q) ||
                                       i.Detail.ToLowerInvariant().Contains(q)).ToList();

            if (matchedItems.Count > 0)
            {
                FilteredCategories.Add(new RuleHelpCategory(cat.Name, matchedItems));
            }
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.NavigateToTitle();
    }

    private static List<RuleHelpCategory> BuildCategories() => new()
    {
        new("1. 革命系", new RuleHelpItem[]
        {
            new("革命 (4枚以上)", "同数字4枚以上で強さが逆転する", "通常時 3<...<2 の強さが、2<...<3 に反転します。場が流れてもゲーム終了まで継続します。"),
            new("階段革命", "階段で4枚以上出した場合に革命が起きる", "同じスートの4枚以上の連番を出した瞬間に強さの逆転が発生します。"),
            new("革命返し", "革命中に再度革命を起こすと元に戻る", "反転している強さが即座に通常時（3<...<2）へと復帰します。"),
            new("ジョーカー含み革命", "革命にJokerを含めることができる", "自然札＋Jokerの組み合わせでも4枚以上であれば革命が成立します。"),
            new("革命選択", "条件を満たしても革命を起こすか選択できる", "4枚以上を出した際に、プレイヤーが「革命を起こす / 起こさない」を選択できます。"),
            new("クーデター (9×3枚)", "9を3枚出すと革命が起きる", "革命条件を満たしていなくても、9を3枚同時に出すことで即座に革命が発生します。"),
            new("オーメン (6×3枚)", "6を3枚出すと革命が起きる", "6のカードを3枚同時に出すことで強さの逆転が発生します。"),
            new("ナナサン革命 (7×3枚)", "7を3枚出すと革命が起きる", "7のカードを3枚同時に出すことで強さの逆転が発生します。"),
        }),
        new("2. 階段系", new RuleHelpItem[]
        {
            new("階段 (シーケンス)", "同スートの連続した数字を3枚以上同時に出せる", "例: ♠4・♠5・♠6 のように同じスートの連番をまとめて出せます。"),
            new("階段中のカード効果", "階段に含まれる特殊カードの効果が発動する", "階段に8が含まれていれば8切り、7が含まれていれば7渡しなどが同時に発生します。"),
            new("エンペラー", "4つのスートを各1枚使った4枚連番の最強役", "♠3・♥4・♦5・♣6 のように4スートを1枚ずつ揃えた連番。4枚の階段に必ず勝ちます。「エンペラーで場流し」をオンにすると場も流れます（砂嵐で止められることがあります）。"),
        }),
        new("3. 縛り系", new RuleHelpItem[]
        {
            new("スート縛り", "同じスートが連続して出されると、以降そのスートしか出せなくなる", "前の手と同じスート構成でカードが出されたとき、場が流れるまでそのスートに限定されます。"),
            new("数縛り (連番数字)", "連続した数字が続くと以降も連番でしか出せなくなる", "4の次に5が出された場合、次は6しか出せなくなります。"),
            new("両しば", "スート縛りと数縛りの両方の条件が同時に適用される", "スートの一致と数字の連番が同時に満たされた強力な縛り状態です。"),
            new("激しば", "スート縛りの成立と同時に、以降は数字も1つ上の連番に限定される", "非常に強力な縛りで、出せる手が極端に狭まります。"),
        }),
        new("4. 流し・切り系", new RuleHelpItem[]
        {
            new("8切り", "8を含むカードを出すと、即座に場が流れて自分の親番になる", "単体・ペア・階段を問わず、8を出すと場が強制的に流れます。"),
            new("4止め", "8切りの直後、4を出して割り込むと8切りを止められる（標準は8の2倍の枚数：8が1枚なら4を2枚）", "止めると場は流れず、4が場に残って次の人へ続きます。設定で「8と同じ枚数」にもできます。階段の8切りは4では止められません（砂嵐なら止められます）。"),
            new("砂嵐 (3×3枚)", "3を3枚出すと、場のカードに関わらず即座に流れる", "同じ枚数の組に対して絶対的な強さを持ち、即座に親番を獲得します（砂嵐は誰にも止められません）。8切り・ろくろ首・99車・エンペラーの直後は、砂嵐を持っている人に「砂嵐で止める／パス（流す）」の確認が出ます（操作ガイドが「初心者用」なら持っていなくても出ます）。"),
            new("99車 (9×2枚)", "9を2枚出すと、即座に場が流れる", "ペアの役に対して即時流しを発生させます。"),
            new("ろくろ首 (6×2枚)", "6を2枚出して場に勝つと、場が流れる", "強さで場のカードに勝っている場合、そのまま場を流して親番となります。"),
        }),
        new("5. 11バック系", new RuleHelpItem[]
        {
            new("11バック (Jack Back)", "11(J)を出すと、一時的に強さが逆転する", "その場が流れるまでの一時的な革命状態となり、3が最強、2が最弱になります。"),
            new("強化11バック", "場が流れても次の11が出るまで反転が継続する", "通常の一時的反転と異なり、次の11が出るまで反転が持続します。"),
        }),
        new("6. スキップ・リバース系", new RuleHelpItem[]
        {
            new("5スキップ", "5を出すと、出した枚数分だけ次のプレイヤーを飛ばす", "1枚なら1人、2枚なら2人がスキップされます。残っている全員を飛ばすと場が流れ、5を出した人が親になります。"),
            new("13スキップ", "13(K)を出すと、出した枚数分だけ次のプレイヤーを飛ばす", "キングによるスキップ効果です。"),
            new("9リバース", "9を出すと、手番の回り順が逆転する", "画面上の時計回り（自分 → 左 → 上 → 右）と反時計回りが入れ替わります。2枚出せば元に戻ります。"),
            new("12リバース", "12(Q)を出すと、手番の回り順が逆転する", "クイーンによるリバース効果です。"),
        }),
        new("7. 特殊効果系", new RuleHelpItem[]
        {
            new("7渡し", "7を出したプレイヤーは、手札を次のプレイヤーに渡す", "出した枚数分だけ、不要なカードを次のプレイヤーへ押し付けます。"),
            new("10捨て", "10を出したプレイヤーは、手札を任意に捨てられる", "出した枚数分だけ、自分の手札を自由に捨てて減らすことができます。"),
            new("12ボンバー", "12(Q)を出した際、数字を宣言して全員がその数字を捨てる", "宣言された数字のカードを全プレイヤーが手札から強制破棄します。"),
            new("ダウンナンバー", "場のカードと同じスート構成で、ちょうど1つ下の数字を出せる", "通常は強いカードしか出せませんが、1つ弱いカードを出して受け流せます。"),
        }),
        new("8. ジョーカー・切り返し", new RuleHelpItem[]
        {
            new("Joker (切り札)", "どんなカードの代用にもなり、単体では最強のカード", "反転時も含めて常に最強の強さを持ちます。"),
            new("スペ3返し", "Joker単体が出された際、スペードの3で即座に流せる", "最強のJoker単体を唯一撃破できるカウンター役です。"),
        }),
        new("9. 階級変動", new RuleHelpItem[]
        {
            new("都落ち", "前ラウンドの大富豪が1位になれなかった場合、即座に大貧民へ転落", "大富豪以外の誰かが最初にあがった瞬間に確定します。"),
            new("下剋上", "前ラウンドの大貧民が1位であがると、階級が上下反転する", "大富豪と大貧民、富豪と貧民の階級が逆転します。"),
        }),
        new("10. 禁止あがり", new RuleHelpItem[]
        {
            new("禁止あがり (反則負け)", "特定の強力なカードを手札の最後に出してあがると反則", "2、8、Joker、スペ3、12ボンバーなどで最後にあがると、最下位（大貧民）になります。"),
        }),
        new("11. カード交換", new RuleHelpItem[]
        {
            new("階級カード交換", "ラウンド開始時、上位と下位でカードを強制交換する", "大富豪は大貧民から最強の2枚を受け取り不要な2枚を渡す。富豪は貧民と1枚交換。"),
            new("天変地異", "大貧民の手札が10以下のみの場合、大富豪と全手札を交換する", "下位プレイヤーへの救済ルールです。"),
        }),
        new("12. 進行・配牌", new RuleHelpItem[]
        {
            new("初手ルール", "第1ラウンドはダイヤの3を持つプレイヤーからスタート", "親番の決定ルールです。"),
            new("パス制限", "1ラウンド中にパスできる回数を制限する", "安易なパスを防ぎ、スピーディな展開を促します。"),
        }),
    };
}

