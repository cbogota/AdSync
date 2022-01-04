using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Collections;
using BackgroundTasks;
using IpAddressingUtility;
using Health.Abstractions;
using SearchOption = System.DirectoryServices.Protocols.SearchOption;
using SearchScope = System.DirectoryServices.Protocols.SearchScope;

namespace AdSync
{
    public readonly struct AdSubnet : IValueSnapshot<IpNetworkAddress>
    {
        public readonly IpNetworkAddress Subnet;        
        public string Value { get; }
        public bool IsDeleted => false;

        public IpNetworkAddress Key => Subnet;

        public long Version => 0;

        public AdSubnet(IpNetworkAddress subnet, string str)
        {
            Subnet = subnet;
            Value = str;
        }
    }
    public class AdSync
    {
        public PipelineFilter<SearchResultEntry, SearchResultEntry> BulkLoadProcessingPipeline;
        private AsyncSearcher _bulkLoader;
        public PipelineFilter<SearchResultEntry, SearchResultEntry> ChangeNotifyProcessingPipeline;
        private AdChangeNotifier _changeNotifier;
        public string ServerName { get; private set; }
        /// <summary>
        /// true if the dc server is switched during runtime (due to exceptions)
        /// </summary>
        public bool ServerSwitch { get; internal set; }
        public string DomainName { get; }
        public string DomainFlatName { get; }

        public readonly IpNetworkSearcher<AdSubnet> Subnets;
        public readonly Dictionary<string, List<string>> SiteDomainControllers;
        public readonly Dictionary<string, string> SiteDescriptions;
        public string BaseDn { get; }
        public string IdentityName { get; }
        public FileInfo CacheFileInfo { get; }
        public FileInfo CacheLogFileInfo { get; }
        public StreamWriter CacheLogFile { get; }
        public int EntriesProcessed { get; private set; } 

        public Task<bool> BulkLoadTask { get; private set; }
        public Task<bool> ChangeNotifyTask { get; private set; }

        public CancellationTokenSource Cancel { get; private set; }

        public readonly AdStore Store;
        
        public BackgroundRepeatingTask CacheSaverTask;
        public BackgroundRepeatingTask SyncWatchdog;

        public Exception ProcessResponseError { get; private set; }
        public Exception ProcessCompleteError { get; private set; }
        private readonly IMetricTimer _hiBootstrapConnection;
        private readonly IMetricTimer _hiProcessResponse;
        private readonly IMetricTimer _hiBulkLoadComplete;
        private readonly IMetricTimer _hiBulkLoad;
        private readonly IMetricTimer _hiSyncWatchdog;
        private IMetricTimerStopwatch _swBulkLoad;
        private readonly IMetricTimer _hiCacheSaverTask;

        private void ProcessResponse(PipelineFilter<SearchResultEntry, SearchResultEntry> pipeline, SearchResultEntry entry)
        {
            var sw = _hiProcessResponse?.Start();
            try
            {
                EntriesProcessed++;
                var isChangeNotified = pipeline == ChangeNotifyProcessingPipeline;
                Store.AddOrUpdate(entry, isChangeNotified);
                sw?.Success();
            }
            catch (Exception ex)
            {
                ProcessResponseError = ex;
                sw?.Failure(ex);
            }
        }

        public TimeSpan UpdateFileInterval { get; set; } = TimeSpan.FromMinutes(5);
        private void ProcessComplete(PipelineFilter<SearchResultEntry, SearchResultEntry> pipeline)
        {
            var sw = _hiBulkLoadComplete?.Start();
            try
            {
                lock (Store.SyncRoot)
                {
                    Store.ResolveAllDeferred();
                    Store.DeleteUndetected();
                    Store.InitialLoadComplete = true;
                    CacheLogFile.WriteLine($"{DateTimeOffset.Now} Bulk Load compelted");
                    CacheSaverTask?.Terminate();
                    CacheSaverTask = new BackgroundRepeatingTask(() => { Store.WriteToFile(CacheFileInfo); }, _hiCacheSaverTask, UpdateFileInterval.TotalSeconds, UpdateFileInterval.TotalSeconds);
                }
                CacheSaverTask.ActionCompletedManualResetEvent.Wait();
                CacheLogFile.WriteLine($"{DateTimeOffset.Now} Cache file updated");
                CacheLogFile.Close();
                sw?.Success();
                _swBulkLoad?.Success();
            }
            catch (Exception ex)
            {
                ProcessCompleteError = ex;
                sw?.Failure(ex);
                _swBulkLoad?.Failure(ex);
            }
        }

