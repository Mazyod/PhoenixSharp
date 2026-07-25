
# Migration Guide

## 2.0 Hardening Effort (July 2026)

Version 2.0 tightens the library's failure, threading, presence, and transport
contracts. Start with the compile-time changes below, then review the runtime
changes even if your application builds without edits.

### What breaks at compile time

#### `ILogger` implementations

`ILogger` used to have one write method:

```csharp
// Before 2.0
public sealed class AppLogger : ILogger
{
    public void Log(LogLevel level, string source, string message)
    {
        Console.Error.WriteLine($"[{level}] [{source}] {message}");
    }
}
```

In 2.0, the sink owns filtering and receives the original exception separately:

```csharp
// 2.0
public sealed class AppLogger : ILogger
{
    public bool IsEnabled(LogLevel level, string source)
    {
        return level >= LogLevel.Info;
    }

    public void Log(
        LogLevel level,
        string source,
        string message,
        Exception? exception
    )
    {
        try
        {
            Console.Error.WriteLine($"[{level}] [{source}] {message}");
            if (exception != null)
            {
                Console.Error.WriteLine(exception);
            }
        }
        catch
        {
            // ILogger implementations must not throw.
        }
    }
}
```

`IsEnabled` should be fast; neither method may throw. PhoenixSharp calls it
before formatting suppressed entries. If either method throws, that sink
instance is disabled for that socket and PhoenixSharp attempts one fail-safe
`Console.Error` diagnostic naming the sink. Use the `LogSource` constants
instead of matching source strings yourself. In particular, the old
`"Channel"` source is now `LogSource.Channel`, whose value is `"channel"`;
the other constants are `Transport`, `Push`, `Socket`, and `Receive`.

#### `Socket.OnError`

The error callback used to receive only a message:

```csharp
// Before 2.0
socket.OnError += message => Console.Error.WriteLine(message);
```

It now receives a structured `PhoenixError`:

```csharp
// 2.0
socket.OnError += error =>
{
    Console.Error.WriteLine($"{error.Kind}: {error.Message}");
    if (error.Exception != null)
    {
        Console.Error.WriteLine(error.Exception);
    }
};
```

`PhoenixError` exposes `Message`, `Kind`, and an optional `Exception`.
`PhoenixErrorKind` has the concrete values `Transport`, `Send`, `Heartbeat`,
`Serialization`, and `Dispatch`. `Dispatch` on this callback means a
delivery-blocking channel `OnMessage` hook failed; contained failures after
delivery use `OnUnhandledError` instead. A heartbeat timeout now emits a
`Heartbeat` error before teardown. Calling `Disconnect()` from that error
handler is honored and prevents the timeout path from scheduling a reconnect.

#### Custom WebSocket adapters

`WebsocketConfiguration` used to be a mutable struct with lower-case fields:

```csharp
// Before 2.0
public IWebsocket Build(WebsocketConfiguration config)
{
    var transport = new MyWebsocket(config.uri);
    transport.Opened += () => config.onOpenCallback(transport);
    transport.Closed += (code, reason) =>
        config.onCloseCallback(transport, code, reason);
    transport.Failed += message =>
        config.onErrorCallback(transport, message);
    transport.MessageReceived += message =>
        config.onMessageCallback(transport, message);
    return transport;
}
```

It is now a sealed immutable class with Pascal-case properties:

```csharp
// 2.0
public IWebsocket Build(WebsocketConfiguration config)
{
    var transport = new MyWebsocket(config.Uri);
    transport.Opened += () => config.OnOpenCallback(transport);
    transport.Closed += (code, reason) =>
        config.OnCloseCallback(transport, code, reason);
    transport.Failed += message =>
        config.OnErrorCallback(transport, message);
    transport.MessageReceived += message =>
        config.OnMessageCallback(transport, message);
    return transport;
}
```

Code that constructs configurations directly must replace object initializers
with `new WebsocketConfiguration(uri, onOpen, onClose, onError, onMessage)`.
`IWebsocketFactory.Build` must not invoke a configuration callback before
`Connect()` is called on the returned transport. Callbacks may run
synchronously from `Connect()` or later from any thread, `State` must be safe
to read from any thread, and adapters should preserve close codes and reasons
when their underlying transport exposes them.

