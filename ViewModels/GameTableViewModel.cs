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
using DTTS.Services.Database;
using DTTS.Services.Settings;
using Microsoft.UI.Dispatching;

namespace DTTS.ViewModels;

public sealed partial class GameTableViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly SettingsRepository _settingsRepo;
    private readonly MatchHistoryRepository _historyRepo;
    private readonly DispatcherQueue _dispatcherQueue;

    private GameEngine? _engine;
    private NpcController? _npcController;
    private RuleSettings _rules = RuleSettings.CreateDefault();
    private PendingDecision? _currentDecision;
    private Dictionary<int, NpcProfile> _npcProfiles = new();

    // --- プレイヤー ---
    public PlayerViewModel Player0 { get; } = new(0, "あなた", true);
    public PlayerViewModel Player1 { get; } = new(1, "NPC 東", false);
    public PlayerViewModel Player2 { get; } = new(2, "NPC 北", false);
    public PlayerViewModel Player3 { get; } = new(3, "NPC 西", false);

    public PlayerViewModel GetPlayerVm(int id) => id switch
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
    private string _roundInfoText = "第 1 / 5 ラウンド";

    [ObservableProperty]
    private string _statusMessage = "ゲームを開始しています...";

    [ObservableProperty]
    private bool _isRevolution;

    [ObservableProperty]
    private bool _isElevenBack;

    [ObservableProperty]
    private string _lockDescription = "";

    [ObservableProperty]
    private bool _canPlay;

    [ObservableProperty]
    private bool _canPass;

    // --- モーダル・ダイアログ ---
    [ObservableProperty]
    private bool _isDecisionDialogOpen;

    [ObservableProperty]
    private string _decisionTitle = "";

    [ObservableProperty]
    private string _decisionMessage = "";

    [ObservableProperty]
    private bool _isRevolutionChoice;

    [ObservableProperty]
    private bool _isRoundResultOpen;

    [ObservableProperty]
    private string _roundResultText = "";

    [ObservableProperty]
    private bool _isGameOver;

    public GameTableViewModel(
        INavigationService navigation,
        SettingsRepository settingsRepo,
        MatchHistoryRepository historyRepo)
    {
        _navigation = navigation;
        _settingsRepo = settingsRepo;
        _historyRepo = historyRepo;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync()
    {
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
        var scheduler = new DispatcherQueueNpcScheduler(_dispatcherQueue);
        _npcController = new NpcController(_engine, scheduler);

        _npcController.Assign(1, _npcProfiles[1]);
        _npcController.Assign(2, _npcProfiles[2]);
        _npcController.Assign(3, _npcProfiles[3]);

        // イベント購読
        _engine.EventOccurred += (sender, ev) => OnEngineEvent(ev);

        // ゲーム開始
        _engine.StartGame();
        UpdateAllPlayersInfo();
        UpdateHumanHandVisuals();
    }

    private void OnEngineEvent(GameEvent ev)
    {
        _dispatcherQueue.TryEnqueue(() => HandleEngineEvent(ev));
    }

    private void HandleEngineEvent(GameEvent ev)
    {
        if (_engine == null) return;

        switch (ev)
        {
            case DecisionRequestedEvent dr:
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
                StatusMessage = $"{GetPlayerVm(cp.PlayerId).Name} がカードを出しました";
                UpdateAllPlayersInfo();
                break;

            case PlayerPassedEvent pp:
                GetPlayerVm(pp.PlayerId).IsPassed = true;
                StatusMessage = $"{GetPlayerVm(pp.PlayerId).Name} がパスしました";
                break;

            case EffectAppliedEvent ea:
                IsRevolution = ea.Field.IsRevolution;
                IsElevenBack = ea.Field.IsElevenBack;
                UpdateLockState(ea.Field.Lock);
                break;

            case FieldFlushedEvent ff:
                FieldCards.Clear();
                StatusMessage = ff.Cause.HasValue ? $"場が流れました（{ff.Cause.Value}）" : "場が流れました";
                for (int i = 0; i < 4; i++) GetPlayerVm(i).IsPassed = false;
                break;

            case PlayerFinishedEvent pf:
                var pos = _engine.GetPlayer(pf.PlayerId).FinishPosition;
                GetPlayerVm(pf.PlayerId).FinishPlace = pos;
                StatusMessage = $"{GetPlayerVm(pf.PlayerId).Name} が {pos}位 であがりました！";
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
        StatusMessage = isMyTurn ? "あなたのターンです" : $"{GetPlayerVm(activePlayerId).Name} のターン...";
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

        CanPlay = legalPlays.Any(lp => lp.Play != null && lp.Play.Cards.Count == selected.Count && lp.Play.Cards.All(selected.Contains));

        bool isMyTurn = (_engine.CurrentPlayerId == 0);
        foreach (var cardVm in HumanHand)
        {
            cardVm.CanPlay = !isMyTurn || legalPlays.Any(lp => lp.Play != null && lp.Play.Cards.Contains(cardVm.Card));
        }
    }

    [RelayCommand]
    private void ToggleCardSelection(CardViewModel cardVm)
    {
        cardVm.IsSelected = !cardVm.IsSelected;
        UpdateHumanHandVisuals();
    }

    [RelayCommand]
    private void PlaySelectedCards()
    {
        if (_engine == null) return;

        var selected = HumanHand.Where(vm => vm.IsSelected).Select(vm => vm.Card).ToList();
        if (selected.Count == 0) return;

        var result = _engine.SubmitPlay(0, selected);
        if (result.IsAccepted)
        {
            HumanHand.Clear();
            UpdateHumanHandVisuals();
        }
    }

    [RelayCommand]
    private void Pass()
    {
        if (_engine == null) return;
        _engine.SubmitPass(0);
        Player0.IsPassed = true;
    }

    private void HandleDecisionRequested(PendingDecision dec)
    {
        if (dec.PlayerId != 0) return;

        _currentDecision = dec;

        switch (dec.Kind)
        {
            case DecisionKind.RevolutionChoice:
                DecisionTitle = "革命の選択";
                DecisionMessage = "革命を起こしますか？";
                IsRevolutionChoice = true;
                IsDecisionDialogOpen = true;
                break;

            case DecisionKind.SelectCards:
                DecisionTitle = "カード選択";
                string reason = dec.Purpose switch
                {
                    CardSelectionPurpose.ExchangeGive => "カード交換に出すカードを選んでください",
                    CardSelectionPurpose.SevenPass => "次のプレイヤーに渡すカードを選んでください",
                    CardSelectionPurpose.TenDiscard => "捨てるカードを選んでください",
                    _ => "カードを選んでください"
                };
                DecisionMessage = $"{reason}（{dec.MinCount}〜{dec.MaxCount}枚）";
                IsRevolutionChoice = false;
                IsDecisionDialogOpen = true;
                break;
        }
    }

    [RelayCommand]
    private void SubmitRevolutionDecision(bool accept)
    {
        if (_engine == null) return;
        IsDecisionDialogOpen = false;
        _engine.SubmitRevolutionChoice(0, accept);
    }

    [RelayCommand]
    private void SubmitCardSelectionDecision()
    {
        if (_engine == null) return;
        var selected = HumanHand.Where(vm => vm.IsSelected).Select(vm => vm.Card).ToList();
        IsDecisionDialogOpen = false;
        _engine.SubmitCardSelection(0, selected);
        UpdateHumanHandVisuals();
    }

    private void HandleRoundEnded(RoundResult result)
    {
        RoundInfoText = $"第 {_engine?.RoundNumber} / 5 ラウンド終了";
        var lines = result.Standings.Select(s => $"{s.Position}位: {GetPlayerVm(s.PlayerId).Name} ({s.Rank}) -> 得点: {s.Points}pt");
        RoundResultText = string.Join("\n", lines);
        IsRoundResultOpen = true;
    }

    [RelayCommand]
    private void StartNextRound()
    {
        IsRoundResultOpen = false;
        if (_engine == null) return;

        if (IsGameOver)
        {
            ReturnToTitle();
            return;
        }

        // 次のラウンドへ進む
        for (int i = 0; i < 4; i++)
        {
            var p = GetPlayerVm(i);
            p.FinishPlace = null;
            p.IsPassed = false;
        }
        FieldCards.Clear();
        _engine.StartNextRound();
        RoundInfoText = $"第 {_engine.RoundNumber} / 5 ラウンド";
    }

    private void HandleGameEnded(GameResult result)
    {
        IsGameOver = true;
        StatusMessage = "全ラウンドが終了しました！";
        if (_engine != null)
        {
            var record = MatchRecord.FromGame(_engine, result, 0, _npcProfiles, isNetwork: false);
            _ = _historyRepo.SaveAsync(record);
        }
    }

    [RelayCommand]
    private void ReturnToTitle()
    {
        _navigation.NavigateToTitle();
    }
}
