using System;
using System.Collections.Generic;
using System.Linq;

/**
	## Presence data structure

	The presence information is returned as a map with presences grouped
	by key, cast as a string, and accumulated metadata, with the following form:

			%{key => %{metas: [%{phx_ref: ..., ...}, ...]}}

	For example, imagine a user with id `123` online from two
	different devices, as well as a user with id `456` online from
	just one device. The following presence information might be returned:

			%{"123" => %{metas: [%{status: "away", phx_ref: ...},
													 %{status: "online", phx_ref: ...}]},
				"456" => %{metas: [%{status: "online", phx_ref: ...}]}}

	The keys of the map will usually point to a resource ID. The value
	will contain a map with a `:metas` key containing a list of metadata
	for each resource. Additionally, every metadata entry will contain a
	`:phx_ref` key which can be used to uniquely identify metadata for a
	given key. In the event that the metadata was previously updated,
	a `:phx_ref_prev` key will be present containing the previous
	`:phx_ref` value.
 */
using State = System.Collections.Generic.Dictionary<
	string, Phoenix.Presence.MetadataContainer>;

namespace Phoenix {
	/**
	* Initializes the Presence
	* @param {Channel} channel - The Channel
	* @param {Object} opts - The options,
	*        for example `{events: {state: "state", diff: "diff"}}`
	*/

	public sealed class Presence {

		#region nested types

		public sealed class Metadata {
			public uint id;
			public string state;
			public string phxRef;
		}

		public sealed class MetadataContainer {
			public List<Metadata> metas;
		}

		public sealed class Diff {
			public State joins;
			public State leaves;
		}

		#endregion

		#region events

		public delegate void OnJoinDelegate(
			string key, MetadataContainer currentPresence, MetadataContainer newPresence);
		public OnJoinDelegate OnJoin;

		public delegate void OnLeaveDelegate(
			string key, MetadataContainer currentPresence, MetadataContainer newPresence);
		public OnLeaveDelegate OnLeave;

		public delegate void OnSyncDelegate();
		public OnSyncDelegate OnSync;

		#endregion

		private Channel channel;
		private readonly State state = new();
		private string joinRef = null;

		public Presence(Channel channel) {
			this.channel = channel;

			// TODO: Add Presence Event enum under InboundEvent
			channel.On("events.state", newState => {
				joinRef = channel.joinRef;

				OnSync?.Invoke();
			});

			channel.On("events.diff", diff => {

			});
		}

		public List<MetadataContainer> List(
			Func<KeyValuePair<string, MetadataContainer>, MetadataContainer> by
		) {
			return List(state, by);
		}

		// lower-level public static API

		/**
		* Used to sync the list of presences on the server
		* with the client's state. An optional `onJoin` and `onLeave` callback can
		* be provided to react to changes in the client's local presences across
		* disconnects and reconnects with the server.
		*
		* @returns {Presence}
		*
		static syncState(currentState, newState, onJoin, onLeave){
			let state = this.clone(currentState)
			let joins = {}
			let leaves = {}

			this.map(state, (key, presence) => {
				if(!newState[key]){
					leaves[key] = presence
				}
			})
			this.map(newState, (key, newPresence) => {
				let currentPresence = state[key]
				if(currentPresence){
					let newRefs = newPresence.metas.map(m => m.phx_ref)
					let curRefs = currentPresence.metas.map(m => m.phx_ref)
					let joinedMetas = newPresence.metas.filter(m => curRefs.indexOf(m.phx_ref) < 0)
					let leftMetas = currentPresence.metas.filter(m => newRefs.indexOf(m.phx_ref) < 0)
					if(joinedMetas.length > 0){
						joins[key] = newPresence
						joins[key].metas = joinedMetas
					}
					if(leftMetas.length > 0){
						leaves[key] = this.clone(currentPresence)
						leaves[key].metas = leftMetas
					}
				} else {
					joins[key] = newPresence
				}
			})
			return this.syncDiff(state, {joins: joins, leaves: leaves}, onJoin, onLeave)
		}
		*/
		public static State SyncState(
			State currentState,
			State newState,
			OnJoinDelegate onJoin,
			OnLeaveDelegate onLeave
		) {
			// TODO: figure out how to deep clone
			var state = new State(currentState);
			var joins = new State();
			var leaves = new State();

			foreach (var key in state.Keys) {
				if (!newState.ContainsKey(key)) {
					leaves[key] = state[key];
				}
			}

			foreach (var key in newState.Keys) {
				var newPresence = newState[key];
				var currentPresence = state.GetValueOrDefault(key);
				if (currentPresence != null) {
					var newRefs = newPresence.metas.Select(m => m.phxRef).ToList();
					var curRefs = currentPresence.metas.Select(m => m.phxRef).ToList();
					var joinedMetas = newPresence.metas.Where(m => curRefs.IndexOf(m.phxRef) < 0).ToList();
					var leftMetas = currentPresence.metas.Where(m => newRefs.IndexOf(m.phxRef) < 0).ToList();
					if (joinedMetas.Count > 0) {
						joins[key] = newPresence;
						joins[key].metas = joinedMetas;
					}
					if (leftMetas.Count > 0) {
						// TODO: figure out deep clone of currentPresence
						leaves[key] = currentPresence;
						leaves[key].metas = leftMetas;
					}
				} else {
					joins[key] = newPresence;
				}
			}

			return SyncDiff(state, new() { joins = joins, leaves = leaves }, onJoin, onLeave);
		}

