using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AdSync;
using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.AccountManagement;
using System.Text.RegularExpressions;
using System.IO;
using AdSyncTest.Properties;

namespace AdSyncTest
{
    class Program
    {
        static string Lookup(string domain, string value)
        {
            var pc = new PrincipalContext(ContextType.Domain, domain);
            var up = UserPrincipal.FindByIdentity(pc, value);
            return $"Guid:\t{up?.Guid}\r\nSamAccount:\t{up?.SamAccountName}\r\nSid:\t{up?.Sid}";
        }
        public static string EmailValidationPattern = @"^(?=.{5,256})[a-zA-Z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-zA-Z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?$";
        public static bool IsValidEmailAddress(string emailAddress)
        {
            return (emailAddress != null && Regex.Match(emailAddress, EmailValidationPattern,
                                     RegexOptions.Compiled | RegexOptions.IgnoreCase).Success);
        }
        public static string EmailDomain(string emailAddress)
        {
            return IsValidEmailAddress(emailAddress) ? emailAddress.Substring(emailAddress.IndexOf('@') + 1) : null;
        }

        public static StreamWriter GetTempFile(string domainName, string tempFileName)
        {
            var identityAccount = System.Security.Principal.WindowsIdentity.GetCurrent();
            var identityName = identityAccount?.Name ?? "UnknownAccount";
            var cacheFileInfo = new FileInfo(System.IO.Path.GetTempPath() + identityName.Replace('\\', '_') + "." + domainName + "." + tempFileName);
            try
            {
                if (cacheFileInfo.Exists)
                    cacheFileInfo.Delete();
                return cacheFileInfo.CreateText();
            }

            catch
            {
                return cacheFileInfo.CreateText();
            }
        }
        static void Main(string[] args)
        {
            Console.WriteLine($"Started: {DateTime.Now}");
            //var ex = new DynamicExpression("A + B", ExpressionLanguage.Csharp);
            //var ctx = new ExpressionContext();
            //var bex = ex.Bind(ctx);



            ////Trusts for current domain
            //Domain currentDomain = Domain.GetCurrentDomain();
            ////Console.WriteLine(JsonConvert.SerializeObject(currentDomain));
            //var domainTrusts = currentDomain.GetAllTrustRelationships().Cast<TrustRelationshipInformation>().Where(t => t.TrustDirection == TrustDirection.Bidirectional).ToArray();

            ////Trusts for current forest
            //Forest currentForest = Forest.GetCurrentForest();
            //var forestTrusts = currentForest.GetAllTrustRelationships();

            //var lanSync = new AdSync.AdSync("lan.local", null, null);
            var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN", EnvironmentVariableTarget.Process);
            var goaSync = new AdSync.AdSync(domain, null, true, TimeSpan.FromMinutes(Settings.Default.UpdateFileIntervalMinutes));
            //, new string[] {
            //"albertaGenericAttribute5", // employee id (should match employeeID attribute)
            //"albertaGenericAttribute24", // "[P]erson, [N]on-Person"
            //"extensionAttribute6", // location (eg B9125A - A5342)
            //"msExchExtensionAttribute22", // job classification code (eg "M42Z1")
            //"msExchExtensionAttribute23", // building code (eg "B9125A")
            //"msExchExtensionAttribute24", // building office location code (eg "A5342")
            //"msExchExtensionAttribute20" // building office location text (eg "Commerce Place, Third Floor 10155 - 102 Street Edmonton, Alberta, Canada T5J 4G8")
            //}

            //Task.WaitAll(lanSync.BulkLoadTask, goaSync.BulkLoadTask);
            Task.WaitAll(goaSync.BulkLoadTask);

            Console.WriteLine("BulkLoadCompleted");



            var AccountsByEmailDomainAndEmployeeType = new Dictionary<string, (int count, string names)>();
            var dumpAccounts = GetTempFile(goaSync.DomainFlatName, "users.tsv");
            dumpAccounts.WriteLine("EmailDomain\tSamAccountName\tEmployeeType\tDepartment\tDescription");
            foreach (var e in goaSync.Store.Objects)
            {
                if (e.IsGroup || ((e.UserAccountControl & Types.UserAccountControlFlags.AccountDisabled) != 0) || ((e.SamAccountType & Types.SamAccountTypeEnum.UserAccount) == 0))
                    continue;
                var targetEmailDomain = EmailDomain(e.TargetEmail);
                var reportDomain = !string.IsNullOrEmpty(targetEmailDomain) ? $"<T>{targetEmailDomain}" : EmailDomain(e.Email);
                if (string.IsNullOrEmpty(reportDomain))
                    reportDomain = "<none>";
                var emailDomainAndEmployeeType = $"{reportDomain} {e.EmployeeType}";
                AccountsByEmailDomainAndEmployeeType.TryGetValue(emailDomainAndEmployeeType, out var currentCount);
                AccountsByEmailDomainAndEmployeeType[emailDomainAndEmployeeType] = (currentCount.count + 1, $"{((currentCount.count < 10) ? (currentCount.names + ',' + e.SamAccountName) : currentCount.names)}");
                dumpAccounts.WriteLine($"{reportDomain}\t{e.SamAccountName}\t{e.EmployeeType}\t{e.Department}\t{e.Title}\t{e.Description?.Replace("\r"," ")?.Replace("\n"," ")}");
            }
            dumpAccounts.Close();
            foreach (var kv in AccountsByEmailDomainAndEmployeeType.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"{kv.Key},{kv.Value.count}");
            }

