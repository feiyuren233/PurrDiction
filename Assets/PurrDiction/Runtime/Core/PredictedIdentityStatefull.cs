using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Prediction.Profiler;
using PurrNet.Utils;
using UnityEngine;

namespace PurrNet.Prediction
{
    public abstract class PredictedIdentity<STATE> : PredictedIdentity where STATE : struct, IPredictedData<STATE>
    {
        [SerializeField, Range(0, 30), Tooltip(
            "Ticks to render behind the latest received state on observers. " +
            "Higher = more jitter tolerance, more visible lag. " +
            "Default 1 tick. 0 = auto (tickRate/10, minimum 2 ticks; ~100 ms at 60 Hz).")]
        private int _interpolationDelayTicks = 1;

        public int interpolationDelayTicks => _interpolationDelayTicks;

        [SerializeField, Tooltip(
            "Controls whether the framework propagates state forward via the observer Simulate(ref state) hook.\n\n" +
            "When TRUE (default) — full extrapolation:\n" +
            "  - Mode C observers: hook fires at verified arrival AND per forward-replay tick.\n" +
            "  - Mode B observers: hook fires per forward-replay tick.\n\n" +
            "When FALSE — notification-only:\n" +
            "  - Mode C observers: hook STILL FIRES at verified arrival, but no forward-replay calls.\n" +
            "  - Mode B observers: hook is not called at all. State held at verified-tick value.\n\n" +
            "Has no effect on Mode A identities (input-Simulate covers every tick).")]
        private bool _extrapolateState = true;

        public override bool extrapolateState
        {
            get => _extrapolateState;
            set
            {
                if (_extrapolateState == value) return;
                _extrapolateState = value;
                predictionManager?.RecomputeRoleFor(this);
            }
        }

        [SerializeField, Range(0, 30), Tooltip(
            "Maximum number of ticks the observer Simulate(ref state) hook will be called forward from the last " +
            "authoritative state during forward-replay before the framework stops calling it and lets the state hold.")]
        private int _maxStateExtrapolationTicks = 1;

        public int maxStateExtrapolationTicks
        {
            get => _maxStateExtrapolationTicks;
            set => _maxStateExtrapolationTicks = value;
        }

        public PredictedHierarchy hierarchy { get; private set; }

        public override string ToString()
        {
            return currentState.ToString();
        }

        private InterpolatedWithDispose<FULL_STATE<STATE>> _interpolatedState;
        private History<FULL_STATE<STATE>> _stateHistory;

        protected TickManager tickModule { get; private set; }
        private bool _firstViewUpdate = true;


        public override void ResetInterpolation()
        {
            _interpolatedState?.Teleport(fullPredictedState.DeepCopy());
        }

        public override void ResetState()
        {
            base.ResetState();
            ResetInterpolation();
            fullPredictedState = default;
            _firstViewUpdate = true;
        }

        internal override void PrepareInput(bool isServer, bool isLocal, ulong tick, bool extrapolate) { }

        private FULL_STATE<STATE> FULLInterpolate(FULL_STATE<STATE> from, FULL_STATE<STATE> to, float t)
        {
            var state = Interpolate(from.state, to.state, t);
            return new FULL_STATE<STATE>
            {
                state = state,
                prediction = from.prediction
            };
        }

        internal FULL_STATE<STATE> fullPredictedState;

        public ref STATE currentState
        {
            get => ref fullPredictedState.state;
        }

        protected Type myType;

        private void ResetStateToInitialState()
        {
            fullPredictedState.prediction.wasOnSimulationStartCalled = false;
            fullPredictedState.state = GetInitialState();
        }

        internal override void Setup(NetworkManager manager, PredictionManager world, PredictedComponentID id, PlayerID? owner)
        {
            myType = GetType();
            hierarchy = world.hierarchy;

            if (!isFreshSpawn)
            {
                ResetStateToInitialState();
                GetLatestUnityState();
                base.Setup(manager, world, id, owner);
                return;
            }

            base.Setup(manager, world, id, owner);

            tickModule = manager.tickModule;

            if (tickModule == null)
                return;

            ResetStateToInitialState();
            GetLatestUnityState();

            var interpolationBuffer = _interpolationDelayTicks > 0
                ? _interpolationDelayTicks
                : (int)Mathf.Max(world.tickRate / (float)10, 2);

            if (_interpolatedState == null)
            {
                _interpolatedState = new InterpolatedWithDispose<FULL_STATE<STATE>>(
                    FULLInterpolate, 1f / world.tickRate, fullPredictedState.DeepCopy(), interpolationBuffer);
            }
            else _interpolatedState.Teleport(fullPredictedState.DeepCopy());

            if (_stateHistory == null)
                 _stateHistory = new History<FULL_STATE<STATE>>(world.tickRate * 10);
            else _stateHistory.Clear();

            _stateHistory.Write(0, fullPredictedState.DeepCopy());
        }

