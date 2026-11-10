using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SmartHdrManager
{
    /// <summary>
    /// Finds the local address that reaches the TV directly. A VPN client that takes over the default route would
    /// otherwise carry the connection into the tunnel, which makes a TV in the same LAN look offline, so the socket is
    /// bound to the physical LAN address that shares the TV's subnet.
    /// </summary>
    internal static class TvNetworkRoute
    {
        private const int MaxPrefixLength = 32;
        private const int BitsPerByte = 8;

        public static IPAddress? FindSourceAddress(string host)
        {
            if (!IPAddress.TryParse(host, out var target)) return null;
            if (target.AddressFamily != AddressFamily.InterNetwork) return null;

            IPAddress? otherCandidate = null;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsUsable(adapter)) continue;

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!SharesSubnet(unicast.Address, target, unicast.PrefixLength)) continue;

                    if (IsPhysical(adapter)) return unicast.Address;
                    otherCandidate ??= unicast.Address;
                }
            }

            return otherCandidate;
        }

        private static bool IsUsable(NetworkInterface adapter)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) return false;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
            return true;
        }

        private static bool IsPhysical(NetworkInterface adapter) =>
            adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit;

        private static bool SharesSubnet(IPAddress candidate, IPAddress target, int prefixLength)
        {
            if (prefixLength <= 0 || prefixLength > MaxPrefixLength) prefixLength = MaxPrefixLength;

            byte[] candidateBytes = candidate.GetAddressBytes();
            byte[] targetBytes = target.GetAddressBytes();
            if (candidateBytes.Length != targetBytes.Length) return false;

            for (int index = 0; index < candidateBytes.Length; index++)
            {
                int bits = Math.Min(BitsPerByte, Math.Max(0, prefixLength - index * BitsPerByte));
                int mask = bits == 0 ? 0 : (0xFF << (BitsPerByte - bits)) & 0xFF;
                if ((candidateBytes[index] & mask) != (targetBytes[index] & mask)) return false;
            }

            return true;
        }
    }
}
