using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Common;
using DTTS.Services.Database;
using DTTS.Services.Feedback;
using DTTS.Services.Settings;

namespace DTTS.ViewModels;

public sealed record FeedbackItemViewModel(
    string CategoryText,
    string TimeText,
    string StatusText,
    string Message
);

public sealed partial class FeedbackViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly FeedbackService _feedbackService;
    private readonly SettingsRepository _settingsRepo;

    public ObservableCollection<FeedbackItemViewModel> Messages { get; } = new();

    [ObservableProperty]
    private string _inputMessage = "";

    [ObservableProperty]
    private FeedbackCategory _selectedCategory = FeedbackCategory.Request;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _webhookUrl = "";

    public FeedbackViewModel(
        INavigationService navigation,
        FeedbackService feedbackService,
        SettingsRepository settingsRepo)
    {
        _navigation = navigation;
        _feedbackService = feedbackService;
        _settingsRepo = settingsRepo;
    }

    public async Task InitializeAsync()
    {
        var options = await _settingsRepo.LoadFeedbackOptionsAsync();
        WebhookUrl = options.WebhookUrl ?? "";

        await ReloadMessagesAsync();
    }

    public async Task ReloadMessagesAsync()
    {
        Messages.Clear();
        var list = await _feedbackService.GetHistoryAsync();
        foreach (var item in list)
        {
            string catText = item.Category switch
            {
                FeedbackCategory.Bug => "不具合報告",
                FeedbackCategory.Request => "機能要望",
                _ => "感想・他"
            };
            string timeText = item.CreatedAt.LocalDateTime.ToString("MM/dd HH:mm");
            string statusText = item.Status == FeedbackStatus.Sent ? "送信済" : "未送信";

            Messages.Add(new FeedbackItemViewModel(catText, timeText, statusText, item.Message));
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        string text = InputMessage.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputMessage = "";
        await _feedbackService.SubmitAsync(text, SelectedCategory);
        await ReloadMessagesAsync();
        StatusMessage = "フィードバックを保存しました";
    }

    [RelayCommand]
    private async Task SyncPendingAsync()
    {
        StatusMessage = "送信中...";
        var result = await _feedbackService.SendPendingAsync();
        await ReloadMessagesAsync();
        StatusMessage = $"送信完了: {result.Sent}件成功, {result.Failed}件失敗";
    }

    [RelayCommand]
    private async Task ExportJsonAsync(string filePath)
    {
        try
        {
            using var stream = File.Create(filePath);
            await _feedbackService.ExportAsync(stream);
            StatusMessage = "JSONエクスポートが完了しました";
        }
        catch (Exception ex)
        {
            StatusMessage = $"エクスポートエラー: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveWebhookUrlAsync()
    {
        var options = await _settingsRepo.LoadFeedbackOptionsAsync();
        options.WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim();
        await _settingsRepo.SaveFeedbackOptionsAsync(options);
        StatusMessage = "Webhook URLを保存しました";
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.NavigateToTitle();
    }
}
