using System;
using DTTS.Common;
using DTTS.Core.Enums;
using DTTS.Services.Network;
using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DTTS.Views;

public sealed partial class LobbyPage : Page, IBackRequestHandler
{
    /// <inheritdoc />
    public void OnBackRequested() => ViewModel.GoBackCommand.Execute(null);

    public LobbyViewModel ViewModel { get; }

    public LobbyPage()
    {
        ViewModel = App.Services.GetRequiredService<LobbyViewModel>();
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private async void OnJoinRoomClicked(object sender, RoutedEventArgs e)
    {
        // 行の DataContext は確実ではないので、テンプレートで Tag に入れた部屋の情報を使う
        if (sender is Button btn && (btn.Tag as RoomInfo ?? btn.DataContext as RoomInfo) is { } room)
        {
            await ViewModel.JoinRoomCommand.ExecuteAsync(room);
        }
    }

    // ===== x:Bind 用の表示ヘルパー =====

    public static string RoomCaption(string hostName, int openSeats, bool isPlaying)
        => isPlaying
            ? $"ホスト：{hostName} • 対戦中"
            : $"ホスト：{hostName} • 空き {openSeats} 席";

    public static string RoomRoleText(bool isHost)
        => isHost ? "あなたはホストです。参加者がそろったら対戦を始めましょう" : "参加しました。ホストの開始を待っています";

    public static string SeatLabel(int seatId) => $"席 {seatId + 1}";

    public static string SeatGlyph(PlayerKind kind) => kind == PlayerKind.Npc ? "" : "";

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