        /// <summary>
        /// Called when the object is first created.
        /// Future updates will come only through Simulate.
        /// </summary>
        /// <returns>The initial state of the object.</returns>
        protected virtual void GetUnityState(ref STATE state) {}

        internal override void GetLatestUnityState()
        {
            fullPredictedState.prediction.owner = owner;
            // fullPredictedState.prediction.predictedID = id;
            GetUnityState(ref fullPredictedState.state);
        }

        /// <summary>
        /// Called before the first Simulate is executed
        /// </summary>
        protected virtual void SimulationStart() {}

        internal override void SimulateTick(ulong tick, float delta)
        {
            using (simulateMarker.Auto())
            {
                if (!fullPredictedState.prediction.wasOnSimulationStartCalled)
                {
                    SimulationStart();
                    fullPredictedState.prediction.wasOnSimulationStartCalled = true;
                }

                Simulate(ref fullPredictedState.state, delta);
            }
        }

        internal override void LateSimulateTick(float delta)
            => LateSimulate(ref fullPredictedState.state, delta);

        internal override void SaveStateInHistory(ulong tick)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<STATE>() && _stateHistory.Count > 0)
            {
                var lastIdx = _stateHistory.Count - 1;
                if (Packer.AreEqual(_stateHistory[lastIdx].state, fullPredictedState.state))
                    return;
            }

            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }

        FULL_STATE<STATE>? _viewState;

        public override void UpdateRollbackInterpolationState(float delta, bool accumulateError)
        {
            var copy = fullPredictedState.DeepCopy();
            ModifyRollbackViewState(ref copy.state, delta, accumulateError);

            _viewState?.Dispose();
            _viewState = copy;
        }

        protected virtual void ModifyRollbackViewState(ref STATE state, float delta, bool accumulateError) { }

        protected virtual bool IsViewSettled(STATE viewState, STATE authoritativeState)
        {
            return Packer.AreEqual(viewState, authoritativeState);
        }

        internal override bool IsViewConverged()
        {
            if (_viewState == null) return true;
            return IsViewSettled(_viewState.Value.state, fullPredictedState.state);
        }

        protected virtual STATE GetInitialState() => default;

        protected virtual void Simulate(ref STATE state, float delta) {}

        protected virtual void LateSimulate(ref STATE state, float delta) {}

        internal override void Rollback(ulong tick)
        {
            if (!_stateHistory.ReadOrPrevious(tick, out var state))
                return;

            fullPredictedState.Dispose();
            fullPredictedState = state.DeepCopy();

            owner = fullPredictedState.prediction.owner;
            SetUnityState(fullPredictedState.state);
        }

        protected virtual void SetUnityState(STATE state) {}

        protected DeltaKey<STATE> stateKey => new (sceneId, id);

        private DeltaKey<PredictedIdentityState, STATE> internalKey => new (sceneId, id);

        internal override void WriteFirstState(ulong tick, BitPacker packer)
        {
            var savedState = fullPredictedState;

            if (tick > 0 && _stateHistory.ReadOrPrevious(tick, out var state))
                savedState = state;

            Packer<PredictedIdentityState>.Write(packer, savedState.prediction);
            Packer<STATE>.Write(packer, savedState.state);
        }

        internal override void ReadFirstState(ulong tick, BitPacker packer)
        {
            PredictedIdentityState prediction = default;
            STATE state = default;

            Packer<PredictedIdentityState>.Read(packer, ref prediction);
            Packer<STATE>.Read(packer, ref state);

            _stateHistory.Write(tick, new FULL_STATE<STATE>
            {
                state = state,
                prediction = prediction
            });
        }

        internal override bool WriteCurrentState(PlayerID target, BitPacker packer, DeltaModule deltaModule, bool reliable)
        {
            int pos = packer.positionInBits;
            bool changed;

            if (reliable)
            {
                int flagPos = packer.AdvanceBits(1);
                changed = deltaModule.WriteReliable(packer, target, internalKey, fullPredictedState.prediction);
                changed = WriteDeltaState(target, packer, deltaModule, reliable) || changed;
                packer.WriteAt(flagPos, changed);
                if (!changed)
                    packer.SetBitPosition(flagPos + 1);
            }
            else
            {
                int flagPos = packer.AdvanceBits(1);
                changed = deltaModule.Write(packer, target, internalKey, fullPredictedState.prediction);
                changed = WriteDeltaState(target, packer, deltaModule, reliable) || changed;
                packer.WriteAt(flagPos, changed);
                if (!changed)
                    packer.SetBitPosition(flagPos + 1);
            }

            TickBandwidthProfiler.OnWroteState(myType, packer.positionInBits - pos, this);
            return changed;
        }

        protected virtual bool WriteDeltaState(PlayerID target, BitPacker packer, DeltaModule deltaModule, bool reliable)
        {
            return reliable
                ? deltaModule.WriteReliable(packer, target, stateKey, fullPredictedState.state)
                : deltaModule.Write(packer, target, stateKey, fullPredictedState.state);
        }

        [UsedImplicitly]
        internal override void ReadState(ulong tick, BitPacker packer, DeltaModule deltaModule, bool reliable)
        {
            int pos = packer.positionInBits;
            FULL_STATE<STATE> newState = default;

            if (reliable)
            {
                bool changed = Packer<bool>.Read(packer);
                if (changed)
                {
                    deltaModule.ReadReliable(packer, internalKey, ref newState.prediction);
                }
                else
                {
                    packer.SetBitPosition(pos);
                    deltaModule.ReadReliable(packer, internalKey, ref newState.prediction);
                    packer.SetBitPosition(pos);
                }
                ReadDeltaState(packer, deltaModule, reliable, ref newState.state);
            }
            else
            {
                bool changed = Packer<bool>.Read(packer);
                if (changed)
                {
                    deltaModule.Read(packer, internalKey, default, ref newState.prediction);
                    ReadDeltaState(packer, deltaModule, reliable, ref newState.state);
                }
                else if (_stateHistory.ReadOrPrevious(tick, out var prev))
                {
                    newState = prev.DeepCopy();
                }
            }

            _stateHistory.Write(tick, newState);
            TickBandwidthProfiler.OnReadState(myType, packer.positionInBits - pos, this);
        }

        protected virtual void ReadDeltaState(BitPacker packer, DeltaModule deltaModule, bool reliable, ref STATE state)
        {
            if (reliable)
                deltaModule.ReadReliable(packer, stateKey, ref state);
            else
                deltaModule.Read(packer, stateKey, default, ref state);
        }

        internal override void WriteInput(ulong localTick, PlayerID receiver, BitPacker input, DeltaModule deltaModule, bool reliable) { }

        internal override void ReadInput(ulong tick,  PlayerID sender, BitPacker packer, DeltaModule deltaModule, bool reliable) { }

        internal override void QueueInput(BitPacker packer, PlayerID sender, DeltaModule deltaModule, bool reliable) { }

        public STATE viewState;

        public STATE? verifiedState
        {
            get
            {
                if (lastVerifiedTick.HasValue && _stateHistory.ReadOrPrevious(lastVerifiedTick.Value, out var state))
                    return state.state;
                return null;
            }
        }

        internal override void LateUpdateView(float deltaTime)
        {
            LateUpdateView(viewState, verifiedState);
        }

        internal override void UpdateView(float deltaTime)
        {
            base.UpdateView(deltaTime);

            if (_interpolatedState == null)
                return;

            if (_viewState.HasValue)
            {
                _interpolatedState.Add(_viewState.Value);
                _viewState = null;
            }

            viewState = _interpolatedState.Advance(deltaTime).state;

            if (_firstViewUpdate)
            {
                ViewStart(viewState, verifiedState);
                _firstViewUpdate = false;
            }

            UpdateView(viewState, verifiedState);
        }

        protected virtual void LateUpdateView(STATE viewState, STATE? verified) {}

        protected virtual void ViewStart(STATE viewState, STATE? verified) {}

        internal override void RunObserverSimulateTick(ulong tick, float delta, ulong lastAuthoritativeTick, bool isVerifiedArrival)
        {
            if (isVerifiedArrival)
            {
                Simulate(ref fullPredictedState.state, delta);
                if (_extrapolateState)
                    _stateHistory.Write(tick, fullPredictedState.DeepCopy());
                return;
            }

            var budget = _maxStateExtrapolationTicks > 0
                ? _maxStateExtrapolationTicks
                : Mathf.CeilToInt(0.8f * 10f / (delta * 60f));

            var distance = (long)tick - (long)lastAuthoritativeTick;
            if (distance > budget)
            {
                _stateHistory.Write(tick, fullPredictedState.DeepCopy());
                return;
            }

            Simulate(ref fullPredictedState.state, delta);
            _stateHistory.Write(tick, fullPredictedState.DeepCopy());
        }

        protected virtual void UpdateView(STATE viewState, STATE? verified) {}

        protected virtual STATE Interpolate(STATE from, STATE to, float t)
        {
            var offset = to.Add(to, from.Negate(from));
            var scaled = offset.Scale(offset, t);
            return from.Add(from, scaled);
        }

        internal override void ClearFuture(ulong stateTick)
        {
            _stateHistory.ClearFuture(stateTick);
        }

        public override void ReadFirstInput(ulong localTick, BitPacker packer) {}

        public override void WriteFirstInput(ulong localTick, BitPacker packer) {}
    }
}
