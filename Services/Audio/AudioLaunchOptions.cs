using System;
using System.Collections.Generic;
using System.Linq;

namespace DTTS.Services.Audio;

/// <summary>
/// 起動時の「無音」指定。自動テスト（E2E）や検証で、保存済みの設定を変えずにその回だけ音を止めるために使う。
/// <list type="bullet">
/// <item>コマンドライン引数 <c>--mute</c></item>
/// <item>環境変数 <c>DTTS_MUTE=1</c>（true・yes も可）</item>
/// </list>
/// </summary>
public static class AudioLaunchOptions
{
    /// <summary>無音で起動するコマンドライン引数。</summary>
    public const string MuteArgument = "--mute";

    /// <summary>無音で起動する環境変数。</summary>
    public const string MuteEnvironmentVariable = "DTTS_MUTE";

    /// <summary>引数・環境変数のどちらかで無音が指定されているか。</summary>
    public static bool IsMuteRequested(IEnumerable<string>? arguments, string? environmentValue)
    {
        bool byArgument = arguments?.Any(a => string.Equals(a?.Trim(), MuteArgument, StringComparison.OrdinalIgnoreCase)) ?? false;
        return byArgument || IsTruthy(environmentValue);
    }

    /// <summary>現在のプロセスの引数・環境変数から判定する。</summary>
    public static bool IsMuteRequestedForCurrentProcess()
        => IsMuteRequested(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(MuteEnvironmentVariable));

    private static bool IsTruthy(string? value)
    {
        var v = value?.Trim();
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