`IWebsocket.Send` remains a synchronous `void` contract. An adapter that wraps
an asynchronous send cannot report a failure that arrives after `Send`
returns by throwing it from `Send`; it must invoke the configuration's
`OnErrorCallback` instead. PhoenixSharp surfaces that late callback as a
`Transport` error, not a `Send` error. A Task-based transport contract is a
3.0 candidate for representing asynchronous send completion directly.

#### Socket and Presence callback events

The `Socket` callback members (`OnOpen`, `OnClose`, `OnError`,
`OnUnhandledError`, and `OnMessage`) and the `Presence` callback members
(`OnJoin`, `OnLeave`, and `OnSync`) are now C# events instead of public
delegate fields. Existing `+=` and `-=` subscriptions are unchanged. Code
that directly assigned a handler with `=`, invoked a callback externally, or
read its delegate value now fails to compile; retain your own delegate when
you need to remove or invoke it.

`OnClosedDelegate`'s reason parameter is now annotated `string?`: the close
reason is genuinely absent (`null`) for unexpected disconnections, matching
1.x runtime behavior. Handlers written with an explicit non-nullable `string`
parameter type get a nullable-reference warning; transports must not coalesce
an absent reason into an empty string.

#### `ChannelSubscription` construction and mutation

`ChannelSubscription` instances are created only by `Channel.On(...)` in 2.0.
The public parameterless constructor is gone, and the `Event` and `Callback`
members are now get-only properties (mutating `Event` on a live subscription
silently broke `Off(subscription)` lookups). Code that constructed
subscriptions manually or reassigned their members must instead subscribe via
`On(...)` and keep the returned token:

```csharp
// Before 2.0
var sub = new ChannelSubscription { Event = "my_event", Callback = handler };

// 2.0
var sub = channel.On("my_event", handler);
channel.Off(sub);
```

#### `TaskExecution` construction

`TaskExecution`'s public parameterless constructor is now internal. A directly
constructed instance was never associated with a scheduled action, so it could
only cancel a never-scheduled execution. Custom `IDelayedExecutor`
implementations should return their own `IDelayedExecution` implementation
instead of reusing `TaskExecution`.

#### `Presence.State` writers

`State` used to be a public field, so callers could replace it or take it by
reference:

```csharp
// Before 2.0
presence.State = restoredState;
ref var writableState = ref presence.State;
writableState[userId] = replacement;
```

It is now a get-only snapshot property. Keep application-owned state separately
if it must be edited:

```csharp
// 2.0
var snapshot = presence.State;
var applicationState =
    new Dictionary<string, PresencePayload>(snapshot);
applicationState[userId] = replacement;
```

There is no supported setter for the presence instance. Treat the returned
dictionary as immutable even though its concrete type is `Dictionary`;
mutating it is not a supported write-back path and must not be expected to feed
into later presence state. Presence updates publish a fresh dictionary rather
than updating a previously published snapshot. Reflection that expected a
field must also be changed to read the property.

### What behaves differently at runtime

#### Send and buffering policy

Previously, transport and wire-serialization failures could escape the socket
send path, disturb channel/rejoin state, strand or grow the send buffer, or
prevent later buffered entries from being attempted. In 2.0, transport
`Send` and wire-serialization exceptions no longer escape synchronously from
that path. Normal pushes use a contained at-least-once retry policy within one
connection generation: a buffered entry is dropped after its fifth failed
transport send and failures surface through `OnError` as `Send`; a
serialization-poisoned entry surfaces as `Serialization`, is dropped
immediately, and does not block the remaining buffer. This is not an
across-reconnect delivery guarantee. Frames buffered before reconnect retain
their old `join_ref`; when flushed in the new generation, the server drops
those stale frames, matching phoenix.js behavior. Heartbeats are sent once or
dropped and are never buffered. Send failures do not error channels, reset
rejoin backoff, or fault connection waiters. Subscribe to `OnError` and make
non-idempotent server operations deduplicatable: if a transport partially
transmits a frame and then throws, PhoenixSharp cannot detect that delivery
and a retry can execute the operation twice.

