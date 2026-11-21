using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services;
using DTTS.Services.Audio;
using DTTS.Services.Network;
using DTTS.Services.Presentation;
using DTTS.Services.Settings;
using Microsoft.UI.Dispatching;

namespace DTTS.ViewModels;

/// <summary>
/// 対戦画面の「手触り」まわり：まとめて選ぶ候補・出せる組の発光・カード交換の結果表示・
/// NPC のアバターと吹き出し・BGM の切り替え・場へカードが飛ぶ演出の出どころ・検証用の自動プレイ。
/// どれもゲームの進行（エンジン）が先に決まり、画面はそれを追いかけて見せるだけ。
/// </summary>
public sealed partial class GameTableViewModel
{
    // ------------------------------------------------------------------
    // 場へカードが飛ぶ演出：誰が出したカードか（表示側が出どころの席を求める）
    // ------------------------------------------------------------------

    /// <summary>今の場札を出したプレイヤー（場が空なら null）。<see cref="FieldCards"/> を入れ替える直前に更新する。</summary>
    public int? FieldPlayedBy { get; private set; }

    /// <summary>
    /// カードが出された（出したプレイヤーの席から場へ飛ばす演出の合図）。場札を入れ替える前に発行する。
    /// 8切り・砂嵐のようにすぐ場が流れる出し札でも、出したカードが卓上を飛んで見えるよう、場札の一覧ではなくこの合図で演出する。
    /// </summary>
    public event EventHandler<CardsFlightEventArgs>? CardsFlightTriggered;

    /// <summary>カードが出されたことを表示側へ知らせる（スキップ中は演出しない）。</summary>
    private void RaiseCardsFlight(int playerId, IReadOnlyList<Card> cards)
    {
        if (_isFastForwarding || _isShutDown || cards.Count == 0) return;
        CardsFlightTriggered?.Invoke(this, new CardsFlightEventArgs(playerId, cards));
    }

    /// <summary>「残りをスキップ」中か（表示側は演出を省く）。</summary>
    public bool IsFastForwarding => _isFastForwarding;

    /// <summary>プレイヤーが画面のどの席（下・左・上・右）にいるか。</summary>
    public int SlotOfPlayer(int playerId) => SeatLayout.SlotOf(_seatOrder, _myPlayerId, playerId);

    // ------------------------------------------------------------------
    // まとめて選ぶ候補（ペア・3枚・階段）と、出せる組になったときの発光
    // ------------------------------------------------------------------

    /// <summary>今の場に出せる2枚以上の組の候補（自分の手番だけ）。</summary>
    public ObservableCollection<PlaySuggestion> PlaySuggestionChips { get; } = new();

    /// <summary>候補を出しているか。</summary>
    [ObservableProperty]
    public partial bool HasPlaySuggestions { get; set; }

    /// <summary>
    /// 手番の案内（「あなたのターンです」など）。操作ガイドが「上級者用（ガイドなし）」なら出さない
    /// （割り込みの確認・10捨てなどの専用の案内は、この関数を通さずにそのまま出す）。
    /// </summary>
    private string TurnGuidance(string text) => _guide.Level == DTTS.Services.Settings.GuideLevel.Expert ? "" : text;

    /// <summary>候補を作り直す（自分の「出すかパスか」の番でなければ null を渡して消す）。上級者用では出さない。</summary>
    private void RefreshSuggestions(IEnumerable<IReadOnlyList<Card>>? legalPlays)
    {
        if (_guide.Level == DTTS.Services.Settings.GuideLevel.Expert)
        {
            legalPlays = null; // 上級者用（ガイドなし）：まとめて選ぶ候補も出さない
        }
        var next = legalPlays is null ? new List<PlaySuggestion>() : PlaySuggestions.Build(legalPlays).ToList();
        // 同じ候補なら作り直さない（カードを選ぶたびに呼ばれるため、ちらつかせない）
        if (next.Select(s => s.Label).SequenceEqual(PlaySuggestionChips.Select(s => s.Label)))
        {
            HasPlaySuggestions = PlaySuggestionChips.Count > 0;
            return;
        }
        PlaySuggestionChips.Clear();
        foreach (var s in next)
        {
            PlaySuggestionChips.Add(s);
        }
        HasPlaySuggestions = PlaySuggestionChips.Count > 0;
    }

