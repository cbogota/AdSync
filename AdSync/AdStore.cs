using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Data.SqlClient;
using Newtonsoft.Json;
using Health.Abstractions;
using System.Threading.Tasks;

namespace AdSync
{
    public class AdStore
    {
        public object SyncRoot { get; }

        public AdSync AdSync { get; }
        private readonly IMetricCounter _hiAddOrUpdate;
        private readonly IMetricCounter _hiUpdateNew;
        private readonly IMetricCounter _hiUpdateChange;
        private readonly IMetricCounter _hiUpdateNoChange;
        private readonly IMetricCounter _hiLoadNew;
        private readonly IMetricCounter _hiLoadChange;
        private readonly IMetricCounter _hiLoadNoChange;
        private readonly IMetricCounter _hiAddOrUpdateErrors;
        private readonly IMetricCounter _hiDeleteTag;
        private readonly IMetricCounter _hiDeleteTagErrors;

        private readonly IMetricTimer _hiLoadFromFile;
        private readonly IMetricTimer _hiWriteToFile;

        public FileInfo CacheFileInfo { get; }
        public StreamWriter CacheLog { get; }
        private readonly List<Entry> _objByTag;
        private readonly ConcurrentDictionary<Guid, int> _tagByGuid;
        private readonly ConcurrentDictionary<string, int> _tagByDn;
        private readonly ConcurrentDictionary<string, int> _tagBySamAccountName;
        private readonly ConcurrentDictionary<string, int> _tagBySidOrSidHistory;
        private readonly ConcurrentDictionary<string, int> _tagByForeignSecurityPrincipalSid;
        private readonly ConcurrentDictionary<string, int> _tagByUpn;
        private readonly ConcurrentDictionary<string, int> _tagByEmail;
        private readonly ConcurrentDictionary<int, HashSet<int>> _primaryGroupMemberTags;
        private readonly ConcurrentDictionary<int, int> _tagByPrimaryGroupToken;
        public IEnumerable<string> OtherAttributes { get; }
        public IEnumerable<string> AllAttributes { get; }
        public bool LoadAllAttributes { get; }
        public int Count => _objByTag.Count;
        public Entry GetByTag(int? tag)
        {
            if (tag.HasValue && (_objByTag == null || tag.Value >= _objByTag.Count))
                throw new InvalidOperationException($"Tag {tag.Value} exceeds max tag {_objByTag?.Count}");
            return tag.HasValue ? _objByTag[tag.Value] : null;
        }
        public IEnumerable<Entry> GetByTag(IEnumerable<int> tags) => tags?.Select(tag => GetByTag(tag)).Where(e => e != null) ?? Enumerable.Empty<Entry>(); 
        public Entry this[int? tag] => GetByTag(tag);
        public Entry GetByDn(string dn)
        {
            int tag;
            return dn != null && _tagByDn.TryGetValue(dn, out tag) ? _objByTag[tag] : null;
        }
        public Entry this[string dn] => GetByDn(dn);
        public Entry GetByGuid(Guid objectguid)
        {
            int tag;
            return _tagByGuid.TryGetValue(objectguid, out tag) ? _objByTag[tag] : null;
        }
        public Entry this[Guid guid] => GetByGuid(guid);
        public Entry GetBySamAccountName(string samAccountName)
        {
            int tag;
            if (string.IsNullOrEmpty(samAccountName))
                return null;
            // if samAccountName has a domain flat name prefix that matches this store's domain flat name, 
            // search without the prefix
            if (samAccountName.StartsWith(DomainFlatName, StringComparison.OrdinalIgnoreCase) &&
                samAccountName.Length > DomainFlatName.Length && samAccountName[DomainFlatName.Length] == '\\')
                samAccountName = samAccountName.Substring(DomainFlatName.Length+1);
            return _tagBySamAccountName.TryGetValue(samAccountName, out tag) ? _objByTag[tag] : null;
        }
        public Entry GetByUpn(string upn)
        {
            int tag;
            return upn != null && _tagByUpn.TryGetValue(upn, out tag) ? _objByTag[tag] : null;
        }
        public Entry GetByEmail(string email)
        {
            int tag;
            return email != null && _tagByEmail.TryGetValue(email, out tag) ? _objByTag[tag] : null;
        }

        public Entry GetByPrimaryGroupToken(int? primaryGoupToken)
        {
            int tag;
            return primaryGoupToken.HasValue && _tagByPrimaryGroupToken.TryGetValue(primaryGoupToken.Value, out tag)
                ? _objByTag[tag]
                : null;
        }

        public IEnumerable<Entry> AllPrimaryGroupsHavingMembers => _primaryGroupMemberTags.Where(kv => kv.Value.Count > 0).Select(kv => GetByPrimaryGroupToken(kv.Key));
        
