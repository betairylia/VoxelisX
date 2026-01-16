using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using VoxelisX;

namespace Voxelis
{
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
            int neighborCount = neighborhoodType == NeighborhoodType.VonNeumann
                ? SectorNeighborHandles.VON_NEUMANN_COUNT
                : SectorNeighborHandles.MOORE_COUNT;

            // Create helper for convenient neighbor access
            var helper = new SectorNeighborhoodHelper(handle, neighbors);

            // For each brick, check neighbors' dirty flags
            for (int brickIdx = 0; brickIdx < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS; brickIdx++)
            {
                int3 brickPos = Sector.ToBrickPos((short)brickIdx);
                ushort propagatedFlags = 0;

                for (int dir = 0; dir < neighborCount; dir++)
                {
                    int3 neighborBrickPos = brickPos + SectorNeighborHandles.Directions[dir];
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
