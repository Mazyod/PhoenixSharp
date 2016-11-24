namespace Phoenix {
	// ## Presence
	//
	// The `Presence` object provides features for syncing presence information
	// from the server with the client and handling presences joining and leaving.
	//
	// ### Syncing initial state from the server
	//
	// `Presence.syncState` is used to sync the list of presences on the server
	// with the client's state. An optional `onJoin` and `onLeave` callback can
	// be provided to react to changes in the client's local presences across
	// disconnects and reconnects with the server.
	//
	// `Presence.syncDiff` is used to sync a diff of presence join and leave
	// events from the server, as they happen. Like `syncState`, `syncDiff`
	// accepts optional `onJoin` and `onLeave` callbacks to react to a user
	// joining or leaving from a device.
	//
	// ### Listing Presences
	//
	// `Presence.list` is used to return a list of presence information
	// based on the local state of metadata. By default, all presence
	// metadata is returned, but a `listBy` function can be supplied to
	// allow the client to select which metadata to use for a given presence.
	// For example, you may have a user online from different devices with a
	// a metadata status of "online", but they have set themselves to "away"
	// on another device. In this case, they app may choose to use the "away"
	// status for what appears on the UI. The example below defines a `listBy`
	// function which prioritizes the first metadata which was registered for
	// each user. This could be the first tab they opened, or the first device
	// they came online from:
	//
	//     let state = {}
	//     state = Presence.syncState(state, stateFromServer)
	//     let listBy = (id, {metas: [first, ...rest]}) => {
	//       first.count = rest.length + 1 // count of this user's presences
	//       first.id = id
	//       return first
	//     }
	//     let onlineUsers = Presence.list(state, listBy)
	//
	public class Presence {
		// TODO
	}
}