using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Audio;
using DTTS.Services.Database;
using DTTS.Services.Network;
using DTTS.Services.Settings;
using Microsoft.UI.Dispatching;

namespace DTTS.ViewModels;

public sealed partial class GameTableViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly SettingsRepository _settingsRepo;
    private readonly MatchHistoryRepository _historyRepo;
    private readonly IAudioService _audio;
    private readonly DispatcherQueue _dispatcherQueue;

    private GameEngine? _engine;
    private NpcController? _npcController;
    private RuleSettings _rules = RuleSettings.CreateDefault();
    private PendingDecision? _currentDecision;
    private Dictionary<int, NpcProfile> _npcProfiles = new();

    private GameHostSession? _hostSession;
    private RemoteGameClient? _client;
    private int _myPlayerId = 0;

    // --- 後始末用 ---
    private DispatcherQueueNpcScheduler? _npcScheduler;
    private EventHandler<GameEvent>? _engineHandler;
    private Task? _shutdownTask;
    private bool _isShutDown;
    private bool _connectionLost;
    private Task _saveTask = Task.CompletedTask;
    private int _guestResultRound;
    private bool _guestDecisionInFlight;
    private List<List<Card>> _guestLegalPlays = new();

    // --- プレイヤー ---
    public PlayerViewModel Player0 { get; } = new(0, "あなた", true);
    public PlayerViewModel Player1 { get; } = new(1, "NPC 東", false);
    public PlayerViewModel Player2 { get; } = new(2, "NPC 北", false);
    public PlayerViewModel Player3 { get; } = new(3, "NPC 西", false);

    /// <summary>
    /// プレイヤーID（エンジンの席番号）に対応する表示欄。Player0（画面下）が常に自分になるよう、
    /// ゲストでは自分の席番号を基準に回して割り当てる（ホスト・1人用は自分が席 0）。
    /// </summary>
    public PlayerViewModel GetPlayerVm(int id) => (((id - _myPlayerId) % 4 + 4) % 4) switch
    {
        0 => Player0,
        1 => Player1,
        2 => Player2,
        3 => Player3,
        _ => Player0
    };

    // --- 手札と場 ---
    public ObservableCollection<CardViewModel> HumanHand { get; } = new();
    public ObservableCollection<Card> FieldCards { get; } = new();

    // --- 状態プロパティ ---
    [ObservableProperty]
    public partial string RoundInfoText { get; set; } = "第 1 / 5 ラウンド";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "ゲームを開始しています...";

    [ObservableProperty]
    public partial bool IsRevolution { get; set; }

    [ObservableProperty]
    public partial bool IsElevenBack { get; set; }

    /// <summary>
    /// 11バックのバッジの文言。通常の11バック（場が流れるまで）と、強化11バック（次のJが出るまで続く）を
    /// 見分けられるように出し分ける。
    /// </summary>
    [ObservableProperty]
    public partial string ElevenBackBadgeText { get; set; } = NormalElevenBackLabel;

    /// <summary>強さが反転しているか（革命 XOR 11バック）。HUD の強弱方向に使う。</summary>
    [ObservableProperty]
    public partial bool IsStrengthReversed { get; set; }

    /// <summary>手番が反時計回りか（リバース中）。</summary>
    [ObservableProperty]
    public partial bool IsCounterClockwise { get; set; }

    /// <summary>HUD：強弱の向き（弱い → 強い）。</summary>
    public string StrengthWeakText => IsStrengthReversed ? "2" : "3";

    /// <summary>HUD：強弱の向きの強い側。</summary>
    public string StrengthStrongText => IsStrengthReversed ? "3" : "2";

    /// <summary>HUD：強弱の説明。</summary>
    public string StrengthCaption => IsStrengthReversed ? "逆転中" : "通常";

    /// <summary>HUD：手番の回り方向。</summary>
    public string DirectionText => IsCounterClockwise ? "反時計回り" : "時計回り";

    /// <summary>HUD：手番の回り方向のアイコン（Segoe Fluent Icons）。</summary>
    public string DirectionGlyph => IsCounterClockwise ? "\uE7A7" : "\uE7A6";

    partial void OnIsStrengthReversedChanged(bool value)
    {
        OnPropertyChanged(nameof(StrengthWeakText));
        OnPropertyChanged(nameof(StrengthStrongText));
        OnPropertyChanged(nameof(StrengthCaption));
    }

    partial void OnIsCounterClockwiseChanged(bool value)
    {
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(DirectionGlyph));
    }

    private const string NormalElevenBackLabel = "11バック";
    private const string StrongElevenBackLabel = "強化11バック（永続）";

    [ObservableProperty]
    public partial string LockDescription { get; set; } = "";

    [ObservableProperty]
    public partial bool CanPlay { get; set; }

    [ObservableProperty]
    public partial bool CanPass { get; set; }

    // --- モーダル・ダイアログ ---
    [ObservableProperty]
    public partial bool IsDecisionDialogOpen { get; set; }

    [ObservableProperty]
    public partial string DecisionTitle { get; set; } = "";

    [ObservableProperty]
    public partial string DecisionMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRevolutionChoice { get; set; }

    /// <summary>12ボンバーの数字宣言中か（数字ボタンを出す）。</summary>
    [ObservableProperty]
    public partial bool IsRankDeclaration { get; set; }

    /// <summary>
    /// カード選択（交換・7渡し・10捨て）中か。画面を覆わない操作バーとして出し、手札はマウスでもキーボードでも選べる。
    /// </summary>
    [ObservableProperty]
    public partial bool IsCardSelectionMode { get; set; }

    /// <summary>画面全体を暗くして操作を止めるダイアログ（革命の選択・数字の宣言）を出しているか。</summary>
    [ObservableProperty]
    public partial bool IsModalDecisionOpen { get; set; }

    /// <summary>選択数の表示（例：「1 / 2 枚」）。</summary>
    [ObservableProperty]
    public partial string SelectionCountText { get; set; } = "";

    /// <summary>規定の数を選んでいて決定できるか（カード選択・数字の宣言）。</summary>
    [ObservableProperty]
    public partial bool CanConfirmDecision { get; set; }

    // ------------------------------------------------------------------
    // 割り込みの確認（8切りへの4止め／ろくろ首・99車への砂嵐）
    // ------------------------------------------------------------------

    /// <summary>
    /// 手番外の割り込み（4止め・砂嵐）の確認を出しているか。画面は覆わず、手札の上の操作バーで
    /// 「止める」か「見送る」かを選ぶ。止める札は自動で選択済みにする。
    /// </summary>
    [ObservableProperty]
    public partial bool IsInterruptPromptOpen { get; set; }

    [ObservableProperty]
    public partial string InterruptTitle { get; set; } = "";

    [ObservableProperty]
    public partial string InterruptMessage { get; set; } = "";

    /// <summary>「止める」ボタンの文言（例：「砂嵐で止める」）。</summary>
    [ObservableProperty]
    public partial string InterruptAcceptText { get; set; } = "止める";

    // ------------------------------------------------------------------
    // 進行スピード（NPCが考える時間）
    // ------------------------------------------------------------------

    /// <summary>現在の進行スピード。</summary>
    [ObservableProperty]
    public partial GameSpeed Speed { get; set; } = GameSpeed.Normal;

    /// <summary>ヘッダーのボタンに出す進行スピードの名前。</summary>
    public string SpeedLabel => NpcTimingOptions.SpeedName(Speed);

    /// <summary>進行スピードのボタンの説明。</summary>
    public string SpeedToolTip => $"進行スピード: {SpeedLabel}（クリックで切り替え：ゆっくり → 普通 → 速い → 超速）";

    /// <summary>進行スピードを変えられるか（ゲストは変えられない。NPCはホスト側で動いている）。</summary>
    public bool CanChangeSpeed => !IsGuest;

    partial void OnSpeedChanged(GameSpeed value)
    {
        OnPropertyChanged(nameof(SpeedLabel));
        OnPropertyChanged(nameof(SpeedToolTip));
        ApplySpeed();
    }

    partial void OnIsGuestChanged(bool value) => OnPropertyChanged(nameof(CanChangeSpeed));

    /// <summary>NPCの思考時間へ進行スピードを反映する（次の判断から効く）。</summary>
    private void ApplySpeed()
    {
        var timing = NpcTimingOptions.ForSpeed(Speed);
        if (_npcController is not null)
        {
            _npcController.Timing = timing;
        }
        if (_hostSession?.Npc is { } hostNpc)
        {
            hostNpc.Timing = timing;
        }
    }

    /// <summary>進行スピードを次の段階へ切り替えて保存する（ゆっくり → 普通 → 速い → 超速 → ゆっくり）。</summary>
    [RelayCommand]
    private async Task CycleSpeedAsync()
    {
        if (!CanChangeSpeed) return;
        var next = (GameSpeed)(((int)Speed + 1) % 4);
        Speed = next;
        NoticeMessage = $"進行スピード: {SpeedLabel}";
        try
        {
            await _settingsRepo.UpdateAppearanceAsync(a => a.GameSpeed = next);
        }
        catch (Exception ex)
        {
            // 保存に失敗してもこの対戦中の切り替えは効いている
            App.WriteCrashLog("GameTable.SaveSpeed", ex);
        }
    }

    // ------------------------------------------------------------------
    // 演出の要求（表示側が Composition で描く）
    // ------------------------------------------------------------------

    /// <summary>12ボンバーで消えた数字を、画面中央に弱い順で1つずつ見せる演出の要求。</summary>
    public event EventHandler<BomberVanishEventArgs>? BomberVanishTriggered;

    /// <summary>あがりの演出（柔らかい光と順位リボン）の要求。</summary>
    public event EventHandler<FinishCelebrationEventArgs>? FinishCelebrationTriggered;

    /// <summary>12ボンバーで宣言できる数字（3〜2 とジョーカー）。</summary>
    public ObservableCollection<RankOptionViewModel> RankOptions { get; } = new();

    partial void OnIsDecisionDialogOpenChanged(bool value)
    {
        if (!value)
        {
            // どの経路で閉じても種類ごとの表示をまとめて片付ける
            IsRevolutionChoice = false;
            IsRankDeclaration = false;
            IsCardSelectionMode = false;
            IsModalDecisionOpen = false;
            CanConfirmDecision = false;
            SelectionCountText = "";
        }
    }

    partial void OnIsRoundResultOpenChanged(bool value)
    {
        if (value)
        {
            IsInterruptPromptOpen = false;
        }
    }

    [ObservableProperty]
    public partial bool IsRoundResultOpen { get; set; }

    [ObservableProperty]
    public partial string RoundResultText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsGameOver { get; set; }

    /// <summary>通信対戦のゲスト（参加者）か。ラウンドの進行はホストが行う。</summary>
    [ObservableProperty]
    public partial bool IsGuest { get; set; }

    /// <summary>ラウンド結果画面の決定ボタンを押せるか（ゲストは対戦中のラウンド進行ができない）。</summary>
    [ObservableProperty]
    public partial bool CanAdvanceRound { get; set; } = true;

    /// <summary>ラウンド結果画面の決定ボタンの文言。</summary>
    [ObservableProperty]
    public partial string NextRoundButtonText { get; set; } = "次のラウンドへ進む";

    /// <summary>
    /// タイトルへ戻る前に確認が必要か（対戦中のみ。ラウンド結果表示中・ゲーム終了後は不要）。
    /// </summary>
    public bool NeedsLeaveConfirmation
        => !_isShutDown && !_connectionLost && (_engine is not null || _client is not null) && !IsRoundResultOpen && !IsGameOver;

    /// <summary>中断確認ダイアログの本文。</summary>
    public string LeaveConfirmationMessage => _hostSession is not null
        ? "対戦を中断してタイトルに戻りますか？\n（進行中のゲームは破棄され、参加者との部屋も閉じられます）"
        : _client is not null
            ? "対戦を中断してタイトルに戻りますか？\n（部屋から退出します。あなたの席はNPCが引き継ぎます）"
            : "対戦を中断してタイトルに戻りますか？\n（進行中のゲームは破棄されます）";

    public event EventHandler<CutInEventArgs>? CutInTriggered;
    public event EventHandler? FieldFlushedTriggered;
    public event EventHandler? HandDealtTriggered;

    public GameTableViewModel(
        INavigationService navigation,
        SettingsRepository settingsRepo,
        MatchHistoryRepository historyRepo,
        IAudioService audio)
    {
        _navigation = navigation;
        _settingsRepo = settingsRepo;
        _historyRepo = historyRepo;
        _audio = audio;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync(NetworkGameParams? netParams = null)
    {
        // 進行スピード（設定画面・前回の対戦で選んだもの）
        try
        {
            Speed = (await _settingsRepo.LoadAppearanceAsync()).GameSpeed;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("GameTable.LoadSpeed", ex);
        }

        if (netParams?.Client != null)
        {
            // ゲスト（クライアント）モード
            _client = netParams.Client;
            _rules = netParams.Rules;
            IsGuest = true;
            _client.ViewUpdated += OnClientViewUpdated;
            _client.Disconnected += OnClientDisconnected;
            _client.NoticeReceived += OnClientNoticeReceived;

            if (_client.View != null)
            {
                ApplyClientView(_client.View);
            }
            return;
        }

        if (netParams?.HostSession != null)
        {
            // ホストモード
            _hostSession = netParams.HostSession;
            _engine = _hostSession.Engine ?? throw new InvalidOperationException("Host Engine is null");
            _rules = netParams.Rules;
            _myPlayerId = 0;

            // 席の名前（ゲスト・補充NPC）はロビーで決まったものをエンジンから受け取る
            for (int i = 0; i < 4; i++)
            {
                GetPlayerVm(i).Name = DisplayName(i, _engine.GetPlayer(i).Name);
            }

            _engineHandler = (sender, ev) => OnEngineEvent(ev);
            _engine.EventOccurred += _engineHandler;
            ApplySpeed(); // 補充・引き継ぎのNPCにも進行スピードを反映する
            RoundInfoText = FormatRoundInfo(Math.Max(1, _engine.RoundNumber), _engine.Rules.Progress.RoundCount);
            ApplyFieldBadges(_engine.Field);
            UpdateAllPlayersInfo();

            // ロビーでゲームを開始してからこの画面が購読するまでに出た判断要求は届いていないため、
            // 現在の判断待ちを直接反映する（再発行すると NPC にも二重に届くので行わない）
            if (_engine.PendingDecision is { } pending)
            {
                UpdateTurnState(pending.PlayerId);
                HandleDecisionRequested(pending);
            }
            UpdateHumanHandVisuals();
            return;
        }

        // 1人用モード
        _rules = await _settingsRepo.LoadRulesAsync();
        var npcSettings = await _settingsRepo.LoadNpcAsync();

        // プレイヤー設定
        Player0.Name = npcSettings.PlayerName;
        Player1.Name = npcSettings.Seats[0].Name;
        Player2.Name = npcSettings.Seats[1].Name;
        Player3.Name = npcSettings.Seats[2].Name;

        _npcProfiles = new Dictionary<int, NpcProfile>
        {
            [1] = npcSettings.Seats[0].ToProfile(),
            [2] = npcSettings.Seats[1].ToProfile(),
            [3] = npcSettings.Seats[2].ToProfile(),
        };

        var setups = new List<PlayerSetup>
        {
            new(Player0.Name, PlayerKind.Human),
            new(Player1.Name, PlayerKind.Npc),
            new(Player2.Name, PlayerKind.Npc),
            new(Player3.Name, PlayerKind.Npc)
        };

        _engine = new GameEngine(_rules, setups);

        // NPCコントローラー初期化（UIのワンショットタイマーを渡す）
        _npcScheduler = new DispatcherQueueNpcScheduler(_dispatcherQueue);
        _npcController = new NpcController(_engine, _npcScheduler, NpcTimingOptions.ForSpeed(Speed));

        _npcController.Assign(1, _npcProfiles[1]);
        _npcController.Assign(2, _npcProfiles[2]);
        _npcController.Assign(3, _npcProfiles[3]);

        // イベント購読（後始末で解除するためハンドラを保持）
        _engineHandler = (sender, ev) => OnEngineEvent(ev);
        _engine.EventOccurred += _engineHandler;

        // ゲーム開始（ラウンド表示は実際のルールのラウンド数で初期化する）
        _engine.StartGame();
        RoundInfoText = FormatRoundInfo(_engine.RoundNumber, _engine.Rules.Progress.RoundCount);
        UpdateAllPlayersInfo();
        UpdateHumanHandVisuals();
    }

    private void OnEngineEvent(GameEvent ev)
    {
        _dispatcherQueue.TryEnqueue(() => HandleEngineEvent(ev));
    }

    private void HandleEngineEvent(GameEvent ev)
    {
        // 後始末の後に、UIスレッドの待ち行列に残っていたイベントが届いても無視する
        if (_engine == null || _isShutDown) return;

        switch (ev)
        {
            case RoundStartedEvent:
                // 新しいラウンドは、革命・11バック・縛りが一切残っていない状態から始める
                ResetRoundBadges();
                break;

            case HandsDealtEvent:
                ApplyFieldBadges(_engine.Field); // 新しいラウンドの場の状態に戻す
                NoticeMessage = "";
                HandDealtTriggered?.Invoke(this, EventArgs.Empty);
                _audio.Play(SoundEffect.CardDeal);
                break;

            case DecisionRequestedEvent dr:
                IsInterruptPromptOpen = false; // 前の割り込み確認は決着済み。自分宛ての割り込みなら下で開き直す
                UpdateTurnState(dr.Decision.PlayerId);
                HandleDecisionRequested(dr.Decision);
                break;

            case CardsPlayedEvent cp:
                FieldCards.Clear();
                if (cp.Judgement.Play != null)
                {
                    foreach (var c in cp.Judgement.Play.Cards) FieldCards.Add(c);
                }
                GetPlayerVm(cp.PlayerId).IsPassed = false;
                ApplyFieldBadges(cp.Field);
                StatusMessage = $"{GetPlayerVm(cp.PlayerId).Name} がカードを出しました";
                UpdateAllPlayersInfo();
                _audio.Play(SoundEffect.CardPlay);
                break;

            case PlayerPassedEvent pp:
                GetPlayerVm(pp.PlayerId).IsPassed = true;
                StatusMessage = $"{GetPlayerVm(pp.PlayerId).Name} がパスしました";
                break;

            case EffectAppliedEvent ea:
                ApplyFieldBadges(ea.Field);
                if (ea.Applied)
                {
                    TriggerCutInForEffect(ea.Effect.Type, ea.PlayerId, ea.Field.IsElevenBack);
                    PlayEffectSound(ea.Effect.Type);
                }
                break;

            case FieldFlushedEvent ff:
                FieldCards.Clear();
                ApplyFieldBadges(ff.Field); // 11バック・縛りは場が流れると解除される
                StatusMessage = ff.Cause.HasValue ? $"場が流れました（{ff.Cause.Value}）" : "場が流れました";
                for (int i = 0; i < 4; i++) GetPlayerVm(i).IsPassed = false;
                FieldFlushedTriggered?.Invoke(this, EventArgs.Empty);
                SweepTriggered?.Invoke(this, SweepKind.PrismFlash);
                _audio.Play(SoundEffect.FieldFlush);
                break;

            case RanksDeclaredEvent rd:
                OnRanksDeclared(rd.PlayerId, rd.Ranks);
                break;

            case CardsDiscardedEvent cd:
                OnCardsDiscarded(cd.PlayerId, cd.Cards, cd.Cards.Count, cd.Reason);
                UpdateAllPlayersInfo(); // 残り手札の枚数をすぐ反映する
                break;

            case PlayerFinishedEvent pf:
                OnPlayerFinished(pf.PlayerId, pf.Kind, pf.Place);
                break;

            case RoundEndedEvent re:
                IsGameOver = re.IsGameOver;
                HandleRoundEnded(re.Result);
                break;

            case GameEndedEvent ge:
                HandleGameEnded(ge.Result);
                break;
        }

        UpdateHumanHandVisuals();
    }

    // ------------------------------------------------------------------
    // 捨て札の通知（10捨て・12ボンバー・都落ち・反則）。1人用・ホスト・ゲストで共通
    // ------------------------------------------------------------------

    /// <summary>場面が切り替わったことを示す全画面演出の種類。</summary>
    public enum SweepKind
    {
        /// <summary>場が流れた：画面の四辺を光が走る（手札を隠さない）。</summary>
        PrismFlash,
        /// <summary>12ボンバー・革命など大きな出来事：斜めの光の帯が画面を横切る。</summary>
        AuroraSweep,
    }

    /// <summary>全画面演出の要求（表示側が Composition で描く）。</summary>
    public event EventHandler<SweepKind>? SweepTriggered;

    /// <summary>直近の出来事（捨て札・12ボンバーの結果など）。手番の表示で上書きされないよう、状況表示とは別に出す。</summary>
    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = "";

    private int? _bomberDeclarer;
    private List<Rank> _bomberRanks = new();
    private readonly Dictionary<int, int> _bomberDiscards = new();
    private readonly Dictionary<Rank, int> _bomberRankCounts = new();

    /// <summary>革命・11バック・縛りの表示を、ラウンド開始時の「何もない」状態へ戻す。</summary>
    private void ResetRoundBadges()
    {
        IsRevolution = false;
        IsElevenBack = false;
        IsStrengthReversed = false;
        IsCounterClockwise = false;
        ElevenBackBadgeText = NormalElevenBackLabel;
        LockDescription = "";
        IsInterruptPromptOpen = false;
    }

    /// <summary>あがり・脱落の表示と演出。1人用・ホスト・ゲストで共通。</summary>
    private void OnPlayerFinished(int playerId, FinishKind kind, int place)
    {
        var vm = GetPlayerVm(playerId);
        if (place > 0)
        {
            vm.FinishPlace = place;
        }
        vm.IsTurn = false;
        vm.IsThinking = false;
        string name = vm.Name;
        StatusMessage = kind switch
        {
            FinishKind.Foul => $"{name} は禁止あがりで反則負けになりました",
            FinishKind.MiyakoOchi => $"{name} は都落ちしました",
            FinishKind.LastRemaining => place > 0 ? $"{name} が {place}位 で確定しました" : $"{name} の順位が確定しました",
            _ => place > 0 ? $"{name} が {place}位 であがりました！" : $"{name} があがりました！",
        };
        PlayFinishSound(playerId, kind);

        // きれいなあがり方（通常のあがり）だけ、静かな光の演出で祝う
        if (kind == FinishKind.Normal && place > 0)
        {
            FinishCelebrationTriggered?.Invoke(this, new FinishCelebrationEventArgs(
                name, place, playerId == _myPlayerId, RankTitle(GameEngine.RankForPosition(place, 4))));
        }
    }

    private static string RankTitle(PlayerRank rank) => rank switch
    {
        PlayerRank.Daifugo => "大富豪",
        PlayerRank.Fugo => "富豪",
        PlayerRank.Hinmin => "貧民",
        PlayerRank.Daihinmin => "大貧民",
        _ => "平民",
    };

    private static string RankList(IEnumerable<Rank> ranks) => string.Join("・", ranks.Select(RankOptionViewModel.LabelOf));

    private void OnRanksDeclared(int playerId, IReadOnlyList<Rank> ranks)
    {
        _bomberDeclarer = playerId;
        // 画面に出す順番はいつも弱い順（3 … K・A・2・ジョーカー）
        _bomberRanks = ranks.OrderBy(Card.GetBaseStrength).ToList();
        _bomberDiscards.Clear();
        _bomberRankCounts.Clear();
        NoticeMessage = $"{GetPlayerVm(playerId).Name} が12ボンバーで【{RankList(_bomberRanks)}】を宣言しました";
    }

    private void OnCardsDiscarded(int playerId, IReadOnlyList<Card>? cards, int count, CardDiscardReason? reason)
    {
        string name = GetPlayerVm(playerId).Name;
        switch (reason)
        {
            case CardDiscardReason.TwelveBomber:
                _bomberDiscards[playerId] = (_bomberDiscards.TryGetValue(playerId, out int n) ? n : 0) + count;
                foreach (var card in cards ?? Array.Empty<Card>())
                {
                    _bomberRankCounts[card.Rank] = (_bomberRankCounts.TryGetValue(card.Rank, out int k) ? k : 0) + 1;
                }
                break;
            case CardDiscardReason.TenDiscard:
                NoticeMessage = $"{name} が10捨てで {count}枚 捨てました";
                break;
            case CardDiscardReason.MiyakoOchi:
                NoticeMessage = $"{name} の残り手札 {count}枚 が都落ちで没収されました";
                break;
            case CardDiscardReason.ForcedFoul:
                NoticeMessage = $"{name} は出せるカードがなく、残り手札 {count}枚 が没収されました";
                break;
        }
    }

    /// <summary>12ボンバーの結果（誰が何を宣言し、誰が何枚捨てたか）。</summary>
    private string BuildBomberSummary(int declarerId)
    {
        string ranks = _bomberRanks.Count > 0 ? $"【{RankList(_bomberRanks)}】" : "宣言した数字";
        int total = _bomberDiscards.Values.Sum();
        string name = GetPlayerVm(declarerId).Name;
        if (total == 0)
        {
            return $"{name} の12ボンバー：{ranks}を持っている人はいませんでした";
        }
        var detail = _bomberDiscards
            .Where(kv => kv.Value > 0)
            .Select(kv => $"{GetPlayerVm(kv.Key).Name} {kv.Value}枚");
        return $"{name} の12ボンバーで{ranks}が合計 {total}枚 捨てられました（{string.Join("・", detail)}）";
    }

    /// <summary>
    /// 通信対戦での表示名。参加者が既定の名前「あなた」のままだと誰の話か分からなくなるため、
    /// 自分以外の「あなた」は「プレイヤーN」（N は席番号＋1）と表示する。
    /// </summary>
    private string DisplayName(int playerId, string name)
    {
        const string DefaultSelfName = "あなた";
        if (playerId != _myPlayerId && (string.IsNullOrWhiteSpace(name) || name == DefaultSelfName))
        {
            return $"プレイヤー{playerId + 1}";
        }
        return name;
    }

    /// <summary>ラウンド表示。規定ラウンドを超えた同点決着の延長戦は「延長戦」と表示する。</summary>
    private static string FormatRoundInfo(int roundNumber, int roundCount)
    {
        if (roundCount > 0 && roundNumber > roundCount)
        {
            int extra = roundNumber - roundCount;
            return extra == 1 ? $"延長戦（第 {roundNumber} ラウンド）" : $"延長戦 {extra}（第 {roundNumber} ラウンド）";
        }
        return $"第 {roundNumber} / {roundCount} ラウンド";
    }

    private void UpdateTurnState(int activePlayerId)
    {
        if (_engine == null) return;

        for (int i = 0; i < 4; i++)
        {
            var p = GetPlayerVm(i);
            p.IsTurn = (i == activePlayerId);
            p.IsThinking = (i == activePlayerId && !p.IsHuman && !p.FinishPlace.HasValue);
        }

        UpdateAllPlayersInfo();

        bool isMyTurn = (activePlayerId == 0);
        CanPass = isMyTurn && (_engine.PendingDecision?.CanPass ?? !_engine.Field.IsEmpty);
        bool interrupt = _engine.PendingDecision is { } pd && pd.Kind.IsInterruptChance();
        StatusMessage = isMyTurn
            ? (interrupt ? "割り込みのチャンスです（止める / 見送る）" : "あなたのターンです")
            : (interrupt ? $"{GetPlayerVm(activePlayerId).Name} が割り込むか考えています..." : $"{GetPlayerVm(activePlayerId).Name} のターン...");
    }

    private void UpdateAllPlayersInfo()
    {
        if (_engine == null) return;

        for (int i = 0; i < 4; i++)
        {
            var enginePlayer = _engine.GetPlayer(i);
            var vm = GetPlayerVm(i);
            vm.HandCount = enginePlayer.HandCount;
            vm.Rank = enginePlayer.Rank;
            vm.TotalScore = enginePlayer.TotalScore;
            vm.IsPassed = _engine.Field.HasPassed(i);
        }
    }

    /// <summary>革命・11バック・縛りの表示を場の状態に合わせる。</summary>
    private void ApplyFieldBadges(FieldState field)
    {
        IsRevolution = field.IsRevolution;
        ElevenBackBadgeText = field.IsElevenBackPersistent ? StrongElevenBackLabel : NormalElevenBackLabel;
        IsElevenBack = field.IsElevenBack;
        IsStrengthReversed = field.IsStrengthReversed;
        IsCounterClockwise = field.Direction == PlayDirection.CounterClockwise;
        UpdateLockState(field.Lock);
    }

    private void UpdateLockState(LockState lockState)
    {
        if (!lockState.IsAnyLocked)
        {
            LockDescription = "";
        }
        else
        {
            var list = new List<string>();
            if (lockState.IsSuitLocked) list.Add("スート縛り");
            if (lockState.IsNumberLocked) list.Add("数しば");
            LockDescription = $"【{string.Join("+", list)}】";
        }
    }

    private void UpdateHumanHandVisuals()
    {
        if (_engine == null) return;

        var myHand = _engine.GetPlayer(0).Hand;

        // 手札カードリストの同期
        var currentCards = HumanHand.Select(vm => vm.Card).ToList();
        if (!currentCards.SequenceEqual(myHand))
        {
            HumanHand.Clear();
            foreach (var card in myHand)
            {
                HumanHand.Add(new CardViewModel(card));
            }
        }

        // 合法手チェック
        var legalPlays = _engine.GetLegalPlays(0);
        var selected = HumanHand.Where(vm => vm.IsSelected).Select(vm => vm.Card).ToList();

        // カード選択中は「出す」の判定をせず、どのカードも選べる表示にする
        CanPlay = !IsCardSelectionMode
            && legalPlays.Any(lp => lp.Play != null && lp.Play.Cards.Count == selected.Count && lp.Play.Cards.All(selected.Contains));

        // 自分が「出すかパスか」を決める番か（手番、または 4止め・砂嵐の割り込み）。出せないカードを暗く表示する
        bool isMyTurn = _engine.PendingDecision is { } pending && pending.PlayerId == _myPlayerId && pending.Kind.IsPlayDecision();
        foreach (var cardVm in HumanHand)
        {
            cardVm.CanPlay = IsCardSelectionMode || !isMyTurn || legalPlays.Any(lp => lp.Play != null && lp.Play.Cards.Contains(cardVm.Card));
        }
        UpdateSelectionCount();
    }

    [RelayCommand]
    private void ToggleCardSelection(CardViewModel cardVm)
    {
        cardVm.IsSelected = !cardVm.IsSelected;
        _audio.Play(SoundEffect.CardSelect);
        if (_client != null)
        {
            UpdateGuestCanPlay(); // ゲストはホストから届いた合法手で判定する
        }
        else
        {
            UpdateHumanHandVisuals();
        }
        UpdateSelectionCount();
    }

    /// <summary>ゲスト：最後に届いた合法手と現在の選択から「出す」可否を決める。</summary>
    private void UpdateGuestCanPlay()
    {
        var selected = HumanHand.Where(vm => vm.IsSelected).Select(vm => vm.Card).ToList();
        CanPlay = !IsCardSelectionMode && selected.Count > 0
            && _guestLegalPlays.Any(lp => lp.Count == selected.Count && lp.All(selected.Contains));
    }

    // ------------------------------------------------------------------
    // 効果音（イベントを受けて鳴らすだけ。再生完了は待たない）
    // ------------------------------------------------------------------

    /// <summary>特殊効果に対応する効果音を鳴らす。場流し系は FieldFlushedEvent 側の音と重ねる。</summary>
    private void PlayEffectSound(SpecialEffectType effectType)
    {
        if (effectType.IsRevolutionTrigger())
        {
            _audio.Play(SoundEffect.CutInRevolution);
            return;
        }
        switch (effectType)
        {
            case SpecialEffectType.EightCut:
            case SpecialEffectType.ElevenBack:
            case SpecialEffectType.StrongElevenBack:
            case SpecialEffectType.SpadeThreeReturn:
            case SpecialEffectType.Emperor:
            case SpecialEffectType.Sandstorm:
            case SpecialEffectType.Ambulance:
            case SpecialEffectType.Rokurokubi:
            case SpecialEffectType.TwelveBomber:
            case SpecialEffectType.SevenPass:
            case SpecialEffectType.TenDiscard:
            case SpecialEffectType.SuitLock:
            case SpecialEffectType.NumberLock:
            case SpecialEffectType.FourStop:
                _audio.Play(SoundEffect.CutInSpecial);
                break;
        }
    }

    /// <summary>自分があがった・転落したときのジングル（他人のあがりでは鳴らさない）。</summary>
    private void PlayFinishSound(int playerId, FinishKind kind)
    {
        if (playerId != _myPlayerId)
        {
            return;
        }
        switch (kind)
        {
            case FinishKind.Normal:
                _audio.Play(SoundEffect.VictoryJingle);
                break;
            case FinishKind.Foul:
            case FinishKind.MiyakoOchi:
                _audio.Play(SoundEffect.DefeatJingle);
                break;
        }
    }

    /// <summary>ラウンド終了時：自分が下剋上を達成したら勝利、大貧民が確定したら転落のジングル。</summary>
    private void PlayRoundEndSound(RoundResult result)
    {
        var mine = result.Standings.FirstOrDefault(s => s.PlayerId == _myPlayerId);
        if (mine is null)
        {
            return;
        }
        if (result.IsGekokujo)
        {
            _audio.Play(mine.Rank == PlayerRank.Daifugo ? SoundEffect.VictoryJingle : SoundEffect.CutInSpecial);
        }
        else if (mine.Rank == PlayerRank.Daihinmin && mine.Kind is FinishKind.Normal or FinishKind.LastRemaining)
        {
            // 反則・都落ちはあがり確定時に鳴らし済み
            _audio.Play(SoundEffect.DefeatJingle);
        }
    }

    private void PlayGuestRoundEndSound(StandingDto[] standings)
    {
        var mine = standings.FirstOrDefault(s => s.PlayerId == _myPlayerId);
        if (mine is not null && mine.Rank == PlayerRank.Daihinmin)
        {
            _audio.Play(SoundEffect.DefeatJingle);
        }
    }

    /// <summary>ゲスト（通信対戦の参加者）側：ホストからの演出用通知で効果音とラウンド結果を反映する。</summary>
    private void OnClientNoticeReceived(object? sender, NoticeDto notice)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isShutDown) return;
            switch (notice.Kind)
            {
                case "RoundEnded" when notice.Standings is { } standings:
                    // ゲスト：ラウンド結果を表示し、ホストの進行を待つ（Applied はゲーム終了かどうか）
                    _guestResultRound = notice.Count;
                    RoundResultText = string.Join("\n", standings
                        .OrderBy(s => s.Position)
                        .Select(s => $"{s.Position}位: {GetPlayerVm(s.PlayerId).Name} ({s.Rank}) -> 得点: {s.Points}pt"));
                    IsGameOver = notice.Applied;
                    IsRoundResultOpen = true;
                    UpdateRoundResultActions();
                    PlayGuestRoundEndSound(standings);
                    break;
                case "GameEnded":
                    IsGameOver = true;
                    StatusMessage = "全ラウンドが終了しました！";
                    UpdateRoundResultActions();
                    break;
                case "RoundStarted":
                    NoticeMessage = "";
                    ResetRoundBadges();
                    for (int i = 0; i < 4; i++)
                    {
                        GetPlayerVm(i).FinishPlace = null;
                    }
                    _audio.Play(SoundEffect.CardDeal);
                    break;
                case "CardsPlayed":
                    _audio.Play(SoundEffect.CardPlay);
                    break;
                case "FieldFlushed":
                    FieldFlushedTriggered?.Invoke(this, EventArgs.Empty);
                    SweepTriggered?.Invoke(this, SweepKind.PrismFlash);
                    _audio.Play(SoundEffect.FieldFlush);
                    break;
                case "EffectApplied" when notice.Applied && notice.Effect is { } effect:
                    // ゲストにもホストと同じカットインを出す（12ボンバーは誰が何枚捨てたかも表示）
                    if (notice.PlayerId is { } effectPlayer)
                    {
                        TriggerCutInForEffect(effect, effectPlayer, null);
                    }
                    PlayEffectSound(effect);
                    break;
                case "RanksDeclared" when notice.PlayerId is { } declarer && notice.Ranks is { } ranks:
                    OnRanksDeclared(declarer, ranks);
                    break;
                case "CardsDiscarded" when notice.PlayerId is { } discarder:
                    OnCardsDiscarded(discarder, notice.CardIds?.Select(Card.FromId).ToList(), notice.Count, notice.DiscardReason);
                    break;
                case "PlayerFinished" when notice.PlayerId is { } pid && notice.FinishKind is { } kind:
                    OnPlayerFinished(pid, kind, notice.Count);
                    break;
            }
        });
    }

    [RelayCommand]
    private async Task PlaySelectedCardsAsync()
    {
        var selected = HumanHand.Where(vm => vm.IsSelected).Select(vm => vm.Card).ToList();
        if (selected.Count == 0) return;

        if (_client != null)
        {
            ShowGuestRejected(await SendGuestCommandAsync(_client.PlayAsync(selected)));
            return;
        }

        if (_engine != null)
        {
            var result = _engine.SubmitPlay(_myPlayerId, selected);
            if (result.IsAccepted)
            {
                HumanHand.Clear();
                UpdateHumanHandVisuals();
            }
            else if (!result.IsDeferred)
            {
                ShowRejected(result);
            }
        }
    }

    [RelayCommand]
    private async Task PassAsync()
    {
        if (_client != null)
        {
            ShowGuestRejected(await SendGuestCommandAsync(_client.PassAsync()));
            return;
        }

        if (_engine != null)
        {
            var result = _engine.SubmitPass(_myPlayerId);
            if (result.IsAccepted || result.IsDeferred)
            {
                GetPlayerVm(_myPlayerId).IsPassed = true;
            }
            else
            {
                ShowRejected(result);
            }
        }
    }

    /// <summary>ゲストのコマンドを送る。通信エラーは案内に出して null を返す。</summary>
    private async Task<CommandResponse?> SendGuestCommandAsync(Task<CommandResponse> sending)
    {
        try
        {
            return await sending;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_isShutDown) StatusMessage = $"送信できませんでした: {ex.Message}";
            return null;
        }
    }

    private void ShowGuestRejected(CommandResponse? response)
    {
        if (_isShutDown || response is null || response.Error is CommandError.None or CommandError.Deferred) return;
        ShowRejected(new CommandResult(response.Error, _myPlayerId, response.PlayReject));
    }

    /// <summary>操作がエンジンに受け付けられなかった理由を案内に出す。</summary>
    private void ShowRejected(CommandResult result)
    {
        StatusMessage = $"その操作はできません（{result.Error}{(result.PlayReject != PlayRejectReason.None ? "/" + result.PlayReject : "")}）";
    }

    private void OnClientViewUpdated(object? sender, ClientView view)
    {
        _dispatcherQueue.TryEnqueue(() => ApplyClientView(view));
    }

    private void OnClientDisconnected(object? sender, string reason)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isShutDown) return;
            _connectionLost = true;
            StatusMessage = $"ホストから切断されました: {reason}（Esc でタイトルに戻れます）";
            CanPlay = false;
            CanPass = false;
        });
    }

    private void ApplyClientView(ClientView view)
    {
        if (_isShutDown) return;
        _myPlayerId = view.MyPlayerId;
        RoundInfoText = FormatRoundInfo(view.RoundNumber, view.RoundCount);

        // ゲスト：ホストが次のラウンドを始めたら結果画面を閉じる
        if (IsRoundResultOpen && !IsGameOver && view.RoundNumber > _guestResultRound
            && view.Phase is GamePhase.Dealing or GamePhase.Exchanging or GamePhase.Playing)
        {
            IsRoundResultOpen = false;
            for (int i = 0; i < 4; i++)
            {
                GetPlayerVm(i).FinishPlace = null;
                GetPlayerVm(i).IsPassed = false;
            }
        }

        // 状態バッジ
        IsRevolution = view.Field.IsRevolution;
        ElevenBackBadgeText = view.Field.IsElevenBackPersistent ? StrongElevenBackLabel : NormalElevenBackLabel;
        IsElevenBack = view.Field.IsElevenBack;
        IsStrengthReversed = view.Field.IsStrengthReversed;
        IsCounterClockwise = view.Field.Direction == PlayDirection.CounterClockwise;
        
        var lockList = new List<string>();
        if (view.Field.LockedSuits != null && view.Field.LockedSuits.Length > 0) lockList.Add("スート縛り");
        if (view.Field.IsNumberLocked) lockList.Add("数しば");
        LockDescription = lockList.Count > 0 ? $"【{string.Join("+", lockList)}】" : "";

        // 場札
        FieldCards.Clear();
        if (view.Field.CurrentCardIds != null)
        {
            foreach (var id in view.Field.CurrentCardIds)
            {
                FieldCards.Add(Card.FromId(id));
            }
        }

        // プレイヤー情報
        foreach (var p in view.Players)
        {
            var vm = GetPlayerVm(p.Id);
            vm.Name = DisplayName(p.Id, p.Name);
            vm.HandCount = p.HandCount;
            vm.Rank = p.Rank;
            vm.TotalScore = p.TotalScore;
            vm.IsTurn = (view.Decision?.PlayerId == p.Id);
            vm.IsThinking = (view.Decision?.PlayerId == p.Id && p.Id != _myPlayerId);
        }

        // 手札
        var handCards = view.MyHand != null ? view.MyHand.Select(Card.FromId).ToList() : new List<Card>();
        var currentCards = HumanHand.Select(vm => vm.Card).ToList();
        if (!currentCards.SequenceEqual(handCards))
        {
            HumanHand.Clear();
            foreach (var c in handCards)
            {
                HumanHand.Add(new CardViewModel(c));
            }
        }

        // 判断・合法手
        bool isMyDecision = (view.Decision?.PlayerId == _myPlayerId);
        CanPass = isMyDecision && (view.Decision?.CanPass ?? false);

        var legalSet = view.LegalPlays != null
            ? view.LegalPlays.Select(arr => arr.Select(Card.FromId).ToList()).ToList()
            : new List<List<Card>>();
        _guestLegalPlays = legalSet;
        UpdateGuestCanPlay();

        foreach (var cardVm in HumanHand)
        {
            cardVm.CanPlay = IsCardSelectionMode || !isMyDecision || legalSet.Any(lp => lp.Contains(cardVm.Card));
        }

        StatusMessage = isMyDecision ? "あなたのターンです" : "相手のターンです...";

        // 割り込みの確認（4止め・砂嵐）。同じ確認を出している間は、選び直したカードを上書きしない
        if (isMyDecision && view.Decision is { } interrupt && interrupt.Kind.IsInterruptChance())
        {
            if (!IsInterruptPromptOpen || _interruptPromptKind != interrupt.Kind)
            {
                OpenInterruptPrompt(interrupt.Kind, interrupt.Effect, interrupt.TargetPlayerId, legalSet.FirstOrDefault());
                UpdateGuestCanPlay();
            }
            StatusMessage = "割り込みのチャンスです（止める / 見送る）";
        }
        else
        {
            IsInterruptPromptOpen = false;
        }

        // 判断ダイアログ（革命選択・カード選択）。ホストの判断要求が自分宛てなら開き、解決されたら閉じる
        var dec = view.Decision;
        if (isMyDecision && dec is not null && dec.Kind is DecisionKind.RevolutionChoice or DecisionKind.SelectCards or DecisionKind.DeclareRanks)
        {
            // 送信済みでホストの処理待ちの判断は開き直さない（同じ判断を二重に送らないため）
            bool sameAsCurrent = _currentDecision is not null && _currentDecision.Kind == dec.Kind && _currentDecision.Purpose == dec.Purpose;
            bool waitingHost = _guestDecisionInFlight && sameAsCurrent;
            if (!waitingHost && !(IsDecisionDialogOpen && sameAsCurrent))
            {
                _guestDecisionInFlight = false;
                HandleDecisionRequested(new PendingDecision(dec.Kind, dec.PlayerId)
                {
                    Purpose = dec.Purpose,
                    MinCount = dec.MinCount,
                    MaxCount = dec.MaxCount,
                    TargetPlayerId = dec.TargetPlayerId,
                    Effect = dec.Effect,
                    CanPass = dec.CanPass,
                });
            }
            StatusMessage = "あなたの選択を待っています";
        }
        else
        {
            _guestDecisionInFlight = false;
            if (IsDecisionDialogOpen)
            {
                IsDecisionDialogOpen = false;
                _currentDecision = null;
            }
        }
    }

    private void HandleDecisionRequested(PendingDecision dec)
    {
        if (dec.PlayerId != _myPlayerId) return;

        _currentDecision = dec;

        switch (dec.Kind)
        {
            case DecisionKind.FourStopChance:
            case DecisionKind.SandstormStopChance:
                // 1人用・ホスト：手札を最新にしてから、止められる札を選択済みにして確認を出す
                UpdateHumanHandVisuals();
                OpenInterruptPrompt(dec.Kind, dec.Effect, dec.TargetPlayerId,
                    _engine?.GetLegalPlays(_myPlayerId).FirstOrDefault()?.Play?.Cards);
                UpdateHumanHandVisuals();
                break;

            case DecisionKind.RevolutionChoice:
                DecisionTitle = "革命の選択";
                DecisionMessage = "革命を起こしますか？";
                IsDecisionDialogOpen = true;
                IsRankDeclaration = false;
                IsCardSelectionMode = false;
                IsRevolutionChoice = true;
                IsModalDecisionOpen = true;
                CanConfirmDecision = true;
                break;

            case DecisionKind.SelectCards:
                DecisionTitle = dec.Purpose switch
                {
                    CardSelectionPurpose.ExchangeGive => "カード交換",
                    CardSelectionPurpose.SevenPass => "7渡し",
                    CardSelectionPurpose.TenDiscard => "10捨て",
                    _ => "カード選択",
                };
                string reason = dec.Purpose switch
                {
                    CardSelectionPurpose.ExchangeGive => "相手に渡すカードを手札から選んでください",
                    CardSelectionPurpose.SevenPass => "次のプレイヤーに渡すカードを手札から選んでください",
                    CardSelectionPurpose.TenDiscard => "捨てるカードを手札から選んでください",
                    _ => "カードを手札から選んでください",
                };
                DecisionMessage = reason;
                // 出す操作で選んでいたカードが残っていると数え間違えるため、選択を空にして始める
                foreach (var cardVm in HumanHand)
                {
                    cardVm.IsSelected = false;
                    cardVm.CanPlay = true; // どのカードでも選べる
                }
                CanPlay = false;
                IsDecisionDialogOpen = true;
                IsRevolutionChoice = false;
                IsRankDeclaration = false;
                IsModalDecisionOpen = false;
                IsCardSelectionMode = true;
                UpdateSelectionCount();
                break;

            case DecisionKind.DeclareRanks:
                DecisionTitle = "12ボンバー：数字の宣言";
                string jokerNote = _rules.Joker.JokerCount > 0 ? "（JK でジョーカーも消せます）" : "";
                DecisionMessage = dec.MinCount == dec.MaxCount
                    ? $"全員の手札から捨てさせる数字を {dec.MaxCount} つ選んでください{jokerNote}"
                    : $"全員の手札から捨てさせる数字を {dec.MinCount}〜{dec.MaxCount} つ選んでください{jokerNote}";
                RankOptions.Clear();
                foreach (var rank in RankOptionViewModel.DeclarationOrder)
                {
                    // ジョーカーを使わないルールでは JK ボタンを出さない
                    if (rank == Rank.Joker && _rules.Joker.JokerCount <= 0) continue;
                    RankOptions.Add(new RankOptionViewModel(rank));
                }
                _rankCursor = 0;
                IsDecisionDialogOpen = true;
                IsRevolutionChoice = false;
                IsCardSelectionMode = false;
                IsRankDeclaration = true;
                IsModalDecisionOpen = true;
                UpdateRankCount();
                break;
        }
    }

    // ------------------------------------------------------------------
    // 割り込みの確認
    // ------------------------------------------------------------------

    private DecisionKind _interruptPromptKind;

    /// <summary>
    /// 割り込みの確認を開く。止められる札（<paramref name="stopCards"/>）を選択済みにするので、
    /// そのまま「止める」（Enter）を押せば出せる。別の組み合わせにしたいときは手札で選び直せる。
    /// </summary>
    private void OpenInterruptPrompt(DecisionKind kind, SpecialEffectType? effect, int? flusherId, IReadOnlyList<Card>? stopCards)
    {
        string flusher = flusherId is { } id ? GetPlayerVm(id).Name : "相手";
        if (kind == DecisionKind.SandstormStopChance)
        {
            string target = effect == SpecialEffectType.Ambulance ? "99車（救急車）" : "ろくろ首";
            InterruptTitle = "割り込みチャンス：砂嵐";
            InterruptMessage = $"{flusher} の{target}を、砂嵐で止められます。止めると場が流れて、あなたが親になります。";
            InterruptAcceptText = "砂嵐で止める [Enter]";
        }
        else
        {
            InterruptTitle = "割り込みチャンス：4止め";
            InterruptMessage = $"{flusher} の8切りを、4で止められます。止めると場は流れず、次の人へ続きます。";
            InterruptAcceptText = "4で止める [Enter]";
        }

        foreach (var cardVm in HumanHand)
        {
            cardVm.IsSelected = stopCards is not null && stopCards.Contains(cardVm.Card);
        }
        _interruptPromptKind = kind;
        IsInterruptPromptOpen = true;
    }

    // ------------------------------------------------------------------
    // カード選択・数字宣言の数え方
    // ------------------------------------------------------------------

    private int _rankCursor;

    /// <summary>カード選択中の選択数表示と、決定できるかを更新する。</summary>
    private void UpdateSelectionCount()
    {
        if (!IsCardSelectionMode || _currentDecision is null) return;
        int count = HumanHand.Count(vm => vm.IsSelected);
        SelectionCountText = FormatCount(count, _currentDecision.MinCount, _currentDecision.MaxCount, "枚");
        CanConfirmDecision = count >= _currentDecision.MinCount && count <= _currentDecision.MaxCount;
    }

    private void UpdateRankCount()
    {
        if (!IsRankDeclaration || _currentDecision is null) return;
        int count = RankOptions.Count(o => o.IsSelected);
        SelectionCountText = FormatCount(count, _currentDecision.MinCount, _currentDecision.MaxCount, "つ");
        CanConfirmDecision = count >= _currentDecision.MinCount && count <= _currentDecision.MaxCount;
        for (int i = 0; i < RankOptions.Count; i++)
        {
            RankOptions[i].IsKeyboardCursor = _rankKeyboardUsed && i == _rankCursor;
        }
    }

    private static string FormatCount(int count, int min, int max, string unit)
        => min == max ? $"{count} / {max} {unit}" : $"{count} {unit}（{min}〜{max} {unit}）";

    private bool _rankKeyboardUsed;

    /// <summary>数字宣言の選択を切り替える。上限に達しているときは、1つだけ選ぶ宣言なら入れ替え、それ以外は選ばない。</summary>
    [RelayCommand]
    private void ToggleRank(RankOptionViewModel? option)
    {
        if (option is null || !IsRankDeclaration || _currentDecision is null) return;
        int max = _currentDecision.MaxCount;
        if (!option.IsSelected && RankOptions.Count(o => o.IsSelected) >= max)
        {
            if (max != 1) return;
            foreach (var o in RankOptions) o.IsSelected = false;
        }
        option.IsSelected = !option.IsSelected;
        _rankCursor = RankOptions.IndexOf(option);
        _audio.Play(SoundEffect.CardSelect);
        UpdateRankCount();
    }

    /// <summary>キーボード：数字宣言のカーソルを動かす。</summary>
    public void MoveRankCursor(int delta)
    {
        if (!IsRankDeclaration || RankOptions.Count == 0) return;
        _rankKeyboardUsed = true;
        _rankCursor = Math.Clamp(_rankCursor + delta, 0, RankOptions.Count - 1);
        UpdateRankCount();
    }

    /// <summary>キーボード：カーソル位置の数字を選ぶ・外す。</summary>
    public void ToggleRankAtCursor()
    {
        if (!IsRankDeclaration || RankOptions.Count == 0) return;
        _rankKeyboardUsed = true;
        ToggleRank(RankOptions[Math.Clamp(_rankCursor, 0, RankOptions.Count - 1)]);
    }

    [RelayCommand]
    private void SubmitRankDeclarationDecision()
    {
        if (!IsRankDeclaration || _currentDecision is null) return;
        var ranks = RankOptions.Where(o => o.IsSelected).Select(o => o.Rank).ToList();
        if (ranks.Count < _currentDecision.MinCount || ranks.Count > _currentDecision.MaxCount)
        {
            UpdateRankCount();
            return;
        }

        if (_client != null)
        {
            IsDecisionDialogOpen = false;
            _ = SendGuestDecisionAsync(_client.DeclareRanksAsync(ranks));
            return;
        }
        if (_engine == null) return;
        var result = _engine.SubmitRankDeclaration(_myPlayerId, ranks);
        if (!result.IsAccepted && !result.IsDeferred)
        {
            DecisionMessage = $"その宣言はできません（{result.Error}）";
            return;
        }
        IsDecisionDialogOpen = false;
    }

    [RelayCommand]
    private void SubmitRevolutionDecision(bool accept)
    {
        if (_client != null)
        {
            IsDecisionDialogOpen = false;
            _ = SendGuestDecisionAsync(_client.ChooseRevolutionAsync(accept));
            return;
        }
        if (_engine == null) return;
        IsDecisionDialogOpen = false;
        _engine.SubmitRevolutionChoice(_myPlayerId, accept);
    }

    /// <summary>ゲストの判断をホストへ送る。拒否されたらダイアログを開き直して選び直してもらう。</summary>
    private async Task SendGuestDecisionAsync(Task<CommandResponse> sending)
    {
        _guestDecisionInFlight = true;
        var decision = _currentDecision;
        try
        {
            var response = await sending;
            if (response.Error is not (CommandError.None or CommandError.Deferred))
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (_isShutDown) return;
                    _guestDecisionInFlight = false;
                    if (decision is not null)
                    {
                        HandleDecisionRequested(decision);
                        DecisionMessage = $"選択が受け付けられませんでした（{response.Error}）";
                    }
                });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_isShutDown) return;
                _guestDecisionInFlight = false;
                StatusMessage = $"選択を送れませんでした: {ex.Message}";
            });
        }
    }

    [RelayCommand]
    private void SubmitCardSelectionDecision()
    {
        if ((_engine == null && _client == null) || _currentDecision is null) return;
        var selected = HumanHand.Where(vm => vm.IsSelected).Select(vm => vm.Card).ToList();

        // 枚数が合わないまま閉じるとエンジンに拒否されて進めなくなるため、送信前に確認する
        if (selected.Count < _currentDecision.MinCount || selected.Count > _currentDecision.MaxCount)
        {
            UpdateSelectionCount(); // 決定ボタンは押せないまま、枚数表示で不足を示す
            return;
        }

        if (_client != null)
        {
            IsDecisionDialogOpen = false;
            _ = SendGuestDecisionAsync(_client.SelectCardsAsync(selected));
            return;
        }

        var result = _engine!.SubmitCardSelection(_myPlayerId, selected);
        if (!result.IsAccepted && !result.IsDeferred)
        {
            DecisionMessage = $"そのカードは選べません（{result.Error}）";
            return;
        }
        IsDecisionDialogOpen = false;
        UpdateHumanHandVisuals();
    }

    private void HandleRoundEnded(RoundResult result)
    {
        RoundInfoText = FormatRoundInfo(_engine?.RoundNumber ?? 0, _rules.Progress.RoundCount) + "終了";
        var lines = result.Standings.Select(s => $"{s.Position}位: {GetPlayerVm(s.PlayerId).Name} ({s.Rank}) -> 得点: {s.Points}pt");
        RoundResultText = string.Join("\n", lines);
        IsRoundResultOpen = true;
        UpdateRoundResultActions();
        PlayRoundEndSound(result);

        if (result.MiyakoOchiPlayerId.HasValue)
        {
            var pName = GetPlayerVm(result.MiyakoOchiPlayerId.Value).Name;
            CutInTriggered?.Invoke(this, new CutInEventArgs(
                "都落ち",
                "CAPITAL FALL",
                $"{pName} が都落ちにより大貧民へ転落しました",
                "#9400D3",
                IconGlyphs.ChevronDown));
        }
        else if (result.IsGekokujo)
        {
            CutInTriggered?.Invoke(this, new CutInEventArgs(
                "下剋上",
                "GEKOKUJO",
                "大貧民が大富豪へと這い上がりました",
                "#FF4500",
                IconGlyphs.ChevronUp));
        }
    }

    /// <param name="effectType">発動した効果。</param>
    /// <param name="playerId">発動させたプレイヤー。</param>
    /// <param name="elevenBackOn">11バックの効果のとき、反転が掛かったか（true）解除されたか（false）。分からなければ null。</param>
    private void TriggerCutInForEffect(SpecialEffectType effectType, int playerId, bool? elevenBackOn)
    {
        var playerName = GetPlayerVm(playerId).Name;
        switch (effectType)
        {
            case SpecialEffectType.Revolution:
            case SpecialEffectType.SequenceRevolution:
            case SpecialEffectType.Coup:
            case SpecialEffectType.Omen:
            case SpecialEffectType.Nanasan:
                string revTitle = effectType switch
                {
                    SpecialEffectType.Coup => "クーデター（9×3）",
                    SpecialEffectType.Omen => "オーメン（6×3）",
                    SpecialEffectType.Nanasan => "ナナサン革命（7×3）",
                    SpecialEffectType.SequenceRevolution => "階段革命",
                    _ => "革命"
                };
                SweepTriggered?.Invoke(this, SweepKind.AuroraSweep);
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    revTitle,
                    "REVOLUTION",
                    $"{playerName} により革命が成立！ 強弱が反転しました",
                    "#FF4500",
                    IconGlyphs.Switch));
                break;

            case SpecialEffectType.EightCut:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "8切り",
                    "EIGHT FLUSH",
                    $"{playerName} の8切りにより場が流れました",
                    "#FFD700",
                    IconGlyphs.LightningBolt));
                break;

            case SpecialEffectType.ElevenBack:
            case SpecialEffectType.StrongElevenBack:
                bool strong = effectType == SpecialEffectType.StrongElevenBack;
                string backDetail = elevenBackOn switch
                {
                    false => $"{playerName} のJで反転が解除され、元の強弱に戻りました",
                    _ when strong => $"{playerName} により強弱が反転（場が流れても、次のJが出るまで続きます）",
                    _ => $"{playerName} により、場が流れるまで強弱が反転します",
                };
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    elevenBackOn == false ? "11バック解除" : (strong ? "強化11バック" : "11バック"),
                    strong ? "STRONG ELEVEN BACK" : "ELEVEN BACK",
                    backDetail,
                    "#00E5FF",
                    IconGlyphs.RepeatAll));
                break;

            case SpecialEffectType.SpadeThreeReturn:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "スペ3返し",
                    "SPADE 3 COUNTER",
                    $"{playerName} が単体ジョーカーを撃破しました！",
                    "#BA55D3",
                    IconGlyphs.Undo));
                break;

            case SpecialEffectType.Emperor:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "エンペラー",
                    "EMPEROR",
                    $"{playerName} が4スート連番の最強役を成立させました",
                    "#FFD700",
                    IconGlyphs.Star));
                break;

            case SpecialEffectType.Sandstorm:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "砂嵐（3×3）",
                    "SANDSTORM",
                    $"{playerName} の砂嵐により場が流れました",
                    "#DAA520",
                    IconGlyphs.Cloud));
                break;

            case SpecialEffectType.Ambulance:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "99車（9×2）",
                    "AMBULANCE",
                    $"{playerName} の99車により場が流れました",
                    "#FF6347",
                    IconGlyphs.Car));
                break;

            case SpecialEffectType.Rokurokubi:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "ろくろ首（6×2）",
                    "ROKUROKUBI",
                    $"{playerName} のろくろ首により場が流れました",
                    "#9370DB",
                    IconGlyphs.LightningBolt));
                break;

            case SpecialEffectType.TwelveBomber:
                string bomberSummary = BuildBomberSummary(playerId);
                NoticeMessage = bomberSummary;
                SweepTriggered?.Invoke(this, SweepKind.AuroraSweep);
                if (_bomberRanks.Count > 0)
                {
                    // 宣言された数字を、弱い順に1つずつ画面中央で消していく（文字だけのカットインの代わり）
                    BomberVanishTriggered?.Invoke(this, new BomberVanishEventArgs(
                        playerName,
                        _bomberRanks.Select(r => new BomberVanishItem(
                            r, RankOptionViewModel.LabelOf(r), _bomberRankCounts.TryGetValue(r, out int hit) ? hit : 0)).ToList()));
                }
                else
                {
                    // 宣言の通知を受け取れなかった場合（途中参加など）は従来のカットインで知らせる
                    CutInTriggered?.Invoke(this, new CutInEventArgs(
                        "12ボンバー",
                        "TWELVE BOMBER",
                        bomberSummary,
                        "#FF1493",
                        IconGlyphs.Warning));
                }
                break;

            case SpecialEffectType.SevenPass:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "7渡し",
                    "SEVEN PASS",
                    $"{playerName} が次のプレイヤーへカードを渡します",
                    "#20B2AA",
                    IconGlyphs.Send));
                break;

            case SpecialEffectType.TenDiscard:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "10捨て",
                    "TEN DISCARD",
                    $"{playerName} が手札を捨てます",
                    "#778899",
                    IconGlyphs.Delete));
                break;

            case SpecialEffectType.SuitLock:
            case SpecialEffectType.NumberLock:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "縛り",
                    "FIELD LOCKED",
                    $"{playerName} により出し札の制限が発生しました",
                    "#FF8C00",
                    IconGlyphs.Lock));
                break;

            case SpecialEffectType.FourStop:
                CutInTriggered?.Invoke(this, new CutInEventArgs(
                    "4止め",
                    "FOUR STOP",
                    $"{playerName} が8切りを阻止しました！",
                    "#32CD32",
                    IconGlyphs.Stop));
                break;
        }
    }

    /// <summary>ラウンド結果画面の決定ボタンの状態（文言・押せるか）を更新する。</summary>
    private void UpdateRoundResultActions()
    {
        if (IsGameOver)
        {
            // ゲストは戦績を保存しないため、戦績画面ではなくタイトルへ戻す
            NextRoundButtonText = IsGuest ? "タイトルに戻る" : "最終戦績を見る";
            CanAdvanceRound = true;
        }
        else if (IsGuest)
        {
            NextRoundButtonText = "ホストの進行を待っています...";
            CanAdvanceRound = false;
        }
        else
        {
            NextRoundButtonText = "次のラウンドへ進む";
            CanAdvanceRound = true;
        }
    }

    /// <summary>
    /// ラウンド結果画面の決定操作（ボタン・Space・Enter）。
    /// 対戦中は次のラウンドへ、ゲーム終了後は戦績画面（ゲストはタイトル）へ進む。
    /// </summary>
    [RelayCommand]
    private async Task StartNextRoundAsync()
    {
        if (!IsRoundResultOpen || !CanAdvanceRound || _isShutDown) return;

        if (IsGameOver)
        {
            IsRoundResultOpen = false;
            if (IsGuest)
            {
                await LeaveToTitleAsync();
                return;
            }
            // 戦績の保存が終わってから遷移する（遷移先で今回の対戦が見えるように）
            try
            {
                await _saveTask;
            }
            catch (Exception ex)
            {
                StatusMessage = $"戦績の保存に失敗しました: {ex.Message}";
            }
            _navigation.NavigateToResult();
            return;
        }

        if (_engine == null) return;
        IsRoundResultOpen = false;
        for (int i = 0; i < 4; i++)
        {
            var p = GetPlayerVm(i);
            p.FinishPlace = null;
            p.IsPassed = false;
        }
        FieldCards.Clear();
        _engine.StartNextRound();
        RoundInfoText = FormatRoundInfo(_engine.RoundNumber, _rules.Progress.RoundCount);
    }

    private void HandleGameEnded(GameResult result)
    {
        IsGameOver = true;
        StatusMessage = "全ラウンドが終了しました！";
        UpdateRoundResultActions();
        if (_engine != null)
        {
            var record = MatchRecord.FromGame(_engine, result, _myPlayerId, _npcProfiles, isNetwork: _hostSession is not null);
            _saveTask = _historyRepo.SaveAsync(record);
        }
    }

    // ------------------------------------------------------------------
    // タイトルへ戻る・後始末
    // ------------------------------------------------------------------

    /// <summary>後始末をしてタイトルへ戻る（確認ダイアログはページ側で出す）。</summary>
    public async Task LeaveToTitleAsync()
    {
        await ShutdownAsync();
        _navigation.NavigateToTitle();
    }

    /// <summary>
    /// 対戦の後始末。何度呼んでも1回だけ実行される（タイトル復帰時と画面離脱時の両方から呼ばれる）。
    /// <list type="bullet">
    /// <item>1人用：NPCのタイマーを止め、NPCコントローラーを破棄し、エンジンの購読を解除する。</item>
    /// <item>ホスト：エンジンの購読を解除し、部屋を閉じる（参加者へ退出を通知し、ポートを解放）。</item>
    /// <item>ゲスト：購読を解除し、ホストへ退出を通知して切断する。</item>
    /// </list>
    /// </summary>
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _isShutDown = true;
        IsDecisionDialogOpen = false;
        IsInterruptPromptOpen = false;
        CanPlay = false;
        CanPass = false;

        _npcScheduler?.StopAll();
        _npcController?.Dispose();
        _npcController = null;

        if (_engine is not null && _engineHandler is not null)
        {
            _engine.EventOccurred -= _engineHandler;
        }
        _engineHandler = null;

        if (_client is not null)
        {
            var client = _client;
            client.ViewUpdated -= OnClientViewUpdated;
            client.Disconnected -= OnClientDisconnected;
            client.NoticeReceived -= OnClientNoticeReceived;
            try
            {
                if (client.IsConnected)
                {
                    await client.LeaveAsync();
                }
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameTable] 退出処理に失敗しました: {ex.Message}");
            }
        }

        if (_hostSession is not null)
        {
            try
            {
                await _hostSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameTable] 部屋を閉じる処理に失敗しました: {ex.Message}");
            }
        }

        // 戦績の保存は止めずに完了させる（ゲーム終了直後に戻った場合でも記録を残す）
        try
        {
            await _saveTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameTable] 戦績の保存に失敗しました: {ex.Message}");
        }
    }

    /// <summary>決定操作（Enter/Space）を判断ダイアログに適用する。処理したら true。</summary>
    public bool TryConfirmDecisionDialog()
    {
        if (!IsDecisionDialogOpen) return false;
        if (IsRevolutionChoice)
        {
            SubmitRevolutionDecision(true);
        }
        else if (IsRankDeclaration)
        {
            SubmitRankDeclarationDecision();
        }
        else
        {
            SubmitCardSelectionDecision();
        }
        return true;
    }

    /// <summary>取り消し操作（Esc）を判断ダイアログに適用する。革命選択なら「起こさない」。処理したら true。</summary>
    public bool TryCancelDecisionDialog()
    {
        if (!IsDecisionDialogOpen || !IsRevolutionChoice) return false;
        SubmitRevolutionDecision(false);
        return true;
    }
}
