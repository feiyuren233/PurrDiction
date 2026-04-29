using PurrNet.Modules;
using PurrNet.Utils;

namespace PurrNet.Prediction
{
    public readonly struct DeltaKey<T, S> : IStableHashable
    {
        private readonly PredictedComponentID id;
        private readonly SceneID scene;

        public DeltaKey(SceneID scene, PredictedComponentID id)
        {
            this.id = id;
            this.scene = scene;
        }

        public uint GetStableHash()
        {
            const uint Off = 2166136261u;
            const uint Pri = 16777619u;
            uint h = Off;
            h = (h ^ Hasher<T>.stableHash) * Pri;
            h = (h ^ Hasher<S>.stableHash) * Pri;
            h = (h ^ id.componentId.value) * Pri;
            h = (h ^ id.objectId.instanceId.value) * Pri;
            h = (h ^ scene.id.value) * Pri;
            return h;
        }
    }

    public readonly struct DeltaKey<T> : IStableHashable
    {
        private readonly PredictedComponentID id;
        private readonly SceneID scene;

        public DeltaKey(SceneID scene, PredictedComponentID id)
        {
            this.id = id;
            this.scene = scene;
        }

        public uint GetStableHash()
        {
            const uint Off = 2166136261u;
            const uint Pri = 16777619u;
            uint h = Off;
            h = (h ^ Hasher<T>.stableHash) * Pri;
            h = (h ^ id.componentId.value) * Pri;
            h = (h ^ id.objectId.instanceId.value) * Pri;
            h = (h ^ scene.id.value) * Pri;
            return h;
        }
    }
}