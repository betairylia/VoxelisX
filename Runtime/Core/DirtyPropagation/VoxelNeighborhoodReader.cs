using Unity.Collections;
using Unity.Mathematics;

namespace Voxelis
{
    public struct AlienEntityView
    {
        public int EntityId;
        public RigidTransform LocalToWorld;
        public float4x4 WorldToLocal;
        [ReadOnly] public LockableUnsafeHashMap<int3, SectorHandle> Sectors;
        public float3 WorldAabbMin;
        public float3 WorldAabbMax;

        public bool ContainsWorldPoint(float3 worldPoint)
        {
            return math.all(worldPoint >= WorldAabbMin) && math.all(worldPoint <= WorldAabbMax);
        }

        public Block GetBlockAtLocalVoxel(int3 localVoxel)
        {
            int3 sectorPos = localVoxel >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS);
            if (!Sectors.TryGetValue(sectorPos, out SectorHandle sector))
            {
                return Block.Empty;
            }

            int mask = Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS);
            return sector.GetBlock(localVoxel.x & mask, localVoxel.y & mask, localVoxel.z & mask);
        }
    }

    public struct AlienOccupancyQuery
    {
        [ReadOnly] public NativeArray<AlienEntityView> EntitiesInDeterministicOrder;

        public Block GetFirstOccupyingAlienBlock(int selfEntityId, RigidTransform selfLocalToWorld, int3 selfEntityLocalVoxel)
        {
            float3 localSamplePoint = selfEntityLocalVoxel + new float3(0.5f);
            float3 worldSamplePoint = math.transform(selfLocalToWorld, localSamplePoint);

            for (int i = 0; i < EntitiesInDeterministicOrder.Length; i++)
            {
                AlienEntityView entity = EntitiesInDeterministicOrder[i];
                if (entity.EntityId == selfEntityId || !entity.ContainsWorldPoint(worldSamplePoint))
                {
                    continue;
                }

                float3 alienLocalPoint = math.mul(entity.WorldToLocal, new float4(worldSamplePoint, 1f)).xyz;
                int3 alienVoxel = (int3)math.floor(alienLocalPoint);
                Block alienBlock = entity.GetBlockAtLocalVoxel(alienVoxel);
                if (!alienBlock.isEmpty)
                {
                    return alienBlock;
                }
            }

            return Block.Empty;
        }

        public bool IsVoxelSpaceOccupied(int selfEntityId, RigidTransform selfLocalToWorld, int3 selfEntityLocalVoxel)
        {
            return !GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel).isEmpty;
        }
    }

    public struct VoxelNeighborhoodReader
    {
        private int selfEntityId;
        private int3 centerSectorPos;
        private RigidTransform selfLocalToWorld;
        private SectorNeighborhoodReaderHelper localReader;
        private AlienOccupancyQuery alienQuery;

        public static VoxelNeighborhoodReader Create(VoxelisXCoreWorld.BrickInfo brickInfo, AlienOccupancyQuery alienQuery)
        {
            return new VoxelNeighborhoodReader
            {
                selfEntityId = brickInfo.EntityId,
                centerSectorPos = brickInfo.SectorPos,
                selfLocalToWorld = brickInfo.LocalToWorld,
                localReader = new SectorNeighborhoodReaderHelper(brickInfo.Sector, brickInfo.Neighbors),
                alienQuery = alienQuery
            };
        }

        public Block GetLocalBlock(int3 sectorLocalVoxel) => localReader.GetBlock(sectorLocalVoxel);

        public Block GetBlock(int3 sectorLocalVoxel)
        {
            Block localBlock = localReader.GetBlock(sectorLocalVoxel);
            if (!localBlock.isEmpty)
            {
                return localBlock;
            }

            int3 selfEntityLocalVoxel = centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + sectorLocalVoxel;
            return alienQuery.GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel);
        }

        public bool IsVoxelSpaceOccupied(int3 sectorLocalVoxel)
        {
            if (!localReader.GetBlock(sectorLocalVoxel).isEmpty)
            {
                return true;
            }

            int3 selfEntityLocalVoxel = centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + sectorLocalVoxel;
            return alienQuery.IsVoxelSpaceOccupied(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel);
        }
    }

    public struct AutomataReadContext
    {
        [ReadOnly] public AlienOccupancyQuery AlienQuery;

        public VoxelNeighborhoodReader CreateReader(VoxelisXCoreWorld.BrickInfo brickInfo)
        {
            return VoxelNeighborhoodReader.Create(brickInfo, AlienQuery);
        }
    }
}
