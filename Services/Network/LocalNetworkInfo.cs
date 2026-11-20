using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DTTS.Services.Network;

/// <summary>
/// この端末の LAN 内アドレス（ホストが参加者に伝える IP）を調べる。
/// 動作中のネットワークアダプターの IPv4 アドレスだけを返し、既定ゲートウェイを持つもの（ふつうの Wi-Fi・有線 LAN）を先に並べる。
/// </summary>
public static class LocalNetworkInfo
{
    /// <summary>LAN 内の IPv4 アドレス（ループバック・リンクローカル 169.254.x.x は除く）。</summary>
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<(IPAddress Address, bool HasGateway)>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                {
                    continue;
                }
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue; // リンクローカル（DHCP に失敗したときの仮アドレス）
                }
                result.Add((address, hasGateway));
            }
        }
        return result
            .OrderByDescending(r => r.HasGateway)
            .Select(r => r.Address)
            .Distinct()
            .ToList();
    }
}
