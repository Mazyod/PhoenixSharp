
![Imgur](http://i.imgur.com/B8ClrWe.png)

[![.NET](https://github.com/Mazyod/PhoenixSharp/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Mazyod/PhoenixSharp/actions/workflows/dotnet.yml) &nbsp; ![net](https://img.shields.io/badge/version-netstandard%202.0-blue)

A C# Phoenix Channels client. Unity Compatible. Proudly powering [Dama King][level3-website].

> Graphic is a shameless mix between unity, phoenix logos. Please don't sue me. Thanks.

+ [**Overview**](#overview): What this library is about.
+ [**Getting Started**](#getting-started): A quicky guide on how to use this library.
+ [**PhoenixJS**](#phoenixjs): How this library differs from PhoenixJs.
+ [**Tests**](#tests): How to run the tests to make sure we're golden.
+ [**Dependencies**](#dependencies): A rant about dependencies.
+ [**Unity**](#unity): Important remarks for Unity developers.

## Overview

PhoenixSharp has the following main goals:
- Aspires to be the defacto Phoenix C# client.
- Portable enough to work out of the box in Unity and other C# environments.

In order to achieve the goals stated, it is necessary to:
- Maintain a close resemblance to the Phoenix.js implementation.
- Engage the community to accommodate different requirements based on various environments.

## Getting Started

**Migrating from older versions? [See our migration guide][migration-guide]**

For now, you can use git submodules or simply download the sources and drop them in your project.

Once you grab the source, you can look at `IntegrationTests.cs` for a full example. Otherwise, keep reading to learn more.

### Required Interfaces

#### Implementing `IWebsocketFactory` and `IWebsocket`

The library requires you to implement `IWebsocketFactory` and `IWebsocket` in order to provide a websocket implementation of your choosing.

Under the PhoenixTests/WebSocketImpl folder, you'll find a few sample implementations of these interfaces which you could simply copy to your project as needed.

> [!WARNING]\
> DotNetWebSocket may be unstable. Please consider using BestHTTP, WebSocketSharp, or contributing fixes, or adding new implementations 🤌

#### Implementing `IMessageSerializer` and `IJsonBox`

`IMessageSerializer` is the interface that allows you to customize the serialization of your Phoenix messages.

`IJsonBox` wraps the underlying mutable JSON object, such as JToken in NewtonSoft.Json and JsonElement/JsonObject in System.Text.Json/System.Text.Json.Nodes.

The library ships with a default implementation: `JsonMessageSerializer`. It relies on [Newtonsoft.Json][newtonsoft-website] to provide JSON serialization based on [Phoenix V2 format][phoenix-v2-serialization-format]. The implementation is self-contained in a single file. This means, by removing that one file, you can decouple your code from Newtonsoft.Json if you like.

### Establishing a Connection

#### Creating a Socket

Once you have your websocket and serializer implementation ready, you can proceed to create a socket object. A `Phoenix.Socket` instance represents a connection to a Phoenix server.

In order to ensure that socket connections are self-contained, we pass the socket parameters on initialization. Trying to connect with different parameters requires a new socket instance.

```cs
var socketOptions = new Socket.Options(new JsonMessageSerializer());
var socketAddress = "ws://my-awesome-app.com/socket";
var socketFactory = new WebsocketSharpFactory();
var socket = new Socket(socketAddress, null, socketFactory, socketOptions);

socket.OnOpen += onOpenCallback;
socket.OnMessage += onMessageCallback;

socket.Connect();
```

#### Joining a Channel

Once the socket is created, you can now join a channel. The API is so simple, you could explore it yourself with auto-complete, but here's a quick example:

```cs
// initialize a channel with topic and parameters
var roomChannel = socket.Channel(
  "tester:phoenix-sharp",
  channelParams
);

// prepare any event callbacks
// e.g. listen to phx_error inbound event
roomChannel.On(
  Message.InBoundEvent.Error,
  message => errorMessage = message
);
// ... listen to a custom event
roomChannel.On(
  "after_join",
  message => afterJoinMessage = message
);
// ... you can also use a generic event callback
// this will parse the message payload automatically
roomChannel.On(
  "custom_event",
  (CustomPayload payload) => Handle(payload)
);

// join the channel, handling the reply response as needed
// here, we assume JoinResponse and ChannelError are defined
roomChannel.Join()
  .Receive(
    ReplyStatus.Ok, 
    reply => okResponse = reply.Response.Unbox<JoinResponse>()
  )
  .Receive(
    ReplyStatus.Error,
    reply => errorResponse = reply.Response.Unbox<ChannelError>()
  );

// push a message to the channel
roomChannel
  .Push("reply_test", payload)
  .Receive(
    ReplyStatus.Ok, 
    reply => testOkReply = reply
  );
```

#### Presence Tracking

Presence is also supported by the library.

```cs
var presence = new Presence(channel);
presence.OnJoin += onJoinCallback;
presence.OnLeave += onLeaveCallback;
```

## PhoenixJS

The difference between PhoenixJS and PhoenixSharp can be observed in the following areas:
- The static typing nature of C#, and in contrast, the dynamic nature of JavaScript.
  + Defining types for various constructs.
  + Adding generic callbacks to automatically extract and parse payloads.
  + Using delegates, instead of callbacks, to handle events.
- The flexibility required to allow PhoenixSharp to be adapted to different environments.
  + Abstracting away the websocket implementation.
  + Pluggable "Delayed Executor", useful for Unity developers.
  + Ability to disable socket reconnect / channel rejoin.
- The lack of features in PhoenixSharp due to lesser popularity and contributions.

## Tests

In order to run the integration tests specifically, you need to make sure you have a phoenix server running and point the `host` in the integration tests to it.

I've published [the code for the phoenix server I'm using to run the tests against here][phoenix-integration-tests-repo]. However, if for any reason you don't want to run the phoenix server locally, you can use the following host:

```
phoenix-integration-tester.herokuapp.com
```

## Dependencies

### Production Dependencies

1. (Optional) Newtonsoft.Json

### Development/Test Dependencies

1. NUnit
2. WebSocketSharp
3. Newtonsoft.Json

#### Details about the Dependencies

`Newtonsoft.Json` is marked as optional because it can easily be replaced with another implementation as needed. However, this flexibility when it comes to the serialization process comes at a cost.

Due to the decoupling of the serializer from the rest of the implementation, it left use with an unfortunate side-effect. The use of `object` as the type of `payload` and `response` properties on the `Message` and `Reply` classes, respectively.

We try to mitigate the effects of this "type loss" issue by providing higher-level APIs that abstract away the need to handle the `object` types directly.

## Unity

First off, it would very much be worth your while to read [Microsoft's documentation on Unity's scripting upgrade][microsoft-docs-unity]. It highlights the main opportunities and challenges, which is also an inspiration for this library to take things further with the new scripting upgrade.

#### Main Thread Callbacks

One of the core components of the library is a mechanism that mimics javascipt's `setTimeout` and `setInterval` functions. It is used to trigger timeout event in case we don't get a response back in time.

By default, the library uses the `System.Threading.Task` class to schedule the callbacks. Based on our tests, this works well in Unity out-of-the-box thanks to the `SynchronizationContext`.

If you'd rather not use the `Task` based executor, you can easily replace it with a custom implementation by implementing the `IDelayedExecutor` interface. For example, you can use the `CoroutineDelayedExecutor` available in the Reference directory of this repo. Another option is to provide a custom implementation based on [UniTask][unitask-repo] if you see it more performant and beneficial to your project.

#### Useful Libraries

I'm personally shipping this library with my Unity game, so you can rest assured it will always support Unity. Here are some important notes I learned from integrating PhoenixSharp with Unity:

- **BestHTTP websockets** instead of Websocket-sharp. It's much better maintained and doesn't require synchronizing callbacks from the socket to the main thread. Websocket-sharp does need that.
- **Json.NET** instead of Newtonsoft.Json, that's what I'm using. I've experienced weird issues in the past with the opensource Newtonsoft.Json on mobile platforms.

**NOTE:**
- Many people are using BestHTTP, so I figured it would be useful to add that integration separately in the repo, for people to use. See the directory, `Reference/Unity/BestHTTP`.
- Under `Reference/Unity` directory as well, you will find a sample implementation for `IDelayedExecutor` that can be used in Unity projects.

## Contributions

Whether you open new issues or send in some PRs .. It's all welcome here!

## Author

Maz (Mazyad Alabduljaleel)

[level3-website]: http://level3.io
[newtonsoft-website]: https://www.newtonsoft.com/json
[microsoft-docs-unity]: https://docs.microsoft.com/en-us/visualstudio/gamedev/unity/unity-scripting-upgrade
[unitask-repo]: https://github.com/Cysharp/UniTask
[migration-guide]: https://github.com/Mazyod/PhoenixSharp/blob/master/Migration.md
[phoenix-integration-tests-repo]: https://github.com/Mazyod/phoenix-integration-tester
[phoenix-v2-serialization-format]: https://github.com/phoenixframework/phoenix/blob/master/lib/phoenix/socket/serializers/v2_json_serializer.ex
