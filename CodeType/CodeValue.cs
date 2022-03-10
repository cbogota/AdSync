using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Utility
{
    public class CodeValue : ICodeValue
    {
        public string CodeType { get; }
        public string Code { get; }
        public bool IsEmpty => Code == null;
        public bool IsDeleted { get; private set; }
        public string Display { get; private set; }
        public float DisplaySequence { get; private set; }
        public string ExtendedData { get; private set; }
        public IEnumerable<string> ExtendedDataList => ExtendedData?.Split(',') ?? Enumerable.Empty<string>();
        public string Description { get; private set; }
        public string Comments { get; private set; }
        public long LastUpdatedVersion { get; private set; }

        internal void Update(bool isDeleted, string display, float displaySequence,
            string extendedData, string description, string comments, long lastUpdatedVersion)
        {
            IsDeleted = isDeleted;
            Display = display;
            DisplaySequence = displaySequence;
            ExtendedData = extendedData;
            Description = description;
            Comments = comments;
            LastUpdatedVersion = lastUpdatedVersion;
        }
        internal CodeValue(string codeType, string code, bool isDeleted, string display, float displaySequence,
            string extendedData, string description, string comments, long lastUpdatedVersion)
        {
            CodeType = codeType;
            Code = code;
            Update(isDeleted, display, displaySequence, extendedData, description, comments, lastUpdatedVersion);
        }
    }
}
