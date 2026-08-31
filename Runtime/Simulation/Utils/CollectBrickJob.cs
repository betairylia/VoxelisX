using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Caelix;
using Caelix.Utils;

namespace Simulation.Utils
{
    // public struct CollectBrickJob : IJob
    [BurstCompile]
    public static class BrickCollector
    {
        [BurstCompile]
        public static unsafe void Collect(
            ref NativeHashMap<Guid128, VoxelEntityData> entities,
            ref NativeList<CaelixWorld.BrickInfo> brickList)
        {
            foreach(var kvp in entities)
            {
                var e = kvp.Value;
                foreach (var s in e.sectors)
                {
                    ref Sector sec = ref s.Value.Get();
                    SectorRequiredUpdateBrickEnumerator enumerator =
                        new SectorRequiredUpdateBrickEnumerator(sec, DirtyFlags.All, true);

                    foreach (var b in enumerator)
                    {
                        brickList.Add(new CaelixWorld.BrickInfo
                        {
                            EntityId = kvp.Key,
                            SectorPos = s.Key,
                            BrickOrigin = b.position,
                            BrickId = b.brickIdx,
                            LocalToWorld = e.transform,
                            _BrickDirtyFlag = sec.brickDirtyFlags[b.brickIdx],
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