    /// <summary>候補のカードをまとめて選ぶ（ほかの選択は外す）。</summary>
    [RelayCommand]
    private void ApplySuggestion(PlaySuggestion? suggestion)
    {
        if (suggestion is null || IsCardSelectionMode) return;
        foreach (var vm in HumanHand)
        {
            vm.IsSelected = suggestion.Cards.Contains(vm.Card);
        }
        _audio.Play(SoundEffect.CardSelect);
        RefreshSelectionState();
    }

    /// <summary>
    /// 同じ数字のカードをまとめて選ぶ（右クリック・長押し）。すでに全部選んでいれば外す。
    /// 出す操作では、まとめた組だけを選んだ状態にする（カード選択中は今の選択に足す）。
    /// </summary>
    [RelayCommand]
    private void SelectSameRank(CardViewModel? cardVm)
    {
        if (cardVm is null) return;
        var group = PlaySuggestions.SameRank(HumanHand.Select(v => v.Card), cardVm.Card);
        bool allSelected = HumanHand.Where(v => group.Contains(v.Card)).All(v => v.IsSelected);
        foreach (var vm in HumanHand)
        {
            if (group.Contains(vm.Card))
            {
                vm.IsSelected = !allSelected;
            }
            else if (!IsCardSelectionMode && !allSelected)
            {
                vm.IsSelected = false;
            }
        }
        _audio.Play(SoundEffect.CardSelect);
        RefreshSelectionState();
    }

    /// <summary>選択が変わったあとの再計算（「出す」の可否・発光・選択枚数）。</summary>
    private void RefreshSelectionState()
    {
        if (_client != null)
        {
            UpdateGuestCanPlay();
        }
        else
        {
            UpdateHumanHandVisuals();
        }
        UpdateSelectionCount();
    }

    /// <summary>選んでいるカードがそのまま出せる組なら、選択中のカードを光らせる。</summary>
    private void RefreshLegalSelectionGlow()
    {
        bool legal = CanPlay && !IsCardSelectionMode;
        foreach (var vm in HumanHand)
        {
            vm.IsLegalSelection = legal && vm.IsSelected;
        }
    }

    // ------------------------------------------------------------------
    // カード交換の結果（何を渡して何を受け取ったか）
    // ------------------------------------------------------------------

    private readonly ExchangeSummary _exchange = new();
    private bool _exchangeShown;
    private DispatcherQueueTimer? _exchangeTimer;

    /// <summary>交換の結果を見せる時間（設定画面で変更可能、既定 6 秒）。</summary>
    private TimeSpan _exchangeSummaryDuration = TimeSpan.FromSeconds(6);

    /// <summary>交換の結果を表示しているか。</summary>
    [ObservableProperty]
    public partial bool IsExchangeSummaryOpen { get; set; }

    /// <summary>渡したカード（小さなカードで見せる）。</summary>
    public ObservableCollection<Card> ExchangeGivenCards { get; } = new();

    /// <summary>受け取ったカード。</summary>
    public ObservableCollection<Card> ExchangeReceivedCards { get; } = new();

    /// <summary>渡した相手の説明（例：「大貧民 ミオ へ渡した」）。</summary>
    [ObservableProperty]
    public partial string ExchangeGivenLabel { get; set; } = "";

    /// <summary>受け取った相手の説明。</summary>
    [ObservableProperty]
    public partial string ExchangeReceivedLabel { get; set; } = "";

    /// <summary>交換の結果の文（読み上げ・状況表示用）。</summary>
    [ObservableProperty]
    public partial string ExchangeSummaryText { get; set; } = "";

    /// <summary>渡したカードがあるか。</summary>
    [ObservableProperty]
    public partial bool HasExchangeGiven { get; set; }

    /// <summary>受け取ったカードがあるか。</summary>
    [ObservableProperty]
    public partial bool HasExchangeReceived { get; set; }

    /// <summary>新しいラウンド：交換の記録を消す。</summary>
    private void ResetExchangeSummary()
    {
        _exchange.Clear();
        _exchangeShown = false;
        DismissExchangeSummary();
    }