        public string GetDomainFlatName(LdapConnection connection, string domainName, string baseDn)
        {
            const string configurationNamingContextPropertyName = "configurationNamingContext";
            const string flatNamePropertyName = "nETBIOSName";
            connection.AutoBind = true;

            // Create a search request to locate the Configuration Naming Context for the forest.
            var request = new SearchRequest("", "objectClass=*", SearchScope.Base, configurationNamingContextPropertyName);

            // Execute the search and cast the response as a SearchResponse
            var response = (SearchResponse)connection.SendRequest(request);

            // Get the Configuration container DN for the forest.
            var configurationDn = Types.FromStringValue(response?.Entries[0], configurationNamingContextPropertyName);

            // Calculate the Claims Configuration DN based on the Configuration DN.
            string partitionsDn = $"CN=Partitions,{configurationDn}";

            // Create a new search request for Claim Types.
            request = new SearchRequest(partitionsDn, $"(&({flatNamePropertyName}=*)(dnsRoot={domainName}))", SearchScope.OneLevel, flatNamePropertyName);

            // Execute the search and cast the response as a SearchResponse
            response = (SearchResponse)connection.SendRequest(request);

            return Types.FromStringValue(response?.Entries[0], flatNamePropertyName);
        }

        public void GetSitesAndSubnets(LdapConnection connection, string domainName, string baseDn, out IpNetworkSearcher<AdSubnet> subnets, out Dictionary<string, List<string>> siteDomainControllers, out Dictionary<string, string> siteDescriptions)
        {
            subnets = new IpNetworkSearcher<AdSubnet>(100);
            siteDomainControllers = new Dictionary<string, List<string>>();
            siteDescriptions = new Dictionary<string, string>();

            const string configurationNamingContextPropertyName = "configurationNamingContext";
            connection.AutoBind = true;

            // Create a search request to locate the Configuration Naming Context for the forest.
            var request = new SearchRequest("", "objectClass=*", SearchScope.Base, configurationNamingContextPropertyName);

            // Execute the search and cast the response as a SearchResponse
            var response = (SearchResponse)connection.SendRequest(request);

            // Get the Configuration container DN for the forest.
            var configurationDn = Types.FromStringValue(response?.Entries[0], configurationNamingContextPropertyName);

            // Calculate the Claims Configuration DN based on the Configuration DN.
            string partitionsDn = $"CN=Sites,{configurationDn}";

            // Create a new search request for Claim Types.
            request = new SearchRequest(partitionsDn, "(|(objectClass=site)(objectClass=server)(objectClass=subnet))", SearchScope.Subtree, new string[] {"cn","dn","objectClass","location","serverReference","dNSHostName","siteObject"});

            // Execute the search and cast the response as a SearchResponse
            response = (SearchResponse)connection.SendRequest(request);

            if (response == null) return;

            foreach (SearchResultEntry entry in response.Entries)
            {
                var objectClass = Types.FromStringValues(entry, "objectClass", ";");
                var cn = Types.FromStringValue(entry, "cn");
                if (objectClass.Equals("top;subnet", StringComparison.OrdinalIgnoreCase))
                {
                    var siteObject = Types.FromStringValue(entry, "siteObject")?.Split(',').FirstOrDefault()?.Split('=').Skip(1).FirstOrDefault();
                    if (string.IsNullOrEmpty(siteObject)) continue;
                    var subnet = new IpNetworkAddress(cn);
                    subnets.AddOrReplace(new AdSubnet(subnet, siteObject));
                }
                else if (objectClass.Equals("top;server", StringComparison.OrdinalIgnoreCase))
                {
                    var dnsHostName = Types.FromStringValue(entry, "dNSHostName");
                    var serverReference = Types.FromStringValue(entry, "serverReference");
                    var serverContainer = serverReference?.Split(",".ToCharArray(), 2).Skip(1).FirstOrDefault();
                    if (!string.Equals(serverContainer, "OU=Domain Controllers," + BaseDn)) continue;
                    var siteName = entry?.DistinguishedName?.Split(',').Skip(2).FirstOrDefault()?.Split('=').Skip(1).FirstOrDefault();
                    if (string.IsNullOrEmpty(siteName) || string.IsNullOrEmpty(dnsHostName)) continue;
                    if (!siteDomainControllers.ContainsKey(siteName)) siteDomainControllers[siteName] = new List<string>();
                    siteDomainControllers[siteName].Add(dnsHostName);
                }
                else if (objectClass.Equals("top;site", StringComparison.OrdinalIgnoreCase))
                {
                    var location = Types.FromStringValue(entry, "location") ?? cn;
                    siteDescriptions[cn] = location;
                }
            }
        }

        private string LocateDc(string preferredServer)
        {
            // first check the preferred server (if provided)
            if (IsServerAvailable(preferredServer))
                return preferredServer;
            var rnd = new Random();
            // then try to use a DC from our Site (if we have Site information)...
            if (Subnets != null && SiteDomainControllers != null)
            {
                var site = Subnets?.SubnetSearch(IpNetworkAddress.LocalIpNetworkAddress);
                // if there are no sites or we are not in any Site, grab domain controllers from all sites
                if (site.Value.Data.Value == null || !SiteDomainControllers.TryGetValue(site.Value.Data.Value, out var serversBySite))
                    serversBySite = SiteDomainControllers?.SelectMany(dcList => dcList.Value).ToList();
                // no randomly try each potential server till we get a connection....
                if (serversBySite != null && serversBySite.Count > 0)
                    foreach (var dc in serversBySite.OrderBy(s => rnd.Next()))
                        if (IsServerAvailable(dc))
                            return dc;
            }
            // then try to use any DC that is available using DNS lookups on the domain name
            var serversByDns = System.Net.Dns.GetHostEntry(DomainName).AddressList;
            if (serversByDns != null && serversByDns.Length > 0)
                foreach (var dc in serversByDns.OrderBy(s => rnd.Next()).Select(s => s.ToString()))
                    if (IsServerAvailable(dc))
                        return dc;
            return null;
        }

        readonly IMetricTimer hiAsyncSearcherConstruct;
        readonly IMetricTimer hiAsyncSearcherReadResult;
        readonly IMetricTimer hiAsyncSearcherBeginRequest;
        readonly IMetricTimer hiAdChangeNotifierConstruct;
        readonly IMetricTimer hiAdChangeNotifierReadResult;
        readonly IMetricTimer hiAdChangeNotifierBeginRequest;

        public void InitiateSyncFromDc()
        {
            _swBulkLoad = _hiBulkLoad?.Start();
            Store.MarkAllAsDetecting();
            var connection = CreateConnection(ServerName);
            var bulkLoadResults = new BlockingCollection<SearchResultEntry>(new ConcurrentQueue<SearchResultEntry>());
            BulkLoadProcessingPipeline = new PipelineFilter<SearchResultEntry, SearchResultEntry>(bulkLoadResults,
                ProcessResponse, ProcessComplete, Cancel.Token, "BulkLoad");
            _bulkLoader = new AsyncSearcher(connection,
                BaseDn,
                "(objectguid=*)",
                Store.AllAttributes.ToArray(),
                1000,
                bulkLoadResults,
                hiAsyncSearcherConstruct,
                hiAsyncSearcherReadResult,
                hiAsyncSearcherBeginRequest);

            var changeNotifyResults = new BlockingCollection<SearchResultEntry>(new ConcurrentQueue<SearchResultEntry>());
            ChangeNotifyProcessingPipeline = new PipelineFilter<SearchResultEntry, SearchResultEntry>(changeNotifyResults,
                ProcessResponse, Cancel.Token, "ChangeNotify");
            _changeNotifier = new AdChangeNotifier(connection,
                BaseDn,
                "(objectguid=*)",
                Store.AllAttributes.ToArray(),
                changeNotifyResults,
                hiAdChangeNotifierConstruct,
                hiAdChangeNotifierReadResult,
                hiAdChangeNotifierBeginRequest);
            BulkLoadTask = new Task<bool>(() => BulkLoadProcessingPipeline.Run(), Cancel.Token);
            BulkLoadTask.Start();
            ChangeNotifyTask = new Task<bool>(() => ChangeNotifyProcessingPipeline.Run(), Cancel.Token);
            ChangeNotifyTask.Start();
        }

