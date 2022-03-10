using System;
using System.Collections.Generic;
using System.IO;

namespace Collections
{
    /// <summary>
    /// Adapted from System.Collections.Generic.Dictionary source code.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public class ConcurrentSlimHashSet<TKey, TValue> : IReadOnlyCollection<TValue>, IGetMemoryStats, IReadOnlyDictionary<TKey, TValue>, IEnumerable<TValue>
        where TValue : struct, IValueSnapshot<TKey>
        where TKey : struct
    {
        private struct Entry
        {
            public int Bucket;              // not related to entry - enabled us to eliminate separate bucket array
            public int HashCode;            // Lower 31 bits of hash code, -1 if unused
            public int Next;                // Index of next entry, -1 if last
            public TKey Key => Value.Key;   // Key of entry
            public TValue Value;            // Value of entry 
        }

        private enum InsertMode
        {
            // if key does not already exist the key value pair is added. 
            // If key already exists key value pair is added.
            AddOrReplace = 0,

            // if key does not already exist the key value pair is added. 
            //If key already exists an exception is thrown. 
            AddOnly = 1,

            // if key does not already exist the key value pair is added. 
            // If key already exists and new value is different from existing value as exception is thrown.
            AddOrConfirm = 2

        }
        private readonly object _writeLock = new object();
        private Entry[] _entries;
        private int _count;

        private readonly IEqualityComparer<TKey> _comparer;

        private const string SerializationMarkerStart = "Common.ConcurrentSlimHashSet.Start.1";
        private const string SerializationMarkerEnd = "Common.ConcurrentSlimHashSet.End";

        public void Serialize(Stream writeStream)
        {
            lock (_writeLock)
            {
                var bw = new BinaryWriter(writeStream);
                bw.Write(SerializationMarkerStart);
                bw.Write(_count);
                writeStream.WriteRawArray(_entries, _entries.Length);
                bw.Write(SerializationMarkerEnd);
            }
        }

        public ConcurrentSlimHashSet(Stream readStream, IEqualityComparer<TKey> comparer)
        {
            var br = new BinaryReader(readStream);
            var startStr = br.ReadString();
            if (string.CompareOrdinal(startStr, SerializationMarkerStart) != 0)
                throw new InvalidDataException("Invalid serialization start marker found in stream");
            this._count = br.ReadInt32();
            this._entries = (Entry[])(readStream.ReadRawArray<Entry>(RawReadAllocationMode.UseOriginalCapacity, 0, 0).Array);
            var endStr = br.ReadString();
            if (string.CompareOrdinal(endStr, SerializationMarkerEnd) != 0)
                throw new InvalidDataException("Invalid serialization end marker found in stream");
            if (comparer == null) comparer = EqualityComparer<TKey>.Default;
            this._comparer = comparer;
        }

        public ConcurrentSlimHashSet(Stream readStream) : this(readStream, null) { }

        public ConcurrentSlimHashSet() : this(0, null) { }

        public ConcurrentSlimHashSet(int capacity) : this(capacity, null) { }

        public ConcurrentSlimHashSet(IEqualityComparer<TKey> comparer) : this(0, comparer) { }

        public ConcurrentSlimHashSet(int capacity, IEqualityComparer<TKey> comparer)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= 0 (was " + capacity.ToString() + ")");
            Initialize(capacity);
            if (comparer == null) comparer = EqualityComparer<TKey>.Default;
            this._comparer = comparer;
        }

