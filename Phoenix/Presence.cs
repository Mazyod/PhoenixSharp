using System.Collections.Generic;
using System.Linq;
// ReSharper disable once InvalidXmlDocComment
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
using State = System.Collections.Generic.Dictionary<string, Phoenix.PresencePayload>;
using DiffList = System.Collections.Generic.List<Phoenix.Presence.Diff>;

namespace Phoenix
{
    /**
     * PresencePayload
     * avoiding structs since it's stored in a collection
     */
    public sealed class PresencePayload
    {
        public List<PresenceMeta> Metas;
        public IJsonBox Payload;
    }

    public sealed class PresenceMeta
    {
        public IJsonBox Payload;
        public string PhxRef;
    }

    /**
     * Initializes the Presence
     * @param {Channel} channel - The Channel
     * @param {Object} opts - The options,
     * for example `{events: {state: "state", diff: "diff"}}`
     * 
     * TODO: We are using immutable types since the PhoenixJS implementation uses deep clone.
     * TODO: Immutable types generate a lot of garbage, so we should consider using a different approach.
     */
    public sealed class Presence
    {
        public delegate void OnJoinDelegate(
            string key, PresencePayload currentPresence, PresencePayload newPresence);

        public delegate void OnLeaveDelegate(
            string key, PresencePayload currentPresence, PresencePayload newPresence);

        public delegate void OnSyncDelegate();

        private readonly Channel _channel;
        private readonly DiffList _pendingDiffs = new DiffList();
        private string _joinRef;

        public OnJoinDelegate OnJoin;
        public OnLeaveDelegate OnLeave;
        public OnSyncDelegate OnSync;

        public State State = new State();

        public Presence(Channel channel, Options opts = null)
        {
            _channel = channel;

            var options = opts ?? new Options();

            channel.On(options.StateEvent, message =>
            {
                var newState = message.Payload.Unbox<State>();
                _joinRef = channel.JoinRef;
                State = SyncState(State, newState, OnJoin, OnLeave);

                State = _pendingDiffs.Aggregate(
                    State,
                    (state, diff)
                        => SyncDiff(new State(state), diff, OnJoin, OnLeave)
                );

                _pendingDiffs.Clear();

                OnSync?.Invoke();
            });

            channel.On(options.DiffEvent, message =>
            {
                var diff = message.Payload.Unbox<Diff>();
                if (InPendingSyncState())
                {
                    _pendingDiffs.Add(diff);
                }
                else
                {
                    State = SyncDiff(new State(State), diff, OnJoin, OnLeave);
                    OnSync?.Invoke();
                }
            });
        }

        internal bool InPendingSyncState()
        {
            return _joinRef == null || _joinRef != _channel.JoinRef;
        }

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
            OnJoinDelegate onJoin = null,
            OnLeaveDelegate onLeave = null
        )
        {
            var joins = new State();
            var leaves = new State();

            foreach (var key in currentState.Keys.Where(key => !newState.ContainsKey(key)))
            {
                leaves[key] = currentState[key];
            }

            foreach (var key in newState.Keys)
            {
                var newPresence = newState[key];
                var found = currentState.TryGetValue(key, out var currentPresence);
                if (found)
                {
                    var newRefs = newPresence.Metas.Select(m => m.PhxRef).ToList();
                    var curRefs = currentPresence.Metas.Select(m => m.PhxRef).ToList();
                    var joinedMetas = newPresence.Metas.Where(m => curRefs.IndexOf(m.PhxRef) < 0).ToList();
                    var leftMetas = currentPresence.Metas.Where(m => !newRefs.Contains(m.PhxRef)).ToList();
                    if (joinedMetas.Count > 0)
                    {
                        joins[key] = new PresencePayload {Metas = joinedMetas};
                    }

                    if (leftMetas.Count > 0)
                    {
                        leaves[key] = new PresencePayload {Metas = leftMetas};
                    }
                }
                else
                {
                    joins[key] = newPresence;
                }
            }

            var diff = new Diff {Joins = joins, Leaves = leaves};
            return SyncDiff(new State(currentState), diff, onJoin, onLeave);
        }

        /**
         * Used to sync a diff of presence join and leave
         * events from the server, as they happen. Like `syncState`, `syncDiff`
         * accepts optional `onJoin` and `onLeave` callbacks to react to a user
         * joining or leaving from a device.
         */
        private static State SyncDiff(
            State state,
            Diff diff,
            OnJoinDelegate onJoin,
            OnLeaveDelegate onLeave
        )
        {
            foreach (var key in diff.Joins.Keys)
            {
                var newPresence = diff.Joins[key];
                var found = state.TryGetValue(key, out var currentPresence);
                state[key] = newPresence;
                if (found)
                {
                    var joinedRefs = state[key].Metas.Select(m => m.PhxRef).ToList();
                    var curMetas = currentPresence.Metas.Where(m => joinedRefs.IndexOf(m.PhxRef) < 0).ToList();
                    state[key].Metas.InsertRange(0, curMetas);
                }

                onJoin?.Invoke(key, currentPresence, newPresence);
            }

            foreach (var key in diff.Leaves.Keys)
            {
                var leftPresence = diff.Leaves[key];
                var found = state.TryGetValue(key, out var currentPresence);
                if (!found)
                {
                    continue;
                }

                var refsToRemove = leftPresence.Metas.Select(m => m.PhxRef).ToList();
                var filteredMetas = currentPresence.Metas.Where(
                    m => refsToRemove.IndexOf(m.PhxRef) < 0).ToList();

                var newPresence = new PresencePayload {Metas = filteredMetas};
                onLeave?.Invoke(key, newPresence, leftPresence);
                if (newPresence.Metas.Count == 0)
                {
                    state.Remove(key);
                }
                else
                {
                    state[key] = newPresence;
                }
            }

            return state;
        }

        public sealed class Options
        {
            public string DiffEvent = "presence_diff";
            public string StateEvent = "presence_state";
        }

        /**
         * Diff
         * avoiding structs since it's stored in a collection
         */
        public sealed class Diff
        {
            public State Joins;
            public State Leaves;
        }
    }
}