        public Entry GetBySid(string sid)
        {
            int tag;
            return sid != null && _tagBySidOrSidHistory.TryGetValue(sid, out tag) ? _objByTag[tag] : null;
        }
        public void BulkDnLookup(string[] dnList, out List<int> tags, out List<string> notFound)
        {
            lock (SyncRoot)
            {
                if (dnList == null) dnList = new string[] { };
                tags = new List<int>(dnList.Length);
                notFound = null;
                foreach (var dn in dnList)
                {
                    var tag = this[dn]?.Tag;
                    if (tag.HasValue)
                        tags.Add(tag.Value);
                    else
                    {
                        if (notFound == null) notFound = new List<string>();
                        notFound.Add(dn);
                    }
                }
            }
        }
        private static void AddLookup<TKey>(ConcurrentDictionary<TKey, int> dictionary, TKey key, int tag)
        {
            if (key != null) dictionary[key] = tag;
        }
        private static void RemoveLookup<TKey>(ConcurrentDictionary<TKey, int> dictionary, TKey key)
        {
            int removedTag;
            if (key != null) dictionary.TryRemove(key, out removedTag);
        }
        private static void AddLookups(ConcurrentDictionary<string, int> dictionary, IEnumerable<string> keys, int tag)
        {
            foreach (var key in keys)
                AddLookup(dictionary, key, tag);
        }

        private static void RemoveLookups<TKey>(ConcurrentDictionary<TKey, int> dictionary, IEnumerable<TKey> keys)
        {
            foreach (var key in keys)
                RemoveLookup(dictionary, key);
        }

        private void DeleteTag(int tag)
        {
            _hiDeleteTag?.Increment();
            try
            {
                lock (SyncRoot)
                {
                    var e = GetByTag(tag);
                    RemoveManagesBackLink(e);
                    RemoveDirectMembersDirectMemberOfsBacklinks(e);
                    RemovePrimaryGroupMembership(e);
                    RemoveLookup(_tagBySamAccountName, e.SamAccountName);
                    RemoveLookup(_tagByUpn, e.UserPrincipalName);
                    if (string.Equals(e.Class, "top.foreignSecurityPrincipal", StringComparison.OrdinalIgnoreCase))
                        RemoveLookup(_tagByForeignSecurityPrincipalSid, e.Sid);
                    else
                    {
                        RemoveLookup(_tagBySidOrSidHistory, e.Sid);
                        RemoveLookups(_tagBySidOrSidHistory, e.SidHistory);
                    }
                    RemoveLookup(_tagByEmail, e.Email);
                    RemoveLookups(_tagByEmail, e.EmailAliases);
                    RemoveLookup(_tagByDn, e.Dn);
                    RemoveLookup(_tagByGuid, e.ObjectGuid);
                    if (e.PrimaryGroupToken.HasValue)
                        RemoveLookup(_tagByPrimaryGroupToken, e.PrimaryGroupToken.Value);
                    _objByTag[e.Tag] = null;
                }
            }
            catch
            {
                _hiDeleteTagErrors?.Increment();
            }
        }

        private void CheckForDefect(string keyName, string key, string dn, IDictionary<string, int> lookup)
        {
            if (!string.IsNullOrEmpty(key) && lookup.ContainsKey(key))
                CacheLog.WriteLine($"{DateTimeOffset.Now} Duplicate {keyName}: {key}\r\n\t{dn}\r\n\t{_objByTag[lookup[key]].Dn}");
        }
        private void AddAllLookupsForEntity(Entry e)
        {
            var tag = e.Tag;
            if (tag >= _objByTag.Count)
                throw new InvalidOperationException($"File consistency error tag too large:{e.Tag} {e.Dn}");
            if (tag != _objByTag[tag].Tag)
                throw new InvalidOperationException($"File consistency error tag misplaced:{e.Tag} ({_objByTag[tag].Tag}) {e.Dn}");

            e.Status = Types.ExistenceStatus.Detecting;

            CheckForDefect("samAccountName", e.SamAccountName, e.Dn, _tagBySamAccountName);
            AddLookup(_tagBySamAccountName, e.SamAccountName, tag);
            CheckForDefect("userPrincipalName", e.UserPrincipalName, e.Dn, _tagByUpn);
            AddLookup(_tagByUpn, e.UserPrincipalName, tag);
            if (string.Equals(e.Class, "top.foreignSecurityPrincipal", StringComparison.OrdinalIgnoreCase))
            {
                if (e.Sid != null && _tagByForeignSecurityPrincipalSid.ContainsKey(e.Sid) &&
                    _objByTag[_tagByForeignSecurityPrincipalSid[e.Sid]].Sid != e.Sid)
                {
                    var other = _objByTag[_tagByForeignSecurityPrincipalSid[e.Sid]];
                    CacheLog.WriteLine($"{DateTimeOffset.Now} Inconsistent ForeignSecurityPrincipal {e.Dn}\r\n\tSid:{e.Sid}\r\n\tOther:{other.Dn}\r\n\tSid:{other.Sid}");
                }
                AddLookup(_tagByForeignSecurityPrincipalSid, e.Sid, tag);
            }
            else
            {
                CheckForDefect("Sid", e.Sid, e.Dn, _tagBySidOrSidHistory);
                AddLookup(_tagBySidOrSidHistory, e.Sid, tag);

                if (e.SidHistory != null)
                    foreach (var sid in e.SidHistory)
                        CheckForDefect("SidHistory", sid, e.Dn, _tagBySidOrSidHistory);
                AddLookups(_tagBySidOrSidHistory, e.SidHistory, tag);
            }
            // only track email and email aliases for objects that 1) have a mailbox, 2) are accounts and 3) are not disabled
            // this may need to change for cases where a federating AD does not use Exchange (i.e. none of their objects will have a mailboxguid)
            if (e.MailboxGuid.HasValue && !(e.UserAccountControl ?? Types.UserAccountControlFlags.None).HasFlag(Types.UserAccountControlFlags.AccountDisabled))
            {
                CheckForDefect("Email", e.Email, e.Dn, _tagByEmail);
                AddLookup(_tagByEmail, e.Email, tag);
                if (e.EmailAliases != null)
                    foreach (var email in e.EmailAliases)
                        CheckForDefect("EmailAlias", email, e.Dn, _tagByEmail);
                AddLookups(_tagByEmail, e.EmailAliases, tag);
            }
            CheckForDefect("DN", e.Dn, e.Dn, _tagByDn);
            AddLookup(_tagByDn, e.Dn, tag);
            if (e.PrimaryGroupId.HasValue)
                AddPrimaryGroupMembership(e);
            if (e.PrimaryGroupToken.HasValue)
                AddLookup(_tagByPrimaryGroupToken, e.PrimaryGroupToken.Value, e.Tag);
            if (_tagByGuid.ContainsKey(e.ObjectGuid))
                CacheLog.WriteLine($"{DateTimeOffset.Now} File consistency error Tag:{e.Tag} ObjectGuid:{e.ObjectGuid} AlreadyPresentTag:{_tagByGuid[e.ObjectGuid]}");
            AddLookup(_tagByGuid, e.ObjectGuid, tag);
        }

