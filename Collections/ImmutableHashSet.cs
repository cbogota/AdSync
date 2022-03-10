using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Collections
{
    public class ImmutableHashSet<T> : ICollection<T>, IEnumerable<T>, IEnumerable, ISerializable, IDeserializationCallback, ISet<T>, IReadOnlyCollection<T>
    {
        private readonly HashSet<T> _hashset;

        public ImmutableHashSet(IEnumerable<T> collection)
        {
            _hashset = new HashSet<T>(collection);
        }
        public ImmutableHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
        {
            _hashset = new HashSet<T>(collection, comparer);
        }

        void ICollection<T>.Add(T item)
        {
            throw new InvalidOperationException();
        }

        void ICollection<T>.Clear()
        {
            throw new InvalidOperationException();
        }

        bool ICollection<T>.Contains(T item)
        {
            return _hashset.Contains(item);
        }

        void ICollection<T>.CopyTo(T[] array, int arrayIndex)
        {
            _hashset.CopyTo(array, arrayIndex);
        }

        int ICollection<T>.Count
        {
            get { return _hashset.Count; }
        }

        bool ICollection<T>.IsReadOnly
        {
            get { return true; }
        }

        bool ICollection<T>.Remove(T item)
        {
            throw new InvalidOperationException();
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return _hashset.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _hashset.GetEnumerator();
        }

        void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
        {
            _hashset.GetObjectData(info, context);
        }

        void IDeserializationCallback.OnDeserialization(object sender)
        {
            _hashset.OnDeserialization(sender);
        }

        bool ISet<T>.Add(T item)
        {
            throw new InvalidOperationException();
        }

        void ISet<T>.ExceptWith(IEnumerable<T> other)
        {
            throw new InvalidOperationException();
        }

        void ISet<T>.IntersectWith(IEnumerable<T> other)
        {
            throw new InvalidOperationException();
        }

        bool ISet<T>.IsProperSubsetOf(IEnumerable<T> other)
        {
            return _hashset.IsProperSubsetOf(other);
        }

        bool ISet<T>.IsProperSupersetOf(IEnumerable<T> other)
        {
            return _hashset.IsProperSupersetOf(other);
        }

        bool ISet<T>.IsSubsetOf(IEnumerable<T> other)
        {
            return _hashset.IsSubsetOf(other);
        }

        bool ISet<T>.IsSupersetOf(IEnumerable<T> other)
        {
            return _hashset.IsSupersetOf(other);
        }

        bool ISet<T>.Overlaps(IEnumerable<T> other)
        {
            return _hashset.Overlaps(other);
        }

        bool ISet<T>.SetEquals(IEnumerable<T> other)
        {
            return _hashset.SetEquals(other);
        }

        void ISet<T>.SymmetricExceptWith(IEnumerable<T> other)
        {
            throw new InvalidOperationException();
        }

        void ISet<T>.UnionWith(IEnumerable<T> other)
        {
            throw new InvalidOperationException();
        }

        int IReadOnlyCollection<T>.Count
        {
            get { return _hashset.Count; }
        }
    }
}