    /// <summary>カードの移動を受け取る（交換だけを記録する）。</summary>
    private void OnCardsTransferred(int fromId, int toId, IReadOnlyList<Card>? cards, CardTransferReason? reason)
    {
        if (reason == CardTransferReason.Exchange && _exchange.Record(_myPlayerId, fromId, toId, cards) && IsExchangeSummaryOpen)
        {
            // ゲスト：交換の通知が対戦開始の画面より後に届いた場合も、表示中の結果に足す
            PopulateExchangeSummary();
        }
    }

    /// <summary>交換の結果を見せる間、NPC がカードを出さないよう一時停止／再開を切り替える。</summary>
    private void SetExchangeNpcPaused(bool paused)
    {
        _npcController?.SetPaused(paused);
        _hostSession?.SetNpcPaused(paused);
    }

    /// <summary>交換が終わって対戦が始まったら（最初の「出すかパスか」の判断で）、設定された秒数だけ結果を見せる。</summary>
    private void MaybeShowExchangeSummary(DecisionKind? kind)
    {
        if (_exchangeShown || !_exchange.HasAny || kind != DecisionKind.PlayOrPass || _isFastForwarding || _exchangeSummaryDuration <= TimeSpan.Zero)
        {
            return;
        }
        _exchangeShown = true;
        PopulateExchangeSummary();
        IsExchangeSummaryOpen = true;
        // 交換結果を表示している間は NPC が先出ししないよう待機させる
        SetExchangeNpcPaused(true);

        _exchangeTimer ??= CreateOneShot(_exchangeSummaryDuration, DismissExchangeSummary);
        _exchangeTimer.Interval = _exchangeSummaryDuration;
        _exchangeTimer.Stop();
        _exchangeTimer.Start();
    }

    /// <summary>交換の記録から、表示する内容（カード・相手・文）を作る。</summary>
    private void PopulateExchangeSummary()
    {
        string Partner(int id) => $"{RankTitle(GetPlayerVm(id).Rank)} {GetPlayerVm(id).Name}";

        ExchangeGivenCards.Clear();
        foreach (var c in _exchange.Given.SelectMany(g => g.Cards)) ExchangeGivenCards.Add(c);
        ExchangeReceivedCards.Clear();
        foreach (var c in _exchange.Received.SelectMany(g => g.Cards)) ExchangeReceivedCards.Add(c);
        ExchangeGivenLabel = string.Join("・", _exchange.Given.Select(g => Partner(g.Partner))) + " へ渡した";
        ExchangeReceivedLabel = string.Join("・", _exchange.Received.Select(g => Partner(g.Partner))) + " から受け取った";
        HasExchangeGiven = ExchangeGivenCards.Count > 0;
        HasExchangeReceived = ExchangeReceivedCards.Count > 0;
        ExchangeSummaryText = "カード交換：" + _exchange.Describe(Partner);
    }

    /// <summary>交換の結果を閉じる（時間切れ・クリック）。閉じたタイミングで NPC の待機を解除する。</summary>
    [RelayCommand]
    private void DismissExchangeSummary()
    {
        _exchangeTimer?.Stop();
        IsExchangeSummaryOpen = false;
        SetExchangeNpcPaused(false);
    }

