using System;
using System.Collections.Generic;
using System.Linq;
using Collections;

namespace IpAddressingUtility
{
    /// <summary>
    /// A searchable set of ipv4 network subnets, optimized to enable rapid determination of most-specific-subnet matches for arbitrary ip addresses
    /// </summary>
    /// <typeparam name="TNode">arbitrary node data (TNode) to be associated with each subnet node</typeparam>
    public class IpNetworkSearcher<TNode> where TNode : struct, IValueSnapshot<IpNetworkAddress>
    {
        private ConcurrentSlimHashSet<IpNetworkAddress, TNode> Subnets { get; }
        private int _minPrefix;
        private int _maxPrefix;

        public void Serialize(System.IO.Stream writeStream)
        {
            Subnets.Serialize(writeStream);
        }

        public IpNetworkSearcher(System.IO.Stream readStream)
        {
            Subnets = new ConcurrentSlimHashSet<IpNetworkAddress, TNode>(readStream);
            _minPrefix = 32;
            _maxPrefix = 0;
            foreach (var subnet in Subnets.Values)
                if (!subnet.IsDeleted)
                {
                    if (subnet.Key.PrefixLength > _maxPrefix)
                        _maxPrefix = subnet.Key.PrefixLength;
                    if (subnet.Key.PrefixLength < _minPrefix)
                        _minPrefix = subnet.Key.PrefixLength;
                }
        }

        public IpNetworkSearcher(int capacity)
        {
            Subnets = new ConcurrentSlimHashSet<IpNetworkAddress, TNode>();
            Subnets.PrepareAdditionalCapacity(capacity);
            _minPrefix = 32;
            _maxPrefix = 0;
        }

        public void EnsureCapacity(int capacity)
        {
            Subnets.EnsureCapacity(capacity);
        }
        /// <summary>
        /// Returns the most specific subnet that contains the provided address and its subnet.
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public (IpNetworkAddress Subnet, TNode Data) SubnetSearch(IpNetworkAddress address)
        {
            foreach (var supernet in address.Supernets(_minPrefix, _maxPrefix))
                if (Subnets.TryGetValue(supernet, out var found) && !found.IsDeleted)
                    return (supernet, found);
            return (null, default);
        }
        /// <summary>
        /// Returns the node data associated with a specific subnet. If the subnet is unknown, null is returned.
        /// </summary>
        /// <param name="subnet"></param>
        /// <returns></returns>
        public TNode this[IpNetworkAddress subnet]
        {
            get
            {
                Subnets.TryGetValue(subnet, out var result);
                return result;
            }
        }

        public bool AddOrReplace(TNode value)
        {
            _minPrefix = Math.Min(_minPrefix, value.Key.PrefixLength);
            _maxPrefix = Math.Max(_maxPrefix, value.Key.PrefixLength);
            return Subnets.AddOrReplace(value);
        }

        public IEnumerable<TNode> Entries()
        {
            return Subnets.Values.Where(s => !s.IsDeleted);
        }

    }
}
