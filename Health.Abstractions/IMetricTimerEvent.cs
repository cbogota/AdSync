using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Health.Abstractions
{
    public interface IMetricTimerEvent
    {
        bool Success { get; }
        bool Completed { get; }
        DateTimeOffset CompletedOn { get; }
        TimeSpan Duration { get; }
        Exception Exception { get; }
    }
}
