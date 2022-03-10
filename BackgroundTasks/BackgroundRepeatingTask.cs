using System;
using System.Threading;
using Health.Abstractions;

namespace BackgroundTasks
{
    /// <summary>
    /// The BackgroundRepeatingTask enabled a task action to be executed repeatedly in the background on a regular interval.
    /// The provided internal (minimumDelaySeconds) sets the frequenty that the task is action repeated under normal conditions.
    /// The implementation measures the repeating time from the start of task action execution (eg if you set minimumDelaySeconds 
    /// to 60 seconds and the task action takes 10 seconds, the second execution of the task action will start 50 seconds after the first 
    /// task action execution completed). If the task action execution takes more that the minimumDelaySeconds, the next execution will occur 
    /// immediately.
    /// 
    /// If a task action throws an exception, the delaySeconds is doubled. The doubling is capped, however, at the provided
    /// maximumDelaySeconds. Once a task action completes without exception, the delay is reset to the provided minimumDelaySeconds.
    /// 
    /// The repeating process can be cancelled using the Terminate method, however, if a task action is already in progress, the termination will 
    /// not interupt it. Once terminated, new task actions will no longer be initiated. a terminated BackgroundRepeatingTask instance cannot be restarted.
    /// </summary>
    public class BackgroundRepeatingTask
    {
        private readonly Action _action;
        public ManualResetEventSlim ActionCompletedManualResetEvent { get; }
        public AutoResetEvent ActionCompletedAutoResetEvent { get; }
        private IMetricTimer _metricTimer { get; }
        private readonly double _minimumDelaySeconds;
        private readonly double _maximumDelaySeconds;
        private bool _terminateFlag;
        /// <summary>
        /// This auto reset event can be set to trigger the Action to be run immediately. 
        /// If the task is currently executing, setting this event will cause the taks to be once more immediately after the current iteration completes.
        /// If the task is currently sleeping, the sleep will be interupted and the task will run immediately.
        /// </summary>
        private AutoResetEvent _runTriggerEvent { get; }

        public void RunTrigger()
        {
            _runTriggerEvent.Set();
        }
        public bool TaskTerminated { get; private set; }

        //private IHealthIndicatorStopwatch _stopwatch;
        private double _currentLoopDelaySeconds;

        private void ResetDelay()
        {
            _currentLoopDelaySeconds = _minimumDelaySeconds;
        }

        private void IncreaseDelay()
        {
            _currentLoopDelaySeconds = Math.Min(_currentLoopDelaySeconds * 2.0, _maximumDelaySeconds);
        }

        /// <summary>
        /// Causes the background repeating task to be terminated. 
        /// If the action loop is currently executing, it will continue to completion prior to terminating.
        /// </summary>
        public void Terminate()
        {
            _terminateFlag = true;
            RunTrigger();
        }
        private void ActionLoop()
        {
            TimeSpan ts;
            try
            {
                //_stopwatch = HealthIndicator.Start();
                if (!_terminateFlag) _action();
                //ts = _stopwatch.Success();
                ActionCompletedManualResetEvent.Set();
                ActionCompletedAutoResetEvent.Set();
                // success, so reduce the loop delay to minimum
                ResetDelay();
            }
            catch (ThreadAbortException)
            {
                // we are probably being shut down 
                //ts = _stopwatch.Failure(ex);
                throw;
            }
            catch (Exception)
            {
                // failure, so double the loop delay (up to the maximum)
                //ts = _stopwatch.Failure(ex);
                IncreaseDelay();
            }
            if (_terminateFlag)
            {
                TaskTerminated = true;
                return;
            }
            var netDelay = TimeSpan.FromSeconds(Math.Max(_currentLoopDelaySeconds - ts.TotalSeconds, 0));
            ThreadPool.RegisterWaitForSingleObject(_runTriggerEvent, (state, timedOut) => ActionLoop(), null, netDelay, true);
        }

        public BackgroundRepeatingTask(Action action, IMetricTimer metricTimer,
                                            double minimumDelaySeconds, double maximumDelaySeconds)
        {
            _action = action;
           _metricTimer = metricTimer;
            _minimumDelaySeconds = minimumDelaySeconds;
            _maximumDelaySeconds = maximumDelaySeconds;
            _currentLoopDelaySeconds = minimumDelaySeconds;
            ActionCompletedManualResetEvent = new ManualResetEventSlim(false);
            ActionCompletedAutoResetEvent = new AutoResetEvent(false);
            _terminateFlag = false;
            _runTriggerEvent = new AutoResetEvent(false);
            ThreadPool.QueueUserWorkItem((state) => ActionLoop());
        }
    }

}
