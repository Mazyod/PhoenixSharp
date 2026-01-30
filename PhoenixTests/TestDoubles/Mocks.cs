using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix;

namespace PhoenixTests.TestDoubles
{
    #region Mock Delayed Executor Classes

    /// <summary>
    /// Mock delayed execution that allows synchronous execution for testing.
    /// </summary>
    public sealed class MockDelayedExecution : IDelayedExecution
    {
        public bool Cancelled { get; private set; }
        public Action? Action { get; set; }

        public void Cancel()
        {
            Cancelled = true;
        }

        public void Execute()
        {
            if (!Cancelled)
            {
                Action?.Invoke();
            }
        }
    }

    /// <summary>
    /// Mock delayed executor that allows synchronous execution for testing.
    /// </summary>
    public sealed class MockDelayedExecutor : IDelayedExecutor
    {
        public Action<TimeSpan>? OnExecute { get; set; }
        private MockDelayedExecution? _pendingExecution;

        public IDelayedExecution Execute(Action action, TimeSpan delay)
        {
            OnExecute?.Invoke(delay);
            _pendingExecution = new MockDelayedExecution { Action = action };
            return _pendingExecution;
        }

        public void ExecutePending()
        {
            _pendingExecution?.Execute();
        }
    }

    /// <summary>
    /// Represents a scheduled execution that can be tracked and triggered manually.
    /// Enhanced version that tracks all executions for comprehensive socket testing.
    /// </summary>
    public sealed class TrackedDelayedExecution : IDelayedExecution
    {
        public Action? Action { get; }
        public TimeSpan Delay { get; }
        public bool IsCancelled { get; private set; }

        public TrackedDelayedExecution(Action action, TimeSpan delay)
        {
            Action = action;
            Delay = delay;
        }

        public void Cancel()
        {
            IsCancelled = true;
        }

        /// <summary>
        /// Execute this action if not cancelled.
        /// </summary>
        public void Execute()
        {
            if (!IsCancelled)
            {
                Action?.Invoke();
            }
        }
    }

    /// <summary>
    /// Mock delayed executor that captures all scheduled executions
    /// for manual triggering in tests. Tracks complete execution history.
    /// </summary>
    public sealed class TrackingDelayedExecutor : IDelayedExecutor
    {
        public List<TrackedDelayedExecution> Executions { get; } = new();

        public IDelayedExecution Execute(Action action, TimeSpan delay)
        {
            var execution = new TrackedDelayedExecution(action, delay);
            Executions.Add(execution);
            return execution;
        }

        /// <summary>
        /// Execute the most recently scheduled action.
        /// </summary>
        public void ExecuteLast()
        {
            var last = Executions.LastOrDefault(e => !e.IsCancelled);
            last?.Execute();
        }

        /// <summary>
        /// Execute all pending (non-cancelled) actions.
        /// </summary>
        public void ExecuteAll()
        {
            foreach (var execution in Executions.Where(e => !e.IsCancelled).ToList())
            {
                execution.Execute();
            }
        }

        /// <summary>
        /// Get the count of pending (non-cancelled) executions.
        /// </summary>
        public int PendingCount => Executions.Count(e => !e.IsCancelled);

        /// <summary>
        /// Clear all executions.
        /// </summary>
        public void Clear()
        {
            Executions.Clear();
        }

        /// <summary>
        /// Get pending executions (non-cancelled).
        /// </summary>
        public IEnumerable<TrackedDelayedExecution> PendingExecutions =>
            Executions.Where(e => !e.IsCancelled);
    }

    #endregion

    #region Mock Websocket Classes

    /// <summary>
    /// Extended mock websocket that allows simulating callbacks.
    /// </summary>
    public sealed class MockWebsocketAdapterWithCallbacks : IWebsocket
    {
        private readonly WebsocketConfiguration _config;

        public readonly List<string> CallSend = new();
        public int CallCloseCount;
        public int CallConnectCount;
        public ushort? LastCloseCode;
        public string? LastCloseReason;
        public WebsocketState MockState = WebsocketState.Closed;

        public MockWebsocketAdapterWithCallbacks(WebsocketConfiguration config)
        {
            _config = config;
        }

        public WebsocketState State => MockState;

        public void Connect()
        {
            CallConnectCount += 1;
            MockState = WebsocketState.Open;
            _config.onOpenCallback?.Invoke(this);
        }

        public void Send(string message)
        {
            CallSend.Add(message);
        }

        public void Close(ushort? code = null, string? message = null)
        {
            CallCloseCount += 1;
            LastCloseCode = code;
            LastCloseReason = message;
            MockState = WebsocketState.Closed;
            _config.onCloseCallback?.Invoke(this, code ?? 0, message ?? "");
        }

        public void SimulateError(string error)
        {
            _config.onErrorCallback?.Invoke(this, error);
        }

