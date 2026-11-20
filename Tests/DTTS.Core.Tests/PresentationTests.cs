using System;
using System.Collections.Generic;
using System.Linq;
using DTTS.Core.Ai;
using DTTS.Core.Enums;
using DTTS.Core.Logic;
using DTTS.Core.Models;
using DTTS.Services.Audio;
using DTTS.Services.Network;
using DTTS.Services.Presentation;

/// <summary>
/// 画面の手触りまわりの判定（まとめて選ぶ候補・交換結果の説明・NPC の吹き出し・BGM の選択とフェード）のテスト。
/// どれも画面に依存しない純粋な処理なので、無音のまま確かめられる。
/// </summary>
internal static class PresentationTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond) { _pass++; } else { _fail++; Console.WriteLine("FAIL(presentation): " + name); }
    }

    private static IReadOnlyList<Card> C(string s) => Card.ParseMany(s);

    public static (int Pass, int Fail) Run()
    {
        Suggestions();
        SuggestionsFromEngine();
        Exchange();
        Reactions();
        Bgm();
        Mixer();
        LaunchOptions();
        return (_pass, _fail);
    }

    private static void LaunchOptions()
    {
        Check(DTTS.Services.AppLaunchOptions.IsAutoplay(new[] { "DTTS.exe", "--mute", "--AutoPlay" }), "launch: autoplay (case-insensitive)");
        Check(!DTTS.Services.AppLaunchOptions.IsAutoplay(new[] { "DTTS.exe", "--mute" }) && !DTTS.Services.AppLaunchOptions.IsAutoplay(null), "launch: no autoplay by default");
        Check(DTTS.Services.AppLaunchOptions.DataDirectory(new[] { "DTTS.exe", "--data-dir", "\"C:\\tmp\\guest\"" }) == "C:\\tmp\\guest", "launch: data dir (quotes removed)");
        Check(DTTS.Services.AppLaunchOptions.DataDirectory(new[] { "DTTS.exe", "--data-dir" }) is null, "launch: data dir without a value is ignored");
        Check(DTTS.Services.AppLaunchOptions.DataDirectory(new[] { "DTTS.exe" }) is null, "launch: default data dir");
    }

    private static void Suggestions()
    {
        var plays = new List<IReadOnlyList<Card>>
        {
            C("S5"), C("H5"),                     // 1枚出しは候補にしない
            C("S5 H5"), C("S5 D5"), C("H5 D5"),   // 同じ「5×2」は1つにまとめる
            C("S5 JK0"),                          // ジョーカー入りの5×2より、自然札だけの組を優先
            C("S9 H9 D9"),
            C("S6 S7 S8"), C("S6 JK0 S8"),
            C("JK0 JK1"),                         // ジョーカーだけの組は候補にしない
        };
        var s = PlaySuggestions.Build(plays);
        Check(s.All(x => x.Cards.Count >= 2), "suggest: no singles");
        Check(s.Count(x => x.Label.StartsWith("5 ×2", StringComparison.Ordinal)) == 1, "suggest: one chip per rank and count");
        var five = s.First(x => x.Label.StartsWith("5", StringComparison.Ordinal));
        Check(five.Cards.All(c => !c.IsJoker) && five.Label == "5 ×2", "suggest: prefers natural pairs over joker pairs");
        Check(s.Any(x => x.Label == "9 ×3"), "suggest: triple label");
        Check(s.Any(x => x.Label == "♠6-7-8"), "suggest: sequence label with one suit");
        Check(s.Any(x => x.Label == "♠6-8+JK"), "suggest: joker sequence is its own chip (different naturals)");
        Check(!s.Any(x => x.Cards.All(c => c.IsJoker)), "suggest: no joker-only chip");
        var order = s.Select(x => x.Cards.Where(c => !c.IsJoker).Min(c => Card.GetBaseStrength(c.Rank))).ToList();
        Check(order.SequenceEqual(order.OrderBy(v => v)), "suggest: weakest first");
        Check(PlaySuggestions.Build(Enumerable.Range(0, 30).Select(i => (IReadOnlyList<Card>)C("S5 H5")).Concat(plays), 2).Count == 2, "suggest: capped");
        Check(PlaySuggestions.Build(Array.Empty<IReadOnlyList<Card>>()).Count == 0, "suggest: empty");

        var hand = C("S5 H5 D7 JK0 C5 JK1");
        Check(PlaySuggestions.SameRank(hand, Card.Parse("H5")).Count == 3, "same rank: all three 5s");
        Check(PlaySuggestions.SameRank(hand, Card.Parse("JK0")).Count == 2, "same rank: jokers together");
    }

    private static void SuggestionsFromEngine()
    {
        // 実際の合法手（親番：手札の組み合わせ）から作った候補は、どれもそのまま出せる
        var r = RuleSettings.CreateDefault();
        r.Exchange.Enabled = false;
        var e = new GameEngine(r, Enumerable.Range(0, 4).Select(i => new PlayerSetup("P" + i, PlayerKind.Human)).ToList(), new SystemRandomSource(9));
        e.StartGame();
        int l = e.PendingDecision!.PlayerId;
        e.GetPlayer(l).SetHand(C("D3 S3 D4 D5 S5 H5 C5 S9 S10 SJ HK"));
        var legal = e.GetLegalPlays(l).Select(j => (IReadOnlyList<Card>)j.Play!.Cards).ToList();
        var chips = PlaySuggestions.Build(legal);
        Check(chips.Count > 0 && chips.All(c => legal.Any(lp => lp.Count == c.Cards.Count && lp.All(c.Cards.Contains))),
            "suggest(engine): every chip is a legal play");
        // 最初の親番でも ♦3 は必須ではないため、♦3 以外の候補も出せる
        Check(chips.Any(c => !c.Cards.Contains(Card.Parse("D3"))), "suggest(engine): first lead chips are not restricted to ♦3");
    }

    private static void Exchange()
    {
        var x = new ExchangeSummary();
        Check(!x.HasAny && x.Describe(_ => "?") == "", "exchange: empty");
        Check(!x.Record(0, 1, 2, C("S3")), "exchange: others' swap is ignored");
        Check(x.Record(0, 0, 3, C("S3 D4")), "exchange: given");
        Check(x.Record(0, 3, 0, C("S2")), "exchange: received");
        Check(x.Record(0, 3, 0, C("HA")), "exchange: received more from the same partner");
        Check(x.Given.Count == 1 && x.Received.Count == 1 && x.Received[0].Cards.Count == 2, "exchange: grouped by partner");
        string text = x.Describe(id => id == 3 ? "大富豪 ミオ" : "?");
        Check(text == "大富豪 ミオ へ ♠3・♦4 を渡し、大富豪 ミオ から ♠2・♥A を受け取りました", "exchange: sentence: " + text);
        Check(!x.Record(0, 0, 1, null), "exchange: hidden cards are ignored");
        x.Clear();
        Check(!x.HasAny, "exchange: cleared for the next round");

        // ゲストへの通知にも、交換か7渡しかが入る（旧版の通知は null）
        var ev = new CardsTransferredEvent(1, 0, C("S2"), CardTransferReason.Exchange);
        var notice = ClientViewBuilder.ToNotice(ev, 0)!;
        Check(notice.TransferReason == CardTransferReason.Exchange && notice.CardIds!.Length == 1, "exchange notice carries the reason and cards for the receiver");
        Check(ClientViewBuilder.ToNotice(ev, 2)!.CardIds is null, "exchange notice hides cards from others");
        var back = (GameNotice)NetProtocol.Deserialize(NetProtocol.Serialize(new GameNotice(notice)))!;
        Check(back.Notice.TransferReason == CardTransferReason.Exchange, "exchange notice reason survives JSON");
    }

    private static void Reactions()
    {
        foreach (var p in Enum.GetValues<NpcPersonality>().Cast<NpcPersonality?>().Append(null))
        {
            foreach (var k in Enum.GetValues<ReactionKind>())
            {
                Check(NpcReactions.For(p, k, 0).Length > 0 && NpcReactions.For(p, k, 7).Length > 0, $"reaction text: {p} {k}");
            }
            Check(NpcReactions.AvatarGlyph(p).Length == 1 && NpcReactions.PersonalityName(p).Length > 0, "avatar: " + p);
        }
        Check(NpcReactions.For(NpcPersonality.Steady, ReactionKind.Pass, 0) == "ここは見送るか", "reaction: steady pass");
        Check(NpcReactions.For(NpcPersonality.RevolutionSeeker, ReactionKind.Revolution, 0) == "よし、ひっくり返す！", "reaction: revolution seeker");
        Check(NpcReactions.For(NpcPersonality.Steady, ReactionKind.Pass, 0) != NpcReactions.For(NpcPersonality.Steady, ReactionKind.Pass, 1), "reaction: lines vary");
        Check(NpcReactions.For(NpcPersonality.Steady, ReactionKind.Pass, -1).Length > 0, "reaction: negative variant is safe");
        Check(Enum.GetValues<NpcPersonality>().Select(p => NpcReactions.AvatarGlyph(p)).Distinct().Count() == Enum.GetValues<NpcPersonality>().Length,
            "avatar: each personality has its own icon");
    }

    private static void Bgm()
    {
        Check(BgmPolicy.ForTable(false, 10, 8) == BgmTrack.Lounge, "bgm: calm table");
        Check(BgmPolicy.ForTable(true, 10, 8) == BgmTrack.Tension, "bgm: revolution");
        Check(BgmPolicy.ForTable(false, 10, 2) == BgmTrack.Tension, "bgm: an opponent is about to finish");
        Check(BgmPolicy.ForTable(false, 0, 1) == BgmTrack.Lounge, "bgm: after I finished it calms down");
        Check(BgmPolicy.ForTable(false, 5, null) == BgmTrack.Lounge, "bgm: no opponents left");
        foreach (var t in Enum.GetValues<BgmTrack>())
        {
            Check((t == BgmTrack.None) == (SoundEffectCatalog.BgmFileName(t) is null), "bgm file name: " + t);
        }
        // BGM のファイルが同梱されている（生成スクリプトの出力）
        string dir = System.IO.Path.Combine(FindRepoRoot(), "Assets", "Audio");
        foreach (var t in new[] { BgmTrack.Lounge, BgmTrack.Tension })
        {
            var path = System.IO.Path.Combine(dir, SoundEffectCatalog.BgmFileName(t)!);
            Check(System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 1_000_000, "bgm asset exists: " + t);
        }
        double headroom = SoundEffectCatalog.BgmHeadroom;
        Check(headroom > 0 && headroom < 1, "bgm: quieter than effects at the same slider");
    }

    private static void Mixer()
    {
        var m = new BgmMixer { FadeIn = TimeSpan.FromMilliseconds(1000), FadeOut = TimeSpan.FromMilliseconds(500) };
        Check(m.Current == BgmTrack.None && !m.IsFading, "mixer: silent at start");
        Check(m.Switch(BgmTrack.Lounge) == BgmTrack.Lounge && m.IsFading, "mixer: start lounge");
        Check(m.Switch(BgmTrack.Lounge) is null, "mixer: same track is a no-op");
        m.Advance(TimeSpan.FromMilliseconds(500), out _);
        Check(Math.Abs(m.Level(BgmTrack.Lounge) - 0.5) < 1e-9, "mixer: half way through the fade in");
        m.Advance(TimeSpan.FromMilliseconds(600), out _);
        Check(m.Level(BgmTrack.Lounge) == 1.0 && !m.IsFading, "mixer: fade in completes and stops");

        Check(m.Switch(BgmTrack.Tension) == BgmTrack.Tension, "mixer: crossfade to tension");
        var changed = m.Advance(TimeSpan.FromMilliseconds(250), out var stopped);
        Check(changed.Contains(BgmTrack.Lounge) && changed.Contains(BgmTrack.Tension) && stopped.Count == 0, "mixer: both move during a crossfade");
        Check(Math.Abs(m.Level(BgmTrack.Lounge) - 0.5) < 1e-9 && Math.Abs(m.Level(BgmTrack.Tension) - 0.25) < 1e-9, "mixer: out is faster than in");
        m.Advance(TimeSpan.FromMilliseconds(300), out stopped);
        Check(stopped.SequenceEqual(new[] { BgmTrack.Lounge }) && m.Level(BgmTrack.Lounge) == 0.0, "mixer: the old track stops at zero");

        // 戻すときは、まだ鳴っている曲は頭から鳴らし直さない
        Check(m.Switch(BgmTrack.Lounge) == BgmTrack.Lounge, "mixer: silent lounge restarts from the top");
        m.Advance(TimeSpan.FromMilliseconds(100), out _);
        Check(m.Switch(BgmTrack.Tension) is null, "mixer: a still-audible track is faded back in, not restarted");

        Check(m.Switch(BgmTrack.None) is null, "mixer: stop");
        m.Advance(TimeSpan.FromSeconds(5), out stopped);
        Check(m.Level(BgmTrack.Lounge) == 0 && m.Level(BgmTrack.Tension) == 0 && !m.IsFading, "mixer: everything fades out");

        m.Switch(BgmTrack.Lounge);
        m.Settle();
        Check(m.Level(BgmTrack.Lounge) == 1.0, "mixer: settle jumps to the target");
        Check(m.Rewind() == BgmTrack.Lounge && m.Level(BgmTrack.Lounge) == 0.0 && m.IsFading, "mixer: rewind fades the current track in again");
    }

    private static string FindRepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "DTTS.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? ".";
    }
}
