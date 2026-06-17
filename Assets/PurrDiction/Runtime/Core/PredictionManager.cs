using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JetBrains.Annotations;
using PurrNet.Logging;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction.Profiler;
using PurrNet.Transports;
using PurrNet.Utils;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrNet.Prediction
{
    [Serializable]
    public struct InputQueueSettings
    {
        public bool extrapolateForMissing;
        public int minInputs;
        public int maxInputs;
    }

    public enum FrameChannelMode : byte
    {
        Reliable,
        Unreliable
    }

    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("PurrDiction/Prediction Manager")]
    public class PredictionManager : NetworkIdentity
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Initialize() => _instances.Clear();

#if UNITY_6000_3_OR_NEWER
        static readonly Dictionary<SceneHandle, PredictionManager> _instances = new ();
#else
        static readonly Dictionary<int, PredictionManager> _instances = new ();
#endif

#if UNITY_6000_3_OR_NEWER
        public static event Action<SceneHandle, PredictionManager> OnInstanceAdded;
#else
        public static event Action<int, PredictionManager> OnInstanceAdded;
#endif

        [SerializeField] private PredictionPhysicsProvider _physicsProvider;
        [SerializeField] private UpdateViewMode _updateViewMode = UpdateViewMode.Update;
        [SerializeField, PurrLock] private BuiltInSystems _builtInSystems =
            BuiltInSystems.Physics3D |
            BuiltInSystems.Physics2D |
            BuiltInSystems.Time |
            BuiltInSystems.Hierarchy |
            BuiltInSystems.Players |
            BuiltInSystems.Random;
        [SerializeField] private PredictedPrefabs _predictedPrefabs;
        [SerializeField, PurrLock] private FrameChannelMode _frameChannelMode = FrameChannelMode.Reliable;
        [SerializeField, PurrLock] private bool _syncDeterministicData = true;
        [SerializeField] private InputQueueSettings _inputQueueSettings = new()
        {
            extrapolateForMissing = true,
            minInputs = 1,
            maxInputs = 2
        };

        [Header("Debugging")]
        [SerializeField] private bool _validateDeterministicData;

        public PredictedPrefabs predictedPrefabs
        {
            get => _predictedPrefabs;
            set
            {
                _predictedPrefabs = value;
                InitPooling();
            }
        }

        public bool validateDeterministicData => _validateDeterministicData;

        public FrameChannelMode frameChannelMode => _frameChannelMode;
        public bool isReliable => _frameChannelMode == FrameChannelMode.Reliable;
        public bool syncDeterministicData => _syncDeterministicData;

        static readonly ProfilerMarker SimulateMarker = new("PredictionManager.Simulate");
        static readonly ProfilerMarker SimulateInputsMarker = new("PredictionManager.PrepareSimulationInputs");
        static readonly ProfilerMarker LateSimulateMarker = new("PredictionManager.LateSimulate");
        static readonly ProfilerMarker UpdateViewMarker = new("PredictionManager.UpdateView");
        static readonly ProfilerMarker SaveHistoryMarker = new("PredictionManager.SaveHistory");
        static readonly ProfilerMarker WriteFrameOnServerMarker = new("PredictionManager.WriteFrameOnServer");

        readonly List<PredictedIdentity> _queue = new ();
        readonly List<PredictedIdentity> _systems = new ();
        private int _systemsCount;

        GameObjectPoolCollection _pools;

        [UsedImplicitly]
#if UNITY_6000_3_OR_NEWER
        public static bool TryGetInstance(SceneHandle sceneHandle, out PredictionManager world)
#else
        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
