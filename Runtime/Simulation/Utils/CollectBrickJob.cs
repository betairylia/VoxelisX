using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Caelix;
using Caelix.Utils;

namespace Caelix.Simulation
{
    /// <summary>
    /// Collects every brick that carries a require-update flag, across all entities, into one
    /// list for the automata stage. A Burst job rather than a Burst direct call: jobs compile
    /// asynchronously with a managed fallback, while a direct call compiles synchronously on the
    /// main thread the first time it runs, which blocks the editor.
    /// </summary>
    [BurstCompile]
    public unsafe struct CollectBrickJob : IJob
    {
        [ReadOnly] public NativeHashMap<Guid128, VoxelEntityData> entities;
        public NativeList<BrickInfo> brickList;

        public void Execute()
        {
            foreach (var kvp in entities)
            {
                var e = kvp.Value;
                foreach (var s in e.sectors)
                {
                    ref Sector sec = ref s.Value.Get();
                    SectorRequiredUpdateBrickEnumerator enumerator =
                        new SectorRequiredUpdateBrickEnumerator(sec, DirtyFlags.All, true);
                    foreach (var b in enumerator)
                    {
                        brickList.Add(new BrickInfo
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

    public static class BrickCollector
    {
        /// <summary>Runs the collection on the calling thread and returns when the list is filled.</summary>
        public static void Collect(
            ref NativeHashMap<Guid128, VoxelEntityData> entities,
            ref NativeList<BrickInfo> brickList)
        {
            new CollectBrickJob { entities = entities, brickList = brickList }.Run();
        }
    }
}