        private void BuildLookups()
        {
            lock (SyncRoot)
            {
                foreach (var e in Objects)
                    AddAllLookupsForEntity(e);
            }
        }

        internal void MarkAllAsDetecting()
        {
            lock (SyncRoot)
            {
                foreach (var e in Objects)
                    e.Status = Types.ExistenceStatus.Detecting;
            }
        }

        public void DeleteUndetected()
        {
            lock (SyncRoot)
            {
                for(var i = 0; i<Count; i++)
                    if (_objByTag[i]?.Status == Types.ExistenceStatus.Detecting)
                        DeleteTag(i);
            }
        }
        public int DeferredCount()
        {
            lock (SyncRoot)
                return _objByTag.Sum(e => e.DeferredCount);
        }

        public void ResolveAllDeferred()
        {
            lock (SyncRoot)
                _objByTag.ForEach(ResolveDeferred);
        }

        public IEnumerable<string> DeferredObjects()
        {
            lock (SyncRoot)
                return _objByTag.SelectMany(e => e.DeferredDn);
        }

        public IEnumerable<string> ObjectsWithDeferrals()
        {
            lock (SyncRoot)
                return _objByTag.Where(e => e.DeferredCount > 0).Select(e => e.Dn);
        }

        public IEnumerable<Entry> Objects { get { lock (SyncRoot) foreach (var e in _objByTag) if (e != null) yield return e; } }
        public Entry[] ObjectsSnapshot { get { lock (SyncRoot) return _objByTag.ToArray(); } }

        public string Domain { get; }
        public string BaseDn { get; }

        public string DomainFlatName { get; }

        public bool InitialLoadComplete { get; internal set; }

