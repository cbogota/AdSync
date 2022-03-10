using System;
using System.Collections.Generic;

namespace Utility
{
    public interface ICodeValue
    {
        string CodeType { get; }
        string Code { get; }
        bool IsEmpty { get; }
        bool IsDeleted { get; }
        string Display { get; }
        float DisplaySequence { get; }
        string ExtendedData { get; }
        string Description { get; }
        string Comments { get; }
        long LastUpdatedVersion { get; }
    }
}