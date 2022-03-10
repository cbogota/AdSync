using System.Collections.Generic;

namespace Utility
{
    public interface ICodeLookup<T> where T : ICodeValue, new()
    {
        T LookupByCode(string code);
        IEnumerable<T> All { get; }
    }
}