        public IEnumerable<string> VerifyBacklinks()
        {
            lock (SyncRoot)
            {
                yield return DateTime.Now.ToLongTimeString() + " Verify Manager backlink";
                // verify that for every A that has a Manager M, that M has Manages backlink that says M manages A
                foreach (var mb in Objects.Where(e => e.ManagerTag.HasValue && !(Manager(e)?.ManagesTags?.Contains(e.Tag) ?? false)).Select(e => $"Manager backlink broken for {e.SamAccountName}"))
                    yield return mb;

                yield return DateTime.Now.ToLongTimeString() + " Verify Manages backlink";
                // verify that for every A that Manager M manages, that A's Manager is M
                foreach (var mt in Objects.SelectMany(e => Manages(e).Where(subordinate => subordinate.ManagerTag != e.Tag).Select(subordinate => $"Manages backlink broken for {e.SamAccountName} ({subordinate.SamAccountName})")))
                    yield return mt;

                yield return (DateTime.Now.ToLongTimeString() + " Verify MemberOf backlink");
                // verify that for every M that is in G's DirectMembers set, that G is in M's DirectMemberOfs set
                foreach (var mb in Objects.SelectMany(group => DirectMembers(group).Where(member => //member?.DirectMemberOfsTags == null || 
                !member.DirectMemberOfsTags.Contains(group.Tag)).Select(member => $"MemberOf backlink broken for group {group.SamAccountName} member {member?.SamAccountName}")))
                    yield return mb;

                yield return (DateTime.Now.ToLongTimeString() + " Verify Member backlink");
                // verify that for every G that is in M's DirectMemberOfs set, that M is in G's DirectMembers set
                foreach (var m in Objects.SelectMany(member => DirectMemberOfs(member).Where(group => !group.DirectMembersTags.Contains(member.Tag)).Select(group => $"Member backlink broken for {group.SamAccountName} member {member.SamAccountName}")))
                    yield return m;

                yield return (DateTime.Now.ToLongTimeString() + " Compute Membership lookup");
                var membership = new HashSet<MemberOf>(Objects.SelectMany(group => AllMembersTags(group).Select(memberTag => new MemberOf(group.Tag, memberTag))));

                yield return (DateTime.Now.ToLongTimeString() + " Compute MembershipOf lookup");
                var membershipOf = new HashSet<MemberOf>(Objects.SelectMany(member => AllMemberOfsTags(member).Select(groupTag => new MemberOf(groupTag, member.Tag))));

                yield return (DateTime.Now.ToLongTimeString() + " Verify AllMemberOfs backlink");
                var memberOfBacklinkMissing = new HashSet<MemberOf>(membership);
                memberOfBacklinkMissing.ExceptWith(membershipOf);
                foreach (var m in memberOfBacklinkMissing.Select(mo => $"AllMemberOfs backlink missing for {MemberOfToString(mo)}"))
                    yield return m;

                yield return (DateTime.Now.ToLongTimeString() + " Verify AllMembers backlink");
                var memberLinkMissing = new HashSet<MemberOf>(membershipOf);
                memberLinkMissing.ExceptWith(membership);
                foreach (var m in memberLinkMissing.Select(mo => $"AllMembers link missing for {MemberOfToString(mo)}"))
                    yield return m;

                yield return (DateTime.Now.ToLongTimeString() + " Verify HasMember");
                var hasMemberConfirmed = new HashSet<MemberOf>(membership.Count);
                Parallel.ForEach(
                    membership,
                    () => new HashSet<MemberOf>(),
                    (item, loopState, localState) =>
                    {
                        if (HasMember(GetByTag(item.GroupTag), GetByTag(item.MemberTag)))
                            localState.Add(item);
                        return localState;
                    },
                    localState =>
                    {
                        lock (hasMemberConfirmed)
                            hasMemberConfirmed.UnionWith(localState);
                    }
                );
                //hasMemberDefects.RemoveWhere(mo => HasMember(GetByTag(mo.GroupTag), GetByTag(mo.MemberTag)));
                var hasMemberDefects = new HashSet<MemberOf>(membership);
                hasMemberDefects.ExceptWith(hasMemberConfirmed);
                foreach (var m in hasMemberDefects.Select(mo => $"HasMember defect {MemberOfToString(mo)}"))
                    yield return m;

                yield return (DateTime.Now.ToLongTimeString() + " Done");
            }            
        }
        public Entry Manager(Entry e) => GetByTag(e?.ManagerTag);
        public bool HasSameManager(Entry x, Entry y) => x?.ManagerTag == y?.ManagerTag;
        public IEnumerable<Entry> Manages(Entry e) => GetByTag(e?.ManagesTags);
        private void AddManagesBackLink(Entry e)
        {
            Manager(e)?.ManagesTags.Add(e.Tag);
        }
        private void RemoveManagesBackLink(Entry e)
        {
            Manager(e)?.ManagesTags.Remove(e.Tag);
        }
        public IEnumerable<Entry> DirectMembers(Entry e) => GetByTag(e?.DirectMembersTags);
        public IEnumerable<Entry> DirectGroupTypeMembers(Entry e) => GetByTag(e?.DirectMembersTags).Where(g => g.IsGroup);

        // adds all members (direct and indirect) of the group to the provided membersTags hashset
        private void AddAllMembersTagsToHashSet(Entry group, HashSet<int> membersTags)
        {
            if (membersTags == null) throw new ArgumentNullException(nameof(membersTags));
            HashSet<int> primaryGroupMembers;
            // primary gorup members are never groups, so no need for recursive calls
            if (group.PrimaryGroupToken.HasValue && _primaryGroupMemberTags.TryGetValue(group.PrimaryGroupToken.Value, out primaryGroupMembers))
                lock (primaryGroupMembers)
                    membersTags.UnionWith(primaryGroupMembers);
            if (group?.DirectMembersTags == null) return;
            // direct members may contain groups, call recursively when we see new group type members
            foreach (var directMember in DirectMembers(group).Where(directMember => !membersTags.Contains(directMember.Tag)))
            {
                membersTags.Add(directMember.Tag);
                if (directMember.IsGroup) AddAllMembersTagsToHashSet(directMember, membersTags);
            }
        }

        public HashSet<int> AllMembersTags(Entry group)
        {
            if (!group.IsGroup) return new HashSet<int>();
            var result = new HashSet<int>();
            AddAllMembersTagsToHashSet(group, result);
            return result;
        }

        public IEnumerable<Entry> AllMembers (Entry group) => GetByTag(AllMembersTags(group));

        // adds all group type members (direct and indirect) of this group to the provided groupTypeMembersTags hashset
        private void AddAllGroupTypeMembersTagsToHashSet(Entry group, HashSet<int> groupTypeMembersTags)
        {
            if (groupTypeMembersTags == null) throw new ArgumentNullException(nameof(groupTypeMembersTags));
            if (group?.DirectMembersTags == null) return;
            foreach (var directMember in DirectGroupTypeMembers(group).Where(directMember => !groupTypeMembersTags.Contains(directMember.Tag)))
            {
                groupTypeMembersTags.Add(directMember.Tag);
                AddAllGroupTypeMembersTagsToHashSet(directMember, groupTypeMembersTags);
            }
        }

