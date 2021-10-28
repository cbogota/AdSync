using System;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AdSync
{
    public class Types
    {
        public static DateTime? FromFileTime(SearchResultEntry e, string attribute)
        {
            long fileTime;
            return
                long.TryParse((string)e.Attributes[attribute]?.GetValues(typeof(string)).FirstOrDefault(),
                    out fileTime) && fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTime()
                    ? DateTime.FromFileTimeUtc(fileTime)
                    : (DateTime?)null;
        }

        public static DateTime? FromGeneralizedTime(SearchResultEntry e, string attribute)
        {
            const string format = "yyyyMMddHHmmss.0Z";
            DateTime whenCreated;
            return DateTime.TryParseExact((string)e.Attributes[attribute]?.GetValues(typeof(string)).FirstOrDefault(), format, System.Globalization.CultureInfo.InvariantCulture,DateTimeStyles.None, out whenCreated)
                ? whenCreated
                : (DateTime?) null;
        }

        public static string FromStringValues(SearchResultEntry e, string attribute, string delimiter, bool intern = false)
        {
            var values = e?.Attributes?[attribute]?.GetValues(typeof(string)) as string[];
            return values?.Length > 0 ? (intern ? string.Intern(string.Join(delimiter, values)) : string.Join(delimiter, values)) : string.Empty;
        }
        public struct RangedAttribute
        {
            public string BaseName { get; set; }
            public string FullName { get; set; }
            public string Name => BaseName ?? FullName;
            public int RangeStart { get; set; }
            public int RangeEnd { get; set; }
            public bool IsRanged => BaseName != null;
    }
        public static RangedAttribute asRangedAttribute(string attribute)
        {
            if (attribute != null && attribute.Contains(";range="))
            {
                var parts = attribute.Split(';', '=', '-');
                if (parts.Length == 4 && int.TryParse(parts[2], out var rangeStart) && int.TryParse(parts[3], out var rangeEnd))
                    return new RangedAttribute { BaseName = parts[0], FullName = attribute, RangeStart = rangeStart, RangeEnd = rangeEnd };
            }
            return new RangedAttribute { BaseName = null, FullName = attribute };
        }
        public static string[] FromStringValues(SearchResultEntry e, string attribute)
        {
            var values = e.Attributes?[attribute]?.GetValues(typeof(string)) as string[];
            return values?.Length > 0 ? values : new string[] { };
        }

        public static string[] FromStringValuesLarge(SearchResultEntry e, RangedAttribute range, AdSync adSync)
        {
            return adSync.RetrieveRangedAttribute(e, range.BaseName).ToArray();
        }

        public static string FromStringValue(SearchResultEntry e, string attribute, bool intern = false)
        {
            var r = asRangedAttribute(attribute);
            if (r.BaseName != null)
                return FromStringValue(e, r, null);
            else
            {
                var values = e?.Attributes?[attribute]?.GetValues(typeof(string)) as string[];
                if (values?.Length > 1)
                {
                    return FromStringValues(e, attribute, "\r", true);
                }
                else
                    return values?.Length > 0 ? (intern ? string.Intern(values[0]) : values[0]) : null;
            }
        }

        public static string FromStringValue(SearchResultEntry e, RangedAttribute r, AdSync adSync)
        {
            return (r.IsRanged && adSync != null) ? string.Join("\r", FromStringValuesLarge(e, r, adSync)) : FromStringValues(e, r.FullName, "\r");
        }

        public static Guid? FromGuidValue(SearchResultEntry e, string attribute)
        {
            var guidValues = e?.Attributes?[attribute]?.GetValues(typeof(byte[])) as byte[][];
            return guidValues?.Length == 1 && guidValues[0]?.Length == 16 ? new Guid(guidValues[0]) : (Guid?)null;
        }

        public static long? FromLongValue(SearchResultEntry e, string attribute)
        {
            var values = e?.Attributes?[attribute]?.GetValues(typeof(string)) as string[];
            long result;
            return values?.Length == 1 && values[0]?.Length > 0 && long.TryParse(values[0], out result)
                ? result
                : (long?)null;
        }
        public static int? FromIntValue(SearchResultEntry e, string attribute)
        {
            var values = e?.Attributes?[attribute]?.GetValues(typeof(string)) as string[];
            int result;
            return values?.Length == 1 && values[0]?.Length > 0 && int.TryParse(values[0], out result)
                ? result
                : (int?)null;
        }

        public static byte[] FromBinaryValue(SearchResultEntry e, string attribute)
        {
            return (byte[])e?.Attributes?[attribute]?.GetValues(typeof(byte[]))[0];
        }

        public static string FromSidValue(SearchResultEntry e, string attribute)
        {
            var sidBuf = (byte[])e?.Attributes?[attribute]?.GetValues(typeof(byte[]))[0];
            return sidBuf == null ? null : new SecurityIdentifier(sidBuf, 0).Value;
        }

        public static string[] FromSidValues(SearchResultEntry e, string attribute)
        {
            var values = (byte[][])e?.Attributes?[attribute]?.GetValues(typeof(byte[]));
            return (values ?? new byte[][] { }).Select(buf => new SecurityIdentifier(buf, 0)).Select(sid => sid.Value).ToArray();
        }

        public static UserAccountControlFlags? FromUserAccountControlValue(SearchResultEntry e, string attribute)
        {
            int userAccountControl;
            return
                (UserAccountControlFlags?)
                    (int.TryParse((string)e?.Attributes?[attribute]?.GetValues(typeof(string)).FirstOrDefault(),
                        out userAccountControl)
                        ? userAccountControl
                        : 0);
        }
        public static SamAccountTypeEnum? FromSamAccountTypeValue(SearchResultEntry e, string attribute)
        {
            int samAccountType;
            return
                (SamAccountTypeEnum?)
                    (int.TryParse((string)e?.Attributes?[attribute]?.GetValues(typeof(string)).FirstOrDefault(),
                        out samAccountType)
                        ? samAccountType
                        : 0);
        }
        public static GroupTypeFlags? FromGroupTypeFlagsValue(SearchResultEntry e, string attribute)
        {
            int groupType;
            return
                (GroupTypeFlags?)
                    (int.TryParse((string)e?.Attributes?[attribute]?.GetValues(typeof(string)).FirstOrDefault(),
                        out groupType)
                        ? groupType
                        : 0);
        }

        public enum ExistenceStatus
        {
            Deleted = 0,
            Detecting,
            Exists
        }

        /// <summary>
        /// Flags that control the behavior of a user account.
        /// </summary>
        [Flags()]
        public enum UserAccountControlFlags
        {
            None = 0x0,

            /// <summary>
            /// The logon script is executed. 
            ///</summary>
            Script = 0x00000001,

            /// <summary>
            /// The user account is disabled. 
            ///</summary>
            AccountDisabled = 0x00000002,

            /// <summary>
            /// The home directory is required. 
            ///</summary>
            HomeDirRequired = 0x00000008,

            /// <summary>
            /// The account is currently locked out. 
            ///</summary>
            LockedOut = 0x00000010,

            /// <summary>
            /// No password is required. 
            ///</summary>
            PasswordNotRequired = 0x00000020,

            /// <summary>
            /// The user cannot change the password. 
            ///</summary>
            /// <remarks>
            /// Note:  You cannot assign the permission settings of PASSWD_CANT_CHANGE by directly modifying the UserAccountControl attribute. 
            /// For more information and a code example that shows how to prevent a user from changing the password, see User Cannot Change Password.
            /// </remarks>
            CantChangePassword = 0x00000040,

            /// <summary>
            /// The user can send an encrypted password. 
            ///</summary>
            EncryptedPasswordAllowed = 0x00000080,

            /// <summary>
            /// This is an account for users whose primary account is in another domain. This account provides user access to this domain, but not 
            /// to any domain that trusts this domain. Also known as a local user account. 
            ///</summary>
            TempDuplicateAccount = 0x00000100,

            /// <summary>
            /// This is a default account type that represents a typical user. 
            ///</summary>
            NormalAccount = 0x00000200,

            /// <summary>
            /// This is a permit to trust account for a system domain that trusts other domains. 
            ///</summary>
            InterdomainTrustAccount = 0x00000800,

            /// <summary>
            /// This is a computer account for a computer that is a member of this domain. 
            ///</summary>
            WorkstationTrustAccount = 0x00001000,

            /// <summary>
            /// This is a computer account for a system backup domain controller that is a member of this domain. 
            ///</summary>
            ServerTrustAccount = 0x00002000,

            /// <summary>
            /// Not used. 
            ///</summary>
            Unused1 = 0x00004000,

            /// <summary>
            /// Not used. 
            ///</summary>
            Unused2 = 0x00008000,

            /// <summary>
            /// The password for this account will never expire. 
            ///</summary>
            PasswordDoesntExpire = 0x00010000,

            /// <summary>
            /// This is an MNS logon account. 
            ///</summary>
            MnsLogonAccount = 0x00020000,

            /// <summary>
            /// The user must log on using a smart card. 
            ///</summary>
            SmartcardRequired = 0x00040000,

            /// <summary>
            /// The service account (user or computer account), under which a service runs, is trusted for Kerberos delegation. Any such service 
            /// can impersonate a client requesting the service. 
            ///</summary>
            TrustedForDelegation = 0x00080000,

            /// <summary>
            /// The security context of the user will not be delegated to a service even if the service account is set as trusted for Kerberos delegation. 
            ///</summary>
            DelegationNotPermitted = 0x00100000,

            /// <summary>
            /// Restrict this principal to use only Data Encryption Standard (DES) encryption types for keys. 
            ///</summary>
            UseDesKeyOnly = 0x00200000,

            /// <summary>
            /// This account does not require Kerberos pre-authentication for logon. 
            ///</summary>
            DontRequirePreauth = 0x00400000,

            /// <summary>
            /// The user password has expired. This flag is created by the system using data from the Pwd-Last-Set attribute and the domain policy. 
            ///</summary>
            PasswordExpired = 0x00800000,

            /// <summary>
            /// The account is enabled for delegation. This is a security-sensitive setting; accounts with this option enabled should be strictly 
            /// controlled. This setting enables a service running under the account to assume a client identity and authenticate as that user to 
            /// other remote servers on the network.
            ///</summary>
            TrustedToAuthenticateForDelegation = 0x01000000,

            /// <summary>
            /// 
            /// </summary>
            PartialSecretsAccount = 0x04000000,

            /// <summary>
            /// 
            /// </summary>
            UseAesKeys = 0x08000000
        }

        public enum SamAccountTypeEnum
        {
            Group = 0x10000000,

            NonSecurityGroup = 0x10000001,

            Alias = 0x20000000,

            NonSecurityAlias = 0x20000001,

            UserAccount = 0x30000000,

            MachineAccount = 0x30000001,

            TrustAccount = 0x30000002,
        }

        /// <summary>
        /// Flags that indicate the type of group.
        /// </summary>
        [Flags]
        public enum GroupTypeFlags : uint
        {
            None = 0x0,

            /// <summary>
            /// System created group
            ///</summary>
            System = 0x00000001,

            /// <summary>
            /// group with global scope  
            ///</summary>
            Global = 0x00000002,

            /// <summary>
            /// group with domain local scope
            ///</summary>
            DomainLocal = 0x00000004,

            /// <summary>
            /// group with universal scope 
            ///</summary>
            Universal = 0x0000008,

            /// <summary>
            /// AppBasic group for AzMan
            ///</summary>
            AppBasic = 0x00000010,

            /// <summary>
            /// AppQuery group for AzMan
            ///</summary>
            AppQuery = 0x00000020,

            /// <summary>
            /// Security enabled group
            ///</summary>
            Security = 0x80000000,
        }

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int memcmp(byte[] b1, byte[] b2, long count);

        public static bool ByteArraysAreEqual(byte[] b1, byte[] b2)
        {
            return (b1 == null && b2 == null) ||
                   (b1 != null && b2 != null && b1.Length == b2.Length && memcmp(b1, b2, b1.Length) == 0);
        }

        public class ProxyAddress : IEquatable<ProxyAddress>
        {
            public bool IsPrimary => AddressType.All(char.IsUpper);
            public readonly string AddressType;
            public readonly string Address;
            public override int GetHashCode()
            {
                return Address?.GetHashCode() ?? 0;
            }
            public override bool Equals(object obj)
            {
                if (obj == null) return false;
                var emailAliasObj = obj as ProxyAddress;
                return emailAliasObj != null && Equals(emailAliasObj);
            }
            public bool Equals(ProxyAddress other)
            {
                return Equals(this, other);
            }
            public override string ToString()
            {
                return $"{AddressType}:{Address}";
            }

            public static bool Equals(ProxyAddress x, ProxyAddress y)
            {
                return string.Equals(x?.AddressType, y?.AddressType, StringComparison.Ordinal) && string.Equals(x?.Address, y?.Address, StringComparison.OrdinalIgnoreCase);
            }
            public static bool operator ==(ProxyAddress x, ProxyAddress y)
            {
                if ((object)x == null || (object)y == null) return object.Equals(x, y);
                return x.Equals(y);
            }
            public static bool operator !=(ProxyAddress x, ProxyAddress y)
            {
                if ((object)x == null || (object)y == null) return !object.Equals(x, y);
                return !(x.Equals(y));
            }
            public ProxyAddress(string alias, bool throwExceptionOnInvalidAlias = true)
            {
                if (throwExceptionOnInvalidAlias && alias == null) throw new ArgumentNullException(nameof(alias));
                if (!TryParse(alias, out AddressType, out Address) && throwExceptionOnInvalidAlias) throw new ArgumentException("alias must be of the form [protocol]:[address]", nameof(alias));
            }
            public ProxyAddress(ProxyAddress alias)
            {
                AddressType = alias.AddressType;
                Address = alias.Address;
            }

            public ProxyAddress(string aliasType, string alias)
            {
                AddressType = aliasType;
                Address = alias;
            }

            private static bool TryParse(string alias, out string aliasType, out string aliasAddress)
            {
                aliasType = null;
                aliasAddress = null;
                var parts = alias?.Split(":".ToCharArray(), 2);
                if (parts?.Length != 2) return false;
                aliasType = parts[0];
                aliasAddress = parts[1];
                return true;
            }

            public static bool TryParse(string alias, out ProxyAddress proxyAddress)
            {
                string aliasType;
                string aliasAddress;
                proxyAddress = null;
                if (TryParse(alias, out aliasType, out aliasAddress))
                    proxyAddress = new ProxyAddress(aliasType, aliasAddress);
                return proxyAddress != null;
            }
        }

    }
}
