using System.Runtime.CompilerServices;
using PurrNet.Modules;
using PurrNet.Packing;
using Unity.Profiling;
using UnityEngine;

[assembly: InternalsVisibleTo("PurrDiction.Tests")]

namespace PurrNet.Prediction
{
    public abstract partial class PredictedIdentity : MonoBehaviour
    {
        [SerializeField, Tooltip(
            "When true (default), this identity's input is forwarded to observers and they run " +
            "Simulate locally — full prediction as today. " +
            "When false, input is NOT forwarded; the server still sends pre-sim state in the existing " +
            "WriteInitialFrameToOthers pass (alongside regular non-event-handler identities), just " +
            "without the paired input. Observers never run Simulate for this identity — input-driven " +
            "side effects inside Simulate will not fire on observers, so express them as state " +
            "transitions and react in UpdateView.")]
        private bool _forwardInput = true;

        public bool forwardInput => _forwardInput;

        [SerializeField, Tooltip(
            "When true (default), observers run Simulate forward to localTick during replay — full prediction as today.\n" +
            "When false, observers skip the forward Simulate loop. The verified-tick Simulate still runs once per " +
            "server frame (with authoritative input) for input-forwarded identities, so input-driven Simulate() side " +
            "effects still fire on observers — just once per verified tick instead of every replay tick. Forward state " +
            "projection between verified ticks is handled by ExtrapolateState. CPU savings on observers scale linearly " +
            "with the replay depth (typically 3–10× the per-tick Simulate cost saved per identity per OnPostTick).\n" +
            "Has no effect when _forwardInput == false (Mode C already skips all observer simulation).")]
        private bool _simulateForward = true;

        public bool simulateForward
        {
            get => _simulateForward;
            set
            {
                if (_simulateForward == value) return;
                _simulateForward = value;
                predictionManager?.RecomputeRoleFor(this);
            }
        }

        public ulong? lastVerifiedTick { get; internal set; }

        public virtual string GetExtraString()
        {
            return string.Empty;
        }

        protected readonly ProfilerMarker simulateMarker;

        protected PredictedIdentity()
        {
            simulateMarker = new ProfilerMarker($"{GetType().Name}.Simulate");
        }

        public PredictionManager predictionManager { get; protected set; }

        /// <summary>
        /// Represents the identifier of the owner associated with this object.
        /// Used to track ownership, enabling control over inputs.
        /// </summary>
        public PlayerID? owner;

        /// <summary>
        /// The unique identifier for this object.
        /// Can be used to identify the object across the network.
        /// </summary>
        public PredictedComponentID id;

        internal bool isFreshSpawn = true;

        internal bool _sleeping;

        public bool isSleeping => _sleeping;

        public void SetSleeping(bool sleeping)
        {
            predictionManager.hierarchy.SetSleeping(id, sleeping);
        }

        /// <summary>
        /// This identity's current SimulateRole on the local PredictionManager. Maintained by
        /// PredictionManager.RecomputeRoleFor — DO NOT mutate directly.
        /// </summary>
        internal PredictionManager.SimulateRole currentRole;

        public virtual bool hasInput => false;

        internal virtual bool isEventHandler => false;

        [UsedByIL]
        public bool IsSimulating()
        {
            return predictionManager.isSimulating;
        }

        public virtual void OnPreSetup() {  }

        internal virtual void OnPrepareSimulationInputs(ulong tick, float delta) {  }

        public virtual void ResetState()
        {
            isServer = false;
            isFreshSpawn = true;
            owner = null;
            id = default;
            OnRemovedFromPool();
        }

        internal void TriggerOnRemovedFromPool()
        {
            OnRemovedFromPool();
        }

        protected virtual void OnRemovedFromPool() {}

        protected virtual void OnAddedToPool() {}

        /// <summary>
        /// Invoked immediately after the object is fully initialized and fresh spawned.
        /// </summary>
        protected virtual void LateAwake() {}

        /// <summary>
        /// Invoked when the object is being despawned and cleaned up.
        /// Allows for any necessary teardown or resource release to be handled.
        /// </summary>
        protected virtual void Destroyed() {}

        internal void TriggerDestroyedEvent()
        {
            Destroyed();
        }

        public bool isServer { get; private set; }

        public SceneID sceneId { get; private set; }

        internal virtual void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            isServer = manager.isServer;
            this.owner = owner;
            this.id = id;

            if (!isFreshSpawn)
                return;