		/**
		*
		* Used to sync a diff of presence join and leave
		* events from the server, as they happen. Like `syncState`, `syncDiff`
		* accepts optional `onJoin` and `onLeave` callbacks to react to a user
		* joining or leaving from a device.
		*
		* @returns {Presence}
		*
		static syncDiff(state, diff, onJoin, onLeave){
			let {joins, leaves} = this.clone(diff)
			if(!onJoin){ onJoin = function (){ } }
			if(!onLeave){ onLeave = function (){ } }

			this.map(joins, (key, newPresence) => {
				let currentPresence = state[key]
				state[key] = this.clone(newPresence)
				if(currentPresence){
					let joinedRefs = state[key].metas.map(m => m.phx_ref)
					let curMetas = currentPresence.metas.filter(m => joinedRefs.indexOf(m.phx_ref) < 0)
					state[key].metas.unshift(...curMetas)
				}
				onJoin(key, currentPresence, newPresence)
			})
			this.map(leaves, (key, leftPresence) => {
				let currentPresence = state[key]
				if(!currentPresence){ return }
				let refsToRemove = leftPresence.metas.map(m => m.phx_ref)
				currentPresence.metas = currentPresence.metas.filter(p => {
					return refsToRemove.indexOf(p.phx_ref) < 0
				})
				onLeave(key, currentPresence, leftPresence)
				if(currentPresence.metas.length === 0){
					delete state[key]
				}
			})
			return state
		}
		*/
		public static State SyncDiff(
			State state, 
			Diff diff, 
			OnJoinDelegate onJoin, 
			OnLeaveDelegate onLeave
		) {
			// TODO: figure out how to deep clone
			var joins = diff.joins;
			var leaves = diff.leaves;

			foreach (var key in diff.joins.Keys) {
				var newPresence = diff.joins[key];
				var currentPresence = state.GetValueOrDefault(key);
				// TODO: figure out deep clone of newPresence
				state[key] = newPresence;
				if (currentPresence != null) {
					var joinedRefs = state[key].metas.Select(m => m.phxRef).ToList();
					var curMetas = currentPresence.metas.Where(m => joinedRefs.IndexOf(m.phxRef) < 0).ToList();
					state[key].metas.InsertRange(0, curMetas);
				}
				onJoin?.Invoke(key, currentPresence, newPresence);
			}

			foreach (var key in diff.leaves.Keys) {
				var leftPresence = diff.leaves[key];
				var currentPresence = state.GetValueOrDefault(key);
				if (currentPresence == null) {
					continue;
				}
				var refsToRemove = leftPresence.metas.Select(m => m.phxRef).ToList();
				currentPresence.metas = currentPresence.metas.Where(
					p => refsToRemove.IndexOf(p.phxRef) < 0).ToList();

				onLeave?.Invoke(key, currentPresence, leftPresence);
				if (currentPresence.metas.Count == 0) {
					state.Remove(key);
				}
			}

			return state;
		}

		/**
		* Returns the array of presences, with selected metadata.
		*
		* @param {Object} presences
		* @param {Function} chooser
		*
		* @returns {Presence}
		*
		static list(presences, chooser){
			if(!chooser){ chooser = function (key, pres){ return pres } }

			return this.map(presences, (key, presence) => {
				return chooser(key, presence)
			})
		}
		*/
		public static List<MetadataContainer> List(
			State presences,
			Func<KeyValuePair<string, MetadataContainer>, MetadataContainer> chooser = null
		) {
			if (chooser == null) {
				chooser = keyPresenceTuple => keyPresenceTuple.Value;
			}

			return presences.ToList().Select(chooser).ToList();
		}

		// private

		/**
		static map(obj, func){
			return Object.getOwnPropertyNames(obj).map(key => func(key, obj[key]))
		}

		static clone(obj){ return JSON.parse(JSON.stringify(obj)) }
		*/
	}
}