            //var sidIntersect = lanSync.Store.AllSids().Intersect(goaSync.Store.AllSids());
            //foreach (var s in sidIntersect)
            //{
            //    var lanObj = lanSync.Store.ObjectBySid(s);
            //    var goaObj = goaSync.Store.ObjectBySid(s);
            //    if (lanObj.Class == "top.foreignSecurityPrincipal" || goaObj.Class == "top.foreignSecurityPrincipal" || string.IsNullOrEmpty(lanObj.SamAccountName) || string.IsNullOrEmpty(goaObj.SamAccountName) || string.Equals(lanObj.Department, goaObj.Department))
            //        continue;
            //    Console.WriteLine($"{s}\t{lanObj.SamAccountName} = {goaObj.SamAccountName}");
            //}

            //using (var connection = CreateConnection(ServerName))
            //{
            //    var cts = new CancellationTokenSource();
            //    Task.Run(() =>
            //    {
            //        if (Console.ReadKey(true).KeyChar == 'c')
            //            cts.Cancel();
            //    }, cts.Token);

            //    var bulkLoadResults = new BlockingCollection<SearchResultEntry>(new ConcurrentQueue<SearchResultEntry>());
            //    _bulkLoadProcessingPipeline = new PipelineFilter<SearchResultEntry, SearchResultEntry>(bulkLoadResults,
            //        ProcessResponse, ProcessComplete, cts.Token, "BulkLoad");
            //    var bulkLoader = new AsyncSearcher(connection,
            //        BaseDn,
            //        "(objectguid=*)",
            //        AdStore.Attributes.ToArray(),
            //        1000,
            //        bulkLoadResults, cts.Token);

            //    var changeNotifyResults = new BlockingCollection<SearchResultEntry>(new ConcurrentQueue<SearchResultEntry>());
            //    _changeNotifyProcessingPipeline = new PipelineFilter<SearchResultEntry, SearchResultEntry>(changeNotifyResults,
            //        ProcessResponse, cts.Token, "ChangeNotify");
            //    var changeNotify = new AdChangeNotifier(connection,
            //        BaseDn,
            //        "(objectguid=*)",
            //        AdStore.Attributes.ToArray(),
            //        changeNotifyResults, cts.Token);

            //    Parallel.Invoke(_bulkLoadProcessingPipeline.Run, _changeNotifyProcessingPipeline.Run);


            //    Console.WriteLine($"Done {_objectCount}\t{_objectsSkipped}");
            //    Console.WriteLine($"Deferred Count: {Store.DeferredCount()}");
            //    Console.WriteLine($"Objects Skipped: {_objectsSkipped}");
            //    foreach (var t in MissingAttributes)
            //        Console.WriteLine(t.Key + '\t' + t.Value.ToString());
            //    Console.ReadLine();
            //    Store.ResolveDeferred();
            //    Console.WriteLine($"Deferred Count: {Store.DeferredCount()}");
            //    Console.WriteLine("ObjectsWithDeferrals");
            //    foreach (var i in Store.ObjectsWithDeferrals())
            //        Console.WriteLine(i);
            //    Console.ReadLine();
            //    Console.WriteLine("DeferredObjects");
            //    foreach (var i in Store.DeferredObjects())
            //        Console.WriteLine(i);

