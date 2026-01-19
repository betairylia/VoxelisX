using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Voxelis;

namespace Simulation.Utils
{
    // public struct CollectBrickJob : IJob
    [BurstCompile]
    public static class BrickCollector
    {
        [BurstCompile]
        public static unsafe void Collect(
            ref NativeList<VoxelEntityData> entities,
            ref NativeList<VoxelisXWorld.BrickInfo> brickList)
        {
            for (int ei = 0; ei < entities.Length; ei++)
            {
                var e = entities[ei];
                foreach (var s in e.sectors)
                {
                    ref Sector sec = ref s.Value.Get();
                    SectorRequiredUpdateBrickEnumerator enumerator =
                        new SectorRequiredUpdateBrickEnumerator(sec, DirtyFlags.All, true);

                    foreach (var b in enumerator)
                    {
                        brickList.Add(new VoxelisXWorld.BrickInfo
                        {
                            EntityId = ei,
                            SectorPos = s.Key,
                            BrickOrigin = b.position,
                            BrickId = b.brickIdx,
                            BrickDirtyFlag = sec.brickDirtyFlags[b.brickIdx],
                            BrickRequireUpdateFlag = sec.brickRequireUpdateFlags[b.brickIdx],

                            Sector = s.Value,
                            Neighbors = e.sectorNeighbors[s.Key]
                        });
                    }
                }
            }
        }
    }
}