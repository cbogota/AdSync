using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Threading;
using Health.Abstractions;

namespace AdSync
{
    public class AsyncSearcher
    {
        private readonly LdapConnection _connection;
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

        public AsyncSearcher(LdapConnection connection,
            string baseDN,
            string filter,
            string[] attribs,
            int pageSize,
            BlockingCollection<SearchResultEntry> outputBuffer,
            IMetricTimer hiConstruct,
            IMetricTimer hiReadResult,
            IMetricTimer hiBeginRequest)
        {
            var swc = hiConstruct?.Start();
            try
            {
                _connection = connection;
                _connection.AutoBind = true; //will bind on first search
                _responseBuffer = outputBuffer;
                _cancellation = new CancellationTokenSource();
                var request = new SearchRequest(
                    baseDN,
                    filter,
                    SearchScope.Subtree,
                    attribs
                    );

                var prc = new PageResultRequestControl(pageSize);

                //add the paging control
                request.Controls.Add(prc);

                AsyncCallback rc = null;

                rc = readResult =>
                {
                    var swr = hiReadResult?.Start();
                    try
                    {
                        var response = (SearchResponse) _connection.EndSendRequest(readResult);
                        if (!_cancellation.IsCancellationRequested)
                        {
                            var cookie = response.Controls
                                .Where(c => c is PageResultResponseControl)
                                .Select(s => ((PageResultResponseControl) s).Cookie)
                                .Single();
                            foreach (var entry in response.Entries.Cast<SearchResultEntry>())
                                _responseBuffer.Add(entry, _cancellation.Token);
                            if (cookie != null && cookie.Length != 0 && !_cancellation.Token.IsCancellationRequested)
                            {
                                prc.Cookie = cookie;
                                _connection.BeginSendRequest(request, PartialResultProcessing.NoPartialResultSupport, rc,
                                    null);
                            }
                            else
                                _responseBuffer.CompleteAdding();
                        }
                        swr?.Success();
                    }
                    catch (Exception ex)
                    {
                        ReadResultException = ex;
                        swr?.Failure(ex);
                    }
                    finally
                    {
                        try
                        {
                            readResult?.AsyncWaitHandle?.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    }
                };

                //kick off async
                var swb = hiBeginRequest?.Start();
                try
                {
                    _connection.BeginSendRequest(
                        request,
                        PartialResultProcessing.NoPartialResultSupport,
                        rc,
                        null
                        );
                    swb?.Success();
                }
                catch (Exception ex)
                {
                    InitializeSearchException = ex;
                    swc?.Failure(ex);
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