#### Connection parameters

Previously, connection parameters were appended without per-key/value
escaping, the caller's dictionary remained live and was mutated to add
`vsn`, and later dictionary edits could affect reconnects. In 2.0, the static
dictionary is snapshotted when `Socket` is constructed, each key and value is
URL-escaped exactly once when a connection URI is built, a null value becomes
an empty string, and `Socket.Options.Vsn` always replaces a caller-supplied
`vsn`. Pass raw parameter text now; callers that keep pre-escaping values will
double-encode percent sequences. Use `ParamsProvider`, described below, when a
token or other parameter must be refreshed for each connection attempt.

#### Reply statuses and payloads

Previously, reading `Reply.ReplyStatus` for an unfamiliar wire status could
throw and leave reply waiters or hooks unfinished. In 2.0, the getter never
throws: unknown values such as `"partial"` map to `ReplyStatus.Error`, while
the original atom remains in `Reply.Status`; `Receive(ReplyStatus.Error, ...)`
and the async push/join failure paths therefore run for custom statuses.
Error responses are not required to have the same shape as successful
responses. Check `Status` or `ReplyStatus` before unboxing `Response`, and use
the raw `Status` when your protocol gives a custom error atom its own meaning.

#### Channel join, leave, and dispatch

Previously, late join lifecycle replies could move a leaving or closed channel,
`LeaveAsync` could wait forever on an error reply, and an unrelated state
change during event dispatch could prevent later subscribers from running. In
2.0, join `ok`, `error`, and `timeout` lifecycle hooks refuse
`Leaving`/`Closed` channels; leave closes locally on any reply, including
error, custom-error, or timeout, so `LeaveAsync` completes; and only
`Leave()` or cleanup aborts the remaining user callbacks in a captured
dispatch. One inconsistency remains: if `Leave()` wins locally and the server
later accepts the already-sent join, `JoinAsync` can still return a successful
`JoinResult` while the channel remains `Closed`. Treat channel state as
authoritative after a leave, and do not use a successful late join task to
resume work on that channel instance.

#### Presence callbacks and snapshots

Previously, the third `OnJoin` argument was the merged presence, the remaining
presence passed to `OnLeave` lost its `Payload`, and callbacks could observe
the old `State`. In 2.0, `OnJoin` receives the unmerged presence from the
incoming join diff (matching phoenix.js), the remaining-presence argument to
`OnLeave` retains its payload, and the new snapshot is fully published before
`OnJoin`, `OnLeave`, or `OnSync` runs. Audit callbacks that treated the third
join argument as the user's full accumulated presence; read `presence.State`
inside the callback when the merged value is needed, and continue to treat
every published snapshot as read-only.

#### `Presence.SyncDiff`

Previously, diff synchronization was a private, mutating implementation
detail. `Presence.SyncDiff` is now public and returns a fresh top-level
dictionary without mutating the supplied state, so assign or otherwise use its
return value. The copy is intentionally shallow: when a diff introduces a new
key, that key's `PresencePayload` is stored by reference. Do not mutate diff
payload objects or their metadata after calling `SyncDiff`, or those changes
can also appear in the returned state.

#### Threading and `SynchronizationContext`

Previously, the default `TaskDelayedExecutor` could capture the caller's
`SynchronizationContext`, which sometimes moved reconnect, timeout, or rejoin
work back to a Unity/UI thread; cancellation only marked the timer and retained
its closure until the delay expired. In 2.0, its callbacks always run on the
thread pool, pending cancellation releases the timer immediately, and
PhoenixSharp protects its internal state without promising a thread for user
code. Socket delegates, channel subscriptions, reply hooks, presence events,
and log sinks may all run on any thread. Marshal explicitly before touching
Unity or UI objects, and wrap callback bodies because an exception escaping
thread-pool user code bypasses UI-context unhandled-exception hooks. Socket
and presence callbacks are events, so concurrent `+=`/`-=` operations update
their invocation lists atomically; that does not serialize handler execution
or give callbacks thread affinity.

