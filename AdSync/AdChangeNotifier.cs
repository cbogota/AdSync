using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Health.Abstractions;

namespace AdSync
{
    public class AdChangeNotifier
    {
        private readonly LdapConnection _connection;
        private readonly IAsyncResult _request;
        private BlockingCollection<SearchResultEntry> _responseBuffer;
        private readonly CancellationTokenSource _cancellation;
        public Exception ConstructorException { get; private set; }
        public Exception InitializeSearchException { get; private set; }
        public Exception ReadResultException { get; private set; }

        public void Terminate()
        {
            if (!_cancellation.IsCancellationRequested)
                _cancellation.Cancel();
            _responseBuffer = null;
        }

        public AdChangeNotifier(LdapConnection connection,
            string baseDN,
            string filter,
            string[] attribs,
            BlockingCollection<SearchResultEntry> outputBuffer,
            IMetricTimer hiConstruct,
            IMetricTimer hiReadResult,
            IMetricTimer hiBeginRequest
        )
        {
            var swc = hiConstruct?.Start();
            try
            {
                _connection = connection;
                _connection.AutoBind = true; //will bind on first search
                _responseBuffer = outputBuffer;
                _cancellation = new CancellationTokenSource();

                var request = new SearchRequest(
                    baseDN, //root the search here
                    filter,
                    SearchScope.Subtree,
                    attribs
                    );

                AsyncCallback rc = null;

                rc = readResult =>
                {
                    var swr = hiReadResult?.Start();
                    try
                    {
                        var prc = _connection.GetPartialResults(readResult);
                        if (!_cancellation.IsCancellationRequested) 
                            foreach (var entry in prc.Cast<SearchResultEntry>())
                                _responseBuffer.Add(entry, _cancellation.Token);
                        swr?.Success();
                    }
                    catch (Exception ex)
                    {
                        ReadResultException = ex;
                        swr?.Failure(ex);
                        _responseBuffer.CompleteAdding();
                    }
                };

                var dnc = new DirectoryNotificationControl();
                request.Controls.Add(dnc);

                //kick off async
                var swb = hiBeginRequest?.Start();
                try
                {
                    _request = _connection.BeginSendRequest(
                        request,
                        TimeSpan.FromDays(2), //set timeout to two days...
                        PartialResultProcessing.ReturnPartialResultsAndNotifyCallback,
                        rc,
                        request);
                    swb?.Success();
                }
                catch (Exception ex)
                {
                    InitializeSearchException = ex;
                    swb?.Failure(ex);
                }
                swc?.Success();
            }
            catch (Exception ex)
            {
                ConstructorException = ex;
                swc?.Failure(ex);
            }
        }
    }
}

