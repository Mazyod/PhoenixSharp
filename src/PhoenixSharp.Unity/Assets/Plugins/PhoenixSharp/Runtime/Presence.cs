#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffList = System.Collections.Generic.List<Phoenix.Presence.Diff>;
/*
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

namespace Phoenix
{
    /**
     * PresencePayload
     * avoiding structs since it's stored in a collection
     */
    public sealed class PresencePayload
    {
        public List<PresenceMeta> Metas = new List<PresenceMeta>();
        public IJsonBox Payload = null!;
    }

    public sealed class PresenceMeta
    {
        public IJsonBox Payload = null!;
        public string PhxRef = string.Empty;
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
            string key, PresencePayload? currentPresence, PresencePayload newPresence);

        public delegate void OnLeaveDelegate(
            string key, PresencePayload? currentPresence, PresencePayload newPresence);

        public delegate void OnSyncDelegate();

        private sealed class PresenceChange
        {
            public readonly PresencePayload ChangedPresence;
            public readonly PresencePayload? CurrentPresence;
            public readonly bool IsJoin;
            public readonly string Key;

            public PresenceChange(
                bool isJoin,
                string key,
                PresencePayload? currentPresence,
                PresencePayload changedPresence
            )
            {
                IsJoin = isJoin;
                Key = key;
                CurrentPresence = currentPresence;
                ChangedPresence = changedPresence;
            }
        }

        private readonly Channel _channel;
        private readonly DiffList _pendingDiffs = new DiffList();
        private readonly object _stateLock = new object();
        private bool _hasSynced;
        private string? _joinRef;
        private State _state = new State();

        public OnJoinDelegate? OnJoin;
        public OnLeaveDelegate? OnLeave;
        public OnSyncDelegate? OnSync;

        /// <summary>
        /// Gets the current presence state snapshot.
        /// </summary>
        /// <remarks>
        /// The returned dictionary is an immutable-by-convention snapshot; do not mutate it.
        /// A later presence update publishes a new dictionary and never mutates this snapshot.
        /// </remarks>
        public State State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        public Presence(Channel channel, Options? opts = null)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            _channel = channel;

            var options = opts ?? new Options();

            channel.On(options.StateEvent, HandleState);
            channel.On(options.DiffEvent, HandleDiff);
        }

        internal bool InPendingSyncState()
        {
            var channelJoinRef = _channel.JoinRef;
            lock (_stateLock)
            {
                return InPendingSyncStateUnsafe(channelJoinRef);
            }
        }

        private void HandleState(Message message)
        {
            var newState = message.Payload?.Unbox<State>() ?? new State();
            var channelJoinRef = _channel.JoinRef;
            var changes = new List<PresenceChange>();
            OnJoinDelegate collectJoin = (key, currentPresence, joinedPresence) =>
                changes.Add(new PresenceChange(true, key, currentPresence, joinedPresence));
            OnLeaveDelegate collectLeave = (key, currentPresence, leftPresence) =>
                changes.Add(new PresenceChange(false, key, currentPresence, leftPresence));

            OnJoinDelegate? onJoin;
            OnLeaveDelegate? onLeave;
            OnSyncDelegate? onSync;
            lock (_stateLock)
            {
                _joinRef = channelJoinRef;
                var updatedState = SyncState(
                    _state,
                    newState,
                    collectJoin,
                    collectLeave
                );

                foreach (var diff in _pendingDiffs)
                {
                    updatedState = SyncDiff(
                        updatedState,
                        diff,
                        collectJoin,
                        collectLeave
                    );
                }

                _pendingDiffs.Clear();
                _state = updatedState;
                _hasSynced = true;
                onJoin = OnJoin;
                onLeave = OnLeave;
                onSync = OnSync;
            }

            InvokePresenceChanges(changes, onJoin, onLeave);
            onSync?.Invoke();
        }

        private void HandleDiff(Message message)
        {
            var diff = message.Payload?.Unbox<Diff>();
            if (diff == null)
            {
                return;
            }

            var channelJoinRef = _channel.JoinRef;
            var changes = new List<PresenceChange>();
            OnJoinDelegate collectJoin = (key, currentPresence, joinedPresence) =>
                changes.Add(new PresenceChange(true, key, currentPresence, joinedPresence));
            OnLeaveDelegate collectLeave = (key, currentPresence, leftPresence) =>
                changes.Add(new PresenceChange(false, key, currentPresence, leftPresence));

            OnJoinDelegate? onJoin;
            OnLeaveDelegate? onLeave;
            OnSyncDelegate? onSync;
            lock (_stateLock)
            {
                if (InPendingSyncStateUnsafe(channelJoinRef))
                {
                    _pendingDiffs.Add(diff);
                    return;
                }

                _state = SyncDiff(
                    _state,
                    diff,
                    collectJoin,
                    collectLeave
                );
                onJoin = OnJoin;
                onLeave = OnLeave;
                onSync = OnSync;
            }

            InvokePresenceChanges(changes, onJoin, onLeave);
            onSync?.Invoke();
        }

        private bool InPendingSyncStateUnsafe(string? channelJoinRef)
        {
            return _joinRef == null || _joinRef != channelJoinRef;
        }

        private static void InvokePresenceChanges(
            List<PresenceChange> changes,
            OnJoinDelegate? onJoin,
            OnLeaveDelegate? onLeave
        )
        {
            foreach (var change in changes)
            {
                if (change.IsJoin)
                {
                    onJoin?.Invoke(
                        change.Key,
                        change.CurrentPresence,
                        change.ChangedPresence
                    );
                }
                else
                {
                    onLeave?.Invoke(
                        change.Key,
                        change.CurrentPresence,
                        change.ChangedPresence
                    );
                }
            }
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
            OnJoinDelegate? onJoin = null,
            OnLeaveDelegate? onLeave = null
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
                if (found && currentPresence != null)
                {
                    var newRefs = newPresence.Metas.Select(m => m.PhxRef).ToList();
                    var curRefs = currentPresence.Metas.Select(m => m.PhxRef).ToList();
                    var joinedMetas = newPresence.Metas.Where(m => curRefs.IndexOf(m.PhxRef) < 0).ToList();
                    var leftMetas = currentPresence.Metas.Where(m => !newRefs.Contains(m.PhxRef)).ToList();
                    if (joinedMetas.Count > 0)
                    {
                        joins[key] = new PresencePayload { Metas = joinedMetas };
                    }

                    if (leftMetas.Count > 0)
                    {
                        leaves[key] = new PresencePayload { Metas = leftMetas };
                    }
                }
                else
                {
                    joins[key] = newPresence;
                }
            }

            var diff = new Diff { Joins = joins, Leaves = leaves };
            return SyncDiff(currentState, diff, onJoin, onLeave);
        }

        /**
         * Used to sync a diff of presence join and leave
         * events from the server, as they happen. Like `syncState`, `syncDiff`
         * accepts optional `onJoin` and `onLeave` callbacks to react to a user
         * joining or leaving from a device.
         */
        public static State SyncDiff(
            State state,
            Diff diff,
            OnJoinDelegate? onJoin = null,
            OnLeaveDelegate? onLeave = null
        )
        {
            var syncedState = new State(state);

            foreach (var key in diff.Joins.Keys)
            {
                var newPresence = diff.Joins[key];
                var found = syncedState.TryGetValue(key, out var currentPresence);
                var syncedPresence = newPresence;
                if (found && currentPresence != null)
                {
                    syncedPresence = new PresencePayload
                    {
                        Metas = new List<PresenceMeta>(newPresence.Metas),
                        Payload = newPresence.Payload
                    };
                    var joinedRefs = syncedPresence.Metas.Select(m => m.PhxRef).ToList();
                    var curMetas = currentPresence.Metas.Where(m => joinedRefs.IndexOf(m.PhxRef) < 0).ToList();
                    syncedPresence.Metas.InsertRange(0, curMetas);
                }

                syncedState[key] = syncedPresence;
                onJoin?.Invoke(key, currentPresence, newPresence);
            }

            foreach (var key in diff.Leaves.Keys)
            {
                var leftPresence = diff.Leaves[key];
                var found = syncedState.TryGetValue(key, out var currentPresence);
                if (!found || currentPresence == null)
                {
                    continue;
                }

                var refsToRemove = leftPresence.Metas.Select(m => m.PhxRef).ToList();
                var filteredMetas = currentPresence.Metas.Where(
                    m => refsToRemove.IndexOf(m.PhxRef) < 0).ToList();

                var newPresence = new PresencePayload
                {
                    Metas = filteredMetas,
                    Payload = currentPresence.Payload
                };
                onLeave?.Invoke(key, newPresence, leftPresence);
                if (newPresence.Metas.Count == 0)
                {
                    syncedState.Remove(key);
                }
                else
                {
                    syncedState[key] = newPresence;
                }
            }

            return syncedState;
        }

        /// <summary>
        /// Waits asynchronously for the initial presence sync to complete.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to cancel the wait operation.</param>
        /// <returns>A task that completes when the initial state has synchronized.</returns>
        /// <remarks>
        /// This method completes immediately when the initial state has already
        /// been synchronized. Otherwise, it subscribes atomically with the
        /// synchronized-state check and completes when the first sync occurs.
        /// </remarks>
        public Task WaitForInitialSyncAsync(CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            OnSyncDelegate handler = () => tcs.TrySetResult(true);

            lock (_stateLock)
            {
                if (_hasSynced)
                {
                    return Task.CompletedTask;
                }

                OnSync += handler;
            }

            CancellationTokenRegistration cancellationRegistration;
            try
            {
                cancellationRegistration = cancellationToken.Register(() =>
                    tcs.TrySetCanceled()
                );
            }
            catch
            {
                RemoveInitialSyncHandler(handler);
                throw;
            }

            return AwaitInitialSyncAndCleanupAsync(
                tcs.Task,
                handler,
                cancellationRegistration
            );
        }

        private async Task AwaitInitialSyncAndCleanupAsync(
            Task<bool> waitTask,
            OnSyncDelegate handler,
            CancellationTokenRegistration cancellationRegistration
        )
        {
            try
            {
                await TaskUtilities
                    .AwaitAndDisposeCancellationRegistrationAsync(
                        waitTask,
                        cancellationRegistration
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                RemoveInitialSyncHandler(handler);
            }
        }

        private void RemoveInitialSyncHandler(OnSyncDelegate handler)
        {
            lock (_stateLock)
            {
                OnSync -= handler;
            }
        }

        /// <summary>
        /// Waits asynchronously for a specific user to appear in presence state.
        /// </summary>
        /// <param name="key">The presence key (typically user ID) to wait for.</param>
        /// <param name="timeout">The maximum time to wait for the user to appear.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the wait operation.</param>
        /// <returns>
        /// A task that completes with the user's presence payload when they appear,
        /// or null if the timeout expires before the user appears.
        /// </returns>
        /// <remarks>
        /// If the user is already present in the state, returns immediately with their presence.
        /// Otherwise, subscribes to OnJoin and waits for the user to join.
        /// </remarks>
        public Task<PresencePayload?> WaitForUserAsync(
            string key,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            var tcs = new TaskCompletionSource<PresencePayload?>(TaskCreationOptions.RunContinuationsAsynchronously);
            OnJoinDelegate handler = (joinedKey, _, newPresence) =>
            {
                if (joinedKey == key)
                {
                    tcs.TrySetResult(newPresence);
                }
            };

            State stateSnapshot;
            lock (_stateLock)
            {
                // Capture the snapshot atomically with subscribing. A join either
                // appears in this snapshot or observes the installed handler.
                OnJoin += handler;
                stateSnapshot = _state;
            }

            if (stateSnapshot.TryGetValue(key, out var existingPresence))
            {
                RemoveUserWaitHandler(handler);
                tcs.TrySetResult(existingPresence);
                return tcs.Task;
            }

            if (tcs.Task.IsCompleted)
            {
                RemoveUserWaitHandler(handler);
                return tcs.Task;
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenRegistration timeoutRegistration = default;
            CancellationTokenRegistration cancellationRegistration = default;
            try
            {
                timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(timeout);
                timeoutRegistration = timeoutCts.Token.Register(() =>
                    tcs.TrySetResult(null)
                );

                cancellationRegistration = cancellationToken.Register(() =>
                    tcs.TrySetCanceled()
                );
            }
            catch
            {
                RemoveUserWaitHandler(handler);
                timeoutRegistration.Dispose();
                cancellationRegistration.Dispose();
                timeoutCts?.Dispose();
                throw;
            }

            return AwaitUserAndCleanupAsync(
                tcs.Task,
                handler,
                timeoutCts!,
                timeoutRegistration,
                cancellationRegistration
            );
        }

        private async Task<PresencePayload?> AwaitUserAndCleanupAsync(
            Task<PresencePayload?> waitTask,
            OnJoinDelegate handler,
            CancellationTokenSource timeoutCts,
            CancellationTokenRegistration timeoutRegistration,
            CancellationTokenRegistration cancellationRegistration
        )
        {
            try
            {
                return await TaskUtilities
                    .AwaitAndDisposeCancellationRegistrationAsync(
                        waitTask,
                        cancellationRegistration
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                RemoveUserWaitHandler(handler);
                timeoutRegistration.Dispose();
                timeoutCts.Dispose();
            }
        }

        private void RemoveUserWaitHandler(OnJoinDelegate handler)
        {
            lock (_stateLock)
            {
                OnJoin -= handler;
            }
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
            public State Joins = new State();
            public State Leaves = new State();
        }
    }
}
