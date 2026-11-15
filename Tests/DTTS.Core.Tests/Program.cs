using System;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("========================================");
        Console.WriteLine("DTTS.Core.Tests - Test Runner Starting");
        Console.WriteLine("========================================");

        // テストは無音で実行する。
        // - 再生実装（AudioGraphService）はテストプロジェクトに含めていないので、ここから音が出ることはない
        // - 念のため無音指定（DTTS_MUTE=1）も立てておく（このプロセスから起動するものにも引き継がれる）
        Environment.SetEnvironmentVariable(DTTS.Services.Audio.AudioLaunchOptions.MuteEnvironmentVariable, "1");
        bool hasPlayback = Array.Exists(typeof(Program).Assembly.GetTypes(), t => t.Name == "AudioGraphService");
        Console.WriteLine(hasPlayback ? "WARNING: audio playback is compiled into the tests" : "Audio: muted (no playback in test build)");
        if (hasPlayback)
        {
            return 1;
        }

        int totalFail = 0;

        Console.WriteLine("--- 1. JudgeTests ---");
        var (jp, jf) = JudgeTests.Run();
        Console.WriteLine($"Judge: pass={jp}, fail={jf}");
        totalFail += jf;

        Console.WriteLine("--- 2. EngineTests ---");
        var (ep, ef) = EngineTests.Run();
        Console.WriteLine($"Engine: pass={ep}, fail={ef}");
        totalFail += ef;

        Console.WriteLine("--- 3. NpcTests ---");
        var (np, nf) = NpcTests.Run();
        Console.WriteLine($"NPC: pass={np}, fail={nf}");
        totalFail += nf;

        Console.WriteLine("--- 4. ServicesTests ---");
        var (sp, sf) = await ServicesTests.RunAsync();
        Console.WriteLine($"Services: pass={sp}, fail={sf}");
        totalFail += sf;

        Console.WriteLine("--- 5. TurnOrderTests ---");
        var (tp, tf) = TurnOrderTests.Run();
        Console.WriteLine($"TurnOrder: pass={tp}, fail={tf}");
        totalFail += tf;

        Console.WriteLine("========================================");
        int totalPass = jp + ep + np + sp + tp;
        Console.WriteLine($"TOTAL: pass={totalPass}, fail={totalFail}");
        Console.WriteLine(totalFail == 0 ? "ALL TESTS PASSED!" : "SOME TESTS FAILED!");
        Console.WriteLine("========================================");

        return totalFail == 0 ? 0 : 1;
    }
}
