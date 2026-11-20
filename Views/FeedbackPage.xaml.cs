using System;
using DTTS.Common;
using DTTS.Services.Database;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using Windows.System;

namespace DTTS.Views;

public sealed partial class FeedbackPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public FeedbackViewModel ViewModel { get; }

    public FeedbackPage()
    {
        ViewModel = App.Services.GetRequiredService<FeedbackViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("FeedbackPage.OnPageLoaded", ex);
        }
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ViewModel.SelectedCategory = tag switch
            {
                "Bug" => FeedbackCategory.Bug,
                "Request" => FeedbackCategory.Request,
                _ => FeedbackCategory.Comment
            };
        }
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ViewModel.SendMessageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnExportJsonClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("JSON File", new[] { ".json" });
            picker.SuggestedFileName = $"dtts_feedback_{DateTime.Now:yyyyMMdd_HHmm}.json";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await ViewModel.ExportJsonCommand.ExecuteAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("FeedbackPage.ExportJson", ex);
        }
    }
}
