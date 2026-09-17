using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace AIDeck.Platform
{
    /// <summary>
    /// Device facts the app shows or sends.
    ///
    /// Only what the requirements actually need: a friendly device name for the handshake and
    /// the LAN address the Mac UI displays so a user can type it into the iPad (§5.6, FR-061).
    /// Nothing here reads a serial number, an advertising identifier or anything tied to the
    /// Apple Account — NFR-006 forbids putting that in a log or on the wire.
    /// </summary>
    public static class DeviceInfo
    {
        /// <summary>Human-readable name, e.g. "Studio Mac" or "iPad mini".</summary>
        public static string FriendlyName
        {
            get
            {
                var name = SystemInfo.deviceName;
                if (!string.IsNullOrWhiteSpace(name) && name != SystemInfo.unsupportedIdentifier)
                {
                    return name;
                }

                return Application.platform == RuntimePlatform.IPhonePlayer ? "iPad" : "Mac";
            }
        }

        public static string AppVersion => Application.version;

        /// <summary>
        /// The device's IPv4 address on the LAN, or an empty string when there is none.
        ///
        /// Picks the first up, non-loopback interface with an IPv4 address, preferring
        /// Wi-Fi and Ethernet. Link-local 169.254.x.x addresses are skipped: showing one
        /// would send the user off typing an address that cannot work.
        /// </summary>
        public static string LocalIPv4()
        {
            try
            {
                string fallback = null;

                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    var preferred = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                    adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet;

                    foreach (var info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        var text = info.Address.ToString();
                        if (text.StartsWith("127.", StringComparison.Ordinal) ||
                            text.StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (preferred)
                        {
                            return text;
                        }

                        fallback ??= text;
                    }
                }

                return fallback ?? string.Empty;
            }
            catch (Exception)
            {
                // Enumerating interfaces can fail under sandboxing. An empty address makes the
                // UI say "unknown", which is honest; manual entry still works.
                return string.Empty;
            }
        }

        /// <summary>All usable IPv4 addresses, for the Mac UI when a machine is multi-homed.</summary>
        public static string[] AllLocalIPv4()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var result = new System.Collections.Generic.List<string>();
                foreach (var address in host.AddressList)
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var text = address.ToString();
                    if (text.StartsWith("127.", StringComparison.Ordinal) ||
                        text.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result.Add(text);
                }

                return result.ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
