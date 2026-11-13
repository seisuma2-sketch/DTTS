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

    private static readonly string ScratchRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tmp");

    private static string TempDir()
    {
        var dir = Path.GetFullPath(Path.Combine(ScratchRoot, "dtts-test-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<(int Pass, int Fail)> RunAsync()
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

    // ==================================================================
    // Database
    // ==================================================================

    private static GameEngine PlayQuickGame(int seed, out GameResult result)
    {
        var rules = RuleSettings.CreateDefault();
        rules.Progress.RoundCount = 2;
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
        var zipPath = Path.Combine(root, "My Pack!.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string name, int size = 3) { using var s = zip.CreateEntry(name).Open(); s.Write(new byte[size]); }
            Add("cards/S5.png");
            Add("../../evil.png");
            Add("cards/virus.exe");
            Add("JK0.webp");
            Add("skin.json");
        }
        var installed = SkinPackInstaller.InstallFromZip(zipPath, root);
        Check(installed.Id == "My_Pack!", "safe id " + installed.Id);
        Check(installed.CardImages.ContainsKey(Card.Parse("S5").Id) && installed.CardImages.ContainsKey(Card.Joker(0).Id), "zip extracted (flattened)");
        Check(!File.Exists(Path.Combine(root, "evil.png")) && !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.png")), "zip slip prevented");
        Check(!File.Exists(Path.Combine(installed.DirectoryPath, "virus.exe")), "unsupported skipped");

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
        var d = engine.PendingDecision!;
        Check(view.IsMyDecision == (d.PlayerId == 2) && (view.LegalPlays is null) == (d.PlayerId != 2), "legal plays only for own decision");
        var transfer = new CardsTransferredEvent(0, 3, new[] { Card.Parse("S2") }, CardTransferReason.Exchange);
        Check(ClientViewBuilder.ToNotice(transfer, 0)!.CardIds is not null && ClientViewBuilder.ToNotice(transfer, 3)!.CardIds is not null, "transfer visible to parties");
        Check(ClientViewBuilder.ToNotice(transfer, 1)!.CardIds is null && ClientViewBuilder.ToNotice(transfer, 1)!.Count == 1, "transfer hidden from others");
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
                        DecisionKind.PlayOrPass or DecisionKind.FourStopChance =>
                            v.LegalPlays is { Length: > 0 } lp && (!d.CanPass || _rnd.Next(4) != 0)
                                ? await _client.PlayAsync(lp[_rnd.Next(lp.Length)].Select(Card.FromId))
                                : await _client.PassAsync(),
                        DecisionKind.SelectCards => await _client.SelectCardsAsync(hand.Take(d.MinCount)),
                        DecisionKind.DeclareRanks => await _client.DeclareRanksAsync(Enumerable.Range(1, d.MinCount).Select(i => (Rank)i)),
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
