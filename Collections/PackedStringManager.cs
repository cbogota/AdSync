using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Collections
{
    /// <summary>
    /// PackedStringManager is optimized to store strings
    /// </summary>
    public class PackedStringManager 
    {
        public readonly struct PackedStringHandle : IEquatable<PackedStringHandle>
        {
            // if _index is 0, it refers to the empty string
            // if _index is > 0, it is a direct index to a string in the _storage array located at (index-1)
            private readonly int _indexPlus1;
            internal bool IsEmpty => _indexPlus1 == 0;
            internal int StorageIndex => _indexPlus1 - 1;
            internal PackedStringHandle(int storageIndex)
            {
                _indexPlus1 = storageIndex+1;
            }
            public bool Equals(PackedStringHandle other) => _indexPlus1 == other._indexPlus1;
            public override bool Equals(object other) => other is PackedStringHandle otherHandle && Equals(otherHandle);
            public static bool operator ==(PackedStringHandle x, PackedStringHandle y) => x.Equals(y);
            public static bool operator !=(PackedStringHandle x, PackedStringHandle y) => !x.Equals(y);
            public override int GetHashCode() => _indexPlus1;
        }

        private struct StringStorageEntry
        {
            public int Bucket { get; set; }     // not related to entry - index of first entry in bucket
            public int Next { get; set; }       // Index of next entry, -1 if last
            public int HashCode { get; set; }   // Lower 31 bits of hash code, -1 if unused
            public int ByteStorageStartIndex { get; set; }
            public static int StructureByteLength = StructureLayout.GetLayout(typeof(StringStorageEntry)).TotalSize;
        }

        private StringStorageEntry[] _entries;
        private int _entryCount;
        private byte[] _utf8Storage;
        private int _utf8StorageCount => _entries[_entryCount].ByteStorageStartIndex;

        private const string SerializationTypeName = "Ae.ConcurrentCollections.PackedStringManager";
        private static string SerializationMarkerStart => $"{SerializationTypeName}.Start.1";
        private static string SerializationMarkerEnd => $"{SerializationTypeName}.End";
        private static readonly object _writeLock = new object();
        public void Serialize(Stream writeStream)
        {
            lock (_writeLock)
            {
                var bw = new BinaryWriter(writeStream);
                bw.Write(SerializationMarkerStart);
                bw.Write(_entryCount);
                writeStream.WriteRawArray(_entries, _entryCount);
                bw.Write(_utf8StorageCount);
                writeStream.WriteRawArray(_utf8Storage, _utf8StorageCount);
                bw.Write(SerializationMarkerEnd);
            }
        }
        public PackedStringManager(Stream readStream)
        {
            var br = new BinaryReader(readStream);
            var startStr = br.ReadString();
            if (string.CompareOrdinal(startStr, SerializationMarkerStart) != 0)
                throw new InvalidDataException("Invalid serialization start marker found in stream");
            _entryCount = br.ReadInt32();
            _entries = readStream.ReadRawArray<StringStorageEntry>(RawReadAllocationMode.UseOriginalCapacity, 0, 0).Array;
            for (var i = _entryCount; i < _entries.Length; i++)
                _entries[i].Bucket = -1;
            var utf8StorageCount = br.ReadInt32();
            _entries[_entryCount].ByteStorageStartIndex = utf8StorageCount;
            _utf8Storage = readStream.ReadRawArray<byte>(RawReadAllocationMode.AllocateRequiredPlusPercentage, 1 << 17, 5).Array;
            var endStr = br.ReadString();
            if (string.CompareOrdinal(endStr, SerializationMarkerEnd) != 0)
                throw new InvalidDataException("Invalid serialization end marker found in stream");
        }
        public PackedStringManager(int stringCapacity, int characterCapacity)
        {
            Initialize(stringCapacity);
            _utf8Storage = new byte[characterCapacity];
        }
        private void Initialize(int stringCapacity)
        {
            lock (_writeLock)
            {
                var size = PrimeHelper.GetPrime(stringCapacity);
                _entries = new StringStorageEntry[size];
                for (var i = 0; i < _entries.Length; i++) _entries[i].Bucket = -1;
            }
        }
        /// <summary>
        /// Adds additional capacity
        /// If 0 is provided as the minimum additional, space is not expanded.
        /// If doubling existing capacity would provide more capacity than the minimum provided, capacity will be doubled
        /// Otherwise, the additional capacity is added 
        ///     eg if existing capacity is 8 and 10 minimum additional space is needed, space will be increased to 18 (to accomodate minimum additional)
        ///     if if existing capacity is 8 and 5 minimum additional space is needed, space will be increased to 16 (doubled)
        /// </summary>
        /// <param name="minimumAdditionalStringCapacity"></param>
        /// <param name="minimumAdditionalCharacterCapacity"></param>
        public void PrepareAdditionalStringCapacity(int minimumAdditionalStringCapacity, int minimumAdditionalCharacterCapacity)
        {
            if (minimumAdditionalStringCapacity <= 0 && minimumAdditionalCharacterCapacity <= 0) return;
            lock (_writeLock)
            {
                if (minimumAdditionalStringCapacity > 0)
                {
                    var newStringCapacity = PrimeHelper.GetPrime(Math.Max(_entries.Length * 2, _entries.Length + minimumAdditionalStringCapacity));
                    var newEntries = new StringStorageEntry[newStringCapacity];
                    _entries.AsSpan(0, _entryCount + 1).CopyTo(newEntries);
                    // initialize buckets to unused
                    for (var i = 0; i < newEntries.Length; i++) newEntries[i].Bucket = -1;
                    // build new bucket lists
                    for (var i = 0; i < _entryCount; i++)
                    {
                        var bucket = newEntries[i].HashCode % newStringCapacity;
                        newEntries[i].Next = newEntries[bucket].Bucket;
                        newEntries[bucket].Bucket = i;
                    }
                    _entries = newEntries;
                }
                if (minimumAdditionalCharacterCapacity > 0)
                {                    
                    var newUtf8Storage = new byte[Math.Max(_utf8Storage.Length * 2, _utf8Storage.Length + minimumAdditionalCharacterCapacity)];
                    _utf8Storage.AsSpan(0, _utf8StorageCount).CopyTo(newUtf8Storage);
                    _utf8Storage = newUtf8Storage;
                }
            }
        }

        // if existing capacity is not sufficient, capacities will be increased, otherwise nothing will be done
        public void EnsureCapacityAvailable(int neededStringCapacity, int neededCharacterCapacity)
        {
            PrepareAdditionalStringCapacity((_entryCount + neededStringCapacity + 1) - _entries.Length, (_utf8StorageCount + neededCharacterCapacity) - _utf8Storage.Length);
        }

        public static PackedStringHandle EmptyStringHandle => new PackedStringHandle();
        private static readonly SipHash _hasher = new SipHash(new Guid(new byte[] {1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16}));
        private readonly struct PackedString
        {
            public readonly string StringValue;
            public readonly byte[] Utf8Bytes;
            public ReadOnlySpan<byte> Utf8ByteSpan => new ReadOnlySpan<byte>(Utf8Bytes);
            public readonly int HashCode;        // Lower 31 bits of hash code
            public PackedString(string str)
            {
                StringValue = str;
                Utf8Bytes = Encoding.UTF8.GetBytes(str);
                HashCode = (int) (_hasher.Compute(Utf8Bytes) & 0x7FFF_FFFF);
            }
        }

        private static bool BytesEqual(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
        {
            return x.SequenceEqual(y);
        }

        private (int startIndex, int length) EntryStorageLocation(int entryIndex)
        {
            if (entryIndex < 0 || entryIndex >= _entryCount) return (0,0);
            return (_entries[entryIndex].ByteStorageStartIndex, _entries[entryIndex + 1].ByteStorageStartIndex - _entries[entryIndex].ByteStorageStartIndex);
        }

        private Span<byte> EntryUtf8ByteSpan(int entryIndex)
        {
            try
            {
                var (startIndex, length) = EntryStorageLocation(entryIndex);
               return new Span<byte>(_utf8Storage, startIndex, length);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"invalid span information entryIndex={entryIndex}, _entries[entryIndex].ByteStorageStartIndex={_entries[entryIndex].ByteStorageStartIndex}, _entries[entryIndex + 1].ByteStorageStartIndex={_entries[entryIndex + 1].ByteStorageStartIndex}", ex);
            }
        }
        private Span<byte> NewEntryUtf8ByteSpan()
        {
            return new Span<byte>(_utf8Storage, _utf8StorageCount, _utf8Storage.Length - _utf8StorageCount);
        }
        public PackedStringHandle GetStringHandle(string str)
        {
            if (string.IsNullOrEmpty(str)) return EmptyStringHandle;
            if (_entries == null) Initialize(0);
            // find the bucket to place the entry
            var packedString = new PackedString(str);
            var packedStringSpan = packedString.Utf8ByteSpan;
            var targetBucket = packedString.HashCode % _entries.Length;
            // check if the string exists in the bucket
            for (var i = _entries[targetBucket].Bucket; i >= 0; i = _entries[i].Next)
                if (_entries[i].HashCode == packedString.HashCode && BytesEqual(packedStringSpan, EntryUtf8ByteSpan(i)))                   
                    return new PackedStringHandle(i);
            // string did not exist, add it
            lock (_writeLock)
            {
                EnsureCapacityAvailable(1, packedStringSpan.Length);
                var newUtf8Span = NewEntryUtf8ByteSpan();
                packedString.Utf8ByteSpan.CopyTo(newUtf8Span);
                _entries[_entryCount + 1].ByteStorageStartIndex = _entries[_entryCount].ByteStorageStartIndex + packedString.Utf8Bytes.Length;
                _entries[_entryCount].HashCode = packedString.HashCode;
                _entries[_entryCount].Next = _entries[targetBucket].Bucket;
                _entries[targetBucket].Bucket = _entryCount;
                return new PackedStringHandle(_entryCount++);
            }
        }

       public string GetString(PackedStringHandle handle)
       {
           if (handle.IsEmpty) return string.Empty;
           if (handle.StorageIndex < 0 || handle.StorageIndex >= _entryCount)
               throw new ArgumentException($"Invalid handle value ({handle.StorageIndex},{_entryCount})", nameof(handle));
           var (startIndex, length) = EntryStorageLocation(handle.StorageIndex);
           return Encoding.UTF8.GetString(_utf8Storage, startIndex, length);
        }

        public PackedStringHandle GetLowercaseStringHandle(PackedStringHandle handle)
        {
            var s = GetString(handle);
            return s.All(c => char.ToLowerInvariant(c) == c) ? handle : GetStringHandle(s.ToLowerInvariant());
        }

    }
}
