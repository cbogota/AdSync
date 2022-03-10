using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Health.Abstractions
{
    public interface IMetricCollection
    {
        IMetricCounter CreateCounter(string name);
        IReadOnlyDictionary<string, IMetricCounter> Counters { get; }
        IMetricTimer CreateTimer(string name);
        IReadOnlyDictionary<string, IMetricTimer> Timers { get; }
    }
}
