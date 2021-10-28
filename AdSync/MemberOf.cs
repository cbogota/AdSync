using System;

namespace AdSync
{
    public struct MemberOf : IEquatable<MemberOf>
    {
        public readonly int GroupTag;
        public readonly int MemberTag;
        public override int GetHashCode()
        {
            return GroupTag.GetHashCode() ^ MemberTag.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is MemberOf && Equals((MemberOf)obj);
        }
        public bool Equals(MemberOf other)
        {
            return Equals(this, other);
        }
        public static bool Equals(MemberOf x, MemberOf y)
        {
            return x.GroupTag == y.GroupTag && x.MemberTag == y.MemberTag;
        }
        public static bool operator ==(MemberOf x, MemberOf y)
        {
            return Equals(x, y);
        }
        public static bool operator !=(MemberOf x, MemberOf y)
        {
            return !Equals(x, y);
        }
        public override string ToString()
        {
            return $"({GroupTag}:{MemberTag})";
        }
        public MemberOf(int groupTag, int memberTag)
        {
            GroupTag = groupTag;
            MemberTag = memberTag;
        }
    }

}
