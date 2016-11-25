# PhoenixSharp

A C# Phoenix Channels client. Unity Compatible.

## Dependencies

### Production Dependencies

1. Newtonsoft.Json

### Development/Test Dependencies

1. Newtonsoft.Json
4. Websocket-sharp
2. NUnit
3. NSubstitute

#### Details:

When I started writing this library, I wanted to break the JSON and Websocket dependencies, allowing developers to plug in whatever libraries they prefer to use. Breaking the Websocket dependency was simple, but alas, the JSON dependency remained.

The issue with breaking the JSON dependency is need to properly represent intermidiate data passed in from the socket all the way to the library caller. For example:

- Using plain `Dictionary` objects meant that you had to manually convert those into your application types.
- Using plain `string` meant that you had to deserialize everything on your side, which meant lots of error handling everywhere.
- Using a generic JSON spec requires a lot of time and effort, which is a luxury I do not currently have.

**NOTE**: For Unity developers, I really recommend you grab Json.NET from the AssetStore. I've used it, and it's perfect for compatibility with IL2Cpp and mobile platforms.
