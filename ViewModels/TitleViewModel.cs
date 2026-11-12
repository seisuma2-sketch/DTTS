using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Services.Database;
using DTTS.Services.Settings;

namespace DTTS.ViewModels;

public sealed partial class TitleViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly SettingsRepository _settingsRepo;

    public TitleViewModel(INavigationService navigation, SettingsRepository settingsRepo)
    {
        _navigation = navigation;
        _settingsRepo = settingsRepo;
    }

    [RelayCommand]
    private void StartSinglePlayer()
    {
        _navigation.NavigateToGameTable();
    }

    [RelayCommand]
    private void OpenLobby()
    {
        _navigation.NavigateToLobby();
    }

    [RelayCommand]
    private void OpenRuleSettings()
    {
        _navigation.NavigateToRuleSettings();
    }

    [RelayCommand]
    private void OpenResult()
    {
        _navigation.NavigateToResult();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        _navigation.NavigateToHelp();
    }

    [RelayCommand]
    private void OpenFeedback()
    {
        _navigation.NavigateToFeedback();
    }
}

