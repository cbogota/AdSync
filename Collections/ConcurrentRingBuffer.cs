using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Collections
{
    public class ConcurrentRingBuffer<T> where T : struct
    {
        private T[] _ring;
        // location to add new item (index actually used will be _currentIdx % _ring.Length)
        // _currentIdx starts at 0 and is incremented after each Add operation
        // when _currentIdx is incremented to equal _wrapCountLimit*_ring.Length, it will be decremented by (_wrapCountLimit-1)*_ring.Length
        private int _currentIdx;
        // when _currentIdx hits _wrapIdx, _currentIdx will be reset to _ring.Length
        // _wrapCountLimit 
        private int _wrapCountLimit;
        // returns the most recently added item to the ring buffer. 
        public T Current => _currentIdx > 0 ? _ring[(_currentIdx - 1) % _ring.Length] : default;
        public int Count => Math.Min(_currentIdx, _ring.Length);
        public void Reset() { _currentIdx = 0; }
        public IEnumerable<T> All
        {
            get
            {
                var idx = _currentIdx - 1;
                while (idx >= 0 && idx >= _currentIdx - _ring.Length)
                    yield return _ring[idx-- % _ring.Length];
            }
        }
        public void Add(T item)
        {
            var targetIdx = System.Threading.Interlocked.Increment(ref _currentIdx) - 1;
            _ring[targetIdx % _ring.Length] = item;
            if (targetIdx == _wrapCountLimit * _ring.Length)
                System.Threading.Interlocked.Add(ref _currentIdx, - ((_wrapCountLimit-1)*_ring.Length));
        }

        public ConcurrentRingBuffer(int capacity)
        {
            if (capacity < 1) throw new ArgumentException("capacity must be >= 1");
            if (capacity >= int.MaxValue/2) throw new ArgumentException($"capacity must be < {int.MaxValue/2}");
            _ring = new T[capacity];
            _wrapCountLimit = int.MaxValue / (capacity*2);
        }
    }
}
