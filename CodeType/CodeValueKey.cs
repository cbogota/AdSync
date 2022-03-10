using System;

namespace Utility
{
    public struct CodeValueKey : IEquatable<CodeValueKey>
    {
        public readonly string Code;
        public readonly bool IsDeleted;
        public override int GetHashCode()
        {
            return Code.GetHashCode() ^ IsDeleted.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            return obj != null && GetType() == obj.GetType() && Equals((CodeValueKey)obj);
        }
        public bool Equals(CodeValueKey other)
        {
            return IsDeleted == other.IsDeleted &&
                   Code.Equals(other.Code, StringComparison.Ordinal);
        }
        
        public bool IsEmpty => Code == null;

        public CodeValueKey(string code, bool isDeleted)
        {
            IsDeleted = isDeleted;
            Code = code ?? throw new ArgumentNullException(nameof(code));
        }
    }
}
