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

        static readonly Dictionary<int, PredictionManager> _instances = new ();

        public static event Action<int, PredictionManager> OnInstanceAdded;

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

        readonly List<PredictedIdentity> _firesInputSimAtVerifiedPass = new ();
        readonly List<PredictedIdentity> _firesInputSimAtForwardReplay = new ();
        readonly List<PredictedIdentity> _firesObserverSimAtVerifiedArrival = new ();
        readonly List<PredictedIdentity> _firesObserverSimAtForwardReplay = new ();

        internal enum SimulateRole
        {
            None,
            ControllerAuthority,
            ObserverInputSimForward,
            ObserverInputVerifiedOnly,
            ObserverInputVerifiedStateExtrapolate,
            ObserverStateForward,
            ObserverStateVerifiedOnly,
            ObserverStateVerifiedExtrapolate,
        }

        GameObjectPoolCollection _pools;

        [UsedImplicitly]
        public static bool TryGetInstance(int sceneHandle, out PredictionManager world)
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
            _instances[gameObject.scene.handle] = this;
            OnInstanceAdded?.Invoke(gameObject.scene.handle, this);
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
            networkManager.onLocalPlayerReceivedID += OnLocalPlayerChanged;
        }

        protected override void OnDespawned()
        {
            if (_tickManager != null)
            {
                _tickManager.onPreTick -= OnPreTick;
                _tickManager.onPostTick -= OnPostTick;
                _tickManager = null;
            }

            networkManager.onLocalPlayerReceivedID -= OnLocalPlayerChanged;

            CleanupAllSystems();
        }

        private void OnLocalPlayerChanged(PlayerID _)
        {
            RecomputeAllRoles();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            foreach (var packer in _clientFrames)
                packer.Dispose();
            _clientFrames.Clear();

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
            _clientFrames.Clear();
            localTick = 1;
            _lastVerifiedTick = 1;
            _playedFirst = false;
            localTickInContext = 1;
            _deltas.Clear();
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

            RecomputeRoleFor(system);
        }

        public void UnregisterInstance(PredictedIdentity predictedIdentity)
        {
            var role = predictedIdentity.currentRole;
            if (role.FiresInputSimAtVerifiedPass())       _firesInputSimAtVerifiedPass.Remove(predictedIdentity);
            if (role.FiresInputSimAtForwardReplay())      _firesInputSimAtForwardReplay.Remove(predictedIdentity);
            if (role.FiresObserverSimAtVerifiedArrival()) _firesObserverSimAtVerifiedArrival.Remove(predictedIdentity);
            if (role.FiresObserverSimAtForwardReplay())   _firesObserverSimAtForwardReplay.Remove(predictedIdentity);
            predictedIdentity.currentRole = SimulateRole.None;

            _instanceMap.Remove(predictedIdentity.id);
            if (_systems.Remove(predictedIdentity))
                --_systemsCount;
        }

        internal void RecomputeRoleFor(PredictedIdentity sys)
        {
            var oldR = sys.currentRole;
            var newR = ComputeRole(sys);
            if (oldR == newR) return;

            UpdateBucket(_firesInputSimAtVerifiedPass,       sys, oldR.FiresInputSimAtVerifiedPass(),       newR.FiresInputSimAtVerifiedPass());
            UpdateBucket(_firesInputSimAtForwardReplay,      sys, oldR.FiresInputSimAtForwardReplay(),      newR.FiresInputSimAtForwardReplay());
            UpdateBucket(_firesObserverSimAtVerifiedArrival, sys, oldR.FiresObserverSimAtVerifiedArrival(), newR.FiresObserverSimAtVerifiedArrival());
            UpdateBucket(_firesObserverSimAtForwardReplay,   sys, oldR.FiresObserverSimAtForwardReplay(),   newR.FiresObserverSimAtForwardReplay());

            sys.currentRole = newR;
        }

        void UpdateBucket(List<PredictedIdentity> bucket, PredictedIdentity sys, bool oldIn, bool newIn)
        {
            if (oldIn == newIn) return;
            if (newIn) InsertSorted(bucket, sys);
            else bucket.Remove(sys);
        }

        private static void InsertSorted(List<PredictedIdentity> bucket, PredictedIdentity system)
        {
            var myObjId = system.id.objectId.instanceId.value;
            var myCompId = system.id.componentId.value;
            int posToInsert = bucket.Count;

            for (int i = 0; i < bucket.Count; i++)
            {
                var curObjId = bucket[i].id.objectId.instanceId.value;
                if (curObjId > myObjId || curObjId == myObjId && bucket[i].id.componentId.value > myCompId)
                {
                    posToInsert = i;
                    break;
                }
            }

            bucket.Insert(posToInsert, system);
        }

        internal void RecomputeAllRoles()
        {
            for (int i = 0; i < _systemsCount; i++)
            {
                var system = _systems[i];
                if (system) RecomputeRoleFor(system);
            }
        }

        internal SimulateRole ComputeRole(PredictedIdentity sys)
        {
            if (sys.isController || isServer)
                return SimulateRole.ControllerAuthority;

            if (!sys.hasInput)
            {
                if (sys.simulateForward) return SimulateRole.ObserverStateForward;
                return sys.extrapolateState
                    ? SimulateRole.ObserverStateVerifiedExtrapolate
                    : SimulateRole.ObserverStateVerifiedOnly;
            }

            if (sys.forwardInput)
            {
                if (sys.simulateForward) return SimulateRole.ObserverInputSimForward;
                return sys.extrapolateState
                    ? SimulateRole.ObserverInputVerifiedStateExtrapolate
                    : SimulateRole.ObserverInputVerifiedOnly;
            }

            return sys.extrapolateState
                ? SimulateRole.ObserverStateVerifiedExtrapolate
                : SimulateRole.ObserverStateVerifiedOnly;
        }

        protected override void OnObserverRemoved(PlayerID player)
        {
            _clientTicks.Remove(player);

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

            _clientTicks[player] = new InputQueue();
            _clientFrames.Add(new PlayerPacker
            {
                player = player,
                packer = BitPackerPool.Get()
            });
        }

        protected override void OnObserverAdded(PlayerID player)
        {
            if (player == localPlayer || player.isBot)
                return;

            using var frame = BitPackerPool.Get();
            var tick = localTick - 1;
            RollbackToFrame(tick);

            Packer<Size>.Write(frame, _systemsCount);

            for (var i = 0; i < _systemsCount; i++)
            {
                if (!_systems[i].isEventHandler)
                    _systems[i].RunWriteFirstState(tick, frame);
            }

            for (var i = 0; i < _systemsCount; i++)
                _systems[i].RunWriteFirstInput(tick, frame);

            for (var i = 0; i < _systemsCount; i++)
            {
                if (_systems[i].isEventHandler)
                    _systems[i].RunWriteFirstState(tick, frame);
            }

            SimulateFrameInitial(localTick, tick);
            SyncFullState(player, tickRate, tickDelta, _sessionSeed, frame);
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best)]
        private void SyncFullState([UsedImplicitly] PlayerID target, int tickRate, float delta, uint randomSeed, BitPacker data)
        {
            isSimulating = true;
            _sessionSeed = randomSeed;

            _lastVerifiedTick = 1;
            _playedFirst = false;

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
                _systems[i].RunReadFirstInput(1, data);

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

            for (var j = 0; j < _firesObserverSimAtVerifiedArrival.Count; j++)
                _firesObserverSimAtVerifiedArrival[j].RunObserverSimulateTick(
                    2, tickDelta, 1, isVerifiedArrival: true);

            SyncTransforms();

            isSimulating = false;

            ReplayToLatestTick(1, true);
        }

        readonly List<PlayerPacker> _clientFrames = new (16);

        public bool cachedIsServer { get; private set; }

        private void OnPreTick()
        {
            cachedIsServer = isServer;
            localTickInContext = localTick;

            var myPlayer = isSpawned ? localPlayer ?? default : default;

            isSimulating = true;
            if (cachedIsServer)
                isVerified = true;

            if (cachedIsServer)
                PrepareInputs();

            using var ownedIdentities = DisposableList<PredictedIdentity>.Create(_systemsCount);

            for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
            {
                var system = _firesInputSimAtVerifiedPass[i];
                bool controller = system.IsOwner(myPlayer, cachedIsServer);
                if (controller)
                    ownedIdentities.Add(system);
                system.PrepareInput(cachedIsServer, controller, localTick, _inputQueueSettings.extrapolateForMissing);
            }

            using (SaveHistoryMarker.Auto())
            {
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                {
                    var system = _firesInputSimAtVerifiedPass[i];
                    if (!system.isEventHandler)
                        system.RunSaveState(localTick);
                }
            }

            if (cachedIsServer)
            {
                using (WriteFrameOnServerMarker.Auto())
                {
                    ResetAllPackers();
                    WriteInitialFrameToOthers();
                }
            }

            float delta = this.tickDelta;

            if (time)
                delta *= time.timeScale;

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    _firesInputSimAtVerifiedPass[i].OnPrepareSimulationInputs(localTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    _firesInputSimAtVerifiedPass[i].RunSimulateTick(localTick, delta);
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
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    _firesInputSimAtVerifiedPass[i].RunLateSimulateTick(delta);
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
                    WriteEventHandles();
                    SendFrameToOthers();
                }
            }

            for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                _firesInputSimAtVerifiedPass[i].PostSimulate();

            if (cachedIsServer)
                FinalizeTickOnServer();
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

                var dequeued = queue.inputQueue.Peek();
                HandleIncomingInput(dequeued.inputPacket, dequeued.count, player);
            }
        }

        private void FinalizeInputOnClient(DisposableList<PredictedIdentity> ownedIdentities)
        {
            const int MTU = 1024;

            using var frame = BitPackerPool.Get();
            uint writtenCount = 0;
            for (var systemIdx = 0; systemIdx < _firesInputSimAtVerifiedPass.Count; systemIdx++)
            {
                var system = _firesInputSimAtVerifiedPass[systemIdx];
                system.GetLatestUnityState();
            }

            var count = ownedIdentities.Count;
            for (var ownedIdx = 0; ownedIdx < count; ownedIdx++)
            {
                var owned = ownedIdentities[ownedIdx];
                if (owned && owned.hasInput)
                {
                    Packer<PredictedComponentID>.Write(frame, owned.id);
                    owned.RunWriteInput(localTick, default, frame, _deltaModuleState, false);
                    writtenCount += 1;
                }
            }

            if (frame.positionInBytes >= MTU)
                SendInputToServerReliable(localTick, writtenCount, frame);
            else SendInputToServer(localTick,writtenCount, frame);
        }

        private void FinalizeTickOnServer()
        {
            for (var systemIdx = 0; systemIdx < _systemsCount; systemIdx++)
            {
                var system = _systems[systemIdx];
                system.GetLatestUnityState();
                system.RunUpdateRollbackInterpolation(tickDelta, false);
            }
        }

        private void ResetAllPackers()
        {
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var packer = _clientFrames[i];
                packer.packer.ResetPositionAndMode(false);
            }
        }

        private void WriteInitialFrameToOthers()
        {
            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var frame = _clientFrames[j].packer;
                var player = _clientFrames[j].player;

                Packer<PackedInt>.Write(frame, _systemsCount);

                for (var i = 0; i < _systemsCount; i++)
                {
                    if (_systems[i].isEventHandler)
                        continue;

                    _systems[i].RunWriteCurrentState(player, frame, _deltaModuleState, isReliable);
                }

                for (var i = 0; i < _systemsCount; i++)
                    _systems[i].RunWriteInput(localTick, player, frame, _deltaModuleState, isReliable);
            }
        }

        private void WriteEventHandles()
        {
            var fCount = _clientFrames.Count;

            for (var i = 0; i < _systemsCount; i++)
            {
                if (!_systems[i].isEventHandler)
                    continue;

                var system = _systems[i];

                for (var j = 0; j < fCount; j++)
                {
                    var frame = _clientFrames[j];
                    var packer = frame.packer;
                    system.RunWriteCurrentState(frame.player, packer, _deltaModuleState, isReliable);
                }
            }
        }

        private void SendFrameToOthers()
        {
            var fCount = _clientFrames.Count;

            for (var j = 0; j < fCount; j++)
            {
                var player = _clientFrames[j].player;
                var packer = _clientFrames[j].packer;
                var deltaLen = packer.ToByteData().length;

                if (!_clientTicks.TryGetValue(player, out var queue))
                {
                    SendFrameToRemote(player, 0, new BitPackerWithLength(deltaLen, packer));
                    continue;
                }

                ulong tick = 0;

                if (queue.Count > 0 && !queue.waitForInput)
                {
                    var dequeued = queue.inputQueue.Dequeue();
                    tick = dequeued.clientTick;
                    dequeued.inputPacket.Dispose();
                }

                SendFrameToRemote(player, tick, new BitPackerWithLength(deltaLen, packer));
            }
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

        private void DoPhysicsPass()
        {
            isInPhysicsPass = true;
            // ReSharper disable once NotAccessedVariable
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

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

            isInPhysicsPass = false;
        }

        struct FrameDelta : IDisposable
        {
            public BitPacker packer;
            public ulong clientTick;

            public void Dispose()
            {
                packer?.Dispose();
            }
        }

        readonly Queue<FrameDelta> _deltas = new ();

        private void SendFrameToRemote(PlayerID player, ulong localTick, BitPackerWithLength delta)
        {
            if (isReliable) SendFrameToRemoteReliable(player, localTick, delta);
            else            SendFrameToRemoteUnreliable(player, localTick, delta);
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best, channel: Channel.ReliableOrdered)]
        private void SendFrameToRemoteReliable([UsedImplicitly] PlayerID player, ulong localTick, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);
            _deltas.Enqueue(new FrameDelta
            {
                packer = delta.packer,
                clientTick = localTick
            });
        }

        [TargetRpc(compressionLevel: CompressionLevel.Best, channel: Channel.UnreliableSequenced)]
        private void SendFrameToRemoteUnreliable([UsedImplicitly] PlayerID player, ulong localTick, BitPackerWithLength delta)
        {
            delta.packer.SkipBytes(delta.originalLength);
            _deltas.Enqueue(new FrameDelta
            {
                packer = delta.packer,
                clientTick = localTick
            });
        }

        private void RollbackToFrame(ulong stateTick)
        {
            for (var i = 0; i < _systemsCount; i++)
                _systems[i].RunRollback(stateTick);
            SyncTransforms();
        }

        private void RollbackToFrame(BitPacker frame, ulong stateTick, ulong inputTick)
        {
            frame.ResetPositionAndMode(true);

            PackedInt _count = default;
            Packer<PackedInt>.Read(frame, ref _count);
            int count = _count;

            for (var i = 0; i < count; ++i)
            {
                var system = _systems[i];
                if (system.isEventHandler)
                    continue;
                if (_validateDeterministicData && system.isDeterministic)
                    system.RunRollback(stateTick);
                system.RunClearFuture(stateTick);
                system.RunReadState(stateTick, frame, _deltaModuleState, isReliable);
                system.RunRollback(stateTick);
                system.lastVerifiedTick = stateTick;
            }

            for (var i = 0; i < count; ++i)
                _systems[i].RunReadInput(inputTick, default, frame, _deltaModuleState, isReliable);

            for (var i = 0; i < count; ++i)
            {
                var system = _systems[i];
                if (!system.isEventHandler)
                    continue;
                system.RunClearFuture(stateTick);
                system.RunReadState(stateTick, frame, _deltaModuleState, isReliable);
                system.RunRollback(stateTick);
                system.lastVerifiedTick = stateTick;
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
        private bool _playedFirst;

        private void OnPostTick()
        {
            if (cachedIsServer || _deltas.Count == 0 || localTick <= _lastVerifiedTick)
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

            while (_deltas.Count > 0)
            {
                isVerified = true;
                using var previousFrame = _deltas.Dequeue();
                bool inPlace = previousFrame.clientTick <= 1;
                var lastTick = _lastVerifiedTick;
                if (!inPlace)
                    _lastVerifiedTick = previousFrame.clientTick;

                ulong verifiedTick = _lastVerifiedTick;
                bool isJump = verifiedTick - lastTick > 1;

                var inPlaceTick = isJump ? lastTick : verifiedTick;

                if (inPlace || isJump)
                {
                    if (_playedFirst)
                    {
                        RollbackToFrame(inPlaceTick);
                        SimulateFrameInPlace(inPlaceTick);
                        SimulateFrame(inPlaceTick, true);
                    }

                    _playedFirst = true;
                }

                RollbackToFrame(previousFrame.packer, inPlaceTick, verifiedTick);

                for (var j = 0; j < _firesObserverSimAtVerifiedArrival.Count; j++)
                    _firesObserverSimAtVerifiedArrival[j].RunObserverSimulateTick(
                        verifiedTick + 1, tickDelta, verifiedTick, isVerifiedArrival: true);

                SimulateFrame(verifiedTick, true);
                isVerified = false;
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
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    _firesInputSimAtVerifiedPass[i].OnPrepareSimulationInputs(verifiedTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                    _firesInputSimAtVerifiedPass[j].RunSimulateTick(verifiedTick, delta);
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
                for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                    _firesInputSimAtVerifiedPass[j].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }

            for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                _firesInputSimAtVerifiedPass[i].PostSimulate();
            for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                _firesInputSimAtVerifiedPass[j].GetLatestUnityState();

            isSimulating = false;
            localTickInContext = localTick;
        }

        private void SimulateFrameInitial(ulong stateTick, ulong inputTick)
        {
            var delta = tickDelta;
            if (time)
                delta *= time.timeScale;

            isSimulating = true;
            localTickInContext = stateTick;

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    _firesInputSimAtVerifiedPass[i].OnPrepareSimulationInputs(inputTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                    _firesInputSimAtVerifiedPass[j].RunSimulateTick(stateTick, delta);
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
                for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                    _firesInputSimAtVerifiedPass[j].RunLateSimulateTick(delta);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                lateSimulateMarker.Dispose();
            }

            for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                _firesInputSimAtVerifiedPass[i].PostSimulate();

            for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                _firesInputSimAtVerifiedPass[j].GetLatestUnityState();

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
            ulong lastAuthoritativeTick = saveState ? verifiedTick : _lastVerifiedTick;

            if (saveState)
            {
                using (SaveHistoryMarker.Auto())
                {
                    for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    {
                        var system = _firesInputSimAtVerifiedPass[i];
                        if (!system.isEventHandler)
                            system.RunSaveState(verifiedTick);
                    }
                }
            }

            using (SimulateInputsMarker.Auto())
            {
                for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    _firesInputSimAtVerifiedPass[i].OnPrepareSimulationInputs(verifiedTick, delta);
            }

            var simulateMarker = SimulateMarker.Auto();
            try
            {
                if (saveState)
                {
                    for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                        _firesInputSimAtVerifiedPass[j].RunSimulateTick(verifiedTick, delta);
                }
                else
                {
                    for (var j = 0; j < _firesInputSimAtForwardReplay.Count; j++)
                        _firesInputSimAtForwardReplay[j].RunSimulateTick(verifiedTick, delta);
                    for (var j = 0; j < _firesObserverSimAtForwardReplay.Count; j++)
                        _firesObserverSimAtForwardReplay[j].RunObserverSimulateTick(
                            verifiedTick, delta, lastAuthoritativeTick, isVerifiedArrival: false);
                }
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
                for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                    _firesInputSimAtVerifiedPass[j].RunLateSimulateTick(delta);
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
                    for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                    {
                        var system = _firesInputSimAtVerifiedPass[i];
                        if (system.isEventHandler)
                            system.RunSaveState(verifiedTick);
                    }
                }
            }

            for (var i = 0; i < _firesInputSimAtVerifiedPass.Count; i++)
                _firesInputSimAtVerifiedPass[i].PostSimulate();

            for (var j = 0; j < _firesInputSimAtVerifiedPass.Count; j++)
                _firesInputSimAtVerifiedPass[j].GetLatestUnityState();

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
                        system.RunQueueInput(inputPacket, sender, _deltaModuleState, false);
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

            if (!isClient && !isServer)
                return;

            UpdateView();
        }

        private void LateUpdate()
        {
            if (_updateViewMode != UpdateViewMode.LateUpdate)
                return;

            if (!isClient && !isServer)
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
                RegisterInstance(go, objectId, owner, true, true);
                return go;
            }
            else
            {
                var go = UnityProxy.InstantiateDirectly(prefab, position, rotation, gameObject.scene);
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

    internal static class SimulateRoleExtensions
    {
        public static bool FiresInputSimAtVerifiedPass(this PredictionManager.SimulateRole r)
            => r is PredictionManager.SimulateRole.ControllerAuthority
                  or PredictionManager.SimulateRole.ObserverInputSimForward
                  or PredictionManager.SimulateRole.ObserverInputVerifiedOnly
                  or PredictionManager.SimulateRole.ObserverInputVerifiedStateExtrapolate
                  or PredictionManager.SimulateRole.ObserverStateForward;

        public static bool FiresInputSimAtForwardReplay(this PredictionManager.SimulateRole r)
            => r is PredictionManager.SimulateRole.ControllerAuthority
                  or PredictionManager.SimulateRole.ObserverInputSimForward
                  or PredictionManager.SimulateRole.ObserverStateForward;

        public static bool FiresObserverSimAtVerifiedArrival(this PredictionManager.SimulateRole r)
            => r is PredictionManager.SimulateRole.ObserverStateVerifiedOnly
                  or PredictionManager.SimulateRole.ObserverStateVerifiedExtrapolate;

        public static bool FiresObserverSimAtForwardReplay(this PredictionManager.SimulateRole r)
            => r is PredictionManager.SimulateRole.ObserverInputVerifiedStateExtrapolate
                  or PredictionManager.SimulateRole.ObserverStateVerifiedExtrapolate;
    }
}
