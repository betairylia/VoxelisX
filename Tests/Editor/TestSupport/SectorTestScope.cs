using System;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests.TestSupport
{
    public unsafe sealed class SectorTestScope : IDisposable
    {
        public SectorHandle Handle;

        public SectorTestScope(int initialBricks = 1)
        {
            Handle = SectorHandle.AllocEmpty(initialBricks);
        }

        public ref Sector Sector => ref Handle.Get();

        public void Set(int x, int y, int z, ushort id = 1)
        {
            Handle.SetBlock(x, y, z, new Block(id));
        }

        public ushort DirtyFlagsAt(int3 brickPos)
        {
            return Sector.brickDirtyFlags[Voxelis.Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z)];
        }

        public ushort RequireFlagsAt(int3 brickPos)
        {
            return Sector.brickRequireUpdateFlags[Voxelis.Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z)];
        }

        public BrickUpdateInfo.Type BrickUpdateAt(int3 brickPos)
        {
            ushort flags = RequireFlagsAt(brickPos);
            if ((flags & (ushort)DirtyFlags.BrickRemoved) != 0)
            {
                return BrickUpdateInfo.Type.Removed;
            }

            if ((flags & (ushort)DirtyFlags.BrickAdded) != 0)
            {
                return BrickUpdateInfo.Type.Added;
            }

            if ((flags & (ushort)DirtyFlags.GeometryWithLocalNeighbor) != 0)
            {
                return BrickUpdateInfo.Type.Modified;
            }

            return BrickUpdateInfo.Type.Idle;
        }

        public void Dispose()
        {
            Handle.Dispose(Allocator.Persistent);
        }
    }
}