#### Async socket and channel lifecycle

Previously, `ConnectAsync` and `DisconnectAsync` had state and overlap paths
that could leave their tasks pending forever, and leave error replies had the
same problem. In 2.0, already-open, never-connected, closing, failed, disposed,
and connect/disconnect overlap paths settle with success, cancellation, or a
typed fault, with disconnect winning an overlap. There is one intentional
unbounded case: when `ReconnectAfter` is configured, `ConnectAsync` remains
pending while the retry chain is active and can wait indefinitely if no
attempt ever succeeds. Pass a cancellation token with an application-level
deadline, handle the exception types described below, and expect operations
that reject disposed socket use, such as `ConnectAsync` and `Channel`, to
surface `ObjectDisposedException`.

#### Wait helpers and cancellation cleanup

Previously, `WaitForUserAsync` could miss a join between its state check and
subscription, and calling `WaitForInitialSyncAsync` after the first sync waited
for another sync. In 2.0, the user lookup and subscription are atomic, and
`WaitForInitialSyncAsync` completes immediately once that `Presence` instance
has ever synchronized, including after a later disconnect. Use `OnSync` when
you need fresh state after every reconnect rather than initial-readiness
semantics. Async join, leave, push, receive, event, and presence wrappers also
dispose their cancellation registrations after completion; consumers should
still supply cancellation or timeout bounds where the underlying operation is
allowed to wait.

#### Inbound Phoenix V2 frames

Previously, the JSON converter assumed an array and indexed its fields
directly, so short frames failed incidentally and extra elements were ignored.
In 2.0, an inbound V2 frame must be a JSON array with exactly five elements;
both truncated and extended frames are rejected with a descriptive
deserialization error. This is deliberately stricter than phoenix.js's
tolerant array destructuring. Ensure custom servers and serializers emit
`[join_ref, ref, topic, event, payload]` exactly; malformed inbound frames are
dropped and reported as contained `Serialization` errors through
`OnUnhandledError`.

#### Reply `Message` identity

Previously, code could happen to observe separate record instances as a
`phx_reply` moved through dispatch. Reply values and dispatch order are
unchanged in 2.0: `Channel.OnMessage` still sees the wire `phx_reply` followed
by its `chan_reply_{ref}` remap. To avoid redundant record clones, PhoenixSharp
may now pass the same immutable `Message` instance from an `OnMessage` call to
that event's reply subscriber when the hook leaves the payload and join ref
unchanged. Do not rely on reference inequality between those messages; compare
their record values or fields instead.

### What's new

#### Typed exceptions

2.0 adds `PhoenixException` and its
`PhoenixConnectionException` subclass. Terminal `ConnectAsync` failures now
fault with `PhoenixConnectionException` and preserve the original exception as
`InnerException`; a transport close failure during `DisconnectAsync` faults
with `PhoenixException`. API misuse that previously threw a bare `Exception`,
such as joining twice or pushing before joining, now throws
`InvalidOperationException`. A `Channel.OnMessage` override that returns null
for a non-null payload is also an `InvalidOperationException`. Catch these
types instead of matching exception message text, and keep
`OperationCanceledException` and `ObjectDisposedException` handling separate
from transport failure handling.

#### Refreshable connection parameters

Set `Socket.Options.ParamsProvider` before constructing the socket when
credentials must be refreshed:

```csharp
var options = new Socket.Options(new JsonMessageSerializer())
{
    ParamsProvider = () => new Dictionary<string, string>
    {
        ["token"] = tokenProvider.GetAccessToken()
    }
};

var socket = new Socket(endpoint, null, factory, options);
```

The provider is invoked immediately before each genuine transport build,
including reconnects, and its returned dictionary is snapshotted immediately.
It may be called concurrently, so it and the token source must be thread-safe.
Return raw values; the socket performs escaping. A null result is treated as an
empty caller-parameter dictionary, after which `vsn` is still added, and
`Options.Vsn` always wins. Passing a non-null static parameter dictionary to
the `Socket` constructor while also configuring `ParamsProvider` throws
`ArgumentException`.

