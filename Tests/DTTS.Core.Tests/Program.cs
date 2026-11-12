using System;
using System.Threading.Tasks;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("========================================");
        Console.WriteLine("DTTS.Core.Tests - Test Runner Starting");
        Console.WriteLine("========================================");

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

        Console.WriteLine("========================================");
        int totalPass = jp + ep + np + sp;
        Console.WriteLine($"TOTAL: pass={totalPass}, fail={totalFail}");
        Console.WriteLine(totalFail == 0 ? "ALL TESTS PASSED!" : "SOME TESTS FAILED!");
        Console.WriteLine("========================================");

        return totalFail == 0 ? 0 : 1;
    }
}