        public HashSet<int> AllGroupTypeMembersTags(Entry group)
        {
            if (!group.IsGroup) return null;
            var result = new HashSet<int>();
            AddAllGroupTypeMembersTagsToHashSet(group, result);
            return result;
        }

        public IEnumerable<Entry> AllGroupTypeMembers(Entry group) => GetByTag(AllGroupTypeMembersTags(group));

        // searches group type members for the member
        private bool MemberHasMember(Entry group, Entry checkIfMember, HashSet<int> groupTypeMembersTags)
        {
            if (groupTypeMembersTags == null) throw new ArgumentNullException(nameof(groupTypeMembersTags));
            if (!group.IsGroup) return false;
            foreach (var directMember in DirectGroupTypeMembers(group).Where(directMember => !groupTypeMembersTags.Contains(directMember.Tag)))
            {
                if (directMember.DirectMembersTags.Contains(checkIfMember.Tag) || HasPrimaryGroupMember(directMember, checkIfMember)) return true;
                groupTypeMembersTags.Add(directMember.Tag);
                if (MemberHasMember(directMember, checkIfMember, groupTypeMembersTags)) return true;
            }
            return false;
        }

        private bool HasPrimaryGroupMember(Entry primaryGroup, Entry checkIfMember)
        {
            HashSet<int> memberTags;
            if (!primaryGroup.IsGroup || !primaryGroup.PrimaryGroupToken.HasValue ||
                !_primaryGroupMemberTags.TryGetValue(primaryGroup.PrimaryGroupToken.Value, out memberTags))
                return false;
            lock (memberTags)
                return memberTags.Contains(checkIfMember.Tag);
        }

        private void AddPrimaryGroupMembership(Entry e)
        {
            if (!e.PrimaryGroupId.HasValue) return;
            var memberTags = _primaryGroupMemberTags.GetOrAdd(e.PrimaryGroupId.Value, new HashSet<int>());
            lock (memberTags)
                memberTags.Add(e.Tag);
        }
        public bool HasMember(Entry group, Entry checkIfMember) => group.DirectMembersTags.Contains(checkIfMember.Tag) || HasPrimaryGroupMember(group, checkIfMember) || 
                                                                      MemberHasMember(group, checkIfMember, new HashSet<int>());

        public string MemberOfToString (MemberOf mo) => $"({GetByTag(mo.GroupTag)?.SamAccountName}:{GetByTag(mo.MemberTag)?.SamAccountName})";
        public IEnumerable<Entry> DirectMemberOfs(Entry e) => GetByTag(e?.DirectMemberOfsTags);

        // adds all groups that e is a member of to the provided memberOfTags hashset
        // adds DirectMemberOfsTags of e and then recursively calls this method for each of those objects
        private void AddAllMemberOfsTagsToHashSet(Entry e, HashSet<int> memberOfTags)
        {
            if (memberOfTags == null) throw new ArgumentNullException(nameof(memberOfTags));
            var primaryGroup = GetByPrimaryGroupToken(e.PrimaryGroupId);
            if (primaryGroup != null && !memberOfTags.Contains(primaryGroup.Tag))
            {
                memberOfTags.Add(primaryGroup.Tag);
                if (primaryGroup.DirectMemberOfsTags != null) AddAllMemberOfsTagsToHashSet(primaryGroup, memberOfTags);
            }
            if (e?.DirectMemberOfsTags == null) return;
            foreach (var group in DirectMemberOfs(e).Where(group => !memberOfTags.Contains(group.Tag)))
            {
                memberOfTags.Add(group.Tag);
                if (group.DirectMemberOfsTags != null) AddAllMemberOfsTagsToHashSet(group, memberOfTags);
            }
        }
        public HashSet<int> AllMemberOfsTags (Entry e)
        {
            if (e?.DirectMemberOfsTags == null) return null;
            var result = new HashSet<int>();
            AddAllMemberOfsTagsToHashSet(e, result);
            return result;
        }
        public IEnumerable<Entry> AllMemberOfs(Entry e) => AllMemberOfsTags(e)?.Select(tag => GetByTag(tag));

        private void AddDirectMembersDirectMemberOfsBacklinks(Entry e)
        {
            foreach (var member in DirectMembers(e))
                member?.DirectMemberOfsTags?.Add(e.Tag);
        }
        private void RemoveDirectMembersDirectMemberOfsBacklinks(Entry e)
        {
                foreach (var member in DirectMembers(e))
                    member?.DirectMemberOfsTags?.Remove(e.Tag);
        }

        private void RemovePrimaryGroupMembership(Entry e)
        {
            HashSet<int> memberTags;
            if (e.PrimaryGroupId.HasValue && _primaryGroupMemberTags.TryGetValue(e.PrimaryGroupId.Value, out memberTags))
                lock (memberTags)
                    memberTags.Remove(e.Tag);
        }
        private string TrustedDomainFlatNameEntryDn(string domain) => $"CN={domain},CN=System,{BaseDn}";
        public string GetTrustedDomainFlatName(string domain) => GetByDn(TrustedDomainFlatNameEntryDn(domain))?.DomainFlatName;

