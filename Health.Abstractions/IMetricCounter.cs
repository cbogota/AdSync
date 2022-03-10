using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Health.Abstractions
{
    public interface IMetricCounter
    {
        // Methods
        long Increment();
        void Reset();
        void Reset(long count);
        // Properties
        string Name { get; }
        long CurrentValue { get; }        
    }
}
