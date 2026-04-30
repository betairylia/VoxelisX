using System;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.TestSupport
{
    public unsafe sealed class DirtyPropagationTestWorld : IDisposable
    {
        private readonly EntityDataTestScope entity;

        public DirtyPropagationTestWorld()
        {
            entity = new EntityDataTestScope();
            entity.AddSector(new int3(0, 0, 0));
        }

        public VoxelEntityData Data => entity.Data;

        public SectorHandle Center => entity.SectorAt(new int3(0, 0, 0));

        public DirtyPropagationTestWorld AddSector(int3 sectorPos)
        {
            entity.AddSector(sectorPos);
            return this;
        }

        public DirtyPropagationTestWorld SetBlock(int x, int y, int z, ushort id = 1)
        {
            Center.SetBlock(x, y, z, new Block(id));
            return this;
        }

        public DirtyPropagationTestWorld MarkBrickDirty(int3 brickPos, DirtyFlags flags, uint directionMask = 0xFFFFFFFF)
        {
            Center.Get().MarkBrickDirty(Voxelis.Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z), flags, directionMask);
            return this;
        }

        public void Propagate(DirtyFlags flags = DirtyFlags.Reserved0)
        {
            entity.Data.PropagateDirtyFlags(flags).Complete();
        }

        public SectorHandle SectorAt(int3 sectorPos)
        {
            return entity.SectorAt(sectorPos);
        }

        public ushort RequireFlagsAt(int3 sectorPos, int3 brickPos)
        {
            return entity.RequireFlagsAt(sectorPos, brickPos);
        }

        public void Dispose()
        {
            entity.Dispose();
        }
    }
}
