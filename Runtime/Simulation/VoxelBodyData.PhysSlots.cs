using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Simulation;

namespace Voxelis
{
    public partial struct VoxelBodyData : IDisposable
    {
        private void RefreshPhysicsSlot(
            LockableUnsafeHashMap<int3, SectorHandle> sectors,
            DirtyFlags dirtyMask = DirtyFlags.GeometryWithLocalNeighbor)
        {
            // TODO
        }
    }
}
