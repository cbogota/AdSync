using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Collections
{
    /// <summary>
    /// PackedListManager is optimized to store short lists that are typically of length 0 or 1
    /// Single element lists are stored in the _singleElementStorage array.
    /// List longer than a single element are stored as a linked list in the _multiElementList,
    /// with the last element pointing to the single element storage.
    /// List updates are done within existing list storage and extended as necessary.
    /// This class is thread safe for multiple readers and writers
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class PackedListManager <T> where T : unmanaged
    {
        /// <summary>
        /// Handles are designed such that the default struct value will be a valid handle to the Empty List
        /// </summary>
        public readonly struct PackedListHandle : IEquatable<PackedListHandle>
        {
            // if _index is 0, it refers to the empty list
            // if _index is > 0, it is a direct index to a single element list in the _storage array located at (index-1)
            // if _index is < 0, it refers to a multi-element list with index information in the _multiElementIndex array located at (-1-index)
            private int Index { get; }
            public bool IsEmpty => Index == 0;
            public bool IsSingleElementList => Index > 0;
            public bool IsMultiElementList => Index < 0;
            internal int StorageIndex => Math.Abs(Index) - 1;
            internal static PackedListHandle NewSingleElementStorageIndex(int index) => new PackedListHandle(index+1);
            internal static PackedListHandle NewMultiElementStorageIndex(int index) => new PackedListHandle(-(index+1));
            public bool Equals(PackedListHandle other) => Index == other.Index;
            public override bool Equals(object other) => other is PackedListHandle otherHandle && Equals(otherHandle);
            public override int GetHashCode() => Index;
            public static bool operator == (PackedListHandle x, PackedListHandle y) => x.Equals(y);
            public static bool operator !=(PackedListHandle x, PackedListHandle y) => !x.Equals(y);
            private PackedListHandle(int index)
            {
                Index = index;
            }
        }

        public readonly struct ListItemWithHandle
        {
            public T Item { get; }
            public PackedListHandle Handle { get; }

            internal ListItemWithHandle(T item, PackedListHandle handle)
            {
                Item = item;
                Handle = handle;
            }
        }

        private struct MultiElementListItem
        {
            public T Item { get; set; }
            public PackedListHandle NextElementHandle { get; }
            public MultiElementListItem(T item, PackedListHandle nextElementHandle)
            {
                Item = item;
                NextElementHandle = nextElementHandle;
            }
        }

        private MultiElementListItem[] _multiElementListIndex;
        private int _multiElementListCount;
        private T[] _storage;
        private int _count;
        private readonly object _writeLock = new object();

        private const string SerializationTypeName = "Ae.ConcurrentCollections.PackedListManager";
        private static string SerializationMarkerStart => $"{SerializationTypeName}.Start.1";
        private static string SerializationMarkerEnd => $"{SerializationTypeName}.End";
        public void Serialize(Stream writeStream)
        {
            try
            {
                var bw = new BinaryWriter(writeStream);
                bw.Write(SerializationMarkerStart);
                bw.Write(_count);
                writeStream.WriteRawArray(_storage, _count);
                bw.Write(_multiElementListCount);
                writeStream.WriteRawArray(_multiElementListIndex, _multiElementListCount);
                bw.Write(SerializationMarkerEnd);
            }
            catch (Exception ex)
            {               
                throw new Exception($"Error during serialization (_Storage.Length={_storage?.Length} _count={_count} _multiElementListIndex.Length={_multiElementListIndex?.Length} _multiElementListCount={_multiElementListCount}", ex);
            }
        }
        public PackedListManager(Stream readStream)
        {
            var br = new BinaryReader(readStream);
            var startStr = br.ReadString();
            if (string.CompareOrdinal(startStr, SerializationMarkerStart) != 0)
                throw new InvalidDataException("Invalid serialization start marker found in stream");
            _count = br.ReadInt32();
            _storage = readStream.ReadRawArray<T>(RawReadAllocationMode.AllocateRequiredPlusPercentage, 128, 5).Array;
            _multiElementListCount = br.ReadInt32();
            _multiElementListIndex = readStream.ReadRawArray<MultiElementListItem>(RawReadAllocationMode.AllocateRequiredPlusPercentage, 128, 5).Array;
            var endStr = br.ReadString();
            if (string.CompareOrdinal(endStr, SerializationMarkerEnd) != 0)
                throw new InvalidDataException("Invalid serialization end marker found in stream");
        }
        public PackedListManager(int capacity, int largeListCapacity)
        {
            _storage = new T[Math.Max(capacity,128)];
            _count = 0;
            _multiElementListIndex = new MultiElementListItem[Math.Max(largeListCapacity,128)];
            _multiElementListCount = 0;
        }

        public static PackedListHandle EmptyListHandle => new PackedListHandle();
        public static T[] EmptyList = {};

        public PackedListHandle Create(T singleElementList)
        {
            var resultIdx = System.Threading.Interlocked.Increment(ref _count) - 1;
            if (resultIdx+128 >= _storage.Length)
                lock (_writeLock)
                    if (resultIdx+128 >= _storage.Length)
                    {
                        var newSingleElementListStorage = new T[128+(_storage.Length*2)];
                        try
                        {                            
                            _storage.AsSpan(0, Math.Min(_count, _storage.Length)).CopyTo(newSingleElementListStorage);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                            throw new Exception($"_storage.Length={_storage?.Length} _count={_count}", ex);
                        }

                        _storage = newSingleElementListStorage;
                    }
            _storage[resultIdx] = singleElementList;
            return PackedListHandle.NewSingleElementStorageIndex(resultIdx);
        }

        public PackedListHandle PrependToList(T item, PackedListHandle listHandle)
        {
            if (listHandle.IsEmpty) return Create(item);
            var newItem = new MultiElementListItem(item, listHandle);
            var resultMultiListIdx = System.Threading.Interlocked.Increment(ref _multiElementListCount) - 1;
            // expand list storage if necessary
            if (resultMultiListIdx+128 >= _multiElementListIndex.Length)
                lock (_writeLock)
                    if (resultMultiListIdx+128 >= _multiElementListIndex.Length)
                    {
                        var newMultiElementListStorage = new MultiElementListItem[128+_multiElementListIndex.Length * 2];
                        _multiElementListIndex.AsSpan(0, Math.Min(_multiElementListIndex.Length,_multiElementListCount)).CopyTo(newMultiElementListStorage);
                        _multiElementListIndex = newMultiElementListStorage;
                    }
            _multiElementListIndex[resultMultiListIdx] = newItem;
            return PackedListHandle.NewMultiElementStorageIndex(resultMultiListIdx);
        }

        public PackedListHandle Create(IEnumerable<T> list) => Create(list.ToArray());
        public PackedListHandle Create(T[] list)
        {
            var result = EmptyListHandle;
            if (list == null || list.Length == 0) return result;
            for (var i = list.Length - 1; i >= 0; i--)
                result = result.IsEmpty ? Create(list[i]) : PrependToList(list[i], result);
            return result;
        }

        public void UpdateListElement(PackedListHandle handle, T item)
        {
            if (handle.IsEmpty) throw new ArgumentOutOfRangeException("Attempt to put item into empty handle");
            if (handle.IsSingleElementList)
                _storage[handle.StorageIndex] = item;
            else
                _multiElementListIndex[handle.StorageIndex].Item = item;
        }

        private T GetListElement(PackedListHandle handle) => 
            handle.IsMultiElementList
                ? _multiElementListIndex[handle.StorageIndex].Item
                : handle.IsSingleElementList
                    ? _storage[handle.StorageIndex]
                    : throw new ArgumentOutOfRangeException("Attempt to get item from empty handle");
        /// <summary>
        /// Returns the list items (in proper order)
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        public IEnumerable<T> GetListItems(PackedListHandle handle)
        {
            while (!handle.IsEmpty)
            {
                yield return GetListElement(handle);
                handle = handle.IsMultiElementList
                    ? _multiElementListIndex[handle.StorageIndex].NextElementHandle
                    : EmptyListHandle;
            }
        }
        /// <summary>
        /// Returns the list items (in proper order)
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        public IEnumerable<ListItemWithHandle> GetListItemsWithHandle(PackedListHandle handle)
        {
            while (!handle.IsEmpty)
            {
                yield return new ListItemWithHandle(GetListElement(handle), handle);
                handle = handle.IsMultiElementList
                    ? _multiElementListIndex[handle.StorageIndex].NextElementHandle
                    : EmptyListHandle;
            }
        }
        private static readonly IReadOnlyList<PackedListHandle> EmptyHandleList = new PackedListHandle[0];
        /// <summary>
        /// Returns the list of handles that are storing the list items (in proper list order)
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        private IReadOnlyList<PackedListHandle> GetHandleList(PackedListHandle handle)
        {
            if (handle.IsEmpty) return EmptyHandleList;
            if (handle.IsSingleElementList) return new [] {handle};
            var result = new List<PackedListHandle>(4);
            while (!handle.IsEmpty)
            {
                result.Add(handle);
                handle = handle.IsMultiElementList
                    ? _multiElementListIndex[handle.StorageIndex].NextElementHandle
                    : EmptyListHandle;
            }
            return result;
        }

        /// <summary>
        /// Gets the tail handle of a list
        /// The tail handle is always in the single list item storage array
        /// </summary>
        /// <param name="handle"></param>
        /// <returns></returns>
        private PackedListHandle GetTailHandle(PackedListHandle handle)
        {
            while (handle.IsMultiElementList)
                handle = _multiElementListIndex[handle.StorageIndex].NextElementHandle;
            return handle;
        }
        //public int GetListLength(PackedListHandle handle)
        //{
        //    var result = 0;
        //    while (!handle.IsEmpty)
        //    {
        //        result++;
        //        handle = handle.IsMultiElementList
        //            ? _multiElementListIndex[handle.StorageIndex].NextElementHandle
        //            : EmptyListHandle;
        //    }
        //    return result;
        //}

        public PackedListHandle UpdateList(PackedListHandle handle, T singleElementList)
        {
            // handle case where existing list is empty
            if (handle.IsEmpty) return Create(singleElementList);
            handle = GetTailHandle(handle);
            _storage[handle.StorageIndex] = singleElementList;
            return handle;
        }
        public PackedListHandle UpdateList(PackedListHandle handle, IEnumerable<T> list) => UpdateList(handle, list.ToArray());
        public PackedListHandle UpdateList(PackedListHandle handle, T[] list)
        {
            // handle case where existing list is empty
            if (handle.IsEmpty) return Create(list);
            // handle case where new list is empty
            if (list == null || list.Length == 0) return EmptyListHandle;
            // handle case where current and new list are both single element lists
            if (list.Length == 1 && handle.IsSingleElementList)
            {        
                _storage[handle.StorageIndex] = list[0];
                return handle;
            }
            var existingHandleList = GetHandleList(handle);
            var listLengthIncrease = list.Length - existingHandleList.Count;
            // new list is same length or smaller
            if (listLengthIncrease <= 0)
            {
                for (int listIdx = 0, handleIdx=-listLengthIncrease; listIdx < list.Length; listIdx++, handleIdx++)
                    UpdateListElement(existingHandleList[handleIdx], list[listIdx]);
                return existingHandleList[-listLengthIncrease];
            }
            // list is longer           
            // copy end of list to existing handles
            for (var handleIdx = 0; handleIdx<existingHandleList.Count; handleIdx++)
                UpdateListElement(existingHandleList[handleIdx], list[handleIdx+listLengthIncrease]);
            // then add beginning items in reverse 
            var result = existingHandleList[0];
            for (var listIdx = listLengthIncrease - 1; listIdx >= 0; listIdx--)
                result = PrependToList(list[listIdx], result);
            return handle;
        }
    }
}
