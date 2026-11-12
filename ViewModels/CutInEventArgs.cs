using System;

namespace DTTS.ViewModels;

/// <summary>
/// カットイン演出のパラメータ。
/// </summary>
public sealed class CutInEventArgs : EventArgs
{
    public string Title { get; }
    public string SubTitle { get; }
    public string Detail { get; }
    public string AccentColorHex { get; }

    public CutInEventArgs(string title, string subTitle, string detail, string accentColorHex = "#FFD700")
    {
        Title = title;
        SubTitle = subTitle;
        Detail = detail;
        AccentColorHex = accentColorHex;
    }
}

