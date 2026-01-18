using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxelis
{
    [Flags]
    public enum DirtyFlags : ushort
    {
        None       = 0,
        Reserved0  = 1 << 0,
        Reserved1  = 1 << 1,
        Reserved2  = 1 << 2,
        Reserved3  = 1 << 3,
        Reserved4  = 1 << 4,
        Reserved5  = 1 << 5,
        Reserved6  = 1 << 6,
        Reserved7  = 1 << 7,
        Reserved8  = 1 << 8,
        Reserved9  = 1 << 9,
        Reserved10 = 1 << 10,
        Reserved11 = 1 << 11,
        Reserved12 = 1 << 12,
        Reserved13 = 1 << 13,
        Reserved14 = 1 << 14,
        Reserved15 = 1 << 15,
        All        = 0xFFFF,
    }
    
    [BurstCompile]
    public unsafe struct DirtyPropagationJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int3> allSectorPositions;
        [ReadOnly] public NativeHashMap<int3, SectorHandle> sectors;
        [ReadOnly] public NativeHashMap<int3, SectorNeighborHandles> sectorNeighbors;
        [ReadOnly] public NeighborhoodType neighborhoodType;
        [ReadOnly] public DirtyFlags flagsToPropagate;

        public void Execute(int index)
        {
            int3 sectorPos = allSectorPositions[index];
            if (!sectors.TryGetValue(sectorPos, out SectorHandle handle) || handle.IsNull) return;
            if (!sectorNeighbors.TryGetValue(sectorPos, out SectorNeighborHandles neighbors)) return;

            ref Sector sector = ref handle.Get();
            int neighborCount = NeighborhoodSettings.neighborhoodCount;

            // Create helper for convenient neighbor access
            var helper = new SectorNeighborhoodReaderHelper(handle, neighbors);

            // Early exit: Skip if current sector has no dirty flags AND no neighbors have dirty flags
            bool currentSectorDirty = (sector.sectorDirtyFlags & (ushort)flagsToPropagate) != 0;

            if (!currentSectorDirty)
            {
                // Check if any neighbor sector has dirty flags
                bool anyNeighborDirty = false;
                for (int i = 0; i < neighborCount; i++)
                {
                    SectorHandle neighborHandle = neighbors.Neighbors[i];
                    if (!neighborHandle.IsNull && (neighborHandle.Get().sectorDirtyFlags & (ushort)flagsToPropagate) != 0)
                    {
                        anyNeighborDirty = true;
                        break;
                    }
                }

                // Skip processing if neither current sector nor any neighbor is dirty
                if (!anyNeighborDirty)
                {
                    return;
                }
            }

            // For each brick, check neighbors' dirty flags
            for (int brickIdx = 0; brickIdx < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS; brickIdx++)
            {
                int3 brickPos = Sector.ToBrickPos((short)brickIdx);
                ushort propagatedFlags = sector.brickDirtyFlags[brickIdx];

                for (int dir = 0; dir < neighborCount; dir++)
                {
                    int3 neighborBrickPos = brickPos + NeighborhoodSettings.Directions[dir];
                    ushort neighborDirty = helper.GetBrickDirtyFlags(neighborBrickPos);
                    propagatedFlags |= (ushort)(neighborDirty & (ushort)flagsToPropagate);
                }

                if (propagatedFlags != 0)
                {
                    sector.brickRequireUpdateFlags[brickIdx] |= propagatedFlags;
                    sector.sectorRequireUpdateFlags |= propagatedFlags;
                }
            }
        }
    }
}
