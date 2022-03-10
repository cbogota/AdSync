using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Health.Abstractions;

namespace Health
{
    internal class MetricTimer : IMetricTimer, IStatisticalDistribution
    {
        internal struct MetricTimerEvent : IMetricTimerEvent
        {
            public bool Success { get; }
            public DateTimeOffset CompletedOn { get; }
            public bool Completed => CompletedOn != default;
            public TimeSpan Duration { get; }
            public Exception Exception { get; }
            internal MetricTimerEvent (bool success, DateTimeOffset completedOn, TimeSpan duration, Exception exception)
            {
                Success = success;
                CompletedOn = completedOn;
                Duration = duration;
                Exception = exception;
            }
        }
        internal struct MetricTimerStopwatch : IMetricTimerStopwatch
        {
            private MetricTimer _timer;
            public IMetricTimer Timer => _timer;
            public DateTimeOffset StartedOn { get; }
            private Stopwatch _stopwatch;
            private MetricTimerEvent _evt;
            public IMetricTimerEvent Event => _evt;
            internal MetricTimerStopwatch(MetricTimer healthTimer)
            {
                _evt = default;
                _timer = healthTimer;
                StartedOn = DateTimeOffset.Now;
                _stopwatch = Stopwatch.StartNew();
            }
            public IMetricTimerEvent Success()
            {
                return Complete(true, null);
            }
            public IMetricTimerEvent Failure()
            {
                return Complete(false, null);
            }
            public IMetricTimerEvent Failure(Exception ex)
            {                
                return Complete(false, ex);
            }
            private IMetricTimerEvent Complete(bool success, Exception ex)
            {
                _stopwatch.Stop();
                return _timer?.RecordEvent(success, StartedOn.AddTicks(_stopwatch.ElapsedTicks), _stopwatch.Elapsed, ex);
            }
            public TimeSpan Elapsed => _stopwatch.Elapsed;
        }
        // Fields
        private long _successCount;
        public long SuccessCount => _successCount;
        private long _failureCount;
        public long FailureCount => _failureCount;
        public bool IsFaulted { get; private set; }
        private int _activeTimerCount;
        public int ActiveTimerCount => _activeTimerCount;
        private volatile int _maxActiveTimerCount;
        public int MaxActiveTimerCount => _maxActiveTimerCount;
        private Collections.ConcurrentRingBuffer<MetricTimerEvent> _eventBuffer;
        private readonly StatsLib.TDigest _tDigest;
        public IMetricTimerEvent LastEvent => _eventBuffer.Current;
        public IEnumerable<IMetricTimerEvent> RecentEvents => _eventBuffer.All.Cast<IMetricTimerEvent>();
        public double Min => _tDigest.Min;
        public double Max => _tDigest.Max;
        public double Average => _tDigest.Average;
        public double Count => _tDigest.Count;
        public DistributionPoint[] GetDistribution
        {
            get
            {
                var points = _tDigest.GetDistribution();
                var result = new DistributionPoint[points.Length];
                for (var i = 0; i < points.Length; i++)
                    result[i] = new DistributionPoint(points[i].Value, points[i].Count);
                return result;
            }
        }
        public double Accuracy => _tDigest.Accuracy;
        public string Name { get; }

        // Methods
        public MetricTimer(string name, int recentEventBufferSize = 128)
        {
            Name = name;
            _tDigest = new StatsLib.TDigest();
            _eventBuffer = new Collections.ConcurrentRingBuffer<MetricTimerEvent>(recentEventBufferSize);
        }
        public IMetricTimerStopwatch Start()
        {
            var activeTimerCount = Interlocked.Increment(ref _activeTimerCount);
            while (activeTimerCount > _maxActiveTimerCount)
            {
                var maxActiveTimerCount = _maxActiveTimerCount;
                if (activeTimerCount <= maxActiveTimerCount || Interlocked.CompareExchange(ref _maxActiveTimerCount, activeTimerCount, maxActiveTimerCount) == maxActiveTimerCount)
                    break;
            }
            return new MetricTimerStopwatch(this);
        }
        internal IMetricTimerEvent RecordEvent(bool success, DateTimeOffset completedOn, TimeSpan duration, Exception exception)
        {
            if (success)
                Interlocked.Increment(ref _successCount);
            else
                Interlocked.Increment(ref _failureCount);
            Interlocked.Decrement(ref _activeTimerCount);
            IsFaulted = !success;
            var evt = new MetricTimerEvent(success, completedOn, duration, exception);
            _eventBuffer.Add(evt);
            _tDigest.Add(evt.Duration.TotalSeconds);
            return evt;
        }
        public double Quantile(double quantile) => _tDigest.Quantile(quantile);
    }
}
