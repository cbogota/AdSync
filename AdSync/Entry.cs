using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AdSync
{
    public class Entry
    {
        public int Tag { get; set; }
        public string Dn { get; set; }
        [JsonIgnore]
        public bool IsChangeNotified { get; set; }
        [JsonIgnore]
        public Types.ExistenceStatus Status { get; set; }
        public string Class { get; set; }
        public DateTime? WhenCreated { get; set; }
        public string UserPrincipalName { get; set; }
        [JsonConverter(typeof(OrderedStringArrayJsonConverter))]
        public string[] ServicePrincipalName { get; set; }
        public Guid ObjectGuid { get; set; }
        public string Sid { get; set; }
        [JsonConverter(typeof(OrderedStringHashSetJsonConverter))]
        public HashSet<string> SidHistory { get; set; }

        [JsonIgnore]
        public string SidHistoryList => string.Join(";", SidHistory?.OrderBy(s => s) ?? Enumerable.Empty<string>());
        public string SamAccountName { get; set; }
        [JsonIgnore]
        public bool HasSamAccountName => !string.IsNullOrEmpty(SamAccountName);
        public string DomainFlatName { get; set; }

        [JsonIgnore]
        public string SamAccountNameWithDomainPrefix => $"{DomainFlatName}\\{SamAccountName}";
        [JsonConverter(typeof(StringEnumConverter))]
        public Types.SamAccountTypeEnum? SamAccountType { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public Types.UserAccountControlFlags? UserAccountControl { get; set; }
        public string UserWorkstations { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public Types.GroupTypeFlags? GroupType { get; set; }
        public DateTime? PasswordLastSet { get; set; }
        public DateTime? LastLogonTimeStamp { get; set; }
        public int LogonCount { get; set; }
        public DateTime? AccountExpires { get; set; }
        [JsonConverter(typeof(OrderedStringArrayJsonConverter))]
        public string[] AllowedToDelegateTo { get; set; }
        [JsonIgnore]
        public string AllowedToDelegateToList => string.Join(";", AllowedToDelegateTo?.OrderBy(s => s) ?? Enumerable.Empty<string>());

        public string Telephone { get; set; }
        public string Fax { get; set; }
        public string Mobile { get; set; }
        public string Email { get; set; }
        [JsonConverter(typeof(OrderedStringHashSetJsonConverter))]
        public HashSet<string> EmailAliases { get; set; }
        [JsonIgnore]
        public string EmailAliasesList => string.Join(";", EmailAliases?.OrderBy(s => s) ?? Enumerable.Empty<string>());
        public string TargetEmail { get; set; }
        public Guid? MailboxGuid { get; set; }
        public bool HideFromAddressBook { get; set; }
        public string SipAddress { get; set; }
        public string Country { get; set; }
        public string City { get; set; }
        public string ProvinceState { get; set; }
        public string StreetAddress { get; set; }
        public string PostalCode { get; set; }
        public string Company { get; set; }
        public string Department { get; set; }
        public string Description { get; set; }
        public string Office { get; set; }

        public string DisplayName { get; set; }
        public string Title { get; set; }
        public string PersonalTitle { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Name { get; set; }
        public byte[] Photo { get; set; }

        public string EmployeeType { get; set; }
        public string EmployeeId { get; set; }
        public string ManagerDeferredDn { get; set; }
        public int? ManagerTag { get; set; }
        [JsonConverter(typeof(OrderedIntHashSetJsonConverter))]
        public HashSet<int> ManagesTags { get; set; }
        [JsonIgnore]
        public string ManagesTagsList => string.Join(";", ManagesTags?.OrderBy(t => t) ?? Enumerable.Empty<int>());

        [JsonConverter(typeof(OrderedStringListJsonConverter))]
        public List<string> DirectMembersDeferredDn { get; set; }

        // All objects that are direct members of this group
        [JsonConverter(typeof(OrderedIntHashSetJsonConverter))]
        public HashSet<int> DirectMembersTags { get; set; }
        [JsonIgnore]
        public string DirectMembersTagsList => string.Join(";", DirectMembersTags?.OrderBy(t => t) ?? Enumerable.Empty<int>());

        //// All members (including indirectly nested members) of this group that are groups themselves
        //public HashSet<int> AllGroupTypeMembersTags { get; set; }

        //// All members (including indirectly nested members) of this group that are not groups themselves
        //public HashSet<int> AllUserMembersTags { get; set; }

        // All groups that this entry is directly a member of
        [JsonConverter(typeof(OrderedIntHashSetJsonConverter))]
        public HashSet<int> DirectMemberOfsTags { get; set; }
        [JsonIgnore]
        public string DirectMemberOfsTagsList => string.Join(";", DirectMemberOfsTags?.OrderBy(t => t) ?? Enumerable.Empty<int>());


        //// All groups that this entry is a member of (including indirect membership via nested groups) 
        //public HashSet<int> AllMembersOfsTags { get; set; }

        private Dictionary<string, string> _otherAttributesText { get; set; }
        private Dictionary<string, byte[]> _otherAttributesBinary { get; set; }
        public bool AllAttributesLoaded { get; }
        public IReadOnlyDictionary<string, string> OtherAttributesText => _otherAttributesText;
        public IReadOnlyDictionary<string, byte[]> OtherAttributesBinary => _otherAttributesBinary;
        public int DeferredCount => (ManagerDeferredDn == null ? 0 : 1) + (DirectMembersDeferredDn?.Count ?? 0);

        [JsonIgnore]
        public IEnumerable<string> DeferredDn
        {
            get
            {
                // manager deferred
                if (!string.IsNullOrEmpty(ManagerDeferredDn)) yield return ManagerDeferredDn;
                // group members deferred
                if (DirectMembersDeferredDn == null) yield break;
                foreach (var dn in DirectMembersDeferredDn)
                    yield return dn;
            }
        }

        public int? PrimaryGroupId { get; set; }
        public int? PrimaryGroupToken { get; set; }

        public int Version { get; set; }
        [JsonIgnore]
        public bool IsGroup => DirectMembersTags?.Count > 0 || DirectMembersDeferredDn?.Count > 0 ||
                SamAccountType == Types.SamAccountTypeEnum.Group || SamAccountType == Types.SamAccountTypeEnum.NonSecurityGroup ||
                SamAccountType == Types.SamAccountTypeEnum.Alias || SamAccountType == Types.SamAccountTypeEnum.NonSecurityAlias;

        [JsonIgnore]
        public static HashSet<string> StandardAttributes { get; } = new HashSet<string>(new []
        {
            "objectClass", "userPrincipalName", "servicePrincipalName", "objectGuid", "objectSid", "sidhistory", "sAMAccountName", "sAMAccountType", "flatName",
            "userAccountControl", "groupType", "pwdlastset", "lastlogontimestamp", "logonCount", "accountExpires", "msDS-AllowedToDelegateTo",
            "telephoneNumber", "facsimileTelephoneNumber", "mobile", "mail", "proxyAddresses", "targetAddress", "msExchMailboxGuid", "msExchHideFromAddressLists", "msRTCSIP-PrimaryUserAddress", "msRTCSIP-UserEnabled",
            "co", "l", "st", "streetAddress", "postalCode",
            "company", "department", "physicalDeliveryOfficeName",
            "displayName", "title", "givenName", "sn", "name", "personalTitle", "thumbnailPhoto","employeeType", "employeeID",
            "manager", "member", "userWorkstations", "description", "whenCreated", "primaryGroupToken", "primaryGroupID", "personalTitle"
        }, StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public static HashSet<string> IgnoreAttributes { get; } = new HashSet<string>(new[]
        {
            "objectsid", "distinguishedname"
        }, StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public static HashSet<string> BinaryAttributes { get; } = new HashSet<string>(new[]
        {
            "usercertificate", "userparameters", "msexchmailboxsecuritydescriptor", "logonhours",
            "msrtcsip-userroutinggroupid", "msexchsafesendershash", "msexchblockedsendershash",
            "dpuserpublickey","repsfrom", "repluptodatevector", "repsto", "dsasignature", "auditingpolicy", "msdfsr-replicationgroupguid", "ms-ds-creatorsid",
            "msmqencryptkey", "msmqsignkey", "dpusercredentialsdata", "dpuserprivatedata", "msmqsigncertificates", "msmqdigests", "securityprotocol",
            "msexchmasteraccountsid", "msexchsaferecipientshash", "extensiondata", "jpegphoto", "usersmimecertificate",
            "msdfsr-contentsetguid", "msmqqueuetype", "msexcharchiveguid", "msexchdisabledarchiveguid", "frsreplicasetguid", "frsversionguid",
            "pkt", "msdfs-targetlistv2", "msdfs-generationguidv2", "msdfs-linkidentityguidv2", "securityidentifier", "dnsproperty", "dnsrecord",
            "marshalledinterface", "dplicense", "serviceclassinfo", "serviceclassid", "winsockaddresses", "serviceinstanceversion",
            "msds-allowedtoactonbehalfofotheridentity","msds-managedpasswordpreviousid", "msds-managedpasswordid", "msds-groupmsamembership",
            "msds-generationid", "msds-deviceid", "msds-cloudanchor", "replicationsignature"
        }, StringComparer.OrdinalIgnoreCase);

        private static readonly string[] EmptyArray = { };
        private static readonly Dictionary<string, string> EmptyTextDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, byte[]> EmptyBinaryDictionary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);


        private static HashSet<string> NewHashSetFrom(IEnumerable<string> source)
        {
            return new HashSet<string>(source ?? EmptyArray, StringComparer.OrdinalIgnoreCase);
        }
        private static Dictionary<string,string> NewDictionaryFrom(Dictionary<string,string> source)
        {
            return source == null ? EmptyTextDictionary
                : new Dictionary<string,string>(source, StringComparer.OrdinalIgnoreCase);
        }
        internal void FixupFromLoad(int expectedTag, int tagCount)
        {
            if (expectedTag != Tag)
                throw new InvalidOperationException($"File consistency error Tag:{Tag} ExpectedTag:{expectedTag} Dn:{Dn}");
            ServicePrincipalName = ServicePrincipalName ?? EmptyArray;
            SidHistory = NewHashSetFrom(SidHistory);
            AllowedToDelegateTo = AllowedToDelegateTo ?? EmptyArray;
            EmailAliases = NewHashSetFrom(EmailAliases);
            _otherAttributesText = NewDictionaryFrom(_otherAttributesText);
            ManagesTags = ManagesTags ?? new HashSet<int>();
            DirectMembersTags = DirectMembersTags ?? new HashSet<int>();
            DirectMemberOfsTags = DirectMemberOfsTags ?? new HashSet<int>();

            if (ManagerTag.HasValue && ManagerTag >= tagCount)
                throw new InvalidOperationException($"File consistency error ManagerTag:{ManagerTag} TagCount:{tagCount} Dn:{Dn}");

            var invalidManagesTag = ManagesTags.FirstOrDefault(t => t >= tagCount);
            if (invalidManagesTag >= tagCount)
                throw new InvalidOperationException($"File consistency error ManagesTag:{invalidManagesTag} TagCount:{tagCount} Dn:{Dn}");

            var invalidDirectMemberTag = DirectMembersTags.FirstOrDefault(t => t >= tagCount);
            if (invalidDirectMemberTag >= tagCount)
                throw new InvalidOperationException($"File consistency error DirectMemberTag:{invalidDirectMemberTag} TagCount:{tagCount} Dn:{Dn}");

            var invalidDirectMemberOfTag = DirectMemberOfsTags.FirstOrDefault(t => t >= tagCount);
            if (invalidDirectMemberOfTag >= tagCount)
                throw new InvalidOperationException($"File consistency error DirectMemberOfTag:{invalidDirectMemberOfTag} TagCount:{tagCount} Dn:{Dn}");
        }
        public Entry()
        {
            _otherAttributesText = EmptyTextDictionary;
            _otherAttributesBinary = EmptyBinaryDictionary;
        }
        private static HashSet<string> _unknownBinary = new HashSet<string>(100, StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> KnownTextAttributes = new HashSet<string>(100, StringComparer.OrdinalIgnoreCase);
        public static HashSet<string> KnownBinaryAttributes = new HashSet<string>(100, StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        private Dictionary<string, Types.RangedAttribute> _rangeAttributes;
        public static HashSet<string> _seenRangeAttributes = new HashSet<string>(5);
        public Entry(SearchResultEntry e, bool isChangeNotified, bool allAttributesLoaded, IEnumerable<string> otherAttributesToLoad, AdSync adSync, int version)
        {
            try
            {
                _rangeAttributes = new Dictionary<string, Types.RangedAttribute>(2);
                foreach (string a in e.Attributes.AttributeNames)
                {
                    var rangedAttribute = Types.asRangedAttribute(a);
                    if (rangedAttribute.IsRanged)
                    {
                        _rangeAttributes[rangedAttribute.BaseName] = rangedAttribute;
                        _seenRangeAttributes.Add(rangedAttribute.BaseName);
                    }
                }
                Version = version;
                Tag = -1;
                Dn = e?.DistinguishedName;
                DomainFlatName = Types.FromStringValue(e, "flatName", true);
                IsChangeNotified = isChangeNotified;
                Status = Types.ExistenceStatus.Exists;
                Class = Types.FromStringValues(e, "objectClass", ".", true);
                UserPrincipalName = Types.FromStringValue(e, "userPrincipalName");
                ServicePrincipalName = Types.FromStringValues(e, "servicePrincipalName");
                ObjectGuid = Types.FromGuidValue(e, "objectGuid") ?? Guid.Empty;
                Sid = Types.FromSidValue(e, "objectSid");
                SidHistory = new HashSet<string>(Types.FromSidValues(e, "sidhistory"), StringComparer.OrdinalIgnoreCase);
                SamAccountName = Types.FromStringValue(e, "sAMAccountName");
                SamAccountType = Types.FromSamAccountTypeValue(e, "sAMAccountType");
                UserAccountControl = Types.FromUserAccountControlValue(e, "userAccountControl");
                UserWorkstations = Types.FromStringValue(e, "userWorkstations");
                GroupType = Types.FromGroupTypeFlagsValue(e, "groupType");
                PasswordLastSet = Types.FromFileTime(e, "pwdlastset");
                LastLogonTimeStamp = Types.FromFileTime(e, "lastlogontimestamp");
                LogonCount = Types.FromIntValue(e, "logonCount") ?? 0;
                AccountExpires = Types.FromFileTime(e, "accountExpires");
                AllowedToDelegateTo = Types.FromStringValues(e, "msDS-AllowedToDelegateTo");
                WhenCreated = Types.FromGeneralizedTime(e, "whenCreated");

                Telephone = Types.FromStringValue(e, "telephoneNumber");
                Fax = Types.FromStringValue(e, "facsimileTelephoneNumber");
                Mobile = Types.FromStringValue(e, "mobile");

                var smtpAddresses = Types.FromStringValues(e, "proxyAddresses");
                // grab the primary email address for smtp mail enabled objects
                Email = smtpAddresses.Select(pa => new Types.ProxyAddress(pa, false))
                    .Where(pa => string.Equals(pa.AddressType, "SMTP", StringComparison.Ordinal)).Select(pa => pa.Address).FirstOrDefault();
                // if object is not smtp mail-enabled use the "mail" property directly...
                if (string.IsNullOrEmpty(Email))
                    Email = Types.FromStringValue(e, "mail");
                // grab email aliases for smtp mail enabled objects
                EmailAliases = new HashSet<string>(smtpAddresses.Select(pa => new Types.ProxyAddress(pa, false))
                    .Where(pa => string.Equals(pa.AddressType, "smtp", StringComparison.Ordinal)).Select(pa => pa.Address), StringComparer.OrdinalIgnoreCase);
                var targetProxyAddress = new Types.ProxyAddress(Types.FromStringValue(e, "targetAddress"), false);
                TargetEmail = string.Equals(targetProxyAddress.AddressType, "smtp", StringComparison.OrdinalIgnoreCase) ? targetProxyAddress.Address : null;
                MailboxGuid = Types.FromGuidValue(e, "msExchMailboxGuid");
                HideFromAddressBook = string.Equals(Types.FromStringValue(e, "msExchHideFromAddressLists"), "TRUE", StringComparison.OrdinalIgnoreCase);

                var sipProxyAddress = new Types.ProxyAddress(Types.FromStringValue(e, "msRTCSIP-PrimaryUserAddress"), false);
                SipAddress = (!string.IsNullOrEmpty(sipProxyAddress.Address) && string.Equals(sipProxyAddress.AddressType, "sip", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(Types.FromStringValue(e, "msRTCSIP-UserEnabled"), "TRUE", StringComparison.OrdinalIgnoreCase)) ? sipProxyAddress.Address : null;

                Country = Types.FromStringValue(e, "co", true);
                City = Types.FromStringValue(e, "l", true);
                ProvinceState = Types.FromStringValue(e, "st", true);
                StreetAddress = Types.FromStringValue(e, "streetAddress", true);
                PostalCode = Types.FromStringValue(e, "postalCode", true);

                Company = Types.FromStringValue(e, "company", true);
                Department = Types.FromStringValue(e, "department", true);
                Office = Types.FromStringValue(e, "physicalDeliveryOfficeName", true);
                PrimaryGroupId = Types.FromIntValue(e, "primaryGroupID");
                PrimaryGroupToken = Types.FromIntValue(e, "primaryGroupToken");

                Description = Types.FromStringValue(e, "description");
                DisplayName = Types.FromStringValue(e, "displayName");
                Title = Types.FromStringValue(e, "title");
                PersonalTitle = Types.FromStringValue(e, "personalTitle");
                FirstName = Types.FromStringValue(e, "givenName", true);
                LastName = Types.FromStringValue(e, "sn", true);
                Name = Types.FromStringValue(e, "name");
                Photo = Types.FromBinaryValue(e, "thumbnailPhoto");
                EmployeeType = Types.FromStringValue(e, "employeeType");
                EmployeeId = Types.FromStringValue(e, "employeeID");

                ManagerDeferredDn = Types.FromStringValue(e, "manager");
                if (_rangeAttributes.TryGetValue("member", out var range))
                {
                    DirectMembersDeferredDn = Types.FromStringValuesLarge(e, range, adSync).ToList();
                }
                else
                    DirectMembersDeferredDn = Types.FromStringValues(e, "member").ToList();
                AllAttributesLoaded = allAttributesLoaded;
                if (allAttributesLoaded)
                {
                    _otherAttributesText = new Dictionary<string, string>(Math.Max(1, e.Attributes.Count - StandardAttributes.Count), StringComparer.OrdinalIgnoreCase);
                    _otherAttributesBinary = new Dictionary<string, byte[]>(BinaryAttributes.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (string attributeName in e.Attributes.AttributeNames)
                    {
                        var rangedAttribute = Types.asRangedAttribute(attributeName);
                        if (!StandardAttributes.Contains(rangedAttribute.Name))
                            if (BinaryAttributes.Contains(rangedAttribute.Name) || (rangedAttribute.Name.Contains("guid") && Types.FromBinaryValue(e, rangedAttribute.Name)?.Length == 16))
                                _otherAttributesBinary[rangedAttribute.Name] = Types.FromBinaryValue(e, rangedAttribute.Name);
                            else
                            {
                                _otherAttributesText[rangedAttribute.Name] = Types.FromStringValue(e, rangedAttribute, adSync);
                                if (!_unknownBinary.Contains(rangedAttribute.Name) && (_otherAttributesText[rangedAttribute.Name]?.Contains("\0") ?? false))
                                {
                                    Console.WriteLine($"Unexpected Binary: {rangedAttribute.Name}");
                                    _unknownBinary.Add(rangedAttribute.Name);
                                }
                            }
                    }
                }
                else
                {
                    _otherAttributesBinary = otherAttributesToLoad?.Intersect(BinaryAttributes).ToDictionary(string.Intern, attr => Types.FromBinaryValue(e, attr), StringComparer.OrdinalIgnoreCase) ?? EmptyBinaryDictionary;
                    _otherAttributesText = otherAttributesToLoad?.Except(BinaryAttributes).Select(a => Types.asRangedAttribute(a)).ToDictionary(r => r.Name, r => Types.FromStringValue(e, r, adSync), StringComparer.OrdinalIgnoreCase) ?? EmptyTextDictionary;
                }
                KnownBinaryAttributes.UnionWith(_otherAttributesBinary.Keys);
                KnownTextAttributes.UnionWith(_otherAttributesText.Keys);

                ManagesTags = new HashSet<int>();

                DirectMembersTags = new HashSet<int>();
                DirectMemberOfsTags = new HashSet<int>();
                //AllGroupTypeMembersTags = new HashSet<int>();
                //AllUserMembersTags = new HashSet<int>();
                //AllMembersOfsTags = new HashSet<int>();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public int GetHashCode(Entry obj)
        {
            return ObjectGuid.GetHashCode();
        }
        public static Dictionary<string, int> AttributeChangeCount = new Dictionary<string, int>();
        public static void AttributeChangeIncrement(string name)
        {
            int count;
            AttributeChangeCount.TryGetValue(name, out count);
            AttributeChangeCount[name] = count + 1;
        }

        private static int SetDifferent<T>(ISet<T> x, IEnumerable<T> y, string name)
        {
            var result = (x?.SetEquals(y ?? Enumerable.Empty<T>()) ?? (y == null || !y.Any())) ? 0 : 1;
            if (result != 0) AttributeChangeIncrement(name);
            return result;
        }
        private static int DictionaryDifferent(IReadOnlyDictionary<string, string> x, IReadOnlyDictionary<string, string> y)
        {
            if (x == null && y == null) return 0;
            if (x == null || y == null || x.Count != y.Count) return 1;
            return x.Any(kv => !y.ContainsKey(kv.Key) || string.CompareOrdinal(kv.Value, y[kv.Key]) != 0) ? 1 : 0;
        }
        private static int DictionaryDifferent(IReadOnlyDictionary<string, byte[]> x, IReadOnlyDictionary<string, byte[]> y)
        {
            if (x == null && y == null) return 0;
            if (x == null || y == null || x.Count != y.Count) return 1;
            return x.Any(kv => !y.ContainsKey(kv.Key) || !Types.ByteArraysAreEqual(kv.Value, y[kv.Key])) ? 1 : 0;
        }
        private static int SequenceDifferent<T>(IEnumerable<T> x, IEnumerable<T> y, string name)
        {
            var result = (x?.SequenceEqual(y ?? Enumerable.Empty<T>()) ?? (y == null || !y.Any())) ? 0 : 1;
            if (result != 0) AttributeChangeIncrement(name);
            return result;
        }

        private static int AreDifferent<T>(T x, T y, string name) where T:IEquatable<T> 
        {
            var result = x.Equals(y) ? 0 : 1;
            if (result != 0) AttributeChangeIncrement(name);
            return result;
        }
        private static int AreDifferent<T>(T? x, T? y, string name) where T : struct
        {
            var result = x.Equals(y) ? 0 : 1;
            if (result != 0) AttributeChangeIncrement(name);
            return result;
        }

        private static int AreDifferent(string x, string y, string name) 
        {
            var result = string.Equals(x, y, StringComparison.Ordinal) ? 0 : 1;
            if (result != 0) AttributeChangeIncrement(name);
            return result;
        }

        public bool AllAttributesMatch(Entry other)
        {
            var differentCount = AreDifferent(Tag, other.Tag, nameof(Tag)) +
                                    AreDifferent(Dn, other.Dn, nameof(Dn)) +
                                    AreDifferent(Class, other.Class, nameof(Class)) +
                                    AreDifferent(UserPrincipalName, other.UserPrincipalName, nameof(UserPrincipalName)) +
                                    SequenceDifferent(ServicePrincipalName, other.ServicePrincipalName, nameof(ServicePrincipalName)) +
                                    AreDifferent(Sid, other.Sid, nameof(Sid)) +
                                    SetDifferent(SidHistory, other.SidHistory, nameof(SidHistory)) +
                                    AreDifferent(SamAccountName, other.SamAccountName, nameof(SamAccountName)) +
                                    AreDifferent(DomainFlatName, other.DomainFlatName, nameof(DomainFlatName)) +
                                    AreDifferent(SamAccountType, other.SamAccountType, nameof(SamAccountType)) +
                                    AreDifferent(UserAccountControl, other.UserAccountControl, nameof(UserAccountControl)) +
                                    AreDifferent(UserWorkstations, other.UserWorkstations, nameof(UserWorkstations)) + 
                                    AreDifferent(GroupType, other.GroupType, nameof(GroupType)) +
                                    AreDifferent(PasswordLastSet, other.PasswordLastSet, nameof(PasswordLastSet)) +
                                    AreDifferent(LastLogonTimeStamp, other.LastLogonTimeStamp, nameof(LastLogonTimeStamp)) +
                                    AreDifferent(LogonCount, other.LogonCount, nameof(LogonCount)) +
                                    AreDifferent(WhenCreated, other.WhenCreated, nameof(WhenCreated)) +
                                    AreDifferent(AccountExpires, other.AccountExpires, nameof(AccountExpires)) + 
                                    SequenceDifferent(AllowedToDelegateTo, other.AllowedToDelegateTo, nameof(AllowedToDelegateTo)) + 
                                    AreDifferent(Telephone, other.Telephone, nameof(Telephone)) +
                                    AreDifferent(Fax, other.Fax, nameof(Fax)) + 
                                    AreDifferent(Mobile, other.Mobile, nameof(Mobile)) + 
                                    AreDifferent(Email, other.Email, nameof(Email)) + 
                                    SetDifferent(EmailAliases, other.EmailAliases, nameof(EmailAliases)) + 
                                    AreDifferent(TargetEmail, other.TargetEmail, nameof (TargetEmail)) + 
                                    AreDifferent(MailboxGuid, other.MailboxGuid, nameof(MailboxGuid)) + 
                                    AreDifferent(HideFromAddressBook, other.HideFromAddressBook, nameof(HideFromAddressBook)) + 
                                    AreDifferent(SipAddress, other.SipAddress, nameof (SipAddress)) + 
                                    AreDifferent(Country, other.Country, nameof (Country)) + 
                                    AreDifferent(City, other.City, nameof (City)) + 
                                    AreDifferent(ProvinceState, other.ProvinceState, nameof (ProvinceState)) + 
                                    AreDifferent(StreetAddress, other.StreetAddress, nameof (StreetAddress)) + 
                                    AreDifferent(PostalCode, other.PostalCode, nameof(PostalCode)) + 
                                    AreDifferent(Company, other.Company, nameof (Company)) + 
                                    AreDifferent(Department, other.Department, nameof (Department)) + 
                                    AreDifferent(Office, other.Office, nameof (Office)) + 
                                    AreDifferent(DisplayName, other.DisplayName, nameof (DisplayName)) +
                                    AreDifferent(Description, other.Description, nameof(Description)) +
                                    AreDifferent(Title, other.Title, nameof (Title)) +
                                    AreDifferent(PersonalTitle, other.PersonalTitle, nameof(PersonalTitle)) +
                                    AreDifferent(FirstName, other.FirstName, nameof (FirstName)) + 
                                    AreDifferent(LastName, other.LastName, nameof (LastName)) + 
                                    AreDifferent(Name, other.Name, nameof (Name)) + 
                                    SequenceDifferent(Photo, other.Photo, nameof (Photo)) +
                                    AreDifferent(EmployeeType, other.EmployeeType, nameof(EmployeeType)) +
                                    AreDifferent(EmployeeId, other.EmployeeId, nameof(EmployeeId)) +
                                    AreDifferent(ManagerTag, other.ManagerTag, nameof(ManagerTag)) + 
                                    SetDifferent(DirectMembersTags, other.DirectMembersTags, nameof (DirectMembersTags)) + 
                                    AreDifferent(PrimaryGroupId, other.PrimaryGroupId, nameof(PrimaryGroupId)) +
                                    AreDifferent(PrimaryGroupToken, other.PrimaryGroupToken, nameof(PrimaryGroupToken)) +
                                    DictionaryDifferent(_otherAttributesText, other._otherAttributesText) +
                                    DictionaryDifferent(_otherAttributesBinary, other._otherAttributesBinary)
                                ;
            return differentCount == 0;
        }
    }
}
