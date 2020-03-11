
![Imgur](http://i.imgur.com/B8ClrWe.png)

A C# Phoenix Channels client. Unity Compatible. Proudly powering [Dama King](http://level3.io).

> Graphic is a shameless mix between unity, phoenix logos. Please don't sue me. Thanks.

+ [**Roadmap**](#roadmap): Transparency on what's next!
+ [**Getting Started**](#getting-started): A quicky guide on how to use this library.
+ [**PhoenixJS**](#phoenixjs): How this library differs from PhoenixJs.
+ [**Tests**](#tests): How to run the tests to make sure we're golden.
+ [**Dependencies**](#dependencies): A rant about dependencies.
+ [**Unity**](#unity): Important remarks for Unity developers.

## Roadmap

This project will remain as a prerelease until Unity ships their .Net profile upgrade, which ~should be soon~ **will debut in Unity 2017.1!!**. This will allow this library to utilize the latest and greatest .Net 4.6 features, enhancing the experience further, and allowing us to reach v1.0!

For now, I am also experimenting with the best API implementation, so breaking changes might be introduced. Once we reach v1.0, API should be stabile, and we can then focus on integrating CI, and uploading the package to NuGet.

Also, here is a basic TODO:

- [ ] Presence
- [ ] Socket automatic recovery

## Getting Started

For now, you can use git submodules or simply download the sources and drop them in your project.
Once you grab the source, you can look at `IntegrationTests.cs` for a full example:

##### Implementing `IWebsocketFactory` and `IWebsocket`

```cs
public sealed class WebsocketSharpAdapter: IWebsocket {

	private readonly WebSocket ws;
	private readonly WebsocketConfiguration config;


	public WebsocketSharpAdapter(WebSocket ws, WebsocketConfiguration config) {
		
		this.ws = ws;
		this.config = config;

		ws.OnOpen += OnWebsocketOpen;
		ws.OnClose += OnWebsocketClose;
		ws.OnError += OnWebsocketError;
		ws.OnMessage += OnWebsocketMessage;
	}


	#region IWebsocket methods

	public void Connect() { ws.Connect(); }
	public void Send(string message) { ws.Send(message); }
	public void Close(ushort? code = null, string message = null) { ws.Close(); }

	#endregion


	#region websocketsharp callbacks

	public void OnWebsocketOpen(object sender, EventArgs args) { config.onOpenCallback(this); }
	public void OnWebsocketClose(object sender, CloseEventArgs args) { config.onCloseCallback(this, args.Code, args.Reason); }
	public void OnWebsocketError(object sender, ErrorEventArgs args) { config.onErrorCallback(this, args.Message); }
	public void OnWebsocketMessage(object sender, MessageEventArgs args) { config.onMessageCallback(this, args.Data); }

	#endregion
}

public sealed class WebsocketSharpFactory: IWebsocketFactory {

	public IWebsocket Build(WebsocketConfiguration config) {

		var socket = new WebSocket(config.uri.AbsoluteUri);
		return new WebsocketSharpAdapter(socket, config);
	}
}
```

##### Creating a Socket

```cs
var socketFactory = new WebsocketSharpFactory();
var socket = new Socket(socketFactory);
socket.OnOpen += onOpenCallback;
socket.OnMessage += onMessageCallback;

socket.Connect(string.Format("ws://{0}/socket", host), null);
```

##### Joining a Channel

```cs
var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
roomChannel.On("after_join", m => afterJoinMessage = m);

roomChannel.Join(params)
  .Receive(Reply.Status.Ok, r => okReply = r)
  .Receive(Reply.Status.Error, r => errorReply = r);
```

## PhoenixJS

After porting the PhoenixJs library almost line-by-line to C#, it didn't prove to be a good fit for this statically typed language. JavaScript is chaotic, you can spin off timers quite liberally, and you can simply retry stuff till it works. Not in C#.

In C#, we would like very predictable and controlled behavior. We want to control which threads the library uses, and how it delivers its callbacks. We also want to control the reconnect/rety logic on our end, in order to properly determine the application state.

With that being said, here are the main deviations this library has from the PhoenixJS library:

+ Ability to control channel rejoin
+ Pluggable "Delayed Executor", useful for Unity developers

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
2. Websocket-sharp
3. NUnit

#### Details:

I really wanted to break the JSON and Websocket dependencies, allowing developers to plug in whatever libraries they prefer to use. Breaking the Websocket dependency was simple, but alas, the JSON dependency remained.

The issue with breaking the JSON dependency is the need to properly represent intermidiate data passed in from the socket all the way to the library caller. For example:

- Using plain `Dictionary` objects meant that the caller needs to manually convert those into the application types.
- Using plain `string` meant that the caller has to deserialize everything on their side, which meant lots of error handling everywhere.
- Using generics to inject JSON functionality required a lot of time and effort, which is a luxury I didn't have.

## Unity

#### Main Thread Callbacks

Unity ships with an old .Net profile for now, so our main thread synchronization tools are extremely limited. Hence, for now, you should use the `Socket.Options.delayedExecutor` property to plug in a `MonoBehaviour` that can execute code after a certain delay, using Coroutines or something similar. This will ensure the library will always run on the main thread.

If you are **not** concerned with multithreading issues, the library ships with a default `Timer` based executor, which executes after a delay using `System.Timers.Timer`.

#### Useful Libraries

I'm personally shipping this library with my Unity game, so you can rest assured it will always support Unity. Here are some important notes I learned from integrating PhoenixSharp with Unity:

- **BestHTTP websockets** instead of Websocket-sharp. It's much better maintained and doesn't require synchronizing callbacks from the socket to the main thread. Websocket-sharp does need that.
- **Json.NET** instead of Newtonsoft.Json, that's what I'm using. I've experienced weird issues in the past with the opensource Newtonsoft.Json on mobile platforms.

**NOTE:** Many people are using BestHTTP, so I figured it would be useful to add that integration separately in the repo, for people to use. See the directory, `Vendor/BestHTTP`.

## Contributions

Whether you open new issues or send in some PRs .. It's all welcome here!

## Author

Maz (Mazyad Alabduljalil)

[phoenix-integration-tests-repo]: https://github.com/Mazyod/phoenix-integration-tester