        public void SimulateClose(ushort code, string reason)
        {
            MockState = WebsocketState.Closed;
            _config.onCloseCallback?.Invoke(this, code, reason);
        }

        public void SimulateMessage(string message)
        {
            _config.onMessageCallback?.Invoke(this, message);
        }
    }

    /// <summary>
    /// Factory that creates MockWebsocketAdapterWithCallbacks and tracks the last created instance.
    /// </summary>
    public sealed class MockWebsocketFactoryWithCallbackTracking : IWebsocketFactory
    {
        public MockWebsocketAdapterWithCallbacks? LastCreatedWebsocket { get; private set; }

        public IWebsocket Build(WebsocketConfiguration config)
        {
            LastCreatedWebsocket = new MockWebsocketAdapterWithCallbacks(config);
            return LastCreatedWebsocket;
        }
    }

    /// <summary>
    /// Websocket that throws on Connect.
    /// </summary>
    public sealed class ThrowingWebsocket : IWebsocket
    {
        public WebsocketState State => WebsocketState.Closed;

        public void Connect()
        {
            throw new Exception("Connection failed");
        }

        public void Send(string message)
        {
            throw new Exception("Not connected");
        }

        public void Close(ushort? code = null, string? message = null)
        {
        }
    }

    /// <summary>
    /// Factory that creates websockets that throw on Connect.
    /// </summary>
    public sealed class ThrowingWebsocketFactory : IWebsocketFactory
    {
        public IWebsocket Build(WebsocketConfiguration config)
        {
            return new ThrowingWebsocket();
        }
    }

    /// <summary>
    /// Factory that creates websockets that fail a specified number of times before succeeding.
    /// Useful for testing progressive reconnect backoff.
    /// </summary>
    public sealed class FailingThenSucceedingWebsocketFactory : IWebsocketFactory
    {
        private readonly int _failCount;
        private int _attempts;

        public FailingThenSucceedingWebsocketFactory(int failCount)
        {
            _failCount = failCount;
        }

        public int Attempts => _attempts;

        public IWebsocket Build(WebsocketConfiguration config)
        {
            _attempts++;
            if (_attempts <= _failCount)
            {
                return new ThrowingWebsocket();
            }
            return new MockWebsocketAdapterWithCallbacks(config);
        }
    }

    /// <summary>
    /// Websocket that calls onError callback on Connect.
    /// </summary>
    public sealed class ErrorOnConnectWebsocket : IWebsocket
    {
        private readonly WebsocketConfiguration _config;
        private readonly string _errorMessage;

        public ErrorOnConnectWebsocket(WebsocketConfiguration config, string errorMessage)
        {
            _config = config;
            _errorMessage = errorMessage;
        }

        public WebsocketState State => WebsocketState.Closed;

        public void Connect()
        {
            _config.onErrorCallback?.Invoke(this, _errorMessage);
        }

        public void Send(string message) { }

        public void Close(ushort? code = null, string? message = null) { }
    }

    /// <summary>
    /// Factory that creates websockets that call onError on Connect.
    /// </summary>
    public sealed class ErrorOnConnectWebsocketFactory : IWebsocketFactory
    {
        private readonly string _errorMessage;

        public ErrorOnConnectWebsocketFactory(string errorMessage)
        {
            _errorMessage = errorMessage;
        }

        public IWebsocket Build(WebsocketConfiguration config)
        {
            return new ErrorOnConnectWebsocket(config, _errorMessage);
        }
    }

    /// <summary>
    /// Websocket that delays calling onOpen.
    /// </summary>
    public sealed class DelayedOpenWebsocket : IWebsocket
    {
        private readonly WebsocketConfiguration _config;
        private readonly TimeSpan _delay;
        private WebsocketState _state = WebsocketState.Closed;

        public DelayedOpenWebsocket(WebsocketConfiguration config, TimeSpan delay)
        {
            _config = config;
            _delay = delay;
        }

        public WebsocketState State => _state;

        public void Connect()
        {
            _state = WebsocketState.Connecting;
            // Don't call onOpen - we're simulating a slow connection
            // In real usage, onOpen would be called after the delay
        }

        public void Send(string message) { }

        public void Close(ushort? code = null, string? message = null)
        {
            _state = WebsocketState.Closed;
            _config.onCloseCallback?.Invoke(this, code ?? 0, message ?? "");
        }
    }

    /// <summary>
    /// Factory that creates websockets with delayed open.
    /// </summary>
    public sealed class DelayedOpenWebsocketFactory : IWebsocketFactory
    {
        private readonly TimeSpan _delay;

        public DelayedOpenWebsocketFactory(TimeSpan delay)
        {
            _delay = delay;
        }

        public IWebsocket Build(WebsocketConfiguration config)
        {
            return new DelayedOpenWebsocket(config, _delay);
        }
    }

    #endregion
}
