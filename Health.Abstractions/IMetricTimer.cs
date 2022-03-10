using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Health.Abstractions
{
    public interface IMetricTimer
    {
        // Methods
        IMetricTimerStopwatch Start();
        long SuccessCount { get; }
        long FailureCount { get; }        
        IMetricTimerEvent LastEvent { get; }
        IEnumerable<IMetricTimerEvent> RecentEvents { get; }
        // Properties
        string Name { get; }
        int ActiveTimerCount { get; }
        int MaxActiveTimerCount { get; }
    }
}
