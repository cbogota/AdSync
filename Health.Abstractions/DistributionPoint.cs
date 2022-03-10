using System;
using System.Collections.Generic;
using System.Text;

namespace Health.Abstractions
{    
    public struct DistributionPoint
    {
        public double Value { get; }
        public double Count { get; }
        public DistributionPoint(double value, double count)
        {
            Value = value;
            Count = count;
        }
    }
}