            //    Console.WriteLine("Press any key.");
            //    Console.ReadKey(true);
            //}
            //DumpGroup(Store, "Domain Admins");
            //DumpGroup(Store, "Account Operators");
            ////DumpGroup(Store, "ADMINUNITS-GPO-ALL_DESKTOP-LOCAL_ADMIN-F");
            Console.WriteLine();
            Console.WriteLine($"Finished at {DateTime.Now.ToLongTimeString()}");
            Console.WriteLine("Enter \"x\" to exit.");
            do
            {
                if (string.Equals(Console.ReadLine(), "x")) break;

                //foreach (var p in lanSync.Store.Manages(lanSync.Store.GetBySamAccountName("Mike.Emery")))
                //    Console.WriteLine($"Mike manages: {p.SamAccountName}");
                //Console.WriteLine($"goa Groups: {goaSync.Store.ObjectsSnapshot.Count(obj => obj.IsGroup)}");
                //foreach (var recursiveGroup in goaSync.Store.ObjectsSnapshot.Where(obj => goaSync.Store.HasMember(obj, obj)))
                //    Console.WriteLine($"goa Group in self: {recursiveGroup.Dn}");
                //Console.WriteLine($"lan Groups: {lanSync.Store.ObjectsSnapshot.Count(obj => obj.IsGroup)}");
                //foreach (var recursiveGroup in lanSync.Store.ObjectsSnapshot.Where(obj => lanSync.Store.HasMember(obj, obj)))
                //Console.WriteLine($"lan Group in self: {recursiveGroup.Dn}");
                //foreach (var line in lanSync.Store.VerifyBacklinks())
                //    Console.WriteLine(line);
                foreach (var line in goaSync.Store.VerifyBacklinks())
                    Console.WriteLine(line);
                //Console.WriteLine(
                //    $"goa has {goaSync.Store.ObjectsSnapshot.Count(obj => obj.SamAccountType == Types.SamAccountTypeEnum.UserAccount && (obj.Email?.EndsWith("@gov.ab.ca", StringComparison.OrdinalIgnoreCase) ?? false))} users");
                //Console.WriteLine(
                //    $"goa has {goaSync.Store.ObjectsSnapshot.Count(obj => obj.SamAccountType == Types.SamAccountTypeEnum.UserAccount && (obj.Email?.EndsWith("@gov.ab.ca", StringComparison.OrdinalIgnoreCase) ?? false) && !string.IsNullOrWhiteSpace(obj.Title))} user with Titles");
                //Console.WriteLine(
                //    $"lan has {lanSync.Store.ObjectsSnapshot.Count(obj => obj.SamAccountType == Types.SamAccountTypeEnum.UserAccount && (obj.Email?.EndsWith("@gov.ab.ca", StringComparison.OrdinalIgnoreCase) ?? false))} users");
                //Console.WriteLine(
                //    $"lan has {lanSync.Store.ObjectsSnapshot.Count(obj => obj.SamAccountType == Types.SamAccountTypeEnum.UserAccount && (obj.Email?.EndsWith("@gov.ab.ca", StringComparison.OrdinalIgnoreCase) ?? false) && !string.IsNullOrWhiteSpace(obj.Title))} user with Titles");
                //Console.WriteLine($"{goaSync.Store.ObjectsSnapshot.Count(obj => (((obj.UserAccountControl ?? 0) & Types.UserAccountControlFlags.AccountDisabled) == 0) && obj.SamAccountType == Types.SamAccountTypeEnum.UserAccount && DateTime.UtcNow.Subtract(obj.LastLogonTimeStamp ?? DateTime.MinValue).TotalDays > 365)} GOA accounts not logged on for over a year.");
                //Console.WriteLine($"{lanSync.Store.ObjectsSnapshot.Count(obj => (((obj.UserAccountControl ?? 0) & Types.UserAccountControlFlags.AccountDisabled) == 0) && obj.SamAccountType == Types.SamAccountTypeEnum.UserAccount && DateTime.UtcNow.Subtract(obj.LastLogonTimeStamp ?? DateTime.MinValue).TotalDays > 365)} LAN accounts not logged on for over a year.");
                //foreach (var obj in lanSync.Store.ObjectsSnapshot.Where(obj => (((obj.UserAccountControl ?? 0) & AdStore.UserAccountControlFlags.AccountDisabled) == 0) && obj.SamAccountType == AdStore.SamAccountTypeEnum.UserAccount && DateTime.UtcNow.Subtract(obj.LastLogonTimeStamp ?? DateTime.MinValue).TotalDays > 365).OrderByDescending(obj => DateTime.UtcNow.Subtract(obj.LastLogonTimeStamp ?? DateTime.MinValue).TotalDays))


                //foreach (var obj in goaSync.Store.ObjectsSnapshot.Where(obj => obj.SamAccountType == AdStore.SamAccountTypeEnum.UserAccount &&  (obj.Email?.EndsWith("@gov.ab.ca", StringComparison.OrdinalIgnoreCase) ?? false)))
                //    Console.WriteLine(obj.SamAccountName);
            } while (true);
        }

    }
}
