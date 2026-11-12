using System;
using DTTS.Services.Feedback;
using Windows.Networking.Connectivity;

namespace DTTS.Common;

/// <summary>
/// Windows の <see cref="NetworkInformation"/> を利用したネットワーク接続状態モニター。
/// </summary>
public sealed class WindowsConnectivityMonitor : IConnectivityMonitor
{
    public event EventHandler? ConnectivityChanged;

    public WindowsConnectivityMonitor()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public bool IsOnline
    {
        get
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                return profile != null && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return false;
            }
        }
    }

    private void OnNetworkStatusChanged(object? sender)
    {
        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }
}

