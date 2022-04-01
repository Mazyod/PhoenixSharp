using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using State = System.Collections.Immutable.ImmutableDictionary<
	string, Phoenix.Presence.MetadataContainer>;

using MutableState = System.Collections.Generic.Dictionary<
	string, Phoenix.Presence.MetadataContainer>;

using DiffList = System.Collections.Immutable.ImmutableList<
	Phoenix.Presence.Diff>;

using MetadataList = System.Collections.Immutable.ImmutableList<
	Phoenix.Presence.Metadata>;

namespace Phoenix {
	/**
	* Initializes the Presence
	* @param {Channel} channel - The Channel
	* @param {Object} opts - The options,
	*        for example `{events: {state: "state", diff: "diff"}}`
	*
	* TODO: We are using immutable types since the PhoenixJS implementation uses deep clone.
	* TODO: Immutable types generate a lot of garbage, so we should consider using a different approach.
	*/
	public sealed class Presence {

		#region nested types

		public sealed class Options {
			public string stateEvent = "presence_state";
			public string diffEvent = "presence_diff";
		}

		public sealed record Metadata(
			uint id, string state, string phxRef
		);

		public sealed record MetadataContainer(
			MetadataList metas
		);

		public sealed record Diff(
			State joins, State leaves
		);

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

		public State state = State.Empty;
		private readonly DiffList pendingDiffs = DiffList.Empty;
		private readonly Channel channel;
		private string joinRef = null;

		public Presence(Channel channel, Options opts = null) {
			this.channel = channel;

			var options = opts ?? new();

			channel.On(options.stateEvent, message => {
				var newState = channel.socket.opts.messageSerializer.MapPayload<State>(message.payload);
				joinRef = channel.joinRef;
				state = SyncState(state, newState, OnJoin, OnLeave);

				state = pendingDiffs.Aggregate(
					state,
					(state, diff) => SyncDiff(new(state), diff, OnJoin, OnLeave)
				);

				pendingDiffs.Clear();

				OnSync?.Invoke();
			});

			channel.On(options.diffEvent, message => {
				var diff = channel.socket.opts.messageSerializer.MapPayload<Diff>(message.payload);
				if (InPendingSyncState()) {
					pendingDiffs.Add(diff);
				} else {
					state = SyncDiff(new(state), diff, OnJoin, OnLeave);
					OnSync?.Invoke();
				}
			});
		}

		public List<MetadataContainer> List(
			Func<KeyValuePair<string, MetadataContainer>, MetadataContainer> by
		) => List(state, by);

		public bool InPendingSyncState() => joinRef == null || joinRef != channel.joinRef;

		// lower-level public static API

		/**
		* Used to sync the list of presences on the server
		* with the client's state. An optional `onJoin` and `onLeave` callback can
		* be provided to react to changes in the client's local presences across
		* disconnects and reconnects with the server.
		*/
		public static State SyncState(
			State currentState,
			State newState,
			OnJoinDelegate onJoin,
			OnLeaveDelegate onLeave
		) {
			var state = currentState;
			var joins = new MutableState();
			var leaves = new MutableState();

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
						joins[key] = newPresence with { metas = joinedMetas.ToImmutableList() };
					}
					if (leftMetas.Count > 0) {
						leaves[key] = currentPresence with { metas = leftMetas.ToImmutableList() };
					}
				} else {
					joins[key] = newPresence;
				}
			}

			Diff diff = new(joins.ToImmutableDictionary(), leaves.ToImmutableDictionary());
			return SyncDiff(new(state), diff, onJoin, onLeave);
		}

		/**
		* Used to sync a diff of presence join and leave
		* events from the server, as they happen. Like `syncState`, `syncDiff`
		* accepts optional `onJoin` and `onLeave` callbacks to react to a user
		* joining or leaving from a device.
		*/
		public static State SyncDiff(
			MutableState state,
			Diff diff,
			OnJoinDelegate onJoin,
			OnLeaveDelegate onLeave
		) {
			// PhoenixJS: we don't clone here since we use immutable types
			var joins = diff.joins;
			var leaves = diff.leaves;

			foreach (var key in diff.joins.Keys) {
				var newPresence = diff.joins[key];
				var currentPresence = state.GetValueOrDefault(key);
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
				var filteredMetas = currentPresence.metas.Where(
					p => refsToRemove.IndexOf(p.phxRef) < 0).ToList();

				var newPresence = currentPresence with { metas = filteredMetas.ToImmutableList() };
				onLeave?.Invoke(key, newPresence, leftPresence);
				if (newPresence.metas.Count == 0) {
					state.Remove(key);
				} else {
					state[key] = newPresence;
				}
			}

			return state.ToImmutableDictionary();
		}

		/**
		* Returns the array of presences, with selected metadata.
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
	}
}