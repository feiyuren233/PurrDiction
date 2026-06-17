using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct ArrivedFragment : IDisposable
    {
        public BitPacker packer;
        public uint entryCount;
        public PredictedComponentID firstId; // peeked at ingest; orders fragments so the hierarchy applies first
        public void Dispose() => packer?.Dispose();
    }

    /// <summary>
    /// Buffers arrived unreliable fragments grouped by serverEchoTick, each tick's list kept ascending by
    /// firstId so consume applies fragments in global ascending-id order (hierarchy first). Replaces the old
    /// single-packet-per-tick Queue&lt;FrameDelta&gt;. The per-tick lists are POOLED (rented in <see cref="Add"/>,
    /// returned via <see cref="Recycle"/> after the caller disposes the fragments) so steady-state buffering —
    /// one tick buffered and consumed every client tick — allocates no garbage.
    /// </summary>
    internal sealed class FragmentBuffer
    {
        readonly SortedDictionary<ulong, List<ArrivedFragment>> _byTick = new(); // ticks ascending → consume oldest first
        readonly Stack<List<ArrivedFragment>> _listPool = new();                 // recycled per-tick lists (no per-tick GC)

        public int TickCount => _byTick.Count;

        public void Add(ulong tick, ArrivedFragment fragment)
        {
            if (!_byTick.TryGetValue(tick, out var list))
            {
                list = RentList();
                _byTick[tick] = list;
            }
            int idx = list.Count;
            for (int i = 0; i < list.Count; i++)
                if (Compare(fragment.firstId, list[i].firstId) < 0) { idx = i; break; }
            list.Insert(idx, fragment);
        }

        public bool TryPeekOldestTick(out ulong tick)
        {
            foreach (var kv in _byTick) { tick = kv.Key; return true; }
            tick = 0; return false;
        }

        public List<ArrivedFragment> TakeTick(ulong tick)
        {
            if (_byTick.TryGetValue(tick, out var list)) { _byTick.Remove(tick); return list; }
            return null;
        }

        /// <summary>Return a list obtained from <see cref="TakeTick"/> once the caller has disposed its
        /// fragments, so the next buffered tick reuses it instead of allocating a new one.</summary>
        public void Recycle(List<ArrivedFragment> list)
        {
            if (list == null) return;
            list.Clear();
            if (_listPool.Count < 32) _listPool.Push(list);
        }

        public void Clear()
        {
            foreach (var kv in _byTick)
            {
                foreach (var f in kv.Value) f.Dispose();
                Recycle(kv.Value);
            }
            _byTick.Clear();
        }

        List<ArrivedFragment> RentList() => _listPool.Count > 0 ? _listPool.Pop() : new List<ArrivedFragment>(4);

        static int Compare(PredictedComponentID a, PredictedComponentID b)
        {
            int o = a.objectId.instanceId.value.CompareTo(b.objectId.instanceId.value);
            return o != 0 ? o : a.componentId.value.CompareTo(b.componentId.value);
        }
    }
}
