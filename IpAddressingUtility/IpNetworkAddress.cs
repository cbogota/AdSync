using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IpAddressingUtility
{

    [Serializable, StructLayout(LayoutKind.Sequential)]
    public readonly struct IpNetworkAddress : IEquatable<IpNetworkAddress>
    {
        public int Address { get; }
        // layout of PackedData is:
        // bits 5..0: prefix length (0-32)
        // bit 6: hasPrefix
        // bit 7: IsCanonical
        private byte PackedData { get; }
        public int PrefixLength => PackedData & 0x3F;
        public bool HasPrefix => (PackedData & 0x40) != 0;
        public bool IsCanonical => (PackedData & 0x80) != 0;
        public bool IsHostAddress => HostSuffix != 0 || PrefixLength == 32;
        public bool IsNetworkAddress => HostSuffix == 0 && HasPrefix;
        public bool IsValid => PrefixLength >= 0 && PrefixLength <= 32;
        public bool IsEmpty => !IsValid;
        private int HostSuffix => (this.Address & ~PrefixLengthToMaskInteger(this.PrefixLength));
        private int NetworkPrefix => (this.Address & PrefixLengthToMaskInteger(this.PrefixLength));

        public IpNetworkAddress Subnet => new IpNetworkAddress(this.NetworkPrefix, this.PrefixLength, true, true);
        public System.Net.IPAddress IPAddress => IntegerToIPAddress(this.Address);

        public IpNetworkAddress(string newAddress)
        {
            TryParse(newAddress, out this);
        }

        public static bool TryParse(string newAddress, out IpNetworkAddress ipNetworkAddress)
        {
            ipNetworkAddress = new IpNetworkAddress(0, -1, false, false); 
            if (string.IsNullOrWhiteSpace(newAddress))
                return false;
            var strArray = newAddress.Split('/');
            if (!IPAddress.TryParse(strArray[0], out var ipAddress)) return false;
            var ipAddressPart = IPAddressToInteger(ipAddress);
            var prefix = 32;
            if (strArray.Length > 1 && (!int.TryParse(strArray[1], out prefix) || prefix < 0 || prefix > 32)) return false;
            ipNetworkAddress = new IpNetworkAddress(ipAddressPart, prefix, strArray.Length > 1, true);
            if (!newAddress.Equals(ipNetworkAddress.ToString(), StringComparison.Ordinal))
                ipNetworkAddress = new IpNetworkAddress(ipAddressPart, prefix, strArray.Length > 1, false);
            return true;
        }

        public IpNetworkAddress GetSupernet(int supernetPrefixLength)
        {
            if (HasPrefix && supernetPrefixLength > PrefixLength)
                throw new ArgumentException(
                    $"supernetPrefixLength ({supernetPrefixLength}) was not less than this prefix ({PrefixLength})");
            return new IpNetworkAddress(Address & PrefixLengthToMaskInteger(supernetPrefixLength), supernetPrefixLength, true, true);
        }

        private IpNetworkAddress(int newAddress, int newPrefixLength, bool hasPrefix, bool isCanonical)
        {
            Address = newAddress;
            var packedData = (byte)(newPrefixLength & 0x3F);
            if (hasPrefix) packedData |= 0x40;
            if (isCanonical) packedData |= 0x80;
            PackedData = packedData;
        }

        public IpNetworkAddress(IPAddress newAddress) : this(IPAddressToInteger(newAddress), 32, false, true)
        {
        }

        public bool Contains(IpNetworkAddress testAddress) => IsNetworkAddress && PrefixLength <= testAddress.PrefixLength && Contains(testAddress.Address);

        public bool Contains(IPAddress testAddress) => IsNetworkAddress && Contains(new IpNetworkAddress(testAddress));

        public bool Contains(int testAddress) => IsNetworkAddress && Address == (testAddress & PrefixLengthToMaskInteger(PrefixLength));

        public bool IsAllNetworks() => Address == 0 && PrefixLength == 0;

        public static IpNetworkAddress AllNetworks => new IpNetworkAddress(0, 0, true, true);
        public bool IsNoNetwork() => Address == 0 && PrefixLength == 32;

        private static readonly IpNetworkAddress[] PrivateIpBlocks = {new IpNetworkAddress("10.0.0.0/8"), new IpNetworkAddress("172.16.0.0/12"), new IpNetworkAddress("192.168.0.0/16")};

        public bool ContainsPrivate
        {
            get
            {
                var me = this;
                return PrivateIpBlocks.Any(p => p.Contains(me.Address) || me.Contains(p));
            }
        }

        public bool IsPrivate
        {
            get
            {
                var me = this;
                return PrivateIpBlocks.Any(p => p.Contains(me.Address));
            }
        }

        public static IpNetworkAddress NoNetwork => new IpNetworkAddress(0, 32, true, true);

        public bool Equals(IpNetworkAddress other) => other.Address == Address && other.PrefixLength == PrefixLength;

        public override bool Equals(object obj)
        {
            return base.Equals(obj is IpNetworkAddress && Equals((IpNetworkAddress)obj));
        }
        public override string ToString() => HasPrefix ? $@"{IPAddress}/{PrefixLength}" : IPAddress.ToString();

        public override int GetHashCode()
        {
            var hashCode = 460605596;
            hashCode = hashCode * -1521134295 + Address.GetHashCode();
            hashCode = hashCode * -1521134295 + PrefixLength.GetHashCode();
            return hashCode;
        }

        public static IpNetworkAddress LocalIpNetworkAddress
        {
            get
            {
                IPAddress localIp;
                // create a UDP type socket to a google address (no packets are actually sent)
                // so that we can determine the local IP address used for internet bound connectivity
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    // if no local end point was assigned, we must have no active network interface, return NoNetwork
                    if (!(socket.LocalEndPoint is IPEndPoint endPoint) || endPoint.Address == null) return NoNetwork;
                    localIp = endPoint.Address;
                }
                // now lets figure out the prefix mask for this address...
                var localIpConfig =
                    NetworkInterface.GetAllNetworkInterfaces()
                        .Where(
                            nic =>
                                nic.OperationalStatus == OperationalStatus.Up &&
                                nic.GetIPProperties()?.UnicastAddresses != null)
                        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                        .FirstOrDefault(ua => System.Net.IPAddress.Equals(localIp, ua?.Address));
                return localIpConfig == null ? NoNetwork : new IpNetworkAddress($"{localIp}/{localIpConfig.PrefixLength}");
            }
        }

        public static bool operator ==(IpNetworkAddress address1, IpNetworkAddress address2)
        {
            return address1.Equals(address2);
        }

        public static bool operator !=(IpNetworkAddress address1, IpNetworkAddress address2)
        {
            return !(address1 == address2);
        }

        public static IPAddress IntegerToIPAddress(int address)
        {
            return new IPAddress(IPAddress.HostToNetworkOrder(address) & ((long)0xffffffffL));
        }

        public static int IPAddressToInteger(IPAddress ipAddress)
        {
            return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(ipAddress.GetAddressBytes(), 0));
        }

        public static int PrefixLengthToMaskInteger(int prefix)
        {
            if (prefix < 0 || prefix > 32) throw new ArgumentException("prefix length must be between 0 and 32", nameof(prefix));
            return prefix == 32 ? -1 : prefix > 0 ? -1 << (32 - prefix) : 0;
        }

        public static implicit operator IpNetworkAddress(string a) => new IpNetworkAddress(a);
        public static implicit operator string(IpNetworkAddress a) => a.ToString();
        /// <summary>
        /// Returns the enumeration of the supernets of this IpNetworkAddress's supernets
        /// The range of supernets is restricted to the requested range of prefixes
        /// The maximum prefix used will be the minimum of the provided maximum and this IpNetworkAddresses prefix
        /// Supernets are return in descending order of PrefixLenth (from most specific subnet to least specific subnet)
        /// </summary>
        /// <param name="minPrefix"></param>
        /// <param name="maxPrefix"></param>
        /// <returns></returns>
        public IEnumerable<IpNetworkAddress> Supernets(int minPrefixLength, int maxPrefixLength)
        {
            maxPrefixLength = Math.Min(maxPrefixLength, PrefixLength);
            for (var prefixLength = maxPrefixLength; prefixLength >= minPrefixLength; prefixLength--)
            {
                yield return GetSupernet(prefixLength);
            }
        }
    }
}
