using System;
using DTTS.Common;
using DTTS.Core.Enums;
using DTTS.Services.Network;
using DTTS.Services.Settings;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace DTTS.Views;

public sealed partial class LobbyPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public LobbyViewModel ViewModel { get; }

    private bool _autoHostOnLoad;

    public LobbyPage()
    {
        ViewModel = App.Services.GetRequiredService<LobbyViewModel>();
        InitializeComponent();
    }

    /// <summary>ウィンドウ最小化／非アクティブ時に待機スピナーの無限アニメーションを止める。</summary>
    public void SetWindowMinimized(bool minimized)
    {
        ViewModel.IsWindowSuspended = minimized;
        if (SearchProgressRing is not null)
        {
            SearchProgressRing.IsActive = !minimized && ViewModel.IsSearching;
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _autoHostOnLoad = e.Parameter is string s && s == "auto_host";
    }

    protected override async void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        try
        {
            await ViewModel.OnNavigatedFromAsync();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnNavigatedFrom", ex);
        }
    }

    /// <summary>ウィンドウ終了時にネットワークセッションを確実に閉じる。</summary>
    internal System.Threading.Tasks.Task PrepareForWindowCloseAsync()
        => ViewModel.OnNavigatedFromAsync();

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync(_autoHostOnLoad);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnPageLoaded", ex);
            ViewModel.StatusMessage = $"初期化に失敗しました: {ex.Message}";
        }
    }

    private async void OnJoinRoomClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // 行の DataContext は確実ではないので、テンプレートで Tag に入れた部屋の情報を使う
            if (sender is Button btn && (btn.Tag as RoomInfo ?? btn.DataContext as RoomInfo) is { } room)
            {
                await ViewModel.JoinRoomCommand.ExecuteAsync(room);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnJoinRoomClicked", ex);
            ViewModel.StatusMessage = $"参加処理でエラーが発生しました: {ex.Message}";
        }
    }

    private async void OnPickAvatarClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                await ViewModel.SetCustomAvatarAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnPickAvatarClicked", ex);
            ViewModel.StatusMessage = $"画像の選択に失敗しました: {ex.Message}";
        }
    }

    private async void OnPlayerIconClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is string iconId)
            {
                await ViewModel.SelectPlayerIconCommand.ExecuteAsync(iconId);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnPlayerIconClicked", ex);
        }
    }

    private async void OnThemeSwatchClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is ThemeColor color)
            {
                await ViewModel.SelectThemeColorCommand.ExecuteAsync(color);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnThemeSwatchClicked", ex);
        }
    }

    private async void OnApplyCustomPresetClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is CustomRulePreset preset)
            {
                await ViewModel.ApplyCustomPresetCommand.ExecuteAsync(preset);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnApplyCustomPresetClicked", ex);
            ViewModel.StatusMessage = $"プリセットの適用に失敗しました: {ex.Message}";
        }
    }

    private async void OnDeleteCustomPresetClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is CustomRulePreset preset)
            {
                await ViewModel.DeleteCustomPresetCommand.ExecuteAsync(preset);
            }
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("LobbyPage.OnDeleteCustomPresetClicked", ex);
            ViewModel.StatusMessage = $"プリセットの削除に失敗しました: {ex.Message}";
        }
    }

    private void OnCopyIpClicked(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.LocalAddressText;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("LAN に接続", StringComparison.Ordinal)) return;
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        ViewModel.StatusMessage = "接続用アドレスをクリップボードにコピーしました";
    }

    private void OnCopyPinClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.HostJoinPin)) return;
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(ViewModel.HostJoinPin);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        ViewModel.StatusMessage = $"参加 PIN（{ViewModel.HostJoinPin}）をコピーしました";
    }

    private void OnKickSeatClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SeatInfo seat)
        {
            ViewModel.KickSeatCommand.Execute(seat);
        }
    }

    private void OnToggleSeatNpcClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SeatInfo seat)
        {
            ViewModel.ToggleSeatNpcCommand.Execute(seat);
        }
    }

    private void OnHostSeatControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.Visibility = ViewModel.IsHost ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public static Visibility IsReadyBadgeVisible(PlayerKind kind, bool isReady)
        => kind == PlayerKind.Remote && isReady ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CanKickSeatVisible(bool isSeatHost, PlayerKind kind)
        => !isSeatHost && kind == PlayerKind.Remote ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CanLockSeatVisible(bool isSeatHost)
        => !isSeatHost ? Visibility.Visible : Visibility.Collapsed;

    public static string SeatLockButtonLabel(bool isLocked)
        => isLocked ? "開放する" : "NPC専用";

    public static string ReadyButtonLabel(bool isReady)
        => isReady ? "✓ 準備完了（クリックで解除）" : "準備完了にする";

    // ===== x:Bind 用の表示ヘルパー =====

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush FallbackGrayBrush = new(Colors.Gray);

    public static Brush HexToBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 7 || hex[0] != '#')
        {
            return FallbackGrayBrush;
        }
        return BrushCache.GetOrAdd(hex, static h =>
        {
            byte r = Convert.ToByte(h.Substring(1, 2), 16);
            byte g = Convert.ToByte(h.Substring(3, 2), 16);
            byte b = Convert.ToByte(h.Substring(5, 2), 16);
            return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
        });
    }

    public static string IconGlyphById(string? id) => PlayerIconInfo.Get(id).Glyph;

    public static ImageSource? AvatarImageSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return null;
        }
        try
        {
            return new BitmapImage(new Uri(path)) { DecodePixelWidth = 128 };
        }
        catch
        {
            return null;
        }
    }

    public static Visibility HasCustomAvatar(string? path)
        => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static Visibility NoCustomAvatar(string? path)
        => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public static Visibility CountToVisibility(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string RoomCaption(string hostName, int openSeats, bool isPlaying)
        => isPlaying
            ? $"ホスト：{hostName} • 対戦中"
            : $"ホスト：{hostName} • 空き {openSeats} 席";

    public static string RoomRoleText(bool isHost)
        => isHost ? "あなたはホストです。参加者がそろったら対戦を始めましょう" : "参加しました。ホストの開始を待っています";

    public static string SeatLabel(int seatId) => $"席 {seatId + 1}";

    public static string SeatGlyph(PlayerKind kind, string? avatarIcon = null)
        => kind == PlayerKind.Npc ? "\uE99A" : PlayerIconInfo.Get(avatarIcon).Glyph;

    public static string SeatKindText(PlayerKind kind, bool isConnected) => kind switch
    {
        PlayerKind.Npc => "NPC",
        PlayerKind.Remote => isConnected ? "プレイヤー（接続中）" : "プレイヤー（切断）",
        _ => "プレイヤー",
    };

    private static Color Accent()
        => Application.Current.Resources.TryGetValue("AppAccentColor", out var c) && c is Color color
            ? color
            : ColorHelper.FromArgb(255, 0x3E, 0x8E, 0xF7);

    /// <summary>人の席はテーマカラー、NPC の席は落ち着いた灰色の縁にする。</summary>
    public static Brush SeatBorder(PlayerKind kind)
    {
        if (kind == PlayerKind.Npc)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        var a = Accent();
        return new SolidColorBrush(ColorHelper.FromArgb(0xB0, a.R, a.G, a.B));
    }

    public static Brush SeatIconBackground(PlayerKind kind)
    {
        if (kind == PlayerKind.Npc)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x2E, 0x36, 0x46));
        }
        var a = Accent();
        return new SolidColorBrush(ColorHelper.FromArgb(0x55, a.R, a.G, a.B));
    }
}
