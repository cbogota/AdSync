using System.Threading;
using Health.Abstractions;

namespace Health
{
    internal class MetricCounter : IMetricCounter
    {
        
        private long _counter;
        public long Increment() => Interlocked.Increment(ref _counter);
        public void Reset() => _counter = 0;
        public void Reset(long count) => _counter = count;
        public long CurrentValue => _counter;
        public string Name { get; }
        public MetricCounter (string name)
        {
            Name = name;
        }
    }
}
