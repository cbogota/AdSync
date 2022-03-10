using System;
using System.Threading;
using System.Threading.Tasks;

namespace BackgroundTasks
{
    public class BackgroundInitializerWithProgressiveRetry
    {
        public bool IsCompleted { get; private set; }
        public readonly TaskCompletionSource<bool> InitializationCompleted;
        private readonly ManualResetEvent _retryTimeoutEvent;

        private readonly Action _action;
        //private readonly IHealthIndicator _healthIndicator;
        private readonly double _minimumRetrySeconds;
        private readonly double _maximumRetrySeconds;

        //private IHealthIndicatorStopwatch _stopwatch;
        private double? _currentRetrySeconds;

        private void IncreaseRetryDelay()
        {
            // first set it to Minimum, then double up to Maximum
            _currentRetrySeconds = _currentRetrySeconds.HasValue
                                            ? Math.Min(_currentRetrySeconds.Value*2.0, _maximumRetrySeconds)
                                            : _minimumRetrySeconds;
        }

        private void QueueInvoke()
        {
            if (_currentRetrySeconds.HasValue)
                ThreadPool.RegisterWaitForSingleObject(_retryTimeoutEvent, (state, timedOut) => Invoke(), null, TimeSpan.FromSeconds(_currentRetrySeconds.Value), true);
            else
                ThreadPool.QueueUserWorkItem((state) => Invoke());
        }


        private void Invoke()
        {
            try
            {
                //_stopwatch = _healthIndicator.Start();
                _action();
                //_stopwatch.Success();
                IsCompleted = true;
                InitializationCompleted.SetResult(true);
            }
            catch (ThreadAbortException)
            {
                // we are probably being shut down 
                //_stopwatch.Failure(ex);
                throw;
            }
            catch (Exception)
            {
                // failure, so double the retry interval up to the maximum
                //_stopwatch.Failure(ex);
                IncreaseRetryDelay();
                QueueInvoke();
            }
            
        }

        public BackgroundInitializerWithProgressiveRetry(Action action, //IHealthIndicator healthIndicator,
                                                         double minimumRetrySeconds, double maximumRetrySeconds)
        {
            _action = action;
            //_healthIndicator = healthIndicator;
            _minimumRetrySeconds = minimumRetrySeconds;
            _maximumRetrySeconds = maximumRetrySeconds;
            _currentRetrySeconds = null;
            _retryTimeoutEvent = new ManualResetEvent(false);
            InitializationCompleted = new TaskCompletionSource<bool>();
            QueueInvoke();
        }
    }
}
