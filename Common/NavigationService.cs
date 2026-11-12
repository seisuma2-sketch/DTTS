using System;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Common;

/// <summary>
/// アプリ内のページ遷移管理インターフェース。
/// </summary>
public interface INavigationService
{
    Frame? RootFrame { get; set; }
    bool CanGoBack { get; }
    void GoBack();
    bool Navigate(Type pageType, object? parameter = null);
    bool NavigateToTitle();
    bool NavigateToGameTable(object? parameter = null);
    bool NavigateToRuleSettings();
    bool NavigateToResult(object? parameter = null);
    bool NavigateToHelp();
    bool NavigateToFeedback();
}

public sealed class NavigationService : INavigationService
{
    public Frame? RootFrame { get; set; }

    public bool CanGoBack => RootFrame?.CanGoBack ?? false;

    public void GoBack()
    {
        if (CanGoBack)
        {
            RootFrame?.GoBack();
        }
    }

    public bool Navigate(Type pageType, object? parameter = null)
    {
        if (RootFrame == null) return false;
        return RootFrame.Navigate(pageType, parameter);
    }

    public bool NavigateToTitle() => Navigate(typeof(Views.TitlePage));
    public bool NavigateToGameTable(object? parameter = null) => Navigate(typeof(Views.GameTablePage), parameter);
    public bool NavigateToRuleSettings() => Navigate(typeof(Views.RuleSettingsPage));
    public bool NavigateToResult(object? parameter = null) => Navigate(typeof(Views.ResultPage), parameter);
    public bool NavigateToHelp() => Navigate(typeof(Views.HelpPage));
    public bool NavigateToFeedback() => Navigate(typeof(Views.FeedbackPage));
}