#endif
        {
            return _instances.TryGetValue(sceneHandle, out world);
        }

        [ContextMenu("Debug/Print all systems")]
        public void PrintAllSystems()
        {
            foreach (var system in _systems)
                Debug.Log(system, system);
        }

        private uint _sessionSeed;

        /// <summary>
        /// The session seed for this prediction manager instance.
        /// This is randomly generated on Awake and is used to seed any predicted random number generators.
        ///
        /// </summary>
        public uint sessionSeed => _sessionSeed;

        private void Awake()
        {
            var sceneHandle = gameObject.scene.handle;
            _instances[sceneHandle] = this;
            OnInstanceAdded?.Invoke(sceneHandle, this);
            _sessionSeed = (uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue);

#if UNITY_PHYSICS_2D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
                Physics2D.simulationMode = SimulationMode2D.Script;
#endif
#if UNITY_PHYSICS_3D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
                Physics.simulationMode = SimulationMode.Script;
#endif
            InitPooling();
        }

        [ServerRpc(requireOwnership: false)]
        public Task ClientRequestedToBeObserver(PredictedComponentID component, RPCInfo info = default)
        {
            if (component.TryGetIdentity<PredictedIdentitySpawner>(this, out var pidSpawner))
                pidSpawner.ClientRequestedToBeObserver(info.sender);
            return Task.CompletedTask;
        }

        private GameObject _poolParent;

        private void InitPooling()
        {
            if (!_predictedPrefabs)
                return;

            if (!_poolParent)
            {
                _poolParent = new GameObject("PooledPrefabs");
                SceneManager.MoveGameObjectToScene(_poolParent, gameObject.scene);
#if !PURRNET_DEBUG_POOLING
                _poolParent.hideFlags = HideFlags.HideAndDontSave;
#endif
                _poolParent.SetActive(false);
            }

            _pools ??= new GameObjectPoolCollection(_poolParent.transform);
            for (var i = 0; i < _predictedPrefabs.prefabs.Count; i++)
            {
                var prefab = _predictedPrefabs.prefabs[i];
                if (prefab.pooled)
                    _pools.Register(prefab.prefab, prefab.warmupCount);
            }
        }

        public float tickDelta { get; private set; }

        public int tickRate { get; private set; }

        public ulong localTick { get; private set; } = 1;

        [UsedImplicitly]
        public ulong localTickInContext { get; private set; } = 1;

        public PredictedHierarchy hierarchy { get; private set; }

        public PredictedPlayers players { get; private set; }

        internal Predicted3DPhysics physics3d { get; private set; }

        internal Predicted2DPhysics physics2d { get; private set; }

        public PredictedTime time { get; private set; }

        public PredictedRandomSystem random { get; private set; }

        private DeltaModule _deltaModuleState;

        bool ShouldRegisterSystem(BuiltInSystems system)
        {
            return (_builtInSystems & system) != 0;
        }

        protected override void OnEarlySpawn()
        {
            var deltaModule = networkManager.GetModule<DeltaModule>(isServer);
            _deltaModuleState = deltaModule;

            RegisterScene();

            tickRate = networkManager.tickModule.tickRate;
            tickDelta = 1f / tickRate;

            hierarchy = ShouldRegisterSystem(BuiltInSystems.Hierarchy) ? RegisterSystem<PredictedHierarchy>() : null;
            players = ShouldRegisterSystem(BuiltInSystems.Players) ? RegisterSystem<PredictedPlayers>() : null;
            physics3d = ShouldRegisterSystem(BuiltInSystems.Physics3D) ? RegisterSystem<Predicted3DPhysics>() : null;
            physics2d = ShouldRegisterSystem(BuiltInSystems.Physics2D) ? RegisterSystem<Predicted2DPhysics>() : null;
            time = ShouldRegisterSystem(BuiltInSystems.Time) ? RegisterSystem<PredictedTime>() : null;
            random = ShouldRegisterSystem(BuiltInSystems.Random) ? RegisterSystem<PredictedRandomSystem>() : null;

            var roots = HashSetPool<GameObject>.Instantiate();
            var pid = -1;

            if (hierarchy)
            {
                for (var i = 0; i < _queue.Count; i++)
                {
                    var queued = _queue[i];
                    var root = queued.GetRoot();

                    if (roots.Add(root))
                    {
                        if (!_poolParent || root.transform.root != _poolParent.transform)
                            hierarchy.RegisterSceneObject(root, pid--);
                    }
                }
            }

            HashSetPool<GameObject>.Destroy(roots);

            _queue.Clear();

            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0 ||
                (_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
            {
                Time.fixedDeltaTime = tickDelta;
            }
        }

        private void RegisterScene()
        {
            var identities = ListPool<PredictedIdentity>.Instantiate();

#if HAS_DISCOVERY_RULE
            SceneObjectsModule.GetScenePredictedIdentities(gameObject.scene, identities, networkManager.networkRules.ShouldIncludeInstantiatedSceneObjects());
#else
            SceneObjectsModule.GetScenePredictedIdentities(gameObject.scene, identities);
#endif

            int count = identities.Count;
            for (var i = 0; i < count; ++i)
            {
                var pid = identities[i];
                _queue.Add(pid);
            }
            ListPool<PredictedIdentity>.Destroy(identities);
        }

        private TickManager _tickManager;

        protected override void OnSpawned()
        {
            _tickManager = networkManager.tickModule;
            _tickManager.onPreTick += OnPreTick;
            _tickManager.onPostTick += OnPostTick;
        }

        protected override void OnDespawned()
        {
            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
                _tickManager = null;
            }

            CleanupAllSystems();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();

            for (var i = 0; i < _fragmentPackersInFlight.Count; i++)
                _fragmentPackersInFlight[i].Dispose();
            _fragmentPackersInFlight.Clear();
            _fragments.Clear();

            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
            }

            if (_pools != null)
            {
                _pools.Dispose();
                _pools = null;
            }
        }

        private void CleanupAllSystems()
        {
            if (hierarchy)
                hierarchy.Cleanup();

            for (var i = _systemsCount - 1; i >= 0; i--)
            {
                if (_systems[i])
                    Destroy(_systems[i]);
            }

            _instanceMap.Clear();
            _queue.Clear();
            _systems.Clear();
            _systemsCount = 0;
            _nextSystemId = 0;
            _clientTicks.Clear();

            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();

            for (var i = 0; i < _fragmentPackersInFlight.Count; i++)
                _fragmentPackersInFlight[i].Dispose();
            _fragmentPackersInFlight.Clear();

            localTick = 1;
            _lastVerifiedTick = 1;
            localTickInContext = 1;
            _fragments.Clear();
        }

        private uint _nextSystemId = 0;

        public T RegisterSystem<T>() where T : PredictedIdentity
        {
            var system = gameObject.AddComponent<T>();
            system.hideFlags = HideFlags.NotEditable;
            if (cachedIsServer)
                system.OnPreSetup();
            RegisterInstance(system, new PredictedObjectID(1), _nextSystemId++, null);
            return system;
        }

        public void RegisterInstance(GameObject go, PredictedObjectID objectID, PlayerID? owner, bool reset, bool triggedOnRemovedFromPool)
        {
            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);
            int count = components.Count;

            for (uint i = 0; i < count; i++)
            {
                var component = components[(int)i];

                if (!_systems.Contains(component))
                {
                    component.OnPreSetup();
                    if (reset)
                         component.ResetState();
                    if (triggedOnRemovedFromPool)
                        component.TriggerOnRemovedFromPool();
                    RegisterInstance(component, objectID, i, owner);
                }
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        public void UnregisterInstance(GameObject go, bool reset, bool destroyEvent)
        {
            if (!go)
                return;

            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i].hideFlags != HideFlags.NotEditable)
                {
                    if (reset)
                        components[i].ResetState();
                    UnregisterInstance(components[i]);
                    if (destroyEvent)
                        components[i].TriggerDestroyedEvent();
                }
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        public void UnregisterPooledInstance(GameObject go)
        {
            if (!go) return;

            var components = ListPool<PredictedIdentity>.Instantiate();
            go.GetComponentsInChildren(true, components);

            for (var i = 0; i < components.Count; i++)
            {
                UnregisterInstance(components[i]);
                components[i].TriggerDestroyedEvent();
                components[i].TriggerOnPooledEvent();
            }

            ListPool<PredictedIdentity>.Destroy(components);
        }

        readonly Dictionary<PredictedComponentID, PredictedIdentity> _instanceMap = new ();

        public bool TryGetIdentity(PredictedComponentID id, out PredictedIdentity instance)
        {
            return _instanceMap.TryGetValue(id, out instance);
        }

        public PredictedIdentity GetIdentity(PredictedComponentID id)
        {
            return _instanceMap.GetValueOrDefault(id);
        }

        private void RegisterInstance(PredictedIdentity system, PredictedObjectID objectId, uint componentId, PlayerID? owner)
        {
            if (!isSpawned)
            {
                _queue.Add(system);
                return;
            }

            var pid = new PredictedComponentID(objectId, componentId);
            _instanceMap[pid] = system;
            system.Setup(networkManager, this, pid, owner);

            // i want to insert based on objectid first, then componet id such that I can guarantee that the order of the components is preserved
            var myObjId = pid.objectId.instanceId.value;
            int posToInsert = _systemsCount;

            for (int i = 0; i < _systemsCount; i++)
            {
                var curObjId = _systems[i].id.objectId.instanceId.value;
                if (curObjId > myObjId || curObjId == myObjId && _systems[i].id.componentId.value > pid.componentId.value)
                {
                    posToInsert = i;
                    break;
                }
            }

            _systems.Insert(posToInsert, system);
            ++_systemsCount;
        }

        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            _instanceMap.Remove(predictedIdentity.id);
            if (_systems.Remove(predictedIdentity))
                --_systemsCount;
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);
            _pendingFullSync.Remove(player);

            var frames = _clientFrames.Count;
            for (var i = 0; i < frames; i++)
            {
                if (_clientFrames[i].player == player)
                {
                    _clientFrames[i].Dispose();
                    _clientFrames.RemoveAt(i);
                    break;
                }
            }
        }

        protected override void OnPreObserverAdded(PlayerID player)
        {
            if (player == localPlayer || player.isBot)
                return;

            if (localTick == 1)
                OnPreTick();
        }

        readonly List<PlayerID> _pendingFullSync = new ();

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer || player.isBot)
                return;

            _pendingFullSync.Add(player);
        }

        private void FlushPendingFullSyncs()
        {
            var tick = localTick - 1;

            for (var p = 0; p < _pendingFullSync.Count; p++)
            {
                var player = _pendingFullSync[p];

                _clientTicks[player] = new InputQueue();
                _clientFrames.Add(new PlayerPacker
                {
                    player = player,
                    packer = BitPackerPool.Get(),
                    boundaries = new List<int>(256)
                });

                using var frame = BitPackerPool.Get();

                Packer<Size>.Write(frame, _systemsCount);

                for (var i = 0; i < _systemsCount; i++)
                {
                    if (!_systems[i].isEventHandler)
                        _systems[i].RunWriteFirstState(tick, frame);
                }

                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].WriteFirstInput(tick, frame);

                for (var i = 0; i < _systemsCount; i++)
                {
                    if (_systems[i].isEventHandler)
                        _systems[i].RunWriteFirstState(tick, frame);
                }

                SyncFullState(player, tickRate, tickDelta, _sessionSeed, frame);
            }

            _pendingFullSync.Clear();
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SyncFullState([UsedImplicitly] PlayerID target, int tickRate, float delta, uint randomSeed, BitPacker data)
        {
            isSimulating = true;
            _sessionSeed = randomSeed;

            _lastVerifiedTick = 1;

            while (_deltas.Count > 0)
                _deltas.Dequeue().Dispose();

            tickDelta = delta;
            this.tickRate = tickRate;

            Size _count = default;
            Packer<Size>.Read(data, ref _count);
            int count = _count;

            for (var i = 0; i < count; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler)
                    continue;
                system.RunReadFirstState(1, data);
                system.RunRollback(1);
                system.RunResetInterpolation();
            }

            for (var i = 0; i < count; i++)
                _systems[i].ReadFirstInput(1, data);

            for (var i = 0; i < count; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler)
                    system.RunReadFirstState(1, data);
            }

            for (var i = 0; i < count; i++)
            {
                var system = _systems[i];
                system.RunSaveState(1);
                system.lastVerifiedTick = 1;
            }

            SyncTransforms();

            isSimulating = false;

            ReplayToLatestTick(1, true);
        }

        readonly List<PlayerPacker> _clientFrames = new (16);

        // Ids decoded this consume — so the full-rollback pass rolls each identity back exactly once.
        readonly HashSet<PredictedComponentID> _presentIds = new();

        // Fragment packers handed to the unreliable RPC during this server tick. Their bytes must outlive the RPC
        // call until the network flush, so they are freed at the START of the next server tick (ResetAllPackers) —
        // the same lifetime discipline as the reused per-player staging packers (_clientFrames). Server-only.
        readonly List<BitPacker> _fragmentPackersInFlight = new(16);

        // Conservative wire headroom subtracted from the unreliable MTU before packing entries into a fragment:
        // RPC batch/union header (RPCBatch.MAX_HEADER_SIZE) plus our RPC params (player, echoTick, entryCount) and
        // the BitPackerWithLength byte-length prefix. Mirrors NetworkBones' (RPCBatch.MAX_HEADER_SIZE + params)
        // budget; 64 is a safe floor above that sum (budget is computed against uncompressed bytes).
        const int FRAGMENT_HEADROOM_BYTES = 64;

        public bool cachedIsServer { get; private set; }

        private void OnPreTick()
        {
            cachedIsServer = isServer;
            localTickInContext = localTick;

            var myPlayer = isSpawned ? localPlayer ?? default : default;
            var cachedIsClient = isClient;

            isSimulating = true;
            if (cachedIsServer)
                isVerified = true;

            if (cachedIsServer)
                PrepareInputs();

            using var ownedIdentities = DisposableList<PredictedIdentity>.Create(_systemsCount);

            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                if (controller)
                    ownedIdentities.Add(system);
                system.PrepareInput(cachedIsServer, controller, localTick, _inputQueueSettings.extrapolateForMissing);
            }

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (!system.isEventHandler)
                        system.RunSaveState(localTick);
                }
            }

            if (cachedIsServer)
            {
                // Pre-sim: write the non-event-handler entries while their live state is the start-of-tick state
                // (matching their pre-sim RunSaveState above). Resets the per-player packers for this tick.
                using (WriteFrameOnServerMarker.Auto())
                {
                    if (_pendingFullSync.Count > 0)
                        FlushPendingFullSyncs();
                    ResetAllPackers();
                    for (var j = 0; j < _clientFrames.Count; j++)
                        WriteFrameEntries(_clientFrames[j].player, _clientFrames[j].packer, _clientFrames[j].boundaries, eventHandlers: false);
                }
            }

            float delta = this.tickDelta;

            if (time)
                delta *= time.timeScale;

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].OnPrepareSimulationInputs(localTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunSimulateTick(localTick, delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                simulateMarker.Dispose();
            }

            DoPhysicsPass();

            var lateSimulateMarker = LateSimulateMarker.Auto();
            try
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                {
                    var system = _systems[i];
                    if (system.isEventHandler)
                        system.RunSaveState(localTick);
                }
            }

            if (cachedIsServer)
            {
                using (WriteFrameOnServerMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                        _systems[i].lastVerifiedTick = localTick;
                    // Post-sim: append the event-handler entries (their live state is now end-of-tick, matching
                    // their post-sim RunSaveState), then send. Packers were already reset in the pre-sim write block.
                    for (var j = 0; j < _clientFrames.Count; j++)
                        WriteFrameEntries(_clientFrames[j].player, _clientFrames[j].packer, _clientFrames[j].boundaries, eventHandlers: true);
                    SendFrameToOthers();
                }
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].PostSimulate();

            if (cachedIsServer)
                FinalizeTickOnServer(cachedIsClient);
            else FinalizeInputOnClient(ownedIdentities);

            isSimulating = false;

            localTick += 1;
            localTickInContext = localTick;
        }

        private void PrepareInputs()
        {
            foreach (var (player, queue) in _clientTicks)
            {
                if (queue.Count == 0)
                {
                    queue.waitForInput = true;
                    continue;
                }

                if (queue.waitForInput)
                    continue;

                var dequeued = queue.inputQueue.Peek();
                HandleIncomingInput(dequeued.inputPacket, dequeued.count, player);
            }
        }

        private void FinalizeInputOnClient(DisposableList<PredictedIdentity> ownedIdentities)
        {
            const int MTU = 1024;

            using var frame = BitPackerPool.Get();
            uint writtenCount = 0;
            for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.GetLatestUnityState();
            }

            var count = ownedIdentities.Count;
            for (var ownedIdx = 0; ownedIdx < count; ownedIdx++)
            {
                var owned = ownedIdentities[ownedIdx];
                if (owned && owned.hasInput)
                {
                    Packer<PredictedComponentID>.Write(frame, owned.id);
                    owned.WriteInput(localTick, default, frame, _deltaModuleState, false);
                    writtenCount += 1;
                }
            }

            if (frame.positionInBytes >= MTU)
                SendInputToServerReliable(localTick, writtenCount, frame);
            else SendInputToServer(localTick,writtenCount, frame);
        }

        private void FinalizeTickOnServer(bool cachedIsClient)
        {
            if (cachedIsClient)
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                {
                    var system = _systems[systemIdx];
                    system.GetLatestUnityState();
                    system.RunUpdateRollbackInterpolation(tickDelta, false);
                }
            }
            else
            {
                for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
                    _systems[systemIdx].GetLatestUnityState();
            }
        }

        private void ResetAllPackers()
        {
            // Free last tick's fragment packers now that the network has flushed them.
            for (var i = 0; i < _fragmentPackersInFlight.Count; i++)
                _fragmentPackersInFlight[i].Dispose();
            _fragmentPackersInFlight.Clear();

            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var packer = _clientFrames[i];
                packer.packer.ResetPositionAndMode(false);
                packer.boundaries.Clear();
                packer.boundaries.Add(0); // start of entry 0 (packer position is 0 after reset)
            }
        }

        // Writes [ (PredictedComponentID id, payload) ] for one group into `player`'s `frame`, appending one bit
        // boundary per entry to `boundaries`. After both write phases boundaries.Count - 1 == the total entry count
        // (boundaries is seeded with [0] in ResetAllPackers) — the single source of truth used by both the reliable
        // send and the fragment slicer. payload = WriteCurrentState bits, then WriteInput bits.
        //
        // Skips isDeterministic systems (client-reconstructed — they roll back from local history) UNLESS determinism
        // validation is on, in which case their state is written so the client can compare it against its prediction.
        // Note: when frameChannelMode==Unreliable && syncDeterministicData, isDeterministic is false (syncData==true),
        // so those systems ARE written and delta-packed normally. Deterministic WriteInput is a no-op, so skipping a
        // deterministic system never drops input.
        //
        // The non-event-handler group is written PRE-simulation and the event-handler group POST-simulation, to match
        // each group's RunSaveState phase (non-event-handlers save start-of-tick state pre-sim; event-handlers save
        // end-of-tick state post-sim). Writing both post-sim would put start-of-tick systems on the wire as their
        // end-of-tick state, shifting the client's tick by one. The split is load-bearing for tick alignment.
        private void WriteFrameEntries(PlayerID player, BitPacker frame, List<int> boundaries, bool eventHandlers)
        {
            for (var i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system.isEventHandler != eventHandlers)
                    continue;
                if (system.isDeterministic && !_validateDeterministicData)
                    continue;

                Packer<PredictedComponentID>.Write(frame, system.id);
                system.RunWriteCurrentState(player, frame, _deltaModuleState, isReliable);
                system.WriteInput(localTick, player, frame, _deltaModuleState, isReliable);

                boundaries.Add(frame.positionInBits); // end of this entry / start of next
            }
        }

        private void SendFrameToOthers()
        {
            var fCount = _clientFrames.Count;
            for (var j = 0; j < fCount; j++)
            {
                var player = _clientFrames[j].player;
                var packer = _clientFrames[j].packer;
                var boundaries = _clientFrames[j].boundaries;

                ulong echoTick = 0;
                if (_clientTicks.TryGetValue(player, out var queue) && queue.Count > 0 && !queue.waitForInput)
                {
                    var dequeued = queue.inputQueue.Dequeue();
                    echoTick = dequeued.clientTick;
                    dequeued.inputPacket.Dispose();
                }

                if (isReliable)
                {
                    // ReliableOrdered is transport-fragmented; send the whole frame as one RPC. boundaries.Count - 1
                    // is the entry count (seeded with [0], one element appended per written entry across both phases).
                    uint entryCount = (uint)(boundaries.Count - 1);
                    SendFrameToRemoteReliable(player, echoTick, entryCount, new BitPackerWithLength(packer.ToByteData().length, packer));
                }
                else
                {
                    // Plain Unreliable is MTU-bounded; split into independently-applied fragments.
                    SendFragments(player, packer, boundaries, echoTick);
                }
            }
        }

        // Greedily packs ascending-id entries from `source` into MTU-sized fragments, one unreliable RPC each.
        // Each fragment is a self-contained [ (id, payload) ]×count mini-frame applied independently on the client.
        private void SendFragments(PlayerID player, BitPacker source, List<int> boundaries, ulong echoTick)
        {
            int entries = boundaries.Count - 1;
            if (entries <= 0) { SendOneFragment(player, echoTick, 0, BitPackerPool.Get()); return; } // empty keepalive (advances the tick)

            // GetMTU returns BYTES for the unreliable channel. The frame sender is the server → asServer = cachedIsServer.
            // Loopback/local connections (host self-send, LocalTransport / PurrTransportLayer loopback) report int.MaxValue,
            // and (int.MaxValue - 64) * 8 overflows int32 to a NEGATIVE budget (-520) — every entry then looks "oversized",
            // spamming the error and force-sending each identity as its own fragment. Do the * 8 in long and clamp to
            // int.MaxValue: real MTUs are unchanged, loopback gets an effectively unbounded budget (whole frame → one fragment).
            int mtuBytes = networkManager.GetMTU(player, Channel.Unreliable, cachedIsServer);
            int budgetBits = (int)Math.Min((long)Math.Max(256, mtuBytes - FRAGMENT_HEADROOM_BYTES) * 8, int.MaxValue);

            var frag = BitPackerPool.Get();
            int fragStartEntry = 0;

            for (int k = 0; k < entries; k++)
            {
                int entryBits = boundaries[k + 1] - boundaries[k];

                // One entry larger than the budget cannot be sub-fragmented. Isolate it in its OWN fragment (flush
                // whatever precedes it first) and log: the transport will drop this over-MTU fragment, so that identity
                // holds and recovers via the ack-gated re-send. NEVER blit a partial entry, and never let an oversized
                // entry corrupt the alignment of others.
                if (entryBits > budgetBits)
                {
                    Debug.LogError($"[Fragment] entry {k} = {entryBits} bits > budget {budgetBits}; per-identity sub-fragmentation not implemented. This entry's fragment will be dropped by the transport until it shrinks.", this);
                    if (frag.positionInBits > 0) { SendOneFragment(player, echoTick, (uint)(k - fragStartEntry), frag); frag = BitPackerPool.Get(); }
                    var bigSlice = new BitData(source, boundaries[k], entryBits);
                    frag.WriteBitDataWithoutConsumingIt(bigSlice);
                    SendOneFragment(player, echoTick, 1, frag);
                    frag = BitPackerPool.Get();
                    fragStartEntry = k + 1;
                    continue;
                }

                if (frag.positionInBits > 0 && frag.positionInBits + entryBits > budgetBits)
                {
                    SendOneFragment(player, echoTick, (uint)(k - fragStartEntry), frag);
                    frag = BitPackerPool.Get();
                    fragStartEntry = k;
                }

                var slice = new BitData(source, boundaries[k], entryBits);
                frag.WriteBitDataWithoutConsumingIt(slice); // copies bits; does not move source's cursor
            }

            if (frag.positionInBits > 0) SendOneFragment(player, echoTick, (uint)(entries - fragStartEntry), frag);
            else frag.Dispose();
        }

        // Sends one fragment and registers its packer for end-of-tick disposal (its bytes must live until the flush).
        private void SendOneFragment(PlayerID player, ulong echoTick, uint entryCount, BitPacker frag)
        {
            SendFrameToRemoteUnreliable(player, echoTick, entryCount, new BitPackerWithLength(frag.ToByteData().length, frag));
            _fragmentPackersInFlight.Add(frag);
        }

        /// <summary>
        /// Is the prediction manager currently replaying a frame?
        /// </summary>
        [UsedImplicitly]
        public bool isReplaying { get; private set; }

        /// <summary>
        /// Is the prediction manager currently replaying a verified frame?
        /// </summary>
        [UsedImplicitly]
        public bool isVerified { get; private set; }

        public bool isVerifiedAndReplaying
        {
            get => isVerified && isReplaying;
        }

        /// <summary>
        /// True while the simulation is re-running an already-verified tick purely to rebuild
        /// state (client catch-up before a jumped or in-place frame, server full-state rebuild
        /// when a new observer joins). Deterministic simulation code — including physics event
        /// handlers that mutate predicted state — still runs and must not be skipped.
        /// Gate one-shot side effects (VFX, SFX, scoring, notifications) on this flag to avoid
        /// reacting twice to the same tick.
        /// </summary>
        public bool isCatchingUpFrames { get; private set; }

        /// <summary>
        /// True when one-shot, user-facing reactions (VFX, SFX, scoring, UI) should run for
        /// the tick being simulated: the tick is verified and this is its first delivery,
        /// not a state-rebuilding catch-up pass. Prefer this over checking
        /// <see cref="isVerified"/> directly for visual/audio feedback.
        /// </summary>
        public bool isVerifiedView => isVerified && !isCatchingUpFrames;


        /// <summary>
        /// Is the prediction manager currently simulating a frame?
        /// This includes replaying frames.
        /// If this is false nothing should act on the state of the game and expect it to be correct.
        /// </summary>
        [UsedImplicitly]
        public bool isSimulating
        {
            get; private set;
        }

        /// <summary>
        /// True if the prediction manager is currently in the physics pass.
        /// </summary>
        [UsedImplicitly]
        public bool isInPhysicsPass
        {
            get; private set;
        }

        /// <summary>
        /// Invoked immediately before PurrDiction simulates its configured physics scenes.
        /// This is also fired during resimulation after a rollback, before each replayed physics pass.
        /// This occurs after <see>
        ///     <cref>PredictedIdentity.Simulate()</cref>
        /// </see>
        /// and before
        /// <see>
        ///     <cref>PredictedIdentity.LateSimulate()</cref>
        /// </see>
        /// .
        /// </summary>
        public event Action onBeforePhysicsPass;

        /// <summary>
        /// Invoked immediately after PurrDiction simulates its configured physics scenes.
        /// This is also fired during resimulation after a rollback, after each replayed physics pass.
        /// This occurs after <see>
        ///     <cref>PredictedIdentity.Simulate()</cref>
        /// </see>
        /// and before
        /// <see>
        ///     <cref>PredictedIdentity.LateSimulate()</cref>
        /// </see>
        /// .
        /// </summary>
        public event Action onAfterPhysicsPass;

        private void DoPhysicsPass()
        {
            // ReSharper disable once NotAccessedVariable
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            try
            {
                onBeforePhysicsPass?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            isInPhysicsPass = true;
            try
            {
#if UNITY_PHYSICS_2D
                if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
                {
                    var physicsScene = gameObject.scene.GetPhysicsScene2D();
                    if (physicsScene.IsValid())
                        physicsScene.Simulate(delta);
                }
#endif
#if UNITY_PHYSICS_3D
                if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
                {
                    var physicsScene = gameObject.scene.GetPhysicsScene();
                    if (physicsScene.IsValid())
                        physicsScene.Simulate(delta);
                }
#endif
            }
            finally
            {
                isInPhysicsPass = false;

                try
                {
                    onAfterPhysicsPass?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // Arrived fragments grouped by serverEchoTick, decoded at consume (replaces the single-packet _deltas queue).
        readonly FragmentBuffer _fragments = new();

        [TargetRpc(compressionLevel: CompressionLevel.Best, channel: Channel.ReliableOrdered)]
        private void SendFrameToRemoteReliable([UsedImplicitly] PlayerID player, ulong echoTick, uint entryCount, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);
            IngestFragment(echoTick, entryCount, delta.packer);
        }

        // CompressionLevel.Best (matches the reliable RPC). Compression in PreProcessRpc (RPCModule) runs BEFORE
        // batching/MTU-check, so the transport sees the COMPRESSED size, which is <= our uncompressed SendFragments
        // budget < MTU. compressionLevel is also a compile-time-SYMMETRIC decision (PostProcessRpc decompresses iff
        // level != None), so both peers must agree — keeping Best matches the reliable path and avoids a mismatch.
        [TargetRpc(compressionLevel: CompressionLevel.Best, channel: Channel.Unreliable)]
        private void SendFrameToRemoteUnreliable([UsedImplicitly] PlayerID player, ulong echoTick, uint entryCount, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);
            IngestFragment(echoTick, entryCount, delta.packer);
        }

        // Buffers one arrived fragment under its serverEchoTick. Both channels funnel through here so the consume
        // path is unified (reliable = one whole-frame "fragment"; unreliable = several). Peeks the first id so the
        // buffer can order fragments hierarchy-first. Decode happens later, at consume (OnPostTick) — never here.
        private void IngestFragment(ulong echoTick, uint entryCount, BitPacker packer)
        {
            // Stale-fragment discard: plain Unreliable can reorder, so a late fragment for an already-verified tick
            // must not regress state. echoTick<=1 is the in-place sentinel (see OnPostTick), never stale.
            if (echoTick > 1 && echoTick <= _lastVerifiedTick)
            {
                packer.Dispose();
                return;
            }

            PredictedComponentID firstId = default;
            if (entryCount > 0)
            {
                packer.ResetPositionAndMode(true);
                Packer<PredictedComponentID>.Read(packer, ref firstId);
                packer.ResetPositionAndMode(true); // rewind for the real decode at consume
            }

            _fragments.Add(echoTick, new ArrivedFragment { packer = packer, entryCount = entryCount, firstId = firstId });
        }

        private void RollbackToFrame(ulong stateTick)
        {
            for (var i = 0; i < _systemsCount; i++)
                _systems[i].RunRollback(stateTick);
            SyncTransforms();
        }

        // Decodes entryCount (id, payload) entries from `frame` in ascending id order. State read at stateTick,
        // input at inputTick (matching the old RollbackToFrame param split). Rolls each present identity back so the
        // hierarchy (lowest id) spawns before dependents resolve. Records present ids. Aborts the rest on an
        // unconfirmed id (id absent from _instanceMap == not server-confirmed; no length prefix means we cannot skip it).
        private void ReadFrameEntries(BitPacker frame, uint entryCount, ulong stateTick, ulong inputTick)
        {
            for (uint e = 0; e < entryCount; e++)
            {
                PredictedComponentID id = default;
                Packer<PredictedComponentID>.Read(frame, ref id);

                if (!_instanceMap.TryGetValue(id, out var system))
                    return; // unconfirmed/lost-spawn id — cannot size the remaining payloads; stop.

                if (_validateDeterministicData && system.isDeterministic)
                    system.RunRollback(stateTick);
                system.RunClearFuture(stateTick);
                system.RunReadState(stateTick, frame, _deltaModuleState, isReliable);
                system.RunRollback(stateTick);
                system.lastVerifiedTick = stateTick;
                _presentIds.Add(id);

                system.ReadInput(inputTick, default, frame, _deltaModuleState, isReliable);
            }
        }

        // Decodes one tick's buffered fragments (ascending by firstId → global ascending id order, hierarchy first),
        // then rolls every absent identity to stateTick from history. Present identities were already rolled back
        // during decode, so they are skipped — keeping exactly one rollback per identity per tick.
        //
        // HIERARCHY-GATED DECODE. Per-entry decode self-aligns only when the spawn/hierarchy set the entries were
        // written against matches the client's. The hierarchy entry (globally lowest id, decoded first) carries the
        // spawn/despawn/reparent for this tick. On the unreliable path the hierarchy rides its own fragment which can
        // be DROPPED while a higher-id fragment survives; decoding that survivor against a stale hierarchy could apply
        // state to an identity that should have despawned, or hit an id not yet in _instanceMap. So if the hierarchy
        // entry did not arrive this tick we DECODE NOTHING and hold every identity at its last state (the design's
        // hold-on-loss fallback); the dropped fragments recover next tick via the ack-gated re-send. Reliable mode
        // always carries the whole frame as one hierarchy-first fragment, so this never trips there.
        //
        // TODO(wire-hierarchy-confirmation): holding the whole tick is conservative — it discards surviving fragments
        // that would have decoded fine. A future wire format could carry a per-entry length prefix so a survivor can
        // be skipped by size without needing the hierarchy, or the verified spawn-set delta on the wire.
        private void ReadFragmentsIntoSystems(List<ArrivedFragment> group, ulong stateTick, ulong inputTick)
        {
            _presentIds.Clear();

            // Decode only if the hierarchy entry arrived this tick (it sorts to a fragment whose firstId == its id;
            // a 0-entry keepalive may sort ahead of it, so scan rather than peek group[0]). If the hierarchy is not
            // a registered system at all, we cannot gate — fall back to decoding as before.
            bool hierarchyPresent = hierarchy == null;
            if (!hierarchyPresent)
            {
                var hierarchyId = hierarchy.id;
                for (int g = 0; g < group.Count; g++)
                    if (group[g].entryCount > 0 && group[g].firstId.Equals(hierarchyId)) { hierarchyPresent = true; break; }
            }

            if (hierarchyPresent)
            {
                for (int g = 0; g < group.Count; g++)
                {
                    var frag = group[g];
                    frag.packer.ResetPositionAndMode(true);
                    ReadFrameEntries(frag.packer, frag.entryCount, stateTick, inputTick);
                }
            }

            // Absent identities (deterministic/client-reconstructed, a lost fragment, aborted past an unknown id, or —
            // when the hierarchy fragment was lost — the entire tick) got no authoritative data this tick → roll to
            // stateTick from history (ReadOrPrevious holds last state).
            // TODO(extrapolation): a lost-fragment identity holds its last state here; revisit to extrapolate.
            for (var i = 0; i < _systemsCount; i++)
            {
                var sys = _systems[i];
                if (_presentIds.Contains(sys.id)) continue;
                sys.RunRollback(stateTick);
                // Deterministic systems are absent BY DESIGN (skipped on write, reconstructed from local history),
                // so they are verified-by-reconstruction at stateTick — advance lastVerifiedTick to keep their
                // view-layer `verifiedState` current, matching this repo's pre-fragmentation decode (which set it for
                // every decoded system). A genuinely LOST-fragment (non-deterministic) system is intentionally NOT
                // marked verified here — we received no authoritative data for it this tick.
                if (sys.isDeterministic)
                    sys.lastVerifiedTick = stateTick;
            }
            SyncTransforms();
        }

        private void SyncTransforms()
        {
#if UNITY_PHYSICS_2D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics2D) != 0)
                Physics2D.SyncTransforms();
#endif
#if UNITY_PHYSICS_3D
            if ((_physicsProvider & PredictionPhysicsProvider.UnityPhysics3D) != 0)
                Physics.SyncTransforms();
#endif
        }

        public event Action onStartingToRollback;
        public event Action onRollbackFinished;

        private ulong _lastVerifiedTick = 1;

        private void OnPostTick()
        {
            if (cachedIsServer || _fragments.TickCount == 0 || localTick <= _lastVerifiedTick)
            {
                if (isClient)
                    UpdateInterpolation(false);
                TickBandwidthProfiler.MarkEndOfTick();
                return;
            }

            onStartingToRollback?.Invoke();
            UpdateInterpolation(false);

            isSimulating = true;
            isReplaying = true;

            while (_fragments.TickCount > 0)
            {
                if (!_fragments.TryPeekOldestTick(out var echoTick))
                    break;
                var group = _fragments.TakeTick(echoTick);

                isVerified = true;
                bool inPlace = echoTick <= 1;
                var lastTick = _lastVerifiedTick;
                if (!inPlace)
                    _lastVerifiedTick = echoTick;

                ulong verifiedTick = _lastVerifiedTick;
                bool isJump = verifiedTick - lastTick > 1;

                var inPlaceTick = isJump ? lastTick : verifiedTick;

                if (inPlace || isJump)
                {
                    isCatchingUpFrames = true;
                    RollbackToFrame(inPlaceTick);
                    SimulateFrameInPlace(inPlaceTick);
                    SimulateFrame(inPlaceTick, true);
                    isCatchingUpFrames = false;
                }

                try
                {
                    ReadFragmentsIntoSystems(group, inPlaceTick, verifiedTick);
                    SimulateFrame(verifiedTick, true);
                    isVerified = false;
                }
                finally
                {
                    for (int g = 0; g < group.Count; g++)
                        group[g].Dispose();
                    _fragments.Recycle(group);   // return the per-tick list to the pool (no per-tick GC)
                }
            }

            SimulateFrame(_lastVerifiedTick + 1, true);
            ReplayToLatestTick(_lastVerifiedTick + 2, false);

            SyncTransforms();
            UpdateInterpolation(true);

            isReplaying = false;
            isSimulating = false;

            TickBandwidthProfiler.MarkEndOfTick();
            onRollbackFinished?.Invoke();
        }

        private void UpdateInterpolation(bool accumulateError)
        {
            for (var j = 0; j < _systemsCount; j++)
                _systems[j].RunUpdateRollbackInterpolation(tickDelta, accumulateError);
        }

        private void ReplayToLatestTick(ulong verifiedTick, bool saveState)
        {
            for (ulong simTick = verifiedTick; simTick < localTick; simTick++)
                SimulateFrame(simTick, saveState);
        }

        private void SimulateFrameInPlace(ulong verifiedTick)
        {
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            isSimulating = true;
            localTickInContext = verifiedTick;

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].OnPrepareSimulationInputs(verifiedTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunSimulateTick(verifiedTick, delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                simulateMarker.Dispose();
            }

            DoPhysicsPass();

            var lateSimulateMarker = LateSimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].PostSimulate();
            for (var j = 0; j < _systemsCount; j++)
                _systems[j].GetLatestUnityState();

            isSimulating = false;
            localTickInContext = localTick;
        }

        private void SimulateFrame(ulong verifiedTick, bool saveState)
        {
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            isSimulating = true;
            localTickInContext = verifiedTick;

            if (saveState)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                    {
                        var system = _systems[i];
                        if (!system.isEventHandler)
                            system.RunSaveState(verifiedTick);
                    }
                }
            }

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].OnPrepareSimulationInputs(verifiedTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunSimulateTick(verifiedTick, delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                simulateMarker.Dispose();
            }

            DoPhysicsPass();

            var lateSimulateMarker = LateSimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _systemsCount; j++)
                    _systems[j].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }

            if (saveState)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _systemsCount; i++)
                    {
                        var system = _systems[i];
                        if (system.isEventHandler)
                            system.RunSaveState(verifiedTick);
                    }
                }
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].PostSimulate();

            for (var j = 0; j < _systemsCount; j++)
                _systems[j].GetLatestUnityState();

            isSimulating = false;
            localTickInContext = localTick;
        }

        public struct InputQueueValue
        {
            public PackedUInt count;
            public BitPacker inputPacket;
            public ulong clientTick;
        }

        public class InputQueue
        {
            public bool waitForInput;
            public readonly Queue<InputQueueValue> inputQueue = new ();
            public int Count => inputQueue.Count;
        }

        readonly Dictionary<PlayerID, InputQueue> _clientTicks = new ();

        [ServerRpc(requireOwnership: false)]
        private void SendInputToServerReliable(ulong tick, PackedUInt count, BitPacker inputPacket, RPCInfo info = default)
        {
            ReceivedInput(tick, count, inputPacket, info);
        }

        [ServerRpc(requireOwnership: false, channel: Channel.UnreliableSequenced)]
        private void SendInputToServer(ulong tick, PackedUInt count, BitPacker inputPacket, RPCInfo info = default)
        {
            ReceivedInput(tick, count, inputPacket, info);
        }

        private void ReceivedInput(ulong tick, PackedUInt count, BitPacker inputPacket, RPCInfo info)
        {
            if (!_clientTicks.TryGetValue(info.sender, out var ticks))
            {
                ticks = new InputQueue
                {
                    waitForInput = true
                };
                _clientTicks[info.sender] = ticks;
            }

            // if we are past the max inputs, let's remove until we are at the min inputs
            if (ticks.Count > _inputQueueSettings.maxInputs)
            {
                while (ticks.Count > _inputQueueSettings.minInputs)
                {
                    var oldInput = ticks.inputQueue.Dequeue();
                    oldInput.inputPacket.Dispose();
                }
            }

            ticks.inputQueue.Enqueue(new InputQueueValue
            {
                count = count,
                inputPacket = inputPacket,
                clientTick = tick
            });

            if (ticks.waitForInput && ticks.inputQueue.Count >= _inputQueueSettings.minInputs)
                ticks.waitForInput = false;
        }

        private void HandleIncomingInput(BitPacker inputPacket, PackedUInt count, PlayerID sender)
        {
            try
            {
                bool senderIsServer = sender == default;

                for (var i = 0; i < count; i++)
                {
                    PredictedComponentID pid = default;
                    Packer<PredictedComponentID>.Read(inputPacket, ref pid);

                    if (_instanceMap.TryGetValue(pid, out var system) && system.IsOwner(sender, senderIsServer))
                    {
                        system.QueueInput(inputPacket, sender, _deltaModuleState, false);
                    }
                    else break;
                }
            }
            catch
            {
                // ignored
            }
        }

        private void Update()
        {
            if (_updateViewMode != UpdateViewMode.Update)
                return;

            if (!isClient)
                return;

            UpdateView();
        }

        private void LateUpdate()
        {
            if (_updateViewMode != UpdateViewMode.LateUpdate)
                return;

            if (!isClient)
                return;

            UpdateView();
        }

        private void UpdateView()
        {
            var updateViewMarker = UpdateViewMarker.Auto();
            try
            {
                var dt = Time.unscaledDeltaTime;
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunUpdateView(dt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                updateViewMarker.Dispose();
            }

            LateUpdateView();
        }

        private void LateUpdateView()
        {
            var lateUpdateViewMarker = UpdateViewMarker.Auto();
            try
            {
                var dt = Time.unscaledDeltaTime;
                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunLateUpdateView(dt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateUpdateViewMarker.Dispose();
            }
        }

        public bool TryGetPrefab(int pid, out GameObject prefab)
        {
            if (pid < 0 || pid >= _predictedPrefabs.prefabs.Count)
            {
                prefab = null;
                return false;
            }

            prefab = _predictedPrefabs.prefabs[pid].prefab;
            return true;
        }

        public bool TryGetPrefab(GameObject prefab, out int id)
        {
            if (!_predictedPrefabs)
            {
                PurrLogger.LogError($"No predicted prefabs scriptable found on prediction manager! Make sure you've populated the field.", this);
                id = -1;
                return false;
            }

            var prefabs = _predictedPrefabs.prefabs;
            for (id = 0; id < prefabs.Count; id++)
            {
                if (prefabs[id].prefab == prefab)
                    return true;
            }

            id = -1;
            return false;
        }

        public static void ProperlySetPosAndRot(Transform transform, Vector3 position, Quaternion rotation)
        {
#if UNITY_PHYSICS_2D
            if (transform.TryGetComponent(out Rigidbody2D rb2d))
            {
                rb2d.position = position;
                rb2d.rotation = rotation.eulerAngles.z;
                transform.SetPositionAndRotation(position, rotation);
                return;
            }
#endif
#if UNITY_PHYSICS_3D
            if  (transform.TryGetComponent(out Rigidbody rb))
            {
                rb.position = position;
                rb.rotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
                return;
            }

            if (transform.TryGetComponent(out CharacterController ctrler) && ctrler.enabled)
            {
                ctrler.enabled = false;
                transform.SetPositionAndRotation(position, rotation);
                ctrler.enabled = true;
                return;
            }
#endif
            transform.SetPositionAndRotation(position, rotation);
        }

        internal GameObject InternalCreate(GameObject prefab, Vector3 position, Quaternion rotation, PredictedObjectID objectId, PlayerID? owner)
        {
            if (_pools.TryGetPool(prefab, out var pool))
            {
                var go = pool.Allocate();
                var trs = go.transform;
                ProperlySetPosAndRot(trs, position, rotation);
                trs.SetParent(null);
                if (!go.activeSelf)
                    go.SetActive(true);
                RegisterInstance(go, objectId, owner, true, true);
                return go;
            }
            else
            {
                var go = UnityProxy.InstantiateDirectly(prefab, position, rotation, gameObject.scene);
                if (!go.activeSelf)
                    go.SetActive(true);
                RegisterInstance(go, objectId, owner, false, false);
                return go;
            }
        }

        internal void InternalDelete(PackedInt prefabId, GameObject instance)
        {
            int pid = prefabId;

            if (!_predictedPrefabs || pid < 0 || pid >= _predictedPrefabs.prefabs.Count)
            {
                UnregisterInstance(instance, false, true);
                UnityProxy.DestroyImmediateDirectly(instance);
                return;
            }

            var prefabsInfo = _predictedPrefabs.prefabs[pid];

            if (!prefabsInfo.pooled)
            {
                UnregisterInstance(instance, false, true);
                UnityProxy.DestroyImmediateDirectly(instance);
                return;
            }

            if (_pools != null && _pools.TryGetPool(prefabsInfo.prefab, out var pool))
            {
                UnregisterPooledInstance(instance);
                pool.Delete(instance);
            }
            else
            {
                UnregisterInstance(instance, false, true);
                UnityProxy.DestroyImmediateDirectly(instance);
            }
        }

        public void SetOwnership(PredictedObjectID? root, PlayerID? player)
        {
            if (!hierarchy.TryGetGameObject(root, out var rootGo))
                return;

            var children = ListPool<PredictedIdentity>.Instantiate();

            rootGo.GetComponentsInChildren(true, children);

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                child.owner = player;
            }

            ListPool<PredictedIdentity>.Destroy(children);
        }

        public static bool TryGetClosestPredictedID(GameObject go, out PredictedComponentID pid)
        {
            if (go.TryGetComponent<PredictedIdentity>(out var identity))
            {
                pid = identity.id;
                return true;
            }

            var parent = go.GetComponentInParent<PredictedIdentity>();
            if (parent != null)
            {
                pid = parent.id;
                return true;
            }

            pid = default;
            return false;
        }
    }
}