#### Shipped loggers

PhoenixSharp now includes `ConsoleLogger` for .NET and `UnityLogger` in its own
auto-referenced `Phoenix.UnityLogger` Unity assembly. Neither is enabled
automatically; assign one to `Socket.Options.Logger`. Both default to
`LogLevel.Info` and accept a minimum level in their constructor:

```csharp
var options = new Socket.Options(new JsonMessageSerializer())
{
    Logger = new ConsoleLogger(LogLevel.Info)
    // In Unity, use: Logger = new UnityLogger(LogLevel.Info)
};
```

#### `Socket.OnUnhandledError`

`OnUnhandledError` exposes failures PhoenixSharp deliberately contains so
processing can continue, including per-subscriber callback exceptions,
dropped inbound messages, void `Connect()` failures, and teardown/disposal
close failures. This differs from `OnError`, which carries operational errors
and delivery-blocking channel hook failures:

```csharp
socket.OnUnhandledError += error =>
    Console.Error.WriteLine(
        $"Contained {error.Kind}: {error.Message}"
    );
```

If no handler and no usable logger is configured, the first unobserved
contained failure for that socket attempts a one-line warning on
`Console.Error`; active reconnect attempts suppress the redundant connect
warning. Exceptions thrown by an `OnUnhandledError` handler are themselves
contained and are not recursively reported to the handler.

#### Diagnostics and package contents

`JsonBox.ToString()` now returns compact diagnostic JSON bounded to
`JsonBox.MaximumToStringLength` (4,096 UTF-16 characters). Longer output ends
with `...[truncated]` and is not guaranteed to remain valid JSON, so use
`Unbox<T>()` rather than `ToString()` for data processing. The NuGet build now
includes generated XML documentation and produces a SourceLink-enabled
`.snupkg` with portable symbols. The Unity logger assembly is isolated from the
engine-free core assembly.

#### Removed interim and internal names

The final error taxonomy has no `PhoenixErrorKind.Unknown`; code that used an
intermediate 2.0 build must switch to one of the five concrete kinds listed
above. The dead internal `Socket.AbnormalClose` helper was also removed and
requires no migration for supported consumer code.

## Json Refactoring Effort (May 2023)

The library was missing the ability to expose the full JSON response from the presence payload. This limitation exposed a major weakness in the library's design, which was the lack of a unified JSON response interface.
Also previously, the library abstracted the underlying JSON object type using an opaque `object` type. This caused a lot of frustration due to the lack of type safety and the need to cast the object to the correct type.

Now, `IJsonBox` interface is introduced to abstract the underlying JSON object type. It also lead to a unified interface for interacting with any JSON response.
More nice side-effects of this change were better performance and less memory usage.

If you are implementing your own IMessageSerializer, you may need to update your implementation to support the new `IJsonBox` interface.
The new interface should be simpler, allowing you to easily migrate to the new version.

In order to migrate to the new version, you need to make sure you are using the new `IJsonBox` interface instead of the `object` type. 
Also, the JsonResponse and JsonPayload extension methods are now removed in favor of the new `IJsonBox` interface.
(The type system should detect all these issues.)

```diff
-reply.JsonResponse<ChannelError>()
+reply.Response.Unbox<ChannelError>()

-message.JsonPayload<PresenceEvent>()
+message.Payload.Unbox<PresenceEvent>()
```

## From pre-release (before 2022)

The library underwent a major overhaul since the pre-release version, so it will be very difficult to document every change.

Here is a best-effort guide to the changes made in the latest release. Please feel free to raise a PR / issue in case something is missing.

**IMPORTANT:** The changes are not exhaustive.

#### IWebSocket Changes

`IWebSocket` interface now requires the underlying socket to report its state.

```cs
public WebsocketState State {
  get {
    return ws.State switch {
      WebSocketStates.Connecting => WebsocketState.Connecting,
      WebSocketStates.Open => WebsocketState.Open,
      WebSocketStates.Closing => WebsocketState.Closing,
      _ => WebsocketState.Closed,
    };
  }
}
```

