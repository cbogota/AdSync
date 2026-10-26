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
            if (args.Length > 0)
            {
                domain = args[0];
            }
            var domSync = new AdSync.AdSync(domain, null, true, TimeSpan.FromMinutes(Settings.Default.UpdateFileIntervalMinutes));


            Task.WaitAll(domSync.BulkLoadTask);

            Console.WriteLine("BulkLoadCompleted");



            var AccountsByEmailDomainAndEmployeeType = new Dictionary<string, (int count, string names)>();
            var dumpAccounts = GetTempFile(domSync.DomainFlatName, "users.tsv");
            dumpAccounts.WriteLine("EmailDomain\tSamAccountName\tEmployeeType\tDepartment\tDescription");
            foreach (var e in domSync.Store.Objects)
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
            Console.WriteLine();
            Console.WriteLine($"Finished at {DateTime.Now.ToLongTimeString()}");
            Console.WriteLine("Enter \"x\" to exit.");
            do
            {
                if (string.Equals(Console.ReadLine(), "x")) break;

                foreach (var line in domSync.Store.VerifyBacklinks())
                    Console.WriteLine(line);
            } while (true);
        }

    }
}
