using System.Collections.Generic;

namespace Phoenix {
	/**
	* Initializes the Presence
	* @param {Channel} channel - The Channel
	* @param {Object} opts - The options,
	*        for example `{events: {state: "state", diff: "diff"}}`
	*/

	public sealed class Presence {
		
		#region events

		public delegate void OnJoinDelegate();
		public OnJoinDelegate OnJoin;

		public delegate void OnLeaveDelegate(ushort code, string message);
		public OnLeaveDelegate OnLeave;

		public delegate void OnSyncDelegate(string message);
		public OnSyncDelegate OnSync;

		#endregion


		private Channel channel;
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

		public List<Presence> List(Predicate<string, Presence> by) {
			return Presence.List(this.state, by);
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
		public static Presence syncState() {
			return null;
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
		public static Presence syncDiff() {
			return null;
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
		public static List<Presence> List(Predicate<string, Presence> chooser) {
			return new();
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