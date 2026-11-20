using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Database;
using DTTS.Services.Feedback;
using DTTS.Services.Network;
using DTTS.Services.Settings;
using DTTS.Services.Skin;

internal static class ServicesTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(services): " + name); }
    }

    private static readonly string ScratchRoot = Path.Combine(Path.GetTempPath(), "DTTS_Tests_" + Environment.ProcessId);

    private static string TempDir()
    {
        var dir = Path.GetFullPath(Path.Combine(ScratchRoot, "dtts-test-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<(int Pass, int Fail)> RunAsync()
    {
        try
        {
            await DatabaseTests();
            await FeedbackTests();
            SkinTests();
            ProtocolTests();
            int loops = int.TryParse(Environment.GetEnvironmentVariable("DTTS_NET_LOOPS"), out var n) ? n : 1;
            for (int i = 0; i < loops; i++)
            {
                await NetworkGameTests();
            }
            Console.WriteLine($"  network section x{loops}");
            return (_pass, _fail);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(ScratchRoot))
            {
                try { Directory.Delete(ScratchRoot, recursive: true); } catch { }
            }
        }
    }

    // ==================================================================
    // Database
    // ==================================================================

    private static GameEngine PlayQuickGame(int seed, out GameResult result, Action<RuleSettings>? configure = null)
    {
        var rules = RuleSettings.CreateDefault();
        rules.Progress.RoundCount = 2;
        configure?.Invoke(rules);
        var engine = new GameEngine(rules, Enumerable.Range(0, 4).Select(i => new PlayerSetup("P" + i, PlayerKind.Npc)).ToList(), new SystemRandomSource(seed));
        using var npc = new NpcController(engine);
        for (int i = 0; i < 4; i++) npc.Assign(i, NpcProfile.Default, new SystemRandomSource(seed + i));
        GameResult? r = null;
        engine.EventOccurred += (_, e) =>
        {
            if (e is RoundEndedEvent re && !re.IsGameOver) engine.StartNextRound();
            if (e is GameEndedEvent ge) r = ge.Result;
        };
        engine.StartGame();
        result = r!;
        return engine;
    }

    private static async Task DatabaseTests()
    {
        var dir = TempDir();
        var db = new AppDatabaseContext(Path.Combine(dir, "sub", "dtts.db"));
        await db.InitializeAsync();
        await db.InitializeAsync(); // 2回目は何もしない
        Check(File.Exists(db.DatabasePath), "db created");

        var settings = new SettingsRepository(db);
        var rules = await settings.LoadRulesAsync();
        Check(rules.Flow.EightCutEnabled, "default rules when empty");
        rules.Flow.SandstormEnabled = true;
        rules.Joker.JokerCount = 2;
        await settings.SaveRulesAsync(rules);
        var loaded = await settings.LoadRulesAsync();
        Check(loaded.Flow.SandstormEnabled && loaded.Joker.JokerCount == 2, "rules roundtrip");
        await settings.SetAsync(SettingsRepository.RulesKey, "{broken");
        Check((await settings.LoadRulesAsync()).Joker.JokerCount == 1, "broken rules -> default");

        await settings.SaveAppearanceAsync(new AppearanceSettings { ThemeColor = ThemeColor.HotPink, SkinPackId = "neo" });
        var app = await settings.LoadAppearanceAsync();
        Check(app.ThemeColor == ThemeColor.HotPink && app.SkinPackId == "neo", "appearance roundtrip");
        await settings.SetAsync(SettingsRepository.AppearanceKey, "{\"themeColor\":\"NoSuchColor\"}");
        Check((await settings.LoadAppearanceAsync()).ThemeColor == ThemeColor.DeepAzure, "bad enum -> default");

        // サウンド設定
        var defaults = await settings.LoadAppearanceAsync();
        Check(defaults.SoundEnabled && defaults.SeVolume == AppearanceSettings.DefaultSeVolume && defaults.BgmVolume == AppearanceSettings.DefaultBgmVolume, "sound defaults when missing");
        await settings.SaveAppearanceAsync(new AppearanceSettings { SoundEnabled = false, SeVolume = 35, BgmVolume = 0 });
        var snd = await settings.LoadAppearanceAsync();
        Check(!snd.SoundEnabled && snd.SeVolume == 35 && snd.BgmVolume == 0, "sound settings roundtrip");
        await settings.SetAsync(SettingsRepository.AppearanceKey, "{\"seVolume\":250,\"bgmVolume\":-5}");
        var clamped = await settings.LoadAppearanceAsync();
        Check(clamped.SeVolume == 100 && clamped.BgmVolume == 0, "volume clamped");
        Check(AppearanceSettings.ToUnitVolume(80) == 0.8 && AppearanceSettings.ToUnitVolume(999) == 1.0, "unit volume");
        Check(DTTS.Services.Audio.SoundEffectCatalog.ToGain(0.5) == 0.25 && DTTS.Services.Audio.SoundEffectCatalog.ToGain(double.NaN) == 0.0
            && DTTS.Services.Audio.SoundEffectCatalog.ToGain(2.0) == 1.0, "gain curve");

        // 起動時の無音指定（--mute / DTTS_MUTE）
        Check(DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(new[] { "DTTS.exe", "--mute" }, null), "mute: --mute argument");
        Check(DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(new[] { "DTTS.exe", "--MUTE" }, null), "mute: argument is case-insensitive");
        Check(DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(Array.Empty<string>(), "1") && DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(null, "true")
            && DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(null, " Yes "), "mute: environment variable values");
        Check(!DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(new[] { "DTTS.exe" }, null) && !DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(null, "0")
            && !DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequested(new[] { "--muted" }, "false"), "mute: not requested");
        Check(DTTS.Services.Audio.AudioLaunchOptions.IsMuteRequestedForCurrentProcess(), "mute: test runner itself runs muted");

        // 全効果音のファイルが実在し、44.1kHz/16bit の PCM であること
        var audioDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets", "Audio"));
        foreach (var effect in Enum.GetValues<DTTS.Services.Audio.SoundEffect>())
        {
            var path = Path.Combine(audioDir, DTTS.Services.Audio.SoundEffectCatalog.FileName(effect));
            bool ok = File.Exists(path);
            if (ok)
            {
                var header = File.ReadAllBytes(path).AsSpan(0, 44).ToArray();
                ok = Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WAVE"
                    && BitConverter.ToInt16(header, 20) == 1 && BitConverter.ToInt32(header, 24) == 44100 && BitConverter.ToInt16(header, 34) == 16;
            }
            Check(ok, $"sound file {effect}: {path}");
            Check(DTTS.Services.Audio.SoundEffectCatalog.Polyphony(effect) >= 1, $"polyphony {effect}");
        }

        // 進行スピード（外見設定と一緒に保存する）
        await settings.DeleteAsync(SettingsRepository.AppearanceKey);
        Check((await settings.LoadAppearanceAsync()).GameSpeed == GameSpeed.Normal, "game speed defaults to normal");
        await settings.SaveAppearanceAsync(new AppearanceSettings { GameSpeed = GameSpeed.Slow, SeVolume = 40 });
        Check((await settings.LoadAppearanceAsync()).GameSpeed == GameSpeed.Slow, "game speed roundtrip");
        await settings.SetAsync(SettingsRepository.AppearanceKey, "{\"gameSpeed\":\"Warp\"}");
        Check((await settings.LoadAppearanceAsync()).GameSpeed == GameSpeed.Normal, "unknown game speed -> normal");
        await settings.SetAsync(SettingsRepository.AppearanceKey, "{\"seVolume\":40,\"soundEnabled\":false}");
        Check((await settings.LoadAppearanceAsync()).GameSpeed == GameSpeed.Normal, "older settings without game speed -> normal");

        // 操作ガイド・確認レベル（既定は中級者用。持っていないのに確認が出ることはない）
        Check((await settings.LoadAppearanceAsync()).GuideLevel == GuideLevel.Intermediate, "guide level: older settings -> intermediate");
        Check(GuideLevelInfo.All.All(l => l.AlwaysAsksInterrupt()), "guide level: a human is asked without a stopper at every level");
        foreach (var level in GuideLevelInfo.All)
        {
            await settings.UpdateAppearanceAsync(a => a.GuideLevel = level);
            Check((await settings.LoadAppearanceAsync()).GuideLevel == level, "guide level roundtrip: " + level);
        }
        await settings.SetAsync(SettingsRepository.AppearanceKey, "{\"guideLevel\":\"Tutorial\"}");
        Check((await settings.LoadAppearanceAsync()).GuideLevel == GuideLevel.Intermediate, "guide level: unknown value -> intermediate");
        var badGuide = await settings.UpdateAppearanceAsync(a => a.GuideLevel = (GuideLevel)77);
        Check(badGuide.GuideLevel == GuideLevel.Intermediate, "guide level: out-of-range value normalized");
        await settings.DeleteAsync(SettingsRepository.AppearanceKey);

        // 一部だけの更新：サウンドと進行スピードを同時に切り替えても互いの変更を消さない
        await settings.SaveAppearanceAsync(new AppearanceSettings { ThemeColor = ThemeColor.HotPink, SoundEnabled = true, GameSpeed = GameSpeed.Normal });
        var updates = new List<Task<AppearanceSettings>>();
        for (int i = 0; i < 12; i++)
        {
            bool sound = i % 2 == 1;
            var speed = (GameSpeed)(i % 4);
            updates.Add(settings.UpdateAppearanceAsync(a => a.SoundEnabled = sound));
            updates.Add(settings.UpdateAppearanceAsync(a => a.GameSpeed = speed));
        }
        await Task.WhenAll(updates);
        var merged = await settings.LoadAppearanceAsync();
        Check(merged.SoundEnabled && merged.GameSpeed == GameSpeed.VeryFast && merged.ThemeColor == ThemeColor.HotPink,
            $"partial appearance updates do not overwrite each other: sound={merged.SoundEnabled} speed={merged.GameSpeed}");
        var bad = await settings.UpdateAppearanceAsync(a => a.GameSpeed = (GameSpeed)99);
        Check(bad.GameSpeed == GameSpeed.Normal, "partial update normalizes");

        // マイプリセット（名前を付けて保存したルール構成）
        var presets = await settings.LoadRulePresetsAsync();
        Check(presets.Presets.Count == 0, "no custom presets at first");
        var mine = RuleSettings.CreateDefault();
        mine.Sequence.MinLength = 2;
        mine.Lock.TriggerCount = 2;
        mine.Special.TwelveBomberEnabled = true;
        var t0 = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        Check(presets.Upsert("  うちのルール  ", mine, t0)?.Name == "うちのルール", "preset name is trimmed");
        Check(presets.Upsert("全部盛り改", RulePresets.Create(RulePreset.Expert), t0.AddMinutes(1)) is not null, "second preset");
        Check(presets.Upsert("   ", mine, t0) is null && presets.Upsert(null, mine, t0) is null, "blank preset name rejected");
        Check(presets.Presets.Select(x => x.Name).SequenceEqual(new[] { "全部盛り改", "うちのルール" }), "newest preset first");
        await settings.SaveRulePresetsAsync(presets);
        var reloaded = await settings.LoadRulePresetsAsync();
        Check(reloaded.Presets.Count == 2 && reloaded.Presets[1].SavedAt == t0, "presets roundtrip");
        var restored = reloaded.Presets[1].ToRules();
        Check(restored is not null && restored.Sequence.MinLength == 2 && restored.Lock.TriggerCount == 2
            && restored.Special.TwelveBomberEnabled && restored.ToJson() == mine.ToJson(), "preset restores the exact rules");
        Check(reloaded.Presets[0].ToRules()!.Flow.SandstormEnabled, "second preset restores its own rules");
        // 同じ名前は上書きして先頭へ
        mine.Flow.FourStopEnabled = true;
        reloaded.Upsert("うちのルール", mine, t0.AddHours(1));
        Check(reloaded.Presets.Count == 2 && reloaded.Presets[0].Name == "うちのルール" && reloaded.Presets[0].ToRules()!.Flow.FourStopEnabled,
            "same name overwrites");
        Check(reloaded.Remove("全部盛り改") && !reloaded.Remove("全部盛り改") && reloaded.Presets.Count == 1, "preset removed");
        await settings.SaveRulePresetsAsync(reloaded);
        Check((await settings.LoadRulePresetsAsync()).Presets.Single().Name == "うちのルール", "removal persisted");
        // 保存したプリセットを読み込んでも、元の設定オブジェクトとは独立している
        var copy = reloaded.Presets[0].ToRules()!;
        copy.Flow.EightCutEnabled = false;
        Check(reloaded.Presets[0].ToRules()!.Flow.EightCutEnabled, "loaded preset is a copy");
        // 名前の長さ・件数の上限
        string longName = new string('あ', 60);
        Check(CustomRulePresetList.SanitizeName(longName)!.Length == CustomRulePresetList.MaxNameLength, "long preset name is cut");
        Check(CustomRulePresetList.SanitizeName("a\tb\nc") == "abc", "control characters removed from the name");
        var full = new CustomRulePresetList();
        for (int i = 0; i < CustomRulePresetList.MaxCount; i++) full.Upsert("p" + i, mine, t0);
        Check(full.Presets.Count == CustomRulePresetList.MaxCount && full.Upsert("one more", mine, t0) is null, "preset count is capped");
        Check(full.Upsert("p3", mine, t0) is not null && full.Presets.Count == CustomRulePresetList.MaxCount, "overwrite allowed at the cap");
        // 破損データ：壊れた JSON 全体は空に、壊れた項目・重複は取り除く
        await settings.SetAsync(SettingsRepository.RulePresetsKey, "{broken");
        Check((await settings.LoadRulePresetsAsync()).Presets.Count == 0, "broken presets -> empty");
        await settings.SetAsync(SettingsRepository.RulePresetsKey,
            "{\"presets\":[{\"name\":\"ok\",\"rulesJson\":\"{}\"},{\"name\":\"ok\",\"rulesJson\":\"{}\"},{\"name\":\"bad\",\"rulesJson\":\"{broken\"},{\"name\":\" \",\"rulesJson\":\"{}\"},null]}");
        var cleaned = await settings.LoadRulePresetsAsync();
        Check(cleaned.Presets.Count == 1 && cleaned.Presets[0].Name == "ok" && cleaned.Presets[0].ToRules()!.Flow.EightCutEnabled,
            "invalid and duplicate presets dropped: " + cleaned.Presets.Count);
        await settings.DeleteAsync(SettingsRepository.AppearanceKey);

        var npc = await settings.LoadNpcAsync();
        Check(npc.Seats.Count == 3, "npc default seats");
        npc.Seats[1].Difficulty = NpcDifficulty.Hard;
        npc.PlayerName = "  たろう ";
        await settings.SaveNpcAsync(npc);
        var npc2 = await settings.LoadNpcAsync();
        Check(npc2.Seats[1].Difficulty == NpcDifficulty.Hard && npc2.PlayerName == "たろう", "npc roundtrip+normalize");
        Check(ThemeColorInfo.All.Count == 8 && ThemeColorInfo.All.Select(t => t.Color).Distinct().Count() == 8, "8 theme colors");

        // 戦績
        var history = new MatchHistoryRepository(db);
        Check((await history.GetLocalHumanStatisticsAsync()).TotalGames == 0, "empty stats");
        int wins = 0;
        for (int g = 0; g < 5; g++)
        {
            var engine = PlayQuickGame(g, out var result);
            var record = MatchRecord.FromGame(engine, result, localHumanPlayerId: 0,
                npcProfiles: new Dictionary<int, NpcProfile> { [1] = NpcProfile.Default });
            var id = await history.SaveAsync(record);
            Check(id > 0, "saved id");
            if (result.Standings.First(s => s.PlayerId == 0).Place == 1) wins++;
        }
        var recent = await history.GetRecentAsync(3);
        Check(recent.Count == 3 && recent.All(r => r.Players.Count == 4 && r.Rounds.Count >= 8), "recent with players/rounds");
        Check(recent[0].LocalHuman is { PlayerId: 0 } && recent[0].Players[1].NpcProfile == NpcProfile.Default.DisplayName, "human/profile flags");
        Check(recent[0].Rounds.All(r => Enum.IsDefined(r.Rank)), "round ranks parsed");
        var stats = await history.GetLocalHumanStatisticsAsync();
        Check(stats.TotalGames == 5 && stats.Wins == wins && stats.TotalRounds >= 10 && stats.RankCounts.Values.Sum() == stats.TotalRounds, $"stats games={stats.TotalGames} wins={stats.Wins}/{wins}");
        Check(stats.AveragePlace >= 1 && stats.AveragePlace <= 4, "avg place");
        await history.ClearAsync();
        Check((await history.GetRecentAsync()).Count == 0, "cleared (cascade)");

        // 得点配分「2・1・-1・-2」：マイナスの点数・合計点もそのまま保存・集計される
        var humanTotals = new List<int>();
        bool sawNegativeRound = false;
        for (int g = 0; g < 6; g++)
        {
            var engine = PlayQuickGame(100 + g, out var result, r =>
            {
                r.Progress.RoundCount = 3;
                r.Progress.PointsDaifugo = 2; r.Progress.PointsFugo = 1; r.Progress.PointsHeimin = 0;
                r.Progress.PointsHinmin = -1; r.Progress.PointsDaihinmin = -2;
            });
            Check(result.Rounds.All(rr => rr.Standings.Select(s => s.Points).OrderByDescending(p => p).SequenceEqual(new[] { 2, 1, -1, -2 })),
                "minus points: each round awards 2/1/-1/-2");
            Check(result.Standings.All(s => s.TotalScore == result.Rounds.Sum(rr => rr.Standings.First(x => x.PlayerId == s.PlayerId).Points)),
                "minus points: totals are the sum of round points");
            sawNegativeRound |= result.Standings.Any(s => s.TotalScore < 0);
            humanTotals.Add(result.Standings.First(s => s.PlayerId == 0).TotalScore);
            await history.SaveAsync(MatchRecord.FromGame(engine, result, localHumanPlayerId: 0,
                npcProfiles: new Dictionary<int, NpcProfile>()));
        }
        Check(sawNegativeRound, "minus points: some player finished below zero");
        var minusRecent = await history.GetRecentAsync(10);
        Check(minusRecent.Count == 6 && minusRecent.SelectMany(m => m.Rounds).Any(r => r.Points < 0)
            && minusRecent.Select(m => m.LocalHuman!.TotalScore).OrderBy(x => x).SequenceEqual(humanTotals.OrderBy(x => x)),
            "minus points: negative round points and totals round-trip through the database");
        var minusStats = await history.GetLocalHumanStatisticsAsync();
        Check(minusStats.TotalGames == 6 && minusStats.BestScore == humanTotals.Max(), $"minus points: best score {minusStats.BestScore} == {humanTotals.Max()}");
        await history.ClearAsync();

        SqliteCleanup();
        TryDelete(dir);
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ==================================================================
    // Feedback
    // ==================================================================

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Throw;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource? Entered;
        public readonly ConcurrentQueue<string> Bodies = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw) throw new HttpRequestException("network down");
            if (Gate is { } gate)
            {
                Entered?.TrySetResult();
                await gate.Task;
            }
            Bodies.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class ToggleMonitor : IConnectivityMonitor
    {
        public bool IsOnline { get; set; }
        public event EventHandler? ConnectivityChanged;
        public void Set(bool online) { IsOnline = online; ConnectivityChanged?.Invoke(this, EventArgs.Empty); }
    }

    private static async Task FeedbackTests()
    {
        var dir = TempDir();
        var db = new AppDatabaseContext(Path.Combine(dir, "fb.db"));
        await db.InitializeAsync();
        var repo = new FeedbackRepository(db);
        var options = new FeedbackOptions { WebhookUrl = null, AutoSend = false };
        var handler = new StubHandler();
        var monitor = new ToggleMonitor { IsOnline = false };
        using var service = new FeedbackService(repo, () => options, new HttpClient(handler), monitor, "1.0.0");

        await AssertThrows<ArgumentException>(() => service.SubmitAsync("   ", FeedbackCategory.Bug), "empty message rejected");
        await AssertThrows<ArgumentException>(() => service.SubmitAsync(new string('a', 2001), FeedbackCategory.Bug), "long message rejected");

        await service.SubmitAsync("カードが重なる", FeedbackCategory.Bug);
        await service.SubmitAsync("もっとNPCを", FeedbackCategory.Request);
        Check((await repo.GetPendingAsync()).Count == 2, "stored offline");
        Check((await service.SendPendingAsync()).Skipped is not null, "skip: no url");

        options.WebhookUrl = "http://example.com/hook";
        Check(!FeedbackService.TryGetWebhookUri(options.WebhookUrl, out _), "http non-loopback rejected");
        Check(FeedbackService.TryGetWebhookUri("https://script.google.com/macros/s/x/exec", out _), "https ok");
        Check(FeedbackService.TryGetWebhookUri("http://localhost:8080/x", out _), "localhost ok");
        options.WebhookUrl = "https://script.google.com/macros/s/x/exec";
        Check((await service.SendPendingAsync()).Skipped == "オフラインです", "skip offline");

        monitor.IsOnline = true;
        handler.Status = HttpStatusCode.InternalServerError;
        var r1 = await service.SendPendingAsync();
        Check(r1.Failed == 2 && (await repo.GetPendingAsync()).All(e => e.Attempts == 1 && e.LastError == "HTTP 500"), "failure recorded, still pending");

        handler.Status = HttpStatusCode.OK;
        var r2 = await service.SendPendingAsync();
        Check(r2.Sent == 2 && (await repo.GetPendingAsync()).Count == 0, "sent");
        var body = handler.Bodies.Last();
        using (var doc = JsonDocument.Parse(body))
        {
            Check(doc.RootElement.GetProperty("message").GetString() == "もっとNPCを" && doc.RootElement.GetProperty("category").GetString() == "Request", "payload json");
            Check(!body.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && !body.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "no user/machine name in payload");
        }

        // オンライン復帰で自動送信
        options.AutoSend = true;
        monitor.Set(false);
        var completed = new TaskCompletionSource<FeedbackSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SendCompleted += (_, res) => { if (res.Sent > 0) completed.TrySetResult(res); };
        await service.SubmitAsync("オフライン中の投稿", FeedbackCategory.Comment);
        Check((await repo.GetPendingAsync()).Count == 1, "queued while offline");
        monitor.Set(true);
        var done = await Task.WhenAny(completed.Task, Task.Delay(5000));
        Check(done == completed.Task && (await repo.GetPendingAsync()).Count == 0, "auto send on reconnect");

        // 送信中に届いた投稿も、その送信の続きで送られる
        handler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        options.AutoSend = false;
        await service.SubmitAsync("1件目", FeedbackCategory.Comment);
        var inflight = service.SendPendingAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options.AutoSend = true;
        await service.SubmitAsync("送信中に投稿", FeedbackCategory.Comment); // 自動送信は「送信中」で弾かれる
        handler.Gate.SetResult();
        handler.Gate = null;
        var inflightResult = await inflight;
        Check(inflightResult.Sent == 2 && (await repo.GetPendingAsync()).Count == 0, $"rerun after in-flight send: sent={inflightResult.Sent}");
        options.AutoSend = false;

        // 通信例外では残りを次回に回す
        handler.Throw = true;
        options.AutoSend = false;
        await service.SubmitAsync("a", FeedbackCategory.Comment);
        await service.SubmitAsync("b", FeedbackCategory.Comment);
        var r3 = await service.SendPendingAsync();
        Check(r3.Failed == 1 && (await repo.GetPendingAsync()).Count == 2, "stop after network exception");

        using var ms = new MemoryStream();
        await service.ExportAsync(ms);
        using (var doc = JsonDocument.Parse(ms.ToArray()))
        {
            Check(doc.RootElement.GetProperty("entries").GetArrayLength() == 7, "export all user entries");
        }
        Check((await service.GetHistoryAsync()).Count == 7, "history");

        SqliteCleanup();
        TryDelete(dir);
    }

    private static async Task AssertThrows<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); Check(false, name); }
        catch (T) { Check(true, name); }
    }

    // ==================================================================
    // Skin
    // ==================================================================

    private static void SkinTests()
    {
        var root = TempDir();
        var pack = Path.Combine(root, "neon");
        Directory.CreateDirectory(pack);
        foreach (var f in new[] { "S3.png", "h10.webp", "DQ.JPG", "CA.jpeg", "JK.png", "back.png", "readme.txt", "zz.png" })
        {
            File.WriteAllBytes(Path.Combine(pack, f), new byte[] { 1, 2, 3 });
        }
        File.WriteAllBytes(Path.Combine(pack, "S4.png"), new byte[SkinPackScanner.MaxImageFileBytes + 1]); // 大きすぎ
        File.WriteAllText(Path.Combine(pack, "skin.json"), "{\"name\":\"ネオン\",\"author\":\"me\"}");
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        var packs = SkinPackScanner.ScanRoot(root);
        Check(packs.Count == 1, "empty pack excluded");
        var info = packs[0];
        Check(info.Name == "ネオン" && info.Author == "me" && info.Id == "neon", "manifest");
        Check(info.CardImages.ContainsKey(Card.Parse("S3").Id) && info.CardImages.ContainsKey(Card.Parse("H10").Id)
            && info.CardImages.ContainsKey(Card.Parse("DQ").Id) && info.CardImages.ContainsKey(Card.Parse("CA").Id), "card mapping");
        Check(info.CardImages.ContainsKey(Card.Joker(0).Id) && info.CardImages.ContainsKey(Card.Joker(1).Id), "shared joker");
        Check(!info.CardImages.ContainsKey(Card.Parse("S4").Id), "oversized skipped");
        Check(info.BackImagePath is not null && info.CardImages.Count == 6 && info.MissingCardCount == 48, "back + missing count");

        // zip インストール
        byte[] pngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        byte[] webpHeader = { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' };
        var zipPath = Path.Combine(root, "My Pack!.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void AddBytes(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes); }
            AddBytes("cards/S5.png", pngHeader);
            AddBytes("cards/S6.png", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // 拡張子偽装（EXE ヘッダ）は弾かれる
            AddBytes("../../evil.png", pngHeader);
            AddBytes("cards/virus.exe", new byte[3]);
            AddBytes("JK0.webp", webpHeader);
            AddBytes("skin.json", new byte[] { (byte)'{', (byte)'}' });
        }
        var installed = SkinPackInstaller.InstallFromZip(zipPath, root);
        Check(installed.Id == "My_Pack!", "safe id " + installed.Id);
        Check(installed.CardImages.ContainsKey(Card.Parse("S5").Id) && installed.CardImages.ContainsKey(Card.Joker(0).Id), "zip extracted (flattened)");
        Check(!installed.CardImages.ContainsKey(Card.Parse("S6").Id), "spoofed image signature rejected");
        Check(!File.Exists(Path.Combine(root, "evil.png")) && !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.png")), "zip slip prevented");
        Check(!File.Exists(Path.Combine(installed.DirectoryPath, "virus.exe")), "unsupported skipped");
        Check(SkinPackInstaller.MakeSafeId("CON") == "skin_CON", "reserved windows name avoided");

        var badZip = Path.Combine(root, "bad.zip");
        using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("notes.txt").Open();
            s.Write(new byte[] { 1 });
        }
        try { SkinPackInstaller.InstallFromZip(badZip, root); Check(false, "bad zip rejected"); }
        catch (InvalidDataException) { Check(!Directory.Exists(Path.Combine(root, "bad")) && !Directory.Exists(Path.Combine(root, "bad.installing")), "bad zip rejected cleanly"); }

        Check(SkinPackInstaller.Uninstall(root, installed.Id) && !Directory.Exists(installed.DirectoryPath), "uninstall");
        Check(!SkinPackInstaller.Uninstall(root, "..\\..\\Windows"), "uninstall traversal blocked");
        Check(SkinPackInstaller.MakeSafeId("a/b\\c..d") == "a_b_c__d", "make safe id: " + SkinPackInstaller.MakeSafeId("a/b\\c..d"));
        Check(SkinPackInstaller.MakeSafeId("...") == "skin", "make safe id fallback");
        TryDelete(root);
    }

    // ==================================================================
    // Protocol
    // ==================================================================

    private static void ProtocolTests()
    {
        // フレーム
        var ms = new MemoryStream();
        FrameCodec.WriteAsync(ms, new byte[] { 1, 2, 3 }, default).GetAwaiter().GetResult();
        FrameCodec.WriteAsync(ms, Array.Empty<byte>(), default).GetAwaiter().GetResult();
        ms.Position = 0;
        Check(FrameCodec.ReadAsync(ms, default).GetAwaiter().GetResult()!.SequenceEqual(new byte[] { 1, 2, 3 }), "frame 1");
        Check(FrameCodec.ReadAsync(ms, default).GetAwaiter().GetResult()!.Length == 0, "frame empty");
        Check(FrameCodec.ReadAsync(ms, default).GetAwaiter().GetResult() is null, "eof -> null");
        var bad = new MemoryStream(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF });
        try { FrameCodec.ReadAsync(bad, default).GetAwaiter().GetResult(); Check(false, "oversize frame"); }
        catch (InvalidDataException) { Check(true, "oversize frame"); }
        var truncated = new MemoryStream(new byte[] { 0, 0, 0, 5, 1, 2 });
        try { FrameCodec.ReadAsync(truncated, default).GetAwaiter().GetResult(); Check(false, "truncated"); }
        catch (EndOfStreamException) { Check(true, "truncated"); }

        // ポリモーフィックJSON
        NetMessage[] messages =
        {
            new JoinRequest(1, "たろう", "abc"),
            new CommandRequest(7, new CommandDto(CommandKind.Play, new[] { 0, 52 })),
            new CommandResponse(7, CommandError.IllegalPlay, PlayRejectReason.NotStronger),
            new LobbyUpdate(new[] { new SeatInfo(0, "h", PlayerKind.Human, true, true) }),
        };
        foreach (var m in messages)
        {
            var back = NetProtocol.Deserialize(NetProtocol.Serialize(m));
            Check(back is not null && back.GetType() == m.GetType(), "roundtrip " + m.GetType().Name);
        }
        Check(NetProtocol.Deserialize(Encoding.UTF8.GetBytes("{\"$type\":\"nope\"}")) is null, "unknown type -> null");
        Check(NetProtocol.Deserialize(Encoding.UTF8.GetBytes("garbage")) is null, "garbage -> null");
        var resp = (CommandResponse)NetProtocol.Deserialize(NetProtocol.Serialize(messages[2]))!;
        Check(resp.Error == CommandError.IllegalPlay && resp.PlayReject == PlayRejectReason.NotStronger, "enum values");

        // 視点フィルタ
        var engine = new GameEngine(RuleSettings.CreateDefault(), Enumerable.Range(0, 4).Select(i => new PlayerSetup("P" + i, PlayerKind.Npc)).ToList(), new SystemRandomSource(3));
        engine.StartGame();
        var view = ClientViewBuilder.Build(engine, 2, 1);
        Check(view.MyHand.SequenceEqual(engine.GetPlayer(2).Hand.Select(c => c.Id)), "own hand");
        var json = Encoding.UTF8.GetString(NetProtocol.Serialize(new ViewUpdate(view)));
        using (var doc = JsonDocument.Parse(json))
        {
            var players = doc.RootElement.GetProperty("view").GetProperty("players");
            bool onlyCounts = players.EnumerateArray().All(p => p.EnumerateObject().All(prop => prop.Value.ValueKind != JsonValueKind.Array));
            Check(onlyCounts, "other players carry no card arrays in JSON");
        }
        Check(view.Players.All(p => p.HandCount == engine.GetPlayer(p.Id).HandCount), "counts only");

        // 場の公開情報：強化11バックの持続・砂嵐の割り込み受付がゲストへ届く（古い形式の JSON では false）
        {
            var fr = RuleSettings.CreateDefault();
            fr.ElevenBack.StrongElevenBackEnabled = true;
            fr.Flow.SandstormEnabled = true;
            fr.Flow.RokurokubiEnabled = true;
            fr.Lock.SuitLockEnabled = false;
            fr.Exchange.Enabled = false;
            var fe = new GameEngine(fr, Enumerable.Range(0, 4).Select(i => new PlayerSetup("P" + i, PlayerKind.Remote)).ToList(), new SystemRandomSource(3));
            var notices = new List<NoticeDto>();
            fe.EventOccurred += (_, ev) => { if (ClientViewBuilder.ToNotice(ev, 0) is { } n) notices.Add(n); };
            fe.StartGame();
            int l = fe.PendingDecision!.PlayerId, a = (l + 1) % 4, b = (l + 2) % 4, c = (l + 3) % 4;
            fe.AlwaysAskInterruptPlayerIds.Add(a); // a は操作ガイド「初心者用」のゲスト
            fe.GetPlayer(l).SetHand(Card.ParseMany("D3 SJ S6 H6 SK"));
            fe.GetPlayer(a).SetHand(Card.ParseMany("C8 C9"));
            fe.GetPlayer(b).SetHand(Card.ParseMany("S3 H3 C3 D7"));
            fe.GetPlayer(c).SetHand(Card.ParseMany("D4 CK"));
            fe.SubmitPlay(l, Card.ParseMany("D3"));
            fe.SubmitPass(a); fe.SubmitPass(b); fe.SubmitPass(c);
            fe.SubmitPlay(l, Card.ParseMany("SJ"));
            var jv = ClientViewBuilder.Build(fe, b, 5);
            Check(jv.Field.IsElevenBack && jv.Field.IsElevenBackPersistent, "view carries the persistent 11back flag");
            var jvBack = ((ViewUpdate)NetProtocol.Deserialize(NetProtocol.Serialize(new ViewUpdate(jv)))!).View;
            Check(jvBack.Field.IsElevenBackPersistent && !jvBack.Field.IsSandstormStopPending, "persistent 11back survives JSON");
            fe.SubmitPass(a); fe.SubmitPass(b); fe.SubmitPass(c);
            // 強化11バックは場が流れても続く。l が親のまま、ろくろ首を出す
            fe.SubmitPlay(l, Card.ParseMany("S6 H6"));
            // 「初心者用」のゲスト（a）には、砂嵐を持っていなくても先に確認が届く（出せる手はなく、パス（流す）だけ）。
            // 「初心者用」でない c は持っていないので聞かれない
            var av = ClientViewBuilder.Build(fe, a, 6);
            Check(av.Decision is { Kind: DecisionKind.SandstormStopChance, CanPass: true } ad && ad.PlayerId == a
                && (av.LegalPlays is null || av.LegalPlays.Length == 0), "view: a guest without a sandstorm is asked too, with nothing to play");
            Check(fe.SubmitPass(a).IsAccepted, "view: the guest without a sandstorm passes");
            var sv = ClientViewBuilder.Build(fe, b, 6);
            Check(sv.Decision is { Kind: DecisionKind.SandstormStopChance, CanPass: true } sd && sd.PlayerId == b
                && sd.Effect == SpecialEffectType.Rokurokubi && sd.TargetPlayerId == l, "view carries the sandstorm chance");
            Check(sv.Field.IsSandstormStopPending && sv.LegalPlays is { Length: 1 } lp && lp[0].Length == 3, "guest receives the sandstorm as its only legal play");
            var other = ClientViewBuilder.Build(fe, c, 7);
            Check(other.Decision is { Kind: DecisionKind.SandstormStopChance, CanPass: false } && other.LegalPlays is null, "other guests see who is deciding only");
            var svBack = ((ViewUpdate)NetProtocol.Deserialize(NetProtocol.Serialize(new ViewUpdate(sv)))!).View;
            Check(svBack.Decision!.Kind == DecisionKind.SandstormStopChance && svBack.Field.IsSandstormStopPending, "sandstorm chance survives JSON");
            var legacy = NetProtocol.Deserialize(Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(NetProtocol.Serialize(new ViewUpdate(sv)))
                    .Replace(",\"isElevenBackPersistent\":true", "").Replace(",\"isSandstormStopPending\":true", "")));
            Check(legacy is ViewUpdate { View.Field: { IsElevenBackPersistent: false, IsSandstormStopPending: false, IsElevenBackOneTurn: false } },
                "missing field flags default to false");

            // 「次の手番だけ」の11バックもゲストの画面へ届く（バッジの表示を切り替えるため）
            var or = RuleSettings.CreateDefault();
            or.ElevenBack.Duration = ElevenBackDuration.NextTurnOnly;
            or.Lock.SuitLockEnabled = false;
            or.Exchange.Enabled = false;
            var oe = new GameEngine(or, Enumerable.Range(0, 4).Select(i => new PlayerSetup("P" + i, PlayerKind.Remote)).ToList(), new SystemRandomSource(3));
            oe.StartGame();
            int ol = oe.PendingDecision!.PlayerId, oa = (ol + 1) % 4, ob = (ol + 2) % 4, oc = (ol + 3) % 4;
            oe.GetPlayer(ol).SetHand(Card.ParseMany("D3 S5"));
            oe.GetPlayer(oa).SetHand(Card.ParseMany("SJ C9"));
            oe.GetPlayer(ob).SetHand(Card.ParseMany("H7 D9"));
            oe.GetPlayer(oc).SetHand(Card.ParseMany("D4 CK"));
            oe.SubmitPlay(ol, Card.ParseMany("D3"));
            oe.SubmitPlay(oa, Card.ParseMany("SJ"));
            var ov = ((ViewUpdate)NetProtocol.Deserialize(NetProtocol.Serialize(new ViewUpdate(ClientViewBuilder.Build(oe, ob, 8))))!).View;
            Check(ov.Field.IsElevenBack && ov.Field.IsElevenBackOneTurn && !ov.Field.IsElevenBackPersistent, "view carries the one-turn 11back flag");
            oe.SubmitPass(ob);
            var ov2 = ClientViewBuilder.Build(oe, oc, 9);
            Check(ov2.Field.IsElevenBack && ov2.Field.IsElevenBackOneTurn && ov2.Decision!.PlayerId == oc, "one-turn 11back stays on after pass");
            oe.SubmitPlay(oc, Card.ParseMany("D4"));
            var ov3 = ClientViewBuilder.Build(oe, ol, 10);
            Check(!ov3.Field.IsElevenBack && !ov3.Field.IsElevenBackOneTurn, "one-turn 11back is gone after play");
            // あがりの通知には順位が載る
            var finished = ClientViewBuilder.ToNotice(new PlayerFinishedEvent(2, FinishKind.Normal, 3), 0)!;
            Check(finished.Kind == "PlayerFinished" && finished.PlayerId == 2 && finished.Count == 3 && finished.FinishKind == FinishKind.Normal, "finish notice carries the place");
            var finishedBack = ((GameNotice)NetProtocol.Deserialize(NetProtocol.Serialize(new GameNotice(finished)))!).Notice;
            Check(finishedBack.Count == 3, "finish place survives JSON");
            // 12ボンバーのジョーカー宣言が通知に載る
            var jokerNotice = ClientViewBuilder.ToNotice(new RanksDeclaredEvent(1, new[] { Rank.Three, Rank.Joker }), 0)!;
            var jokerNoticeBack = ((GameNotice)NetProtocol.Deserialize(NetProtocol.Serialize(new GameNotice(jokerNotice)))!).Notice;
            Check(jokerNoticeBack.Ranks is { Length: 2 } rk && rk[1] == Rank.Joker, "joker declaration survives JSON");
            Check(NetProtocol.Version == 2, "protocol version bumped for the new decision kind");
        }
        var d = engine.PendingDecision!;
        Check(view.IsMyDecision == (d.PlayerId == 2) && (view.LegalPlays is null) == (d.PlayerId != 2), "legal plays only for own decision");
        var transfer = new CardsTransferredEvent(0, 3, new[] { Card.Parse("S2") }, CardTransferReason.Exchange);
        Check(ClientViewBuilder.ToNotice(transfer, 0)!.CardIds is not null && ClientViewBuilder.ToNotice(transfer, 3)!.CardIds is not null, "transfer visible to parties");
        Check(ClientViewBuilder.ToNotice(transfer, 1)!.CardIds is null && ClientViewBuilder.ToNotice(transfer, 1)!.Count == 1, "transfer hidden from others");

        // 12ボンバー：宣言した数字と、誰が何枚捨てたか（理由つき）が全員に届く
        var declared = ClientViewBuilder.ToNotice(new RanksDeclaredEvent(2, new[] { Rank.Seven, Rank.King }), 1)!;
        Check(declared.Kind == "RanksDeclared" && declared.PlayerId == 2 && declared.Ranks!.SequenceEqual(new[] { Rank.Seven, Rank.King }), "ranks declared notice");
        var bombed = ClientViewBuilder.ToNotice(new CardsDiscardedEvent(3, new[] { Card.Parse("H7"), Card.Parse("C7") }, CardDiscardReason.TwelveBomber), 0)!;
        Check(bombed.Kind == "CardsDiscarded" && bombed.PlayerId == 3 && bombed.Count == 2 && bombed.DiscardReason == CardDiscardReason.TwelveBomber,
            "bomber discard notice carries reason");
        var declaredJson = JsonSerializer.Serialize<NetMessage>(new GameNotice(declared), NetJsonContext.Default.NetMessage);
        var declaredBack = JsonSerializer.Deserialize(declaredJson, NetJsonContext.Default.NetMessage) as GameNotice;
        Check(declaredBack?.Notice.Ranks?.SequenceEqual(new[] { Rank.Seven, Rank.King }) == true, "ranks survive JSON");
    }

    // ==================================================================
    // Network（ループバックで実対戦）
    // ==================================================================

    /// <summary>ClientView だけを見て応答するリモートプレイヤーのボット。</summary>
    private sealed class ClientBot
    {
        private readonly RemoteGameClient _client;
        private readonly Random _rnd;
        private int _busy;
        private long _actedSeq = -1;
        public int Rejected;
        public int Actions;
        public readonly List<NoticeDto> Notices = new();
        public bool HiddenLeak;

        /// <summary>true の間は判断を見ても応答しない（「自分の番で固まった」状態の再現用）。</summary>
        public volatile bool Paused;

        /// <summary>自分の判断待ちの盤面を受け取った（一時停止中でも通知）。</summary>
        public readonly TaskCompletionSource SawMyDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClientBot(RemoteGameClient client, int seed)
        {
            _client = client;
            _rnd = new Random(seed);
            // 契約どおり「購読してから最新状態を1回読む」
            client.ViewUpdated += (_, v) => _ = ActAsync();
            client.NoticeReceived += (_, n) =>
            {
                lock (Notices) Notices.Add(n);
                if (n.Kind == "CardsTransferred" && n.CardIds is not null && n.PlayerId != client.SeatId && n.OtherPlayerId != client.SeatId)
                {
                    HiddenLeak = true;
                }
            };
            _ = Task.Run(ActAsync);
        }

        public void Resume()
        {
            Paused = false;
            _ = Task.Run(ActAsync);
        }

        private async Task ActAsync()
        {
            if (_client.View is { IsMyDecision: true }) SawMyDecision.TrySetResult();
            if (Paused) return;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                while (!Paused && _client.View is { } v && v.IsMyDecision && v.Sequence > _actedSeq && _client.IsConnected)
                {
                    _actedSeq = v.Sequence;
                    var d = v.Decision!;
                    var hand = v.MyHandCards;
                    CommandResponse r = d.Kind switch
                    {
                        DecisionKind.PlayOrPass or DecisionKind.FourStopChance or DecisionKind.SandstormStopChance =>
                            v.LegalPlays is { Length: > 0 } lp && (!d.CanPass || _rnd.Next(4) != 0)
                                ? await _client.PlayAsync(lp[_rnd.Next(lp.Length)].Select(Card.FromId))
                                : await _client.PassAsync(),
                        DecisionKind.SelectCards => await _client.SelectCardsAsync(hand.Take(d.MinCount)),
                        // ジョーカー（Rank.Joker = 0）も宣言に含める
                        DecisionKind.DeclareRanks => await _client.DeclareRanksAsync(Enumerable.Range(0, d.MinCount).Select(i => (Rank)i)),
                        DecisionKind.RevolutionChoice => await _client.ChooseRevolutionAsync(true),
                        _ => new CommandResponse(0, CommandError.InvalidSelection, PlayRejectReason.None),
                    };
                    Actions++;
                    if (r.Error != CommandError.None && r.Error != CommandError.Deferred) Rejected++;
                }
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
            // 応答待ちの間に新しい判断が届いていたら続けて処理する
            if (_client.IsConnected && _client.View is { } latest && latest.IsMyDecision && latest.Sequence > _actedSeq) _ = Task.Run(ActAsync);
        }
    }

    /// <summary>NPCの実行を止めておけるスケジューラー（開いている間はそのままディスパッチャへ流す）。</summary>
    private sealed class GateScheduler : INpcScheduler
    {
        private readonly ISerialDispatcher _dispatcher;
        private readonly List<Action> _held = new();
        private bool _open = true;
        public GateScheduler(ISerialDispatcher dispatcher) => _dispatcher = dispatcher;
        public void Schedule(TimeSpan delay, Action action)
        {
            lock (_held)
            {
                if (!_open) { _held.Add(action); return; }
            }
            _dispatcher.Post(action);
        }
        public void Close() { lock (_held) _open = false; }
        public void Open()
        {
            List<Action> release;
            lock (_held) { _open = true; release = _held.ToList(); _held.Clear(); }
            foreach (var a in release) _dispatcher.Post(a);
        }
    }

    private static async Task<(GameHostSession Host, SerialActionQueue Queue, TaskCompletionSource<GameResult> Ended)> CreateHostAsync(
        int seed, bool specials, Func<ISerialDispatcher, INpcScheduler>? scheduler = null)
    {
        var queue = new SerialActionQueue();
        queue.Error += (_, ex) => Console.WriteLine("  [dispatcher error] " + ex);
        var host = new GameHostSession(new HostOptions
        {
            RoomName = "テスト部屋",
            HostPlayerName = "ホスト",
            Port = 0,
            BindAddress = IPAddress.Loopback,
            DiscoveryPort = 48000 + (seed % 500),
            NpcTiming = new NpcTimingOptions { BaseDelay = TimeSpan.Zero, Jitter = TimeSpan.Zero, SideDecisionDelay = TimeSpan.Zero },
        }, queue, scheduler?.Invoke(queue));
        var ended = new TaskCompletionSource<GameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.GameCreated += (_, engine) =>
        {
            // ホスト自身の席もテストではNPCに操作させる
            host.Npc!.Assign(0, NpcProfile.Default, new SystemRandomSource(seed));
            engine.SetPlayerKind(0, PlayerKind.Npc);
            engine.EventOccurred += (_, e) =>
            {
                if (e is RoundEndedEvent re && !re.IsGameOver) queue.Post(() => engine.StartNextRound());
                if (e is GameEndedEvent ge) ended.TrySetResult(ge.Result);
            };
        };
        await host.StartAsync();
        return (host, queue, ended);
    }

    /// <summary>条件が満たされるまでディスパッチャを空にしながら待つ（テスト専用・最大10秒）。</summary>
    private static async Task WaitUntilAsync(SerialActionQueue queue, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Post(() => tcs.SetResult(condition()));
            if (await tcs.Task) return;
            await Task.Yield();
        }
    }

    private static async Task NetworkGameTests()
    {
        // --- 探索 ---
        {
            var (host, queue, _) = await CreateHostAsync(1, false);
            var rooms = await LanDiscovery.DiscoverAsync(TimeSpan.FromMilliseconds(800), 48001, localAddress: IPAddress.Loopback);
            Check(rooms.Any(r => r.Port == host.Port && r.RoomName == "テスト部屋" && r.OpenSeats == 3 && !r.IsPlaying), "discovery finds room: " + rooms.Count);
            await host.DisposeAsync();
            await queue.DisposeAsync();
        }

        // --- 2人のリモート参加で最後まで対戦、途中で1人切断 → NPC引き継ぎ ---
        for (int seed = 0; seed < 3; seed++)
        {
            var (host, queue, ended) = await CreateHostAsync(10 + seed, false);
            var c1 = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "リモート1", "client-1");
            var c2 = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "リモート2", "client-2");
            Check(c1.SeatId == 1 && c2.SeatId == 2, "seats assigned");
            var bot1 = new ClientBot(c1, seed);
            var bot2 = new ClientBot(c2, seed + 100);
            await queue.FlushAsync();
            Check(host.Seats.Count(s => s.Kind == PlayerKind.Remote && s.IsConnected) == 2, "lobby shows 2 remotes");

            try
            {
                await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "x", "client-1");
                Check(false, "duplicate client id while connected should not take seat 1");
            }
            catch (JoinFailedException)
            {
                Check(true, "third join ok-or-rejected");
            }
            catch (Exception ex)
            {
                Check(false, "unexpected " + ex.GetType().Name);
            }

            var rules = RuleSettings.CreateDefault();
            rules.Progress.RoundCount = 3;
            host.StartGame(rules, new SystemRandomSource(seed));

            // 2人目は途中で切断
            var disconnectAfter = Task.Run(async () =>
            {
                while (bot2.Actions < 8 && !ended.Task.IsCompleted) await Task.Delay(10);
                await c2.DisposeAsync();
            });

            var finished = await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            Check(finished == ended.Task, $"network game finished seed={seed}");
            await disconnectAfter;
            await Task.Delay(200);
            Check(bot1.Rejected == 0 && bot1.Actions > 0, $"client1 actions={bot1.Actions} rejected={bot1.Rejected}");
            Check(!bot1.HiddenLeak && !bot2.HiddenLeak, "no hidden card leak");
            lock (bot1.Notices) Check(bot1.Notices.Any(n => n.Kind == "GameEnded"), "client got GameEnded");
            Check(host.Engine!.GetPlayer(2).Kind == PlayerKind.Npc, "disconnected seat taken over by NPC");

            await c1.DisposeAsync();
            await host.DisposeAsync();
            await queue.DisposeAsync();
        }

        // --- 自分の番の最中に切断 → 再接続で同じ席に戻り、その判断を引き継いで続行 ---
        {
            GateScheduler? gate = null;
            var (host, queue, ended) = await CreateHostAsync(50, false, q => gate = new GateScheduler(q));
            var c1 = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "R1", "dev-A");
            var bot = new ClientBot(c1, 1) { Paused = true };
            var rules = RuleSettings.CreateDefault();
            rules.Progress.RoundCount = 2;
            host.StartGame(rules, new SystemRandomSource(50));

            // 自分（席1）の判断待ちで固まらせる
            await bot.SawMyDecision.Task.WaitAsync(TimeSpan.FromSeconds(20));
            gate!.Close(); // 以後NPCは動かない＝ゲームは席1の判断で止まったまま
            await queue.FlushAsync();
            Check(!ended.Task.IsCompleted && host.Engine!.PendingDecision?.PlayerId == 1, "paused on seat 1 decision");

            await c1.DisposeAsync();
            await WaitUntilAsync(queue, () => host.Seats.First(s => s.SeatId == 1).Kind == PlayerKind.Npc);
            Check(host.Engine!.GetPlayer(1).Kind == PlayerKind.Npc, "takeover while disconnected");
            Check(host.Engine.PendingDecision?.PlayerId == 1, "decision still pending (npc held by gate)");

            var again = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "R1", "dev-A");
            Check(again.SeatId == 1, "rejoin same seat");
            var bot2 = new ClientBot(again, 2);
            await queue.FlushAsync();
            Check(host.Engine.GetPlayer(1).Kind == PlayerKind.Remote, "rejoin restores remote control");
            var prompted = await Task.WhenAny(bot2.SawMyDecision.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Check(prompted == bot2.SawMyDecision.Task, "rejoined client is prompted for the pending decision");
            await WaitUntilAsync(queue, () => host.Engine.PendingDecision?.PlayerId != 1 || ended.Task.IsCompleted);
            Check(bot2.Actions > 0, "rejoined client answered while NPCs were held");

            try
            {
                await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "new", "dev-B");
                Check(false, "new player rejected during game");
            }
            catch (JoinFailedException ex) { Check(ex.Message.Contains("対戦中"), "new player rejected during game: " + ex.Message); }

            gate.Open();
            var finished = await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            Check(finished == ended.Task, "rejoin game finished");
            await again.DisposeAsync();
            await host.DisposeAsync();
            await queue.DisposeAsync();
        }

        // --- 人が操作する席（ホスト・参加者）は、ガイドのレベルや参加者のアプリの版に関わらず、止められなくても割り込みの確認を受ける。
        //     切断で NPC が引き継ぐと外れる ---
        {
            GateScheduler? gate = null;
            var (host, queue, _) = await CreateHostAsync(70, false, q => gate = new GateScheduler(q));
            var beginner = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "初心者", "dev-G1", alwaysAskInterrupt: true);
            var normal = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "標準", "dev-G2");
            gate!.Close(); // NPC は動かさない（このテストは席の設定だけを見る）
            host.StartGame(RuleSettings.CreateDefault(), new SystemRandomSource(70));
            await WaitUntilAsync(queue, () => host.Engine is not null);
            await queue.FlushAsync();
            var ids = host.Engine!.AlwaysAskInterruptPlayerIds;
            Check(ids.Contains(beginner.SeatId) && ids.Contains(normal.SeatId) && ids.Contains(0) && !ids.Contains(3),
                "guide: every human seat (host and both guests) is always asked; the NPC seat is not");
            await beginner.DisposeAsync();
            await WaitUntilAsync(queue, () => host.Seats.First(s => s.SeatId == 1).Kind == PlayerKind.Npc);
            Check(!host.Engine.AlwaysAskInterruptPlayerIds.Contains(beginner.SeatId), "guide: an NPC taking over the seat is asked only when it can stop");
            var legacyJoin = NetProtocol.Deserialize(Encoding.UTF8.GetBytes("{\"$type\":\"join\",\"protocolVersion\":1,\"playerName\":\"old\",\"clientId\":\"x\"}"));
            Check(legacyJoin is JoinRequest { AlwaysAskInterrupt: false }, "guide: a join request from an older app defaults to holders-only");
            var roundTrip = NetProtocol.Deserialize(NetProtocol.Serialize(new JoinRequest(1, "n", "id", true)));
            Check(roundTrip is JoinRequest { AlwaysAskInterrupt: true }, "guide: the flag survives JSON");
            gate.Open();
            await normal.DisposeAsync();
            await host.DisposeAsync();
            await queue.DisposeAsync();
        }

        // --- 既定ポートが使用中でも空きポートで待ち受ける ---
        {
            var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            int busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var queue = new SerialActionQueue();
            var host = new GameHostSession(new HostOptions { Port = busyPort, BindAddress = IPAddress.Loopback, DiscoveryPort = null }, queue);
            await host.StartAsync();
            Check(host.Port != busyPort && host.Port > 0 && !host.IsDiscoverable, "fallback to free port");
            await host.DisposeAsync();
            await queue.DisposeAsync();
            blocker.Stop();
        }

        // --- 不正なコマンド ---
        {
            var (host, queue, ended) = await CreateHostAsync(60, false);
            var c1 = await RemoteGameClient.JoinAsync("127.0.0.1", host.Port, "R1", "dev-X");
            var notMine = await c1.PlayAsync(new[] { Card.Parse("S3") });
            Check(notMine.Error == CommandError.WrongPhase, "command before game -> WrongPhase");
            await c1.DisposeAsync();
            await host.DisposeAsync();
            await queue.DisposeAsync();
        }
    }
}