        public List<string> RetrieveRangedAttribute(SearchResultEntry e, string attribute)
        {
            Console.WriteLine($"Ranged: {attribute}, {e.DistinguishedName}");
            const string dnAttributeName = "distinguishedName";
            using (var root = new DirectoryEntry($"LDAP://{ServerName}/{e.DistinguishedName}", null, null, AuthenticationTypes.Secure))
            {
                var a = new string[] { dnAttributeName };
                var ds = new DirectorySearcher(root, "(objectClass=*)", a)
                {
                    SearchScope = System.DirectoryServices.SearchScope.Base,
                    AttributeScopeQuery = attribute,
                    PageSize = 1000
                };
                using (var src = ds.FindAll())
                {
                    var result = new List<string>(src.Count);
                    foreach (SearchResult sr in src)
                        if (sr.Properties.Contains(dnAttributeName))
                            result.Add(sr.Properties[dnAttributeName][0].ToString());
                    Console.WriteLine($"Retrieved {result.Count} values");
                    return result;    
                }
            }
        }
        private bool IsServerAvailable(string server)
        {
            if (string.IsNullOrWhiteSpace(server))
                return false;
            try
            {
                using (var bootstrapConnection = CreateConnection(server))
                    return (!string.IsNullOrEmpty(GetDomainFlatName(bootstrapConnection, DomainName, BaseDn)));
            }
            catch
            {
                return false;
            }
        }

