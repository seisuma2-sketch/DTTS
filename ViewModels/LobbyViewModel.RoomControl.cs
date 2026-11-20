using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DTTS.Core.Enums;
using DTTS.Services.Network;

namespace DTTS.ViewModels;

/// <summary>
/// ロビーの部屋管理：参加 PIN・この端末の IP アドレス・準備完了・ホストによる「外す」「NPC 席に切り替え」。
/// 通信の中身はすべて暗号化されるため、PIN も経路上では読めない。
/// </summary>
public sealed partial class LobbyViewModel
{
    // ------------------------------------------------------------------
    // 参加 PIN
    // ------------------------------------------------------------------

    /// <summary>PIN の桁数。</summary>
    public const int JoinPinLength = 6;

    /// <summary>部屋を作るとき、参加 PIN を求めるか（ホスト用）。</summary>
    [ObservableProperty]
    public partial bool UseJoinPin { get; set; } = true;

    /// <summary>この部屋の参加 PIN（ホスト用。参加者に口頭などで伝える）。</summary>
    [ObservableProperty]
    public partial string HostJoinPin { get; set; } = GenerateJoinPin();

    /// <summary>参加するときに入力する PIN（参加者用。PIN なしの部屋なら空のままでよい）。</summary>
    [ObservableProperty]
    public partial string JoinPinInput { get; set; } = "";

    /// <summary>部屋を作るときに使う PIN（PIN を使わない設定なら null）。</summary>
    private string? EffectiveHostPin => UseJoinPin && !string.IsNullOrWhiteSpace(HostJoinPin) ? HostJoinPin.Trim() : null;

    /// <summary>参加するときに送る PIN（空なら null）。</summary>
    private string? EffectiveJoinPin => string.IsNullOrWhiteSpace(JoinPinInput) ? null : JoinPinInput.Trim();

    /// <summary>暗号論的に安全な乱数で、数字だけの PIN を作る。</summary>
    public static string GenerateJoinPin()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D" + JoinPinLength, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>PIN を作り直す（部屋を作る前だけ。作った後に変えても、その部屋の PIN は変わらない）。</summary>
    [RelayCommand]
    private void RegenerateJoinPin()
    {
        if (IsInRoom && IsHost)
        {
            StatusMessage = "PIN は部屋を作り直すと変わります";
            return;
        }
        HostJoinPin = GenerateJoinPin();
    }

    // ------------------------------------------------------------------
    // この端末の LAN 内アドレス（ホストが参加者へ伝える）
    // ------------------------------------------------------------------

    /// <summary>この端末の LAN 内 IPv4 アドレス（Wi-Fi・有線 LAN のものを先に）。</summary>
    public ObservableCollection<string> LocalAddresses { get; } = new();

    /// <summary>参加者に伝えるアドレスの案内（例：「192.168.1.12:47815」）。</summary>
    [ObservableProperty]
    public partial string LocalAddressText { get; set; } = "";

    /// <summary>アドレスを調べ直す（Wi-Fi を切り替えたときなど）。</summary>
    [RelayCommand]
    private void RefreshLocalAddresses()
    {
        LocalAddresses.Clear();
        foreach (var address in LocalNetworkInfo.GetLocalIPv4Addresses())
        {
            LocalAddresses.Add(address.ToString());
        }
        int port = _hostSession is { Port: > 0 } session ? session.Port : NetProtocol.DefaultTcpPort;
        LocalAddressText = LocalAddresses.Count == 0
            ? "LAN に接続されていません"
            : string.Join(" / ", LocalAddresses.Select(a => $"{a}:{port}"));
    }

    // ------------------------------------------------------------------
    // 準備完了（参加者）
    // ------------------------------------------------------------------

    /// <summary>自分が準備完了にしているか（参加者用）。</summary>
    [ObservableProperty]
    public partial bool IsReady { get; set; }

    /// <summary>接続中の参加者が全員「準備完了」か（ホスト用。参加者がいなければ true）。</summary>
    [ObservableProperty]
    public partial bool AreAllGuestsReady { get; set; } = true;

    /// <summary>準備完了を切り替えてホストへ知らせる。</summary>
    [RelayCommand]
    private void ToggleReady()
    {
        if (IsHost || _client is null || !_client.IsConnected) return;
        bool next = !IsReady;
        if (_client.SetReady(next))
        {
            IsReady = next;
            StatusMessage = next ? "準備完了にしました。ホストの開始を待っています..." : "準備完了を取り消しました";
        }
    }

    /// <summary>席の一覧が変わったとき、全員の準備が整ったかを更新する。</summary>
    private void UpdateReadiness()
    {
        AreAllGuestsReady = Seats.Where(s => s.Kind == PlayerKind.Remote && s.IsConnected).All(s => s.IsReady);
    }

    // ------------------------------------------------------------------
    // ホストの席管理：外す・NPC 席に切り替え
    // ------------------------------------------------------------------

    /// <summary>参加者を部屋から外す（その端末はこの部屋に入り直せない）。</summary>
    [RelayCommand]
    private void KickSeat(SeatInfo? seat)
    {
        if (!IsHost || _hostSession is null || seat is null || seat.IsHost || seat.Kind != PlayerKind.Remote) return;
        _hostSession.KickSeat(seat.SeatId);
        StatusMessage = $"{seat.Name} を部屋から外しました";
    }

    /// <summary>
    /// 席を NPC 専用にする／参加者に開放する（ロビーでのみ）。参加者が座っている席を NPC にすると、その参加者は外れる。
    /// </summary>
    [RelayCommand]
    private void ToggleSeatNpc(SeatInfo? seat)
    {
        if (!IsHost || _hostSession is null || seat is null || seat.IsHost) return;
        bool lockIt = !seat.IsLocked;
        _hostSession.SetSeatLocked(seat.SeatId, lockIt);
        StatusMessage = lockIt
            ? $"席 {seat.SeatId} を NPC 専用にしました"
            : $"席 {seat.SeatId} を参加者に開放しました";
    }
}
