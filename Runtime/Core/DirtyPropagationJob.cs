using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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

            // For each brick, check neighbors' dirty flags
            for (int brickIdx = 0; brickIdx < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS; brickIdx++)
            {
                int3 brickPos = Sector.ToBrickPos((short)brickIdx);
                ushort propagatedFlags = 0;

                for (int dir = 0; dir < neighborCount; dir++)
                {
                    int3 neighborBrickPos = brickPos + SectorNeighborHandles.Directions[dir];
                    ushort neighborDirty = GetNeighborDirtyFlags(sectorPos, neighborBrickPos, neighbors);
                    propagatedFlags |= (ushort)(neighborDirty & (ushort)flagsToPropagate);
                }

                if (propagatedFlags != 0)
                {
                    sector.brickRequireUpdateFlags[brickIdx] |= propagatedFlags;
                    sector.sectorRequireUpdateFlags |= propagatedFlags;
                }
            }
        }

        private ushort GetNeighborDirtyFlags(int3 currentSectorPos, int3 neighborBrickPos, SectorNeighborHandles neighbors)
        {
            // Check if in same sector
            bool inBounds =
                neighborBrickPos.x >= 0 && neighborBrickPos.x < Sector.SIZE_IN_BRICKS &&
                neighborBrickPos.y >= 0 && neighborBrickPos.y < Sector.SIZE_IN_BRICKS &&
                neighborBrickPos.z >= 0 && neighborBrickPos.z < Sector.SIZE_IN_BRICKS;

            if (inBounds)
            {
                // Same sector
                if (!sectors.TryGetValue(currentSectorPos, out SectorHandle handle) || handle.IsNull) return 0;
                int brickIdx = Sector.ToBrickIdx(neighborBrickPos.x, neighborBrickPos.y, neighborBrickPos.z);
                return handle.Get().brickDirtyFlags[brickIdx];
            }
            else
            {
                // Different sector - compute offset and find cached neighbor
                int3 sectorOffset = new int3(
                    neighborBrickPos.x < 0 ? -1 : (neighborBrickPos.x >= Sector.SIZE_IN_BRICKS ? 1 : 0),
                    neighborBrickPos.y < 0 ? -1 : (neighborBrickPos.y >= Sector.SIZE_IN_BRICKS ? 1 : 0),
                    neighborBrickPos.z < 0 ? -1 : (neighborBrickPos.z >= Sector.SIZE_IN_BRICKS ? 1 : 0)
                );

                // Find neighbor handle
                SectorHandle neighborHandle = default;
                for (int i = 0; i < SectorNeighborHandles.MOORE_COUNT; i++)
                {
                    if (SectorNeighborHandles.Directions[i].Equals(sectorOffset))
                    {
                        neighborHandle = neighbors.Neighbors[i];
                        break;
                    }
                }

                if (neighborHandle.IsNull) return 0;

                // Wrap brick position
                int3 wrappedBrickPos = new int3(
                    ((neighborBrickPos.x % Sector.SIZE_IN_BRICKS) + Sector.SIZE_IN_BRICKS) % Sector.SIZE_IN_BRICKS,
                    ((neighborBrickPos.y % Sector.SIZE_IN_BRICKS) + Sector.SIZE_IN_BRICKS) % Sector.SIZE_IN_BRICKS,
                    ((neighborBrickPos.z % Sector.SIZE_IN_BRICKS) + Sector.SIZE_IN_BRICKS) % Sector.SIZE_IN_BRICKS
                );

                int brickIdx = Sector.ToBrickIdx(wrappedBrickPos.x, wrappedBrickPos.y, wrappedBrickPos.z);
                return neighborHandle.Get().brickDirtyFlags[brickIdx];
            }
        }
    }
}