        public void ResolveDeferred(Entry e)
        {
            lock (SyncRoot)
            {
                // manager deferred
                if (e?.ManagerDeferredDn != null)
                {
                    e.ManagerTag = GetByDn(e.ManagerDeferredDn)?.Tag;
                    if (e.ManagerTag.HasValue)
                    {
                        // update Manages backlink
                        e.ManagerDeferredDn = null;
                        Manager(e)?.ManagesTags.Add(e.Tag);
                    }
                }

                // group members deferred
                if (e?.DirectMembersDeferredDn == null) return;
                var newDeferred = new List<string>();
                foreach (var dn in e.DirectMembersDeferredDn)
                {
                    var directMember = GetByDn(dn);
                    if (directMember != null)
                    {
                        e.DirectMembersTags.Add(directMember.Tag);
                        // update the member's DirectMembersOfs backlink
                        directMember.DirectMemberOfsTags.Add(e.Tag);
                    }
                    else
                        newDeferred.Add(dn);
                }
                e.DirectMembersDeferredDn = newDeferred.Count > 0 ? newDeferred : null;
            }
        }
        private static bool AreSetsEqual<T>(HashSet<T> x, HashSet<T> y) => (x == null && y == null) || (x != null && y != null && x.SetEquals(y));

        public void AddOrUpdate(SearchResultEntry newEntry, bool isChangeNotified)
        {
            _hiAddOrUpdate?.Increment();
            try
            {
                var e = new Entry(newEntry, isChangeNotified, LoadAllAttributes, OtherAttributes, AdSync);
                if (e.ObjectGuid == Guid.Empty) return;
                // if entry has a samaccountname and no flatname add the flatname for the domain...
                if (!string.IsNullOrEmpty(e.SamAccountName) &&
                    string.IsNullOrEmpty(e.DomainFlatName))
                    e.DomainFlatName = DomainFlatName;
                lock (SyncRoot)
                {
                    var existing = GetByGuid(e.ObjectGuid);

                    // exit and do nothing if existing object in store was change notified and this update is coming from the bulk load
                    if (!isChangeNotified && (existing?.IsChangeNotified ?? false)) return;

                    // new object, handle tag, guid and primary group lookups, remaining lookups handled after this if/then/else block
                    if (existing == null)
                    {
                        (isChangeNotified ? _hiUpdateNew : _hiLoadNew)?.Increment();
                        e.Tag = Count;
                        _objByTag.Add(e);
                        _tagByGuid[e.ObjectGuid] = e.Tag;
                        AddPrimaryGroupMembership(e);
                        ResolveDeferred(e);
                    }
                    // if object already existed in store, copy some of the existing attributes and fix up backlinks and lookup dictionaries
                    else
                    {
                        e.Tag = existing.Tag;
                        ResolveDeferred(e);

                        if (isChangeNotified)
                            (e.AllAttributesMatch(existing) ? _hiUpdateNoChange : _hiUpdateChange)?.Increment();
                        else
                            (e.AllAttributesMatch(existing) ? _hiLoadNoChange : _hiLoadChange)?.Increment();

                        // dn of object changed? Then first make sure we resolve any deferred DnLookups then remove old Dn lookup
                        if (!string.Equals(e.Dn, existing.Dn, StringComparison.OrdinalIgnoreCase))
                        {
                            ResolveAllDeferred();
                            RemoveLookup(_tagByDn, existing.Dn);
                        }

                        // if manager has changed, remove backlinks from old manager
                        if (!HasSameManager(e, existing))
                            RemoveManagesBackLink(existing);

                        // retain the existing objects ManagesTags hashset
                        e.ManagesTags = existing.ManagesTags;

                        // have members changed? If so fixup memberof backlinks
                        if (!AreSetsEqual(e.DirectMembersTags, existing.DirectMembersTags))
                            RemoveDirectMembersDirectMemberOfsBacklinks(existing);

                        // retain the existing objects DirectMemberOf hashset
                        e.DirectMemberOfsTags = existing.DirectMemberOfsTags;

                        // samAccountName of object changed? Then remove old SamAccountName lookup
                        if (!(string.IsNullOrEmpty(existing.SamAccountName) ||
                              string.Equals(e.SamAccountName, existing.SamAccountName,
                                  StringComparison.OrdinalIgnoreCase)))
                            RemoveLookup(_tagBySamAccountName, existing.SamAccountName);

                        // Upn of object changed? Then remove old Upn lookup
                        if (!(string.IsNullOrEmpty(existing.UserPrincipalName) ||
                              string.Equals(e.UserPrincipalName, existing.UserPrincipalName,
                                  StringComparison.OrdinalIgnoreCase)))
                            RemoveLookup(_tagByUpn, existing.UserPrincipalName);

                        // sid of object changed? Then remove old sid lookup
                        if (existing.Sid != null &&
                            !string.Equals(e.Sid, existing.Sid, StringComparison.OrdinalIgnoreCase))
                            RemoveLookup(_tagBySidOrSidHistory, existing.Sid);

                        // if sid history has been modified, move old sid history lookup and add new
                        if (!AreSetsEqual(existing.SidHistory, e.SidHistory))
                            RemoveLookups(_tagBySidOrSidHistory, existing.SidHistory);

                        // if email has been modified, remove old lookup
                        if (existing.Email != null &&
                            !string.Equals(e.Email, existing.Email, StringComparison.OrdinalIgnoreCase))
                            RemoveLookup(_tagByEmail, existing.Email);

                        // if email aliases have been modified, remove old lookups
                        if (!AreSetsEqual(existing.EmailAliases, e.EmailAliases))
                            RemoveLookups(_tagByEmail, existing.EmailAliases);

                        // if primary gorup membership has changed, remove old lookup and add new
                        if (e.PrimaryGroupId != existing.PrimaryGroupId)
                        {
                            RemovePrimaryGroupMembership(existing);    
                            AddPrimaryGroupMembership(e);
                        }

                        _objByTag[e.Tag] = e;
                    }
                    _tagByDn[e.Dn] = e.Tag;
                    AddManagesBackLink(e);
                    AddDirectMembersDirectMemberOfsBacklinks(e);
                    AddLookup(_tagBySamAccountName, e.SamAccountName, e.Tag);
                    AddLookup(_tagByUpn, e.UserPrincipalName, e.Tag);
                    AddLookup(_tagBySidOrSidHistory, e.Sid, e.Tag);
                    AddLookups(_tagBySidOrSidHistory, e.SidHistory, e.Tag);
                    AddLookup(_tagByEmail, e.Email, e.Tag);
                    AddLookups(_tagByEmail, e.EmailAliases, e.Tag);
                    if (e.PrimaryGroupToken.HasValue)
                        AddLookup(_tagByPrimaryGroupToken, e.PrimaryGroupToken.Value, e.Tag);
                }
            }
            catch
            {
                _hiAddOrUpdateErrors?.Increment();
            }
        }