#### DelayedExecutor Changes

Instead of returning `uint`, `DelayedExecutor` now returns `IDelayedExecution` instance. It is a simple object that "knows" how to cancel the delayed execution.

```diff
-public uint Execute(Action action, TimeSpan delay) {
+public IDelayedExecution Execute(Action action, TimeSpan delay) {
   // ...
-  return id;
+  return new DelayedExecution(id, this);
}
```

#### Message Event Enums

Enum values are now standardized as per the C# naming convention.

Avoiding to use the enum names as the corresponding event names also has the advantage of avoiding the use of `.ToString()` on enums, which is much [less performant][enum-tostring-performance] than a simple switch with static strings.

```diff
-Message.InBoundEvent.phx_error.ToString()
+Message.InBoundEvent.Error.Serialized()
```

Also, `Reply.Status` changed to `ReplyStatus`, allowing `reply.Status` to hold the status value.

```diff
-.Receive(Reply.Status.Ok, _ => callback(true))
+.Receive(ReplyStatus.Ok, _ => callback(true))
```

#### Socket / Channel Initialization

1. Instead of passing parameters on connect / join, we pass them on initialization.
2. It is required to explicitly pass a serializer instance along with the options to the socket.
3. Channel creation has been renamed to `Channel`.

```diff
 socket = new Socket(
+  url,
+  @params,
   new BestHTTPWebsocketFactory(),
-  new()
+  new(new JsonMessageSerializer())
 );

-socket.Connect(url, @params);
+socket.Connect();
 
-var channel = socket.MakeChannel(topic);
-channel.Join(@params);
+var channel = socket.Channel(topic, @params);
+channel.Join();
```

#### Channel Push

Previously, pushing to a channel required a `JObject` instance. This required coupling the caller with the serializer, not to a lot of redundant code.

Now, you can simply pass any object that you know the serializer can handle. The library will simply pass this object to the serializer before sending it to the server.

```diff
// here, chat is an instance of some custom class
-channel.PushJson("chat", JObject.FromObject(chat));
+channel.Push("chat", chat);
```

#### Channel & Push Callbacks

If you're interested in the `Message.payload` property of a channel event, you can use the new generic `On` method to get the payload mapped directly.

**NOTE:** you can't pass a method with this approach, due to how generics in C# work.

```diff
 channel.On(
   "on_costs_data",
-  message => {
-    var costs = message.payload.ToObject<CostsData>();
+  (CostsData costs) => {
     @delegate.OnCostsData(costs);
   }
 );
```

If you would like to access the `Message.payload` or `Reply.response` properties directly, it is recommended to use the extension methods, as those property types are `object`.

```cs
var payload = message.JSONPayload<JToken>();
// or...
var response = reply.JSONResponse<CustomType>();
```

Previously, only one subscriber could be attached to the event. Adding more subscribers, subsequently, would overwrite the previous one.

```cs
// OLD BEHAVIOUR
channel.On(@event, DoSomething);
channel.On(@event, DoSomethingElse);
// Only DoSomethingElse would be called
channel.Off(@event);
```

```cs
var sub1 = channel.On(@event, DoSomething);
var sub2 = channel.On(@event, DoSomethingElse);
// Both callbacks will be called
channel.Off(@event, sub1);
channel.Off(@event, sub2);
```

#### Various API Changes

```diff
 // accessing the underlying websocket adapter
-var adapter = socket.websocket as MyAdapter;
+var adapter = socket.conn as MyAdapter;
```

```diff
 // channel canPush check
-channel.canPush;
+channel.CanPush();
```

#### Under the Hood

Under the hood, the library now uses Phoenix V2 serialization format, which uses arrays instead of dictionaries to save on redundant JSON keys. It should be transparent to the user, since the backend will handle the serialization automatically based on the `vsn` property sent with the request.

[enum-tostring-performance]: https://youtu.be/BoE5Y6Xkm6w
