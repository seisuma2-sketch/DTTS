using System;
using DTTS.Services.Feedback;
using Windows.Networking.Connectivity;

namespace DTTS.Common;

/// <summary>
/// Windows の <see cref="NetworkInformation"/> を利用したネットワーク接続状態モニター。
/// </summary>
public sealed class WindowsConnectivityMonitor : IConnectivityMonitor, IDisposable
{
    private bool _disposed;

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
        if (_disposed) return;
        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }
}

