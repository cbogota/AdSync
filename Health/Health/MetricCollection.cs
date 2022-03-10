using System;
using System.Collections.Generic;
using Health.Abstractions;

namespace Health
{
    public class MetricCollection : IMetricCollection
    {
        private Collections.ConcurrentSlimDictionary<string, IMetricCounter> _counters = new Collections.ConcurrentSlimDictionary<string, IMetricCounter>();
        private Collections.ConcurrentSlimDictionary<string, IMetricTimer> _timers = new Collections.ConcurrentSlimDictionary<string, IMetricTimer>();
        public IReadOnlyDictionary<string, IMetricCounter> Counters => _counters;
        public IReadOnlyDictionary<string, IMetricTimer> Timers => _timers;
        public IMetricCounter CreateCounter(string name) => _counters[name] = new MetricCounter(name);
        public IMetricTimer CreateTimer(string name) => _timers[name] = new MetricTimer(name);
    }
}
