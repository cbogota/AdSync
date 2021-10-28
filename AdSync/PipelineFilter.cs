using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdSync
{
    public class PipelineFilter<TInput, TOutput>
    {
        readonly Func<PipelineFilter<TInput, TOutput>, TInput, TOutput> _filterProcessor = null;
        public BlockingCollection<TInput> Input;
        public BlockingCollection<TOutput> Output = null;
        readonly Action<PipelineFilter<TInput, TOutput>, TInput> _renderAction = null;
        readonly Action<PipelineFilter<TInput, TOutput>> _renderCompleteAction = null;
        CancellationToken _cancelToken;
        public Exception PipelineException { get; private set; }
        public string Name { get; private set; }

        public PipelineFilter(
            BlockingCollection<TInput> input,
            Func<PipelineFilter<TInput, TOutput>, TInput, TOutput> filterProcessor,
            BlockingCollection<TOutput> output,
            CancellationToken token,
            string name)
        {
            Input = input;
            _filterProcessor = filterProcessor;
            Output = output;
            _cancelToken = token;
            Name = name;
        }

        // Use this constructor for the final endpoint, which does
        // something like write to file or screen, instead of
        // pushing to another pipeline filter.
        // This pipeline is intended to have a finite lifetime and 
        // supports a "renderComplete" action which will be called 
        // when the input is completed and all items have been sent
        // to the outputRenderer.
        public PipelineFilter(
            BlockingCollection<TInput> input,
            Action<PipelineFilter<TInput, TOutput>, TInput> outputRenderer,
            Action<PipelineFilter<TInput, TOutput>> renderComplete,
            CancellationToken token,
            string name)
        {
            Input = input;
            _renderAction = outputRenderer;
            _renderCompleteAction = renderComplete;
            _cancelToken = token;
            Name = name;
        }

        // Use this constructor for the final endpoint, which does
        // something like write to file or screen, instead of
        // pushing to another pipeline filter.
        // This pipeline is intended to run indefinitely.
        public PipelineFilter(
            BlockingCollection<TInput> input,
            Action<PipelineFilter<TInput, TOutput>, TInput> outputRenderer,
            CancellationToken token,
            string name) : this(input, outputRenderer, null, token, name) {}

        public bool Run()
        {
            PipelineException = null;
            try
            {
                while (!Input.IsCompleted && !_cancelToken.IsCancellationRequested)
                {
                    TInput receivedItem;
                    if (!Input.TryTake(out receivedItem, 1000, _cancelToken)) continue;
                    if (Output != null)
                        Output.Add(_filterProcessor(this, receivedItem), _cancelToken);
                    else
                        _renderAction(this, receivedItem);
                }
                Output?.CompleteAdding();
                _renderCompleteAction?.Invoke(this);
                return true;
            }
            catch (Exception ex)
            {
                PipelineException = ex;
                return false;
            }
        }
    }
}