        public ConcurrentSlimHashSet(ICollection<TValue> collection, IEqualityComparer<TKey> comparer = null) :
            this(collection?.Count ?? 0, comparer)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection), "collection may not be null");
            }

            foreach (var value in collection)
            {
                Add(value);
            }
        }

        public IEqualityComparer<TKey> Comparer => _comparer;

        public int Count => _count;

        public int Capacity => _entries.Length;

        public IEnumerable<TKey> Keys
        {
            get
            {
                // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
                var tempCount = _count;
                var tempEntries = _entries;
                for (var i = 0; i < tempCount; i++)
                    yield return tempEntries[i].Key;
            }
        }

        public IEnumerable<TValue> Values
        {
            get
            {
                // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
                var tempCount = _count;
                var tempEntries = _entries;
                for (var i = 0; i < tempCount; i++)
                    yield return tempEntries[i].Value;
            }
        }

        // Readers are safe, Writers must lock
        public TValue this[TKey key]
        {
            get
            {
                // grab a local copy of the reference in case the entries array is resized 
                var tmpEntries = _entries;
                var i = FindEntry(tmpEntries, key);
                if (i >= 0) return tmpEntries[i].Value;
                throw new KeyNotFoundException();
            }
        }


        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Add(TValue value)
        {
            Insert(value, InsertMode.AddOnly);
        }

        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public bool AddOrConfirm(TValue value)
        {
            return Insert(value, InsertMode.AddOrConfirm);
        }

        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public bool AddOrReplace(TValue value)
        {
            return Insert(value, InsertMode.AddOrReplace);
        }

        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public bool ContainsKey(TKey key)
        {
            return FindEntry(_entries, key) >= 0;
        }

        private int FindEntry(Entry[] entries, TKey key)
        {
            if (entries == null) return -1;
            var hashCode = _comparer.GetHashCode(key) & 0x7FFFFFFF;
            for (var i = entries[hashCode % entries.Length].Bucket; i >= 0; i = entries[i].Next)
            {
                if (entries[i].HashCode == hashCode && _comparer.Equals(entries[i].Key, key)) return i;
            }
            return -1;
        }

        private void Initialize(int capacity)
        {
            var size = PrimeHelper.GetPrime(capacity);
            _entries = new Entry[size];
            for (var i = 0; i < _entries.Length; i++) _entries[i].Bucket = -1;
        }

        // Readers are safe, Writers must lock
        // returns true is item was inserted, false if item was replaced or confirmed
        private bool Insert(TValue value, InsertMode mode)
        {
            lock (_writeLock)
            {
                if (_entries == null) Initialize(0);

                // find the bucket to place the entry
                var hashCode = _comparer.GetHashCode(value.Key) & 0x7FFFFFFF;
                var targetBucket = hashCode % _entries.Length;

                // check if the key exists in the bucket
                for (var i = _entries[targetBucket].Bucket; i >= 0; i = _entries[i].Next)
                {
                    if (_entries[i].HashCode != hashCode || !_comparer.Equals(_entries[i].Key, value.Key)) continue;
                    // key exists
                    switch (mode)
                    {
                        case InsertMode.AddOrReplace:
                            _entries[i].Value = value;
                            return false;

                        case InsertMode.AddOrConfirm:
                            if (!value.Equals(_entries[i].Value))
                                throw new ArgumentException("key already exists with different value");
                            return false;

                        case InsertMode.AddOnly:
                            throw new ArgumentException("key already exists");
                    }
                }
                // key did not exist - add it to the end and link it to the bucket
                if (_count == _entries.Length)
                {
                    PrepareAdditionalCapacity(_count);
                    targetBucket = hashCode % _entries.Length;
                }
                _entries[_count].HashCode = hashCode;
                _entries[_count].Next = _entries[targetBucket].Bucket;
                _entries[_count].Value = value;
                _entries[targetBucket].Bucket = _count;
                _count++;
                return true;
            }
        }

        /// <summary>
        /// Readers are safe, Writers must lock
        /// </summary>
        /// <param name="additionalCapacity"></param>
        public void PrepareAdditionalCapacity(int additionalCapacity)
        {
            lock (_writeLock)
            {
                var newSize = PrimeHelper.GetPrime(_entries.Length + additionalCapacity);
                var newEntries = new Entry[newSize];
                Array.Copy(_entries, 0, newEntries, 0, _count);
                for (var i = 0; i < newEntries.Length; i++) newEntries[i].Bucket = -1;
                for (var i = 0; i < _count; i++)
                {
                    var bucket = newEntries[i].HashCode % newSize;
                    newEntries[i].Next = newEntries[bucket].Bucket;
                    newEntries[bucket].Bucket = i;
                }
                _entries = newEntries;
            }
        }

        public void EnsureCapacity(int capacity)
        {
            if (Capacity < capacity)
                PrepareAdditionalCapacity(capacity - Capacity);
        }
        public void EnsureAdditionalCapacity(int additionalCapacity)
        {
            if (_count + additionalCapacity >= _entries.Length)
                PrepareAdditionalCapacity(additionalCapacity);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            var tmpEntries = this._entries;
            var i = FindEntry(tmpEntries, key);
            if (i >= 0)
            {
                value = tmpEntries[i].Value;
                return true;
            }
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
            var tempCount = _count;
            var tempEntries = _entries;
            for (var i = 0; i < tempCount; i++)
                yield return new KeyValuePair<TKey, TValue>(tempEntries[i].Value.Key, tempEntries[i].Value);
        }

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
        {
            // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
            var tempCount = _count;
            var tempEntries = _entries;
            for (var i = 0; i < tempCount; i++)
                yield return tempEntries[i].Value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
            var tempCount = _count;
            var tempEntries = _entries;
            for (var i = 0; i < tempCount; i++)
                yield return tempEntries[i].Value;
        }

        #region IGetMemoryStats Members

        public MemoryStats GetMemoryStats()
        {
            return new MemoryStats(_entries, Count);
        }

        #endregion
    }


}
