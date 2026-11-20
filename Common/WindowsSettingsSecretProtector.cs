using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using DTTS.Services.Database;
using Windows.Security.Cryptography.DataProtection;

namespace DTTS.Common;

/// <summary>
/// Windows DPAPI（現在ユーザー境界）を使って設定内の機密文字列を暗号化・復号する。
/// </summary>
public sealed class WindowsSettingsSecretProtector : ISettingsSecretProtector
{
    private const string Prefix = "dpapi:";
    private readonly DataProtectionProvider _provider = new("LOCAL=user");

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || plainText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plainText;
        }
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBuffer = _provider.ProtectAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
        return Prefix + Convert.ToBase64String(protectedBuffer.ToArray());
    }

    public string Unprotect(string storedText)
    {
        if (string.IsNullOrEmpty(storedText) || !storedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return storedText;
        }
        try
        {
            var raw = Convert.FromBase64String(storedText.Substring(Prefix.Length));
            var plainBuffer = _provider.UnprotectAsync(raw.AsBuffer()).AsTask().GetAwaiter().GetResult();
            return Encoding.UTF8.GetString(plainBuffer.ToArray());
        }
        catch
        {
            // 別ユーザー・別端末へDBを移行した場合などは復号できないため空文字にフォールバックする
            return string.Empty;
        }
    }
}
