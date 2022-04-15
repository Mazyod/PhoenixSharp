
# Migration Guide

## From pre-release

The library underwent a major overhaul since the pre-release version, so it will be very difficult to document every change.

Here is a best-effort guide to the changes made in the latest release. Please feel free to raise a PR / issue in case something is missing.

**IMPORTANT:** The changes are not exhaustive.

#### IWebSocket Changes

`IWebSocket` interface now requires the underlying socket to report its state.

```cs
public WebsocketState state {
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

Instead of returning `uint`, `DelayedExecutor` now returns `IDelayedExecution` instance. It is a simple object that "knows" how to cancel the delayed exection.

```diff
-public uint Execute(Action action, TimeSpan delay) {
+public IDelayedExecution Execute(Action action, TimeSpan delay) {
   // ...
-  return id;
+  return new DelayedExecution(id, this);
}
```

### Message Event Enums

Enum values are now standardized as per the C# naming convention.

Avoiding to use the enum names as the corresponding event names also has the advantage of avoiding the use of `.ToString()` on enums, which is much [less performant][enum-tostring-performance] than a simple switch with static strings.

```diff
-Message.InBoundEvent.phx_error.ToString()
+Message.InBoundEvent.Error.Serialized()
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
+  new(new JSONMessageSerializer())
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

#### Channel / Push Callbacks

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
