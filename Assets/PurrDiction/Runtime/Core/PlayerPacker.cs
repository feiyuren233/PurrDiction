using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Prediction
{
    internal struct PlayerPacker
    {
        public PlayerID player;
        public BitPacker packer;

        // Per-entry bit boundaries inside `packer`, spanning BOTH write phases (non-event-handler entries written
        // pre-sim, event-handler entries appended post-sim). boundaries[0] == 0; entry k occupies bits
        // [boundaries[k], boundaries[k+1]); entry count == boundaries.Count - 1. Cleared+seeded each server tick
        // in ResetAllPackers and appended to by WriteFrameEntries. Consumed by the fragment slicer (SendFragments).
        public List<int> boundaries;

        public void Dispose()
        {
            packer?.Dispose();
        }
    }
}