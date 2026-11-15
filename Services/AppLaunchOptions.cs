using System;
using System.Collections.Generic;
using System.Linq;

namespace DTTS.Services;

/// <summary>
/// 検証用の起動引数（通しの動作確認・自動テストで使う。付けなければ何も変わらない）。
/// <list type="bullet">
/// <item><c>--autoplay</c>：自分の席も NPC に打たせ、ラウンド結果も自動で次へ進める（最後まで通しで流す確認用）。</item>
/// <item><c>--data-dir &lt;フォルダ&gt;</c>：設定・戦績の保存先を変える（同じ PC でホストとゲストを2つ起動するとき、データを分けるため）。</item>
/// </list>
/// </summary>
public static class AppLaunchOptions
{
    /// <summary>自分の席も自動で打つ引数。</summary>
    public const string AutoplayArgument = "--autoplay";

    /// <summary>保存先を変える引数。</summary>
    public const string DataDirectoryArgument = "--data-dir";

    /// <summary>引数に <c>--autoplay</c> があるか。</summary>
    public static bool IsAutoplay(IEnumerable<string>? arguments)
        => arguments?.Any(a => string.Equals(a?.Trim(), AutoplayArgument, StringComparison.OrdinalIgnoreCase)) ?? false;

    /// <summary><c>--data-dir</c> の次の引数（なければ null）。</summary>
    public static string? DataDirectory(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i]?.Trim(), DataDirectoryArgument, StringComparison.OrdinalIgnoreCase))
            {
                var value = arguments[i + 1]?.Trim().Trim('"');
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        return null;
    }

    /// <summary>このプロセスが <c>--autoplay</c> 付きで起動されたか。</summary>
    public static bool IsAutoplayForCurrentProcess() => IsAutoplay(Environment.GetCommandLineArgs());

    /// <summary>このプロセスの <c>--data-dir</c>。</summary>
    public static string? DataDirectoryForCurrentProcess() => DataDirectory(Environment.GetCommandLineArgs());
}