        public AdSync(string domainName, string serverName, bool loadAllAttributes, TimeSpan _updateFileInterval, IEnumerable<string> attributes = null, FileInfo cacheFileInfo = null, IMetricCollection metricCollection = null)
        {
            UpdateFileInterval = _updateFileInterval;
            if (string.IsNullOrWhiteSpace(domainName)) throw new ArgumentOutOfRangeException(nameof(domainName), "domainName must be supplied");
            DomainName = domainName;
            BaseDn = string.Join(",", domainName.Split(".".ToCharArray(), StringSplitOptions.RemoveEmptyEntries).Select(p => "DC=" + p));
            _hiProcessResponse = metricCollection?.CreateTimer($"AdSync.{DomainName}_ProcessResponse");
            _hiBulkLoadComplete = metricCollection?.CreateTimer($"AdSync.{DomainName}_BulkLoadComplete");
            _hiBulkLoad = metricCollection?.CreateTimer($"AdSync.{DomainName}_BulkLoad");
            _hiCacheSaverTask = metricCollection?.CreateTimer($"AdSync.{DomainName}_CacheSaverTask");
            _hiSyncWatchdog = metricCollection?.CreateTimer($"AdSync.{DomainName}_SyncWatchdog");
            _hiBootstrapConnection = metricCollection?.CreateTimer($"AdSync.{DomainName}_BootstrapConnection");
            var identityAccount = System.Security.Principal.WindowsIdentity.GetCurrent();
            IdentityName = identityAccount?.Name ?? "UnknownAccount";
            CacheFileInfo = cacheFileInfo ?? new FileInfo(System.IO.Path.GetTempPath() + IdentityName.Replace('\\', '_') + "." + domainName + ".cache");
            CacheLogFileInfo = new FileInfo(CacheFileInfo.FullName + ".log");
            try
            {
                if (CacheLogFileInfo.Exists)
                {
                    var lastServer = "";
                    using (var lfs = CacheLogFileInfo.OpenRead())
                        using (var tr = new StreamReader(lfs))
                            lastServer = tr.ReadLine();
                    if (lastServer?.Length > 0)
                        serverName = lastServer;                    
                    CacheLogFileInfo.Delete();
                }
                CacheLogFile = CacheLogFileInfo.CreateText();
            }
            catch 
            {
                CacheLogFileInfo = new FileInfo(CacheLogFileInfo.FullName + DateTime.Now.Ticks);
                CacheLogFile = CacheLogFileInfo.CreateText();
            }
            var swBootstrap = _hiBootstrapConnection?.Start();
            try
            {
                using (var bootstrapConnection = CreateConnection(serverName ?? domainName))
                {
                    DomainFlatName = GetDomainFlatName(bootstrapConnection, domainName, BaseDn);
                    GetSitesAndSubnets(bootstrapConnection, domainName, BaseDn, out Subnets, out SiteDomainControllers, out SiteDescriptions);
                }
                swBootstrap?.Success();
            }
            catch (Exception e)
            {
                swBootstrap?.Failure(e);
            }
            ServerName = LocateDc(serverName);
            CacheLogFile.WriteLine(ServerName);
            CacheLogFile.WriteLine($"{DateTimeOffset.Now} Syncing domain {domainName} from domain controller {ServerName}");
            Store = new AdStore(this, domainName, DomainFlatName, BaseDn, loadAllAttributes, attributes, CacheFileInfo, CacheLogFile);
            if (string.IsNullOrEmpty(DomainFlatName) && !string.IsNullOrEmpty(Store.DomainFlatName))
                DomainFlatName = Store.DomainFlatName;
            Cancel = new CancellationTokenSource();
            //Task.Run(() =>
            //{
            //    if (Console.ReadKey(true).KeyChar == 'c')
            //        cts.Cancel();
            //}, cts.Token);
            hiAsyncSearcherConstruct = metricCollection?.CreateTimer($"AdSync.{DomainName}_BulkLoadInit");
            hiAsyncSearcherReadResult = metricCollection?.CreateTimer($"AdSync.{DomainName}_BulkLoadRead");
            hiAsyncSearcherBeginRequest = metricCollection?.CreateTimer($"AdSync.{DomainName}_BulkLoadBegin");
            hiAdChangeNotifierConstruct = metricCollection?.CreateTimer($"AdSync.{DomainName}_ChangeNotifyInit");
            hiAdChangeNotifierReadResult = metricCollection?.CreateTimer($"AdSync.{DomainName}_ChangeNotifyRead");
            hiAdChangeNotifierBeginRequest = metricCollection?.CreateTimer($"AdSync.{DomainName}_ChangeNotifyBegin");

            InitiateSyncFromDc();
            SyncWatchdog = new BackgroundRepeatingTask(() =>
            {
                if (_bulkLoader?.InitializeSearchException != null ||
                    _bulkLoader?.ReadResultException != null ||
                    _changeNotifier?.InitializeSearchException != null ||
                    _changeNotifier?.ReadResultException != null)
                {
                    _bulkLoader?.Terminate();
                    _changeNotifier?.Terminate();
                    var newServerName = LocateDc(serverName);
                    ServerSwitch = ServerSwitch || !newServerName.Equals(ServerName, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"Source Server switched to {newServerName} ({ServerSwitch})");
                    InitiateSyncFromDc();
                }
            }, _hiSyncWatchdog, 5 * 60, 5 * 60);
        }

        private static LdapConnection CreateConnection(string server)
        {
            var connect = new LdapConnection(
                new LdapDirectoryIdentifier(server),
                null,
                AuthType.Negotiate
                );

            connect.SessionOptions.ProtocolVersion = 3;
            connect.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

            connect.SessionOptions.Sealing = true;
            connect.SessionOptions.Signing = true;

            return connect;
        }
    }
}
