using UnityEngine;
using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

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
		public static JObject syncState(JObject currentState, JObject newState, Action<string, JToken, JToken> onJoin = null, Action<string, JToken, JToken> onLeave = null){
			JObject state = (JObject)JObject.Parse(currentState.ToString());
			JObject joins = new JObject();
			JObject leaves = new JObject();

			IList<string> keys = state.Properties().Select(p => p.Name).ToList();
			foreach (string key in keys) {
				if(newState[key] == null){
					leaves[key] = state[key];
				}
			}

			IList<string> newkeys = newState.Properties().Select(p => p.Name).ToList();	
			foreach (string key in newkeys) {
				var newPresence = newState [key];
				var currentPresence = state[key];
				if(currentPresence != null){
					List<JToken> newRefs = getPhxRef ((JArray) newPresence ["metas"]);
					List<JToken> curRefs = getPhxRef ((JArray) currentPresence ["metas"]);

					List<JToken> joinedMetas = newPresence["metas"]
						.Where (newPresenceMeta => curRefs.Exists (curRef => curRef == newPresenceMeta ["phx_ref"]))
						.ToList();
					
					List<JToken> leftMetas = currentPresence["metas"]
						.Where (currentPresenceMeta => newRefs.Exists (newRef => newRef == currentPresenceMeta ["phx_ref"]))
						.ToList();

					if(joinedMetas.Count != 0){
						joins[key] = newPresence;
						joins[key]["metas"] = new JArray (
							joinedMetas.Select (p => new JObject {
								{ "phx_ref", p["phx_ref"]},
								{ "online_at", p["online_at"]}
							})
						);
					}
					if(leftMetas.Count != 0){
						leaves[key] = (JObject)JObject.Parse(currentPresence.ToString());
						leaves[key]["metas"] = new JArray (
							leftMetas.Select (p => new JObject {
								{ "phx_ref", p["phx_ref"]},
								{ "online_at", p["online_at"]}
							})
						);
					}
				} else {
					joins[key] = newPresence;
				}
			}
			JObject diffState = new JObject ();
			diffState ["joins"] = joins;
			diffState ["leaves"] = leaves;
			return syncDiff( state, diffState, onJoin, onLeave);
		}

		public static JObject syncDiff(JObject currentState, JObject diffState, Action<string, JToken, JToken> onJoin = null, Action<string, JToken, JToken> onLeave = null){
			JObject joins = (JObject)diffState ["joins"];
			JObject leaves = (JObject)diffState ["leaves"];
			JObject state = (JObject)JObject.Parse(currentState.ToString());

			IList<string> joinskeys = joins.Properties().Select(p => p.Name).ToList();	
			foreach (string key in joinskeys) {
				var newPresence = joins[key];
				var currentPresence = state [key];
				state [key] = newPresence;
				if (currentPresence != null) {
					((JArray)state [key]["metas"]).Merge(currentPresence["metas"]);
				}
				if (onJoin != null) { 
					onJoin (key, currentPresence, newPresence);
				}
			}

			IList<string> leaveskeys = leaves.Properties().Select(p => p.Name).ToList();	
			foreach (string key in leaveskeys) {
				var leftPresence = leaves[key];
				var currentPresence = state [key];
				if(currentPresence != null){
					List<JToken> refsToRemove = getPhxRef ((JArray) leftPresence ["metas"]);
					List<JToken> currentPresenceMeta = currentPresence ["metas"]
						.Where (currectMeta => refsToRemove.Exists (newRef => newRef == currectMeta ["phx_ref"]))
						.ToList();

					currentPresence ["metas"] = new JArray (
						currentPresenceMeta.Select (p => new JObject {
							{ "phx_ref", p["phx_ref"]},
							{ "online_at", p["online_at"]}
						})
					);

					if(onLeave != null){
						onLeave(key, currentPresence, leftPresence);
					}
					if(currentPresence["metas"].Count() == 0){
						state.Remove(key);
					}
				}
			}
			return state;
		}

		public static void list(JObject presences,  Action<string, JToken> chooser){
			if(chooser == null){ 
				return;
			}
			IList<string> keys = presences.Properties().Select(p => p.Name).ToList();	
			foreach (string key in keys) {
				chooser (key, presences[key]);
			}
		}

		static List<JToken> getPhxRef(JArray metas){
			List<JToken> newRefs = new List<JToken>();
			foreach (JObject newPresenceMeta in metas ) {
				newRefs.Add (newPresenceMeta["phx_ref"]);
			}
			return newRefs;
		}

	}
}