            isFreshSpawn = false;
            Debug.Assert(!(isEventHandler && !_forwardInput),
                $"PurrDiction: identity '{GetType().Name}' cannot be both isEventHandler and state-only " +
                $"(_forwardInput = false). Event handlers depend on observer-side simulation participation; " +
                $"state-only mode skips that participation.");
            predictionManager = world;
            sceneId = world.sceneId;

            BeginInitialModuleSetup();
            try
            {
                ModuleSetup(manager,world,id, owner);
                LateAwake();
            }
            finally
            {
                EndInitialModuleSetup();
            }
        }

        protected virtual void OnDestroy()
        {
            Destroyed();
            TearDownAllModules();

            if (predictionManager)
                predictionManager.UnregisterInstance(this);
        }

        public bool isOwner => IsOwner();

        public bool isController
        {
            get
            {
                if (!predictionManager)
                    return false;

                var player = predictionManager.isSpawned ? predictionManager.localPlayer ?? default : default;
                return IsOwner(player, predictionManager.cachedIsServer);
            }
        }

        public bool IsOwner()
        {
            if (predictionManager && predictionManager.isSpawned && owner == predictionManager.localPlayer)
                return true;
            return false;
        }

        public bool IsOwner(PlayerID player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID? player)
        {
            return owner == player;
        }

        public bool IsOwner(PlayerID player, bool asServer)
        {
            if (owner.HasValue)
            {
                if (owner.Value.isBot)
                    return asServer;
                return owner == player;
            }
            return asServer;
        }

        /// <summary>
        /// True when the per-identity simulate phase should be skipped on this client this tick.
        /// Triggers only for state-only identities on non-owner observers.
        /// </summary>
        internal bool SkipObserverSimulation()
            => !_forwardInput && !predictionManager.cachedIsServer && !isController;

        /// <summary>
        /// True when the per-tick FORWARD Simulate phase should be skipped on this client this tick.
        /// </summary>
        internal bool SkipObserverForwardSimulate()
            => (!_forwardInput || !_simulateForward) && !predictionManager.cachedIsServer && !isController;

        public virtual bool extrapolateState
        {
            get => false;
            set { }
        }

        internal virtual void RunObserverSimulateTick(ulong tick, float delta, ulong lastAuthoritativeTick, bool isVerifiedArrival) { }

        internal abstract void SimulateTick(ulong tick, float delta);

        internal abstract void LateSimulateTick(float delta);

        public virtual void PostSimulate() {}

        internal abstract void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate);

        internal abstract void SaveStateInHistory(ulong tick);

        internal abstract void Rollback(ulong tick);

        public abstract void UpdateRollbackInterpolationState(float delta, bool accumulateError);

        public abstract void ResetInterpolation();

        internal virtual bool IsViewConverged() => true;

        private PlayerID? _lastOwner;

        public virtual bool isDeterministic => false;

        /// <summary>
        /// Called once when owner changes
        /// This is meant to be used for view/visuals only and not part of the simulation
        /// </summary>
        public virtual void OnViewOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner) { }

        internal virtual void UpdateView(float deltaTime)
        {
            if (owner != _lastOwner)
            {
                OnViewOwnerChanged(_lastOwner, owner);
                predictionManager?.RecomputeRoleFor(this);
                _lastOwner = owner;
            }
        }

        internal virtual void LateUpdateView(float deltaTime) { }

        internal abstract void GetLatestUnityState();

        internal abstract void WriteFirstState(ulong tick, BitPacker packer);

        internal abstract bool WriteCurrentState(PlayerID receiver, BitPacker packer, DeltaModule deltaModule, bool reliable);

        internal abstract void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable);

        internal abstract void ReadFirstState(ulong tick, BitPacker packer);

        internal abstract void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule, bool reliable);

        internal abstract void ReadInput(ulong tick, PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable);

        internal abstract void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable);

        public GameObject GetRoot()
        {
            // get the farthest root with a predicted identity
            var current = transform;

            while (current.parent != null)
            {
                if (current.parent.GetComponent<PredictedIdentity>() == null)
                    break;

                current = current.parent;
            }

            return current.gameObject;
        }

        internal void TriggerOnPooledEvent()
        {
            OnAddedToPool();
        }

        public abstract void WriteFirstInput(ulong localTick, BitPacker packer);

        public abstract void ReadFirstInput(ulong localTick, BitPacker packer);

        internal abstract void ClearFuture(ulong stateTick);
    }
}
