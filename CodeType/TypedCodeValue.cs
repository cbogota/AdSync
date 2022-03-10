using System;
using System.Collections.Generic;
using System.Linq;

namespace Utility
{
    public struct TypedCodeValue<T> : ICodeValue, IEquatable<TypedCodeValue<T>>, IEqualityComparer<TypedCodeValue<T>> where T:ICodeType,new()
    {
        private readonly CodeValue _codeValue;
        private static readonly T CodeTypeInstance;
        public bool IsEmpty => _codeValue?.IsEmpty ?? true;
        public static TypedCodeValue<T> Empty => new TypedCodeValue<T>();
        public TypedCodeValue(CodeValue codeValue)
        {
            if (!string.Equals(codeValue?.CodeType, CodeTypeInstance.CodeType, StringComparison.Ordinal))
                throw new InvalidOperationException($"Incorrect code type provided {codeValue?.CodeType} for code type {CodeTypeInstance}");
            _codeValue = codeValue;
        }
        public string CodeType => CodeTypeInstance.CodeType;
        public string Code => _codeValue?.Code;
        public bool IsDeleted => _codeValue?.IsDeleted ?? false;
        public string Display => _codeValue?.Display;
        public float DisplaySequence => _codeValue?.DisplaySequence ?? 0;
        public string ExtendedData => _codeValue?.ExtendedData;
        public string Description => _codeValue?.Description;
        public string Comments => _codeValue?.Comments;
        public long LastUpdatedVersion => _codeValue?.LastUpdatedVersion ?? 0L;
        static TypedCodeValue()
        {
            CodeTypeInstance = new T();
        }
        public bool Equals(TypedCodeValue<T> other)
        {
            return _codeValue == other._codeValue;
        }
        public bool Equals(TypedCodeValue<T> x, TypedCodeValue<T> y)
        {
            return x._codeValue == y._codeValue;
        }
        public int GetHashCode(TypedCodeValue<T> obj)
        {
            return _codeValue?.GetHashCode() ?? 0;
        }
        public static bool operator ==(TypedCodeValue<T> x, TypedCodeValue<T> y) => x.Equals(y);

        public static bool operator !=(TypedCodeValue<T> x, TypedCodeValue<T> y) => !x.Equals(y);

        public override bool Equals(object other) => other is TypedCodeValue<T> && Equals((TypedCodeValue<T>)other);

        public override int GetHashCode() => _codeValue.GetHashCode();
    }
}