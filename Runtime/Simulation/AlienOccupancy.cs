using Unity.Collections;
using Unity.Mathematics;

namespace Voxelis
{
    public struct AlienEntityView
    {
        public int EntityId;
        public RigidTransform LocalToWorld;
        public float4x4 WorldToLocal;
        public NativeHashMap<int3, SectorHandle> Sectors;
        public float3 WorldAabbMin;
        public float3 WorldAabbMax;

        public bool ContainsWorldPoint(float3 worldPoint)
        {
            return math.all(worldPoint >= WorldAabbMin) && math.all(worldPoint <= WorldAabbMax);
        }

        public Block GetBlockAtLocalVoxel(int3 localVoxel)
        {
            int3 sectorPos = new int3(
                localVoxel.x >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                localVoxel.y >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                localVoxel.z >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS));

            if (!Sectors.TryGetValue(sectorPos, out SectorHandle sector))
                return Block.Empty;

            return sector.GetBlock(
                localVoxel.x & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                localVoxel.y & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                localVoxel.z & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS))
            );
        }
    }

    public struct AlienOccupancyQuery
    {
        [ReadOnly] public NativeArray<AlienEntityView> EntitiesInDeterministicOrder;

        public Block GetFirstOccupyingAlienBlock(int selfEntityId, RigidTransform selfLocalToWorld, int3 selfLocalVoxel)
        {
            float3 selfLocalPoint = (float3)selfLocalVoxel + new float3(0.5f, 0.5f, 0.5f);
            float3 worldPoint = math.transform(selfLocalToWorld, selfLocalPoint);

            for (int i = 0; i < EntitiesInDeterministicOrder.Length; i++)
            {
                AlienEntityView alien = EntitiesInDeterministicOrder[i];
                if (alien.EntityId == selfEntityId)
                    continue;

                if (!alien.ContainsWorldPoint(worldPoint))
                    continue;

                float3 alienLocalPoint = math.transform(alien.WorldToLocal, worldPoint);
                int3 alienVoxel = (int3)math.floor(alienLocalPoint);

                Block alienBlock = alien.GetBlockAtLocalVoxel(alienVoxel);
                if (!alienBlock.isEmpty)
                    return alienBlock;
            }

            return Block.Empty;
        }

        public bool IsVoxelSpaceOccupied(int selfEntityId, RigidTransform selfLocalToWorld, int3 selfLocalVoxel)
        {
            return !GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, selfLocalVoxel).isEmpty;
        }
    }

    public struct AlienAwareNeighborhoodReader
    {
        public int SelfEntityId;
        public RigidTransform SelfLocalToWorld;
        public SectorNeighborhoodReaderHelper LocalReader;
        public AlienOccupancyQuery AlienQuery;

        public Block GetBlock(int3 selfLocalVoxel)
        {
            Block local = LocalReader.GetBlock(selfLocalVoxel);
            if (!local.isEmpty)
                return local;

            return AlienQuery.GetFirstOccupyingAlienBlock(SelfEntityId, SelfLocalToWorld, selfLocalVoxel);
        }

        public bool IsVoxelSpaceOccupied(int3 selfLocalVoxel)
        {
            Block local = LocalReader.GetBlock(selfLocalVoxel);
            if (!local.isEmpty)
                return true;

            return AlienQuery.IsVoxelSpaceOccupied(SelfEntityId, SelfLocalToWorld, selfLocalVoxel);
        }
    }
}
