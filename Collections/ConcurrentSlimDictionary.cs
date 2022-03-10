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
    public class ConcurrentSlimDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey,TValue>>, IGetMemoryStats, IReadOnlyDictionary<TKey, TValue>
    {
        private struct Entry
        {
            public int bucket;      // not related to entry - enabled us to eliminate seperate bucket array
            public int hashCode;    // Lower 31 bits of hash code, -1 if unused
            public int next;        // Index of next entry, -1 if last
            public TKey key;           // Key of entry
            public TValue value;         // Value of entry 
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
        private Entry[] entries;
        private int count;
        private int nullValueCount;

        private IEqualityComparer<TKey> comparer;

        private const string SerializationMarkerStart = "Common.ConcurrentSlimDictionary.Start.1";
        private const string SerializationMarkerEnd = "Common.ConcurrentSlimDictionary.End";

        public void Serialize(Stream writeStream)
        {            
            lock (_writeLock)
            {
                var bw = new BinaryWriter(writeStream);
                bw.Write(SerializationMarkerStart);
                bw.Write(count);
                bw.Write(nullValueCount);
                writeStream.WriteRawArray(entries, entries.Length);
                bw.Write(SerializationMarkerEnd);
            }
        }

        public ConcurrentSlimDictionary(Stream readStream, IEqualityComparer<TKey> comparer)
        {
            var br = new BinaryReader(readStream);
            var startStr = br.ReadString();
            if (string.CompareOrdinal(startStr, SerializationMarkerStart) != 0)
                throw new InvalidDataException("Invalid serialization start marker found in stream");
            this.count = br.ReadInt32();
            this.nullValueCount = br.ReadInt32();
            this.entries = (Entry[])(readStream.ReadRawArray<Entry>(RawReadAllocationMode.UseOriginalCapacity,0,0).Array);
            var endStr = br.ReadString();
            if (string.CompareOrdinal(endStr, SerializationMarkerEnd) != 0)
                throw new InvalidDataException("Invalid serialization end marker found in stream");
            if (comparer == null) comparer = EqualityComparer<TKey>.Default;
            this.comparer = comparer;
        }

        public ConcurrentSlimDictionary(Stream readStream) : this(readStream, null) { }

        public ConcurrentSlimDictionary() : this(0, null) { }

        public ConcurrentSlimDictionary(int capacity) : this(capacity, null) { }

        public ConcurrentSlimDictionary(IEqualityComparer<TKey> comparer) : this(0, comparer) { }

        public ConcurrentSlimDictionary(int capacity, IEqualityComparer<TKey> comparer)
        {
            if (capacity < 0) throw new ArgumentOutOfRangeException("capacity", "capacity must be >= 0 (was " + capacity.ToString() + ")");
            Initialize(capacity);
            if (comparer == null) comparer = EqualityComparer<TKey>.Default;
            this.comparer = comparer;
        }

        public ConcurrentSlimDictionary(IDictionary<TKey, TValue> dictionary) : this(dictionary, null) { }

        public ConcurrentSlimDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer) :
            this(dictionary != null ? dictionary.Count : 0, comparer)
        {

            if (dictionary == null)
            {
                throw new ArgumentNullException("dictionary", "dictionary may not be null");
            }

            foreach (KeyValuePair<TKey, TValue> pair in dictionary)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public IEqualityComparer<TKey> Comparer
        {
            get
            {
                return comparer;
            }
        }

        public int Count
        {
            get { return count; }
        }

        public int NullValueCount
        {
            get { return nullValueCount; }
        }

        public int Capacity
        {
            get { return entries.Length; }
        }

        public IEnumerable<TKey> Keys
        {
            get
            {
                // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
                var tempCount = count;
                var tempEntries = entries;
                for (var i = 0; i < tempCount; i++)
                    yield return tempEntries[i].key;
            }
        }

        public IEnumerable<TValue> Values
        {
            get
            {
                // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
                var tempCount = count;
                var tempEntries = entries;
                for (var i = 0; i < tempCount; i++)
                    yield return tempEntries[i].value;
            }
        }

        // Readers are safe, Writers must lock
        public TValue this[TKey key]
        {
            get
            {
                // grab a local copy of the reference in case the entries array is resized 
                Entry[] tmpEntries = this.entries;
                int i = FindEntry(tmpEntries, key);
                if (i >= 0) return tmpEntries[i].value;
                throw new KeyNotFoundException();
            }
            set
            {
                Insert(key, value, InsertMode.AddOrReplace);
            }
        }

        
        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Add(TKey key, TValue value)
        {
            Insert(key, value, InsertMode.AddOnly);
        }

        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void AddOrConfirm(TKey key, TValue value)
        {
            Insert(key, value, InsertMode.AddOrConfirm);
        }

        /// <summary>
        /// Readers are safe, Writers must lock 
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public bool ContainsKey(TKey key)
        {
            return FindEntry(entries, key) >= 0;
        }

        private int FindEntry(Entry[] entries, TKey key)
        {
            if (key == null)
            {
                throw new ArgumentNullException("key", "key must not be null");
            }

            if (entries != null)
            {
                int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
                for (int i = entries[hashCode % entries.Length].bucket; i >= 0; i = entries[i].next)
                {
                    if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key)) return i;
                }
            }
            return -1;
        }

        private void Initialize(int capacity)
        {
            int size = PrimeHelper.GetPrime(capacity);
            entries = new Entry[size];
            for (int i = 0; i < entries.Length; i++) entries[i].bucket = -1;
        }

        // Readers are safe, Writers must lock
        private void Insert(TKey key, TValue value, InsertMode mode)
        {

            if (key == null)
            {
                throw new ArgumentNullException("key", "key may not be null");
            }

            lock (_writeLock)
            {
                if (entries == null) Initialize(0);

                // find the bucket to place the entry
                int hashCode = comparer.GetHashCode(key) & 0x7FFFFFFF;
                int targetBucket = hashCode % entries.Length;
                
                // check if the key exists in the bucket
                for (int i = entries[targetBucket].bucket; i >= 0; i = entries[i].next)
                {
                    if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                    {
                        // key exists
                        switch (mode)
                        {
                            case InsertMode.AddOrReplace:
                                if (value == null && entries[i].value != null)
                                    nullValueCount++;
                                else if (value != null && entries[i].value == null)
                                    nullValueCount--;

                                entries[i].value = value;
                                break;

                            case InsertMode.AddOrConfirm:
                                if ((value == null && entries[i].value != null) ||
                                    (value != null && entries[i].value == null) ||
                                    (value != null && entries[i].value != null && !value.Equals(entries[i].value)))
                                    throw new ArgumentException("key already exists with different value");
                                break;

                            case InsertMode.AddOnly:
                                throw new ArgumentException("key already exists");
                        }
                        return;
                    }
                }
                // key did not exist - add it to the end and link it to the bucket
                if (count == entries.Length)
                {
                    PrepareAdditionalCapacity(count);
                    targetBucket = hashCode % entries.Length;
                }
                entries[count].hashCode = hashCode;
                entries[count].next = entries[targetBucket].bucket;
                entries[count].key = key;
                entries[count].value = value;
                entries[targetBucket].bucket = count;
                if (value == null) nullValueCount++;
                count++;
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
                int newSize = PrimeHelper.GetPrime(entries.Length + additionalCapacity);
                Entry[] newEntries = new Entry[newSize];
                Array.Copy(entries, 0, newEntries, 0, count);
                for (int i = 0; i < newEntries.Length; i++) newEntries[i].bucket = -1;
                for (int i = 0; i < count; i++)
                {
                    int bucket = newEntries[i].hashCode % newSize;
                    newEntries[i].next = newEntries[bucket].bucket;
                    newEntries[bucket].bucket = i;
                }
                entries = newEntries;
            }
        }

        public void EnsureCapacity(int capacity)
        {
            if (Capacity < capacity)
                PrepareAdditionalCapacity(capacity - Capacity);
        }
        public void EnsureAdditionalCapacity(int additionalCapacity)
        {
            if (count + additionalCapacity >= entries.Length)
                PrepareAdditionalCapacity(additionalCapacity);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            Entry[] tmpEntries = this.entries;
            int i = FindEntry(tmpEntries, key);
            if (i >= 0)
            {
                value = tmpEntries[i].value;
                return true;
            }
            value = default;
            return false;
        }

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        {
            // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
            var tempCount = count;
            var tempEntries = entries;
            for (int i = 0; i < tempCount; i++)
                yield return new KeyValuePair<TKey, TValue>(tempEntries[i].key, tempEntries[i].value);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            // grab count before entries to ensure that if this occurs during an add with resize we are still safe.
            var tempCount = count;
            var tempEntries = entries;
            for (int i = 0; i < tempCount; i++)
                yield return new KeyValuePair<TKey, TValue>(tempEntries[i].key, tempEntries[i].value);
        }


        #region IGetMemoryStats Members

        public MemoryStats GetMemoryStats()
        {
            return new MemoryStats(entries, Count);
        }

        #endregion
    }


}
