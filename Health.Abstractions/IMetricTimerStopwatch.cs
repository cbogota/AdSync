using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Health.Abstractions
{
    public interface IMetricTimerStopwatch
    {
        // Methods
        IMetricTimerEvent Failure();
        IMetricTimerEvent Failure(Exception thrownException);
        IMetricTimerEvent Success();

        // Properties
        IMetricTimer Timer { get; }
        DateTimeOffset StartedOn { get; }
        TimeSpan Elapsed { get; }
    }

}
