using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Health.Abstractions
{
    public interface IStatisticalDistribution
    {
        double Quantile(double quantile);
        double Min { get; }
        double Max { get; }
        double Average { get; }
        double Count { get; }       
        DistributionPoint[] GetDistribution { get; }
        double Accuracy { get; }
    }
}