        internal void WriteToFile(FileInfo fileInfo)
        {
            var sw = _hiWriteToFile?.Start();
            try
            {
                lock (SyncRoot)
                {
                    using (var f = new StreamWriter(fileInfo.FullName, false))
                    {
                        using (var jsonWriter = new JsonTextWriter(f))
                        {
                            var ser = new JsonSerializer
                            {
                                NullValueHandling = NullValueHandling.Ignore,
                                Formatting = Formatting.Indented,
                                ContractResolver = new ShouldSerializeContractResolver()
                            };
                            ser.Serialize(jsonWriter, _objByTag);
                            jsonWriter.Flush();
                        }
                    }
                    Console.WriteLine($"Range Attributes Seen: {string.Join(",", Entry._seenRangeAttributes)}");
                    Console.WriteLine("Dumping to SQL started");
                    var topLevelAttr = 
                        "Tag I" + 
                        ",Dn N" +
                        ",Class N" +
                        ",WhenCreated D" +
                        ",UserPrincipalName N" +
                        ",ServicePrincipalName N" +
                        ",ObjectGuid G" +
                        ",Sid N" +
                        ",SidHistory N" +
                        ",SamAccountName N" +
                        ",DomainFlatName N" +
                        ",SamAccountType N" +
                        ",UserAccountControl N" +
                        ",UserWorkstations N" +
                        ",GroupType N" +
                        ",PasswordLastSet D" +
                        ",LastLogonTimeStamp D" +
                        ",LogonCount I" +
                        ",AccountExpires D" +
                        ",AllowedToDelegateTo N" +
                        ",Telephone N" +
                        ",Fax N" +
                        ",Mobile N" +
                        ",Email N" +
                        ",EmailAliases N" +
                        ",TargetEmail N" +
                        ",MailboxGuid G" +
                        ",HideFromAddressBook T" +
                        ",SipAddress N" +
                        ",Country N" +
                        ",City N" +
                        ",ProvinceState N" +
                        ",StreetAddress N" +
                        ",PostalCode N" +
                        ",Company N" +
                        ",Department N" +
                        ",Description N" +
                        ",Office N" +
                        ",DisplayName N" +
                        ",Title N" +
                        ",PersonalTitle N" +
                        ",FirstName N" +
                        ",LastName N" +
                        ",Name N" +
                        ",Photo B" +
                        ",EmployeeType N" +
                        ",EmployeeId N" +
                        ",ManagerDeferredDn N" +
                        ",ManagerTag I" +
                        ",ManagesTags N" +
                        ",DirectMembersDeferredDn N" +
                        ",DirectMembersTags N" +
                        ",DirectMemberOfsTags N" +
                        ",DeferedCount I" +
                        ",PrimaryGroupId I" +
                        ",PrimaryGroupToken I" +
                        ",Version L";
                    var tl = new Dictionary<string, string> {
                        {"N","NVARCHAR(MAX)"}, {"I", "INT"}, {"D", "DATETIME2"}, {"G", "UNIQUEIDENTIFIER"}, {"L", "BIGINT"}, {"T", "BIT"}, {"B", "VARBINARY(MAX)" }
                    };
                    var schema = string.Join(",\r\n", topLevelAttr.Split(',').Select(a => a.Trim().Split(' ')).Select(nt => $"[{nt[0]}] {tl[nt[1]]} '$.\"{nt[0]}\"'")
                        .Concat(Entry.KnownBinaryAttributes.Select(a => $"[{a}] VARBINARY(MAX) '$.OtherAttributesBinary.\"{a}\"'"))
                        .Concat(Entry.KnownTextAttributes.Select(a => $"[{a}] NVARCHAR(MAX) '$.OtherAttributesText.\"{a}\"'")));

                    //Console.WriteLine(schema);
                    using (SqlConnection con = new SqlConnection(Properties.Settings.Default.Db))
                    {
                        con.Open();
                        using (SqlCommand cmd = new SqlCommand("dbo.LoadAdJson", con))
                        {
                            cmd.CommandType = System.Data.CommandType.StoredProcedure;
                            cmd.CommandTimeout = 600;
                            using (StreamReader file = fileInfo.OpenText())
                            {
                                cmd.Parameters.Add("@adJson", System.Data.SqlDbType.NVarChar, -1).Value = file;
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                    Console.WriteLine("Dumping to SQL complete");
                }
                sw?.Success();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                sw?.Failure(ex);
            }
        }

        private void LoadFromFile(FileInfo fileInfo, out List<Entry> objByTag)
        {
            var sw = _hiLoadFromFile?.Start();
            try
            {
                lock (SyncRoot)
                {
                    using (var f = new StreamReader(fileInfo.FullName))
                    using (var jsonReader = new JsonTextReader(f))
                    {
                        var ser = new JsonSerializer()
                        {
                            NullValueHandling = NullValueHandling.Ignore,
                            Formatting = Formatting.Indented,
                            ContractResolver = new ShouldSerializeContractResolver()
                        };
                        objByTag = ser.Deserialize<List<Entry>>(jsonReader);
                        var tagCount = objByTag?.Count ?? 0;
                        for (var i = 0; i < tagCount; i++)
                            if (objByTag?[i] != null)
                                if (string.IsNullOrEmpty(objByTag[i].Dn))
                                    objByTag[i] = null;
                                else objByTag[i].FixupFromLoad(i, tagCount);
                        BuildLookups();
                    }
                }
                sw?.Success();
            }
            catch (Exception ex)
            {
                sw?.Failure(ex);
                objByTag = new List<Entry>();
            }

        }

        public AdStore(AdSync adSync, string domain, string domainFlatName, string baseDn, bool loadAllAttributes, IEnumerable<string> otherAttributes, FileInfo cacheFileInfo, StreamWriter cacheLog, IMetricCollection metricCollection = null)
        {
            SyncRoot = new object();
            AdSync = adSync;
            CacheFileInfo = cacheFileInfo;
            CacheLog = cacheLog;
            Domain = domain;
            DomainFlatName = domainFlatName ?? string.Empty;
            BaseDn = baseDn;
            _hiAddOrUpdate = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdate");
            _hiUpdateNew = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateNotifyNew");
            _hiUpdateChange = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateNotifyChange");
            _hiUpdateNoChange = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateNotifyNoChange");
            _hiLoadNew = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateLoadNew");
            _hiLoadChange = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateChange");
            _hiLoadNoChange = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateNoChange");
            _hiAddOrUpdateErrors = metricCollection?.CreateCounter($"AdSync.{Domain}_AddOrUpdateErrors");
            _hiDeleteTag = metricCollection?.CreateCounter($"AdSync.{Domain}_DeleteTag");
            _hiDeleteTagErrors = metricCollection?.CreateCounter($"AdSync.{Domain}_DeleteTagErrors");

            _hiLoadFromFile = metricCollection?.CreateTimer($"AdSync.{Domain}_LoadFromFile");
            _hiWriteToFile = metricCollection?.CreateTimer($"AdSync.{Domain}_WriteToFile"); 

            _tagByDn = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _tagByGuid = new ConcurrentDictionary<Guid, int>();
            _tagBySidOrSidHistory = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _tagByForeignSecurityPrincipalSid = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _tagBySamAccountName = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _tagByUpn = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _tagByEmail = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _primaryGroupMemberTags = new ConcurrentDictionary<int, HashSet<int>>();
            _tagByPrimaryGroupToken = new ConcurrentDictionary<int, int>();
            LoadAllAttributes = loadAllAttributes;
            OtherAttributes = loadAllAttributes ? Enumerable.Empty<string>() : otherAttributes?.ToArray() ?? Enumerable.Empty<string>();
            AllAttributes = LoadAllAttributes ? new string[] { "*" } : new HashSet<string>(Entry.StandardAttributes).Union(OtherAttributes, StringComparer.OrdinalIgnoreCase).ToArray();

            if (CacheFileInfo.Exists)
                LoadFromFile(CacheFileInfo, out _objByTag);
            else
                _objByTag = new List<Entry>();
            if (string.IsNullOrEmpty(DomainFlatName) && _objByTag.Any(e => !string.IsNullOrEmpty(e.DomainFlatName)))
                DomainFlatName = _objByTag.FirstOrDefault(e => !string.IsNullOrEmpty(e.DomainFlatName))?.DomainFlatName ?? string.Empty;
            InitialLoadComplete = false;
        }
    }
}
