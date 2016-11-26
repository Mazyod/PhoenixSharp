# PhoenixSharp

A C# Phoenix Channels client. Unity Compatible.

## Getting Started

This project still needs to be prepared and uploaded to NuGet, which isn't done yet. Instead, you can use the git submodule approach or simply download the sources and drop them in your project.

Once you grab the source, you can look at `IntegrationTests.cs` for a full example:

##### Creating a Socket

```cs
var socketFactory = new WebsocetSharpFactory();
var socket = new Socket(socketFactory);
socket.Connect(string.Format("ws://{0}/socket", host), null);
```

##### Joining a Channel

```cs
var roomChannel = socket.MakeChannel("tester:phoenix-sharp", param);
roomChannel.On(Message.InBoundEvent.Close, m => closeMessage = m);
roomChannel.On("after_join", m => afterJoinMessage = m);

roomChannel.Join()
  .Receive(Reply.Status.Ok, r => okReply = r)
  .Receive(Reply.Status.Error, r => errorReply = r);
```

## Tests

In order to run the integration tests specifically, you need to make sure you have a phoenix server running and point the `host` in the integration tests to that.

I've published the [phoenix server I'm using to run the tests against here][phoenix-integration-tests-repo]. However, if for any reason you don't want to run the phoenix server locally, you can use the following host:

```
phoenix-integration-tester.herokuapp.com
```

## Dependencies

### Production Dependencies

1. Newtonsoft.Json

### Development/Test Dependencies

1. Newtonsoft.Json
4. Websocket-sharp
2. NUnit
3. NSubstitute

#### Details:

I really wanted to break the JSON and Websocket dependencies, allowing developers to plug in whatever libraries they prefer to use. Breaking the Websocket dependency was simple, but alas, the JSON dependency remained.

The issue with breaking the JSON dependency is the need to properly represent intermidiate data passed in from the socket all the way to the library caller. For example:

- Using plain `Dictionary` objects meant that the caller needs to manually convert those into the application types.
- Using plain `string` meant that the caller has to deserialize everything on their side, which meant lots of error handling everywhere.
- Using generics to inject JSON functionality required a lot of time and effort, which is a luxury I didn't have.

## Unity

I'm personally shipping this library with my Unity game, so you can rest assured it will always support Unity. Here are some important notes I learned from integrating PhoenixSharp with Unity:

- I am using BestHTTP websockets instead of Websocket-sharp. It's much better maintained and doesn't require synchronizing callbacks from the socket to the main thread. Websocket-sharp does need that.
- I really recommend you grab Json.NET from the AssetStore, that's what I'm using. I've experienced weird issues in the past with the opensource Newtonsoft.Json on mobile platforms.

## Contributions

Whether you open new issues or send in some PRs .. It's all welcome here!

## Author

Maz (Mazyad Alabduljalil)

[phoenix-integration-tests-repo]: https://github.com/Mazyod/phoenix-integration-tester