    private DispatcherQueueTimer CreateOneShot(TimeSpan interval, Action action)
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_isShutDown) action();
        };
        return timer;
    }

    // ------------------------------------------------------------------
    // NPC のアバターと、ひとことの吹き出し
    // ------------------------------------------------------------------

    /// <summary>吹き出しを出す（表示側が、そのプレイヤーの席のパネルに出して自動で消す）。</summary>
    public event EventHandler<ReactionEventArgs>? ReactionTriggered;

    private readonly Dictionary<int, int> _reactionCount = new();

    /// <summary>そのプレイヤーの性格（自分の席・人が操作している席・分からないときは null）。</summary>
    private NpcPersonality? PersonalityOf(int playerId)
    {
        if (playerId == _myPlayerId) return null;
        if (_client is not null) return null; // ゲストには相手の性格が届かない
        var kind = _engine?.GetPlayer(playerId).Kind;
        if (kind != PlayerKind.Npc) return null;
        if (_hostSession is not null) return _hostSession.NpcProfileOf(playerId).Personality;
        return _npcProfiles.TryGetValue(playerId, out var profile) ? profile.Personality : null;
    }

    /// <summary>アバター（性格またはプレイヤー設定のアイコン）を全員分そろえる。</summary>
    private void UpdateAvatars()
    {
        for (int id = 0; id < 4; id++)
        {
            var vm = GetPlayerVm(id);
            if (id == _myPlayerId)
            {
                var info = PlayerIconInfo.Get(_myIconId);
                vm.AvatarGlyph = info.Glyph;
                vm.AvatarAccentHex = info.AccentHex;
                vm.AvatarImagePath = _myCustomAvatarPath;
                vm.AvatarLabel = vm.Name;
                continue;
            }
            var personality = PersonalityOf(id);
            vm.AvatarGlyph = NpcReactions.AvatarGlyph(personality);
            vm.AvatarAccentHex = null;
            vm.AvatarImagePath = null;
            bool isNpc = _client is null && _engine is { } e && e.GetPlayer(id).Kind == PlayerKind.Npc;
            vm.AvatarLabel = isNpc ? $"NPC（{NpcReactions.PersonalityName(personality)}）" : "プレイヤー";
        }
    }

    /// <summary>吹き出しを出す（自分の席・スキップ中は出さない）。</summary>
    private void React(int playerId, ReactionKind kind)
    {
        if (_isFastForwarding || _isShutDown || playerId == _myPlayerId || playerId < 0 || playerId > 3) return;
        int n = _reactionCount.TryGetValue(playerId, out var c) ? c : 0;
        _reactionCount[playerId] = n + 1;
        string text = NpcReactions.For(PersonalityOf(playerId), kind, n + playerId);
        if (text.Length > 0)
        {
            ReactionTriggered?.Invoke(this, new ReactionEventArgs(playerId, text));
        }
    }

    /// <summary>特殊効果に応じた吹き出し。</summary>
    private void ReactToEffect(int playerId, SpecialEffectType effect)
    {
        ReactionKind? kind = effect switch
        {
            _ when effect.IsRevolutionTrigger() => ReactionKind.Revolution,
            SpecialEffectType.EightCut => ReactionKind.EightCut,
            SpecialEffectType.FourStop => ReactionKind.FourStop,
            SpecialEffectType.Sandstorm => ReactionKind.Sandstorm,
            SpecialEffectType.Ambulance or SpecialEffectType.Rokurokubi or SpecialEffectType.Emperor
                or SpecialEffectType.SpadeThreeReturn => ReactionKind.OtherFlush,
            _ => null,
        };
        if (kind is { } k) React(playerId, k);
    }

    /// <summary>あがり・脱落の吹き出し。</summary>
    private void ReactToFinish(int playerId, FinishKind kind, int place)
    {
        switch (kind)
        {
            case FinishKind.Normal:
                React(playerId, place == 1 ? ReactionKind.FinishFirst : ReactionKind.Finish);
                break;
            case FinishKind.Foul:
            case FinishKind.MiyakoOchi:
                React(playerId, ReactionKind.Fall);
                break;
        }
    }

    // ------------------------------------------------------------------
    // BGM（通常はラウンジ、革命中・あがりが迫ったら緊張の曲）
    // ------------------------------------------------------------------

    private void UpdateBgm()
    {
        if (_isShutDown) return;
        if (IsRoundResultOpen)
        {
            _audio.PlayBgm(BgmTrack.Lounge);
            return;
        }
        int mine = Player0.FinishPlace.HasValue ? 0 : Player0.HandCount;
        var others = new[] { Player1, Player2, Player3 }
            .Where(p => !p.FinishPlace.HasValue && p.HandCount > 0)
            .Select(p => p.HandCount)
            .ToList();
        _audio.PlayBgm(BgmPolicy.ForTable(IsRevolution, mine, others.Count > 0 ? others.Min() : null));
    }

    // ------------------------------------------------------------------
    // 検証用の自動プレイ（--autoplay）：自分の席も NPC に打たせ、ラウンド結果も自動で次へ進める
    // ------------------------------------------------------------------

    private readonly bool _autoplay = AppLaunchOptions.IsAutoplayForCurrentProcess();
    private DispatcherQueueTimer? _autoAdvanceTimer;
    private DispatcherQueueTimer? _guestAutoTimer;
    private int _guestAutoBusy;

    /// <summary>自動プレイ中か（画面の右上に小さく表示する）。</summary>
    public bool IsAutoplay => _autoplay;

    /// <summary>1人用・ホスト：自分の席を NPC に任せる。</summary>
    private void SetUpAutoplay(NpcController? controller)
    {
        if (!_autoplay || _engine is null || controller is null) return;
        controller.Assign(_myPlayerId, NpcProfile.Default);
        // 席を NPC に切り替えると、エンジンが今の判断要求を出し直すので、すでに判断待ちでも NPC が引き継ぐ
        _engine.SetPlayerKind(_myPlayerId, PlayerKind.Npc);
    }

    /// <summary>ラウンド結果を少し見せてから、次のラウンドへ自動で進める（ゲーム終了時は止まる）。</summary>
    private void ScheduleAutoAdvance()
    {
        if (!_autoplay || IsGuest) return;
        _autoAdvanceTimer ??= CreateOneShot(TimeSpan.FromSeconds(2.5), () =>
        {
            if (IsRoundResultOpen && !IsGameOver)
            {
                StartNextRoundCommand.Execute(null);
            }
        });
        _autoAdvanceTimer.Stop();
        _autoAdvanceTimer.Start();
    }

    /// <summary>
    /// ゲスト：自分の判断が来たら、少し待ってから簡単な手で答える（出せる最初の組・パス・必要な枚数のカード）。
    /// ホストの NPC と同じく、判断が届いたときだけ動く（画面を監視し続けない）。
    /// </summary>
    private void MaybeGuestAutoplay(ClientView view)
    {
        if (!_autoplay || _client is null || view.Decision is not { } d || d.PlayerId != _myPlayerId) return;
        _guestAutoTimer ??= CreateOneShot(TimeSpan.FromMilliseconds(450), () => _ = GuestAutoActAsync());
        _guestAutoTimer.Stop();
        _guestAutoTimer.Start();
    }

    private async System.Threading.Tasks.Task GuestAutoActAsync()
    {
        var client = _client;
        var view = client?.View;
        if (client is null || view?.Decision is not { } d || d.PlayerId != _myPlayerId) return;
        if (System.Threading.Interlocked.Exchange(ref _guestAutoBusy, 1) == 1) return;
        try
        {
            var hand = view.MyHandCards;
            switch (d.Kind)
            {
                case DecisionKind.PlayOrPass:
                    if (view.LegalPlays is { Length: > 0 } legal)
                    {
                        // 弱い組から出す（あがりを急がない、ふつうの打ち方）
                        var pick = legal.Select(l => l.Select(Card.FromId).ToList())
                            .OrderBy(l => l.Where(c => !c.IsJoker).Select(c => Card.GetBaseStrength(c.Rank)).DefaultIfEmpty(99).Min())
                            .First();
                        await client.PlayAsync(pick);
                    }
                    else
                    {
                        await client.PassAsync();
                    }
                    break;
                case DecisionKind.FourStopChance:
                case DecisionKind.SandstormStopChance:
                    await client.PassAsync();
                    break;
                case DecisionKind.SelectCards:
                    await client.SelectCardsAsync(hand.Take(Math.Max(d.MinCount, 0)).ToList());
                    break;
                case DecisionKind.DeclareRanks:
                    await client.DeclareRanksAsync(Enum.GetValues<Rank>().Where(r => r != Rank.Joker).Take(d.MinCount).ToList());
                    break;
                case DecisionKind.RevolutionChoice:
                    await client.ChooseRevolutionAsync(true);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[Autoplay] {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _guestAutoBusy, 0);
        }
    }

    /// <summary>この画面で使う一度きりのタイマーを止める（後始末）。</summary>
    private void StopFeelTimers()
    {
        _exchangeTimer?.Stop();
        _autoAdvanceTimer?.Stop();
        _guestAutoTimer?.Stop();
    }
}

/// <summary>出されたカード（場へ飛ばす演出用）。</summary>
/// <param name="PlayerId">出したプレイヤー。</param>
/// <param name="Cards">出したカード。</param>
public sealed record CardsFlightEventArgs(int PlayerId, IReadOnlyList<Card> Cards);

/// <summary>吹き出しの内容。</summary>
/// <param name="PlayerId">話すプレイヤー。</param>
/// <param name="Text">ひとこと。</param>
public sealed record ReactionEventArgs(int PlayerId, string Text);
