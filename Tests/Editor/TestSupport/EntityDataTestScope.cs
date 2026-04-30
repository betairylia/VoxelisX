using System;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.TestSupport
{
    public unsafe sealed class EntityDataTestScope : IDisposable
    {
        public VoxelEntityData Data;

        public EntityDataTestScope()
        {
            Data = new VoxelEntityData(Allocator.Persistent);
        }

        public SectorHandle AddSector(int3 sectorPos)
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                Data.AddSectorAt(sectorPos, handle);
            }
            catch
            {
                handle.Dispose(Allocator.Persistent);
                throw;
            }
            return handle;
        }

        public SectorHandle SectorAt(int3 sectorPos)
        {
            return Data.sectors[sectorPos];
        }

        public SectorNeighborHandles NeighborsAt(int3 sectorPos)
        {
            return Data.sectorNeighbors[sectorPos];
        }

        public ushort RequireFlagsAt(int3 sectorPos, int3 brickPos)
        {
            var sector = Data.sectors[sectorPos];
            int brickIdx = Voxelis.Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z);
            return sector.Get().brickRequireUpdateFlags[brickIdx];
        }

        public void Dispose()
        {
            Data.Dispose();
        }
    }
}
