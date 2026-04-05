// UniTask Delayed Executor for PhoenixSharp
//
// Replaces the default TaskDelayedExecutor with a zero-allocation alternative
// powered by Cysharp/UniTask. All delays run on Unity's PlayerLoop instead of
// the thread pool, eliminating Task allocations and ensuring callbacks fire on
// the main thread — no SynchronizationContext gymnastics needed.
//
// Prerequisites:
//   Install UniTask via UPM: https://github.com/Cysharp/UniTask#upm-package
//
// Usage:
//
//   var options = new Socket.Options(new JsonMessageSerializer())
//   {
//       DelayedExecutor = new UniTaskDelayedExecutor()
//   };
//   var socket = new Socket(address, null, factory, options);

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Phoenix;


namespace Phoenix
{
    public sealed class UniTaskDelayedExecutor : IDelayedExecutor
    {
        public IDelayedExecution Execute(Action action, TimeSpan delay)
        {
            var execution = new UniTaskExecution();

            // UniTask.Delay is struct-based (zero allocation) and ticks on
            // Unity's PlayerLoop, so the callback always runs on the main
            // thread. The CancellationToken lets us cancel cleanly if the
            // socket tears down before the delay elapses.
            RunDelayed(action, delay, execution.Token).Forget();

            return execution;
        }

        private static async UniTaskVoid RunDelayed(
            Action action,
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            try
            {
                await UniTask.Delay(delay, cancellationToken: cancellationToken);
                action();
            }
            catch (OperationCanceledException)
            {
                // Expected when IDelayedExecution.Cancel() is called.
            }
        }
    }

    public sealed class UniTaskExecution : IDelayedExecution
    {
        private readonly CancellationTokenSource _cts = new();

        internal CancellationToken Token => _cts.Token;

        public void Cancel()
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }
}
