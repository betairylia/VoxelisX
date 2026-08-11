using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    /// <summary> Counters of the last alien propagation pass over the brick-overlap graph. </summary>
    public struct BrickOverlapPropagationStats
    {
        /// <summary> Source bricks submitted to the query. </summary>
        public int SourceBricks;

        /// <summary> Source bricks that had at least one alien neighbor in the graph. </summary>
        public int OverlappingSourceBricks;

        /// <summary> Alien bricks whose RequireUpdate flags were widened. </summary>
        public int MarkedBricks;
    }

    /// <summary>
    /// Alien dirty propagation over the post-physics brick-overlap graph.
    ///
    /// Each source brick hands the flags it was queried with to every alien brick the graph
    /// pairs it with, exactly as the previous spatial-hash pass did: the target brick's
    /// RequireUpdate flags widen, its dirty flags do not, and no sector or brick is allocated.
    /// </summary>
    public static class BrickOverlapDirtyPropagation
    {
        /// <summary>
        /// Marks the alien neighbors of every source brick in <paramref name="request"/>.
        /// Does nothing when the graph is empty or the request carries no source bricks.
        /// </summary>
        /// <remarks>
        /// The graph must be the one published for this request: it keys sources by
        /// (entity GUID, global brick coordinate), so a stale graph silently propagates the
        /// previous step's overlaps. Marking runs serially — target bricks are shared between
        /// sources and the flag merge is a plain read-modify-write.
        /// </remarks>
        public static BrickOverlapPropagationStats Propagate(
            BrickOverlapGraph graph,
            in BrickOverlapQueryRequest request,
            ref NativeHashMap<Guid128, VoxelEntityData> entities)
        {
            var stats = new BrickOverlapPropagationStats();
            if (!graph.IsCreated || graph.SourceCount == 0 || !request.IsCreated)
            {
                return stats;
            }

            stats.SourceBricks = request.SourceBrickCount;

            using var counters = new NativeArray<int>(2, Allocator.TempJob);
            new MarkAlienRequireUpdatesJob
            {
                BatchEntityIds = request.BatchEntityIds,
                BrickReader = request.Bricks.AsReader(),
                Graph = graph,
                Entities = entities,
                Counters = counters
            }.Schedule().Complete();

            stats.OverlappingSourceBricks = counters[0];
            stats.MarkedBricks = counters[1];
            return stats;
        }

        [BurstCompile]
        unsafe struct MarkAlienRequireUpdatesJob : IJob
        {
            [ReadOnly] public NativeArray<Guid128> BatchEntityIds;
            [ReadOnly] public NativeHashMap<Guid128, VoxelEntityData> Entities;
            public NativeStream.Reader BrickReader;
            public BrickOverlapGraph Graph;

            /// <summary> [0] = sources with neighbors, [1] = marked target bricks. </summary>
            [WriteOnly] public NativeArray<int> Counters;

            public void Execute()
            {
                int overlappingSources = 0;
                int markedBricks = 0;

                for (int batchIndex = 0; batchIndex < BatchEntityIds.Length; batchIndex++)
                {
                    var source = new BrickOverlapKey { EntityId = BatchEntityIds[batchIndex] };
                    int brickCount = BrickReader.BeginForEachIndex(batchIndex);

                    for (int i = 0; i < brickCount; i++)
                    {
                        VoxelBrickOverlapQuery query = BrickReader.Read<VoxelBrickOverlapQuery>();
                        if (query.Flags == 0)
                        {
                            continue;
                        }

                        source.BrickCoord = query.BrickCoord;
                        if (!Graph.TryGetOverlaps(source, out BrickOverlapEnumerator neighbors))
                        {
                            continue;
                        }

                        overlappingSources++;
                        foreach (BrickOverlapKey neighbor in neighbors)
                        {
                            if (MarkBrick(neighbor, query.Flags))
                            {
                                markedBricks++;
                            }
                        }
                    }

                    BrickReader.EndForEachIndex();
                }

                Counters[0] = overlappingSources;
                Counters[1] = markedBricks;
            }

            /// <summary>
            /// Widens one alien brick's RequireUpdate flags. Returns false when the brick is
            /// gone: propagation never creates a sector or a brick.
            /// </summary>
            bool MarkBrick(BrickOverlapKey key, ushort flags)
            {
                if (!Entities.TryGetValue(key.EntityId, out VoxelEntityData entity))
                {
                    return false;
                }

                // Arithmetic shift and mask are the floor-division pair the physics-side query
                // used to split a global brick coordinate, so negative sectors round the same way.
                int3 sectorCoord = key.BrickCoord >> Sector.SHIFT_IN_BRICKS;
                if (!entity.sectors.TryGetValue(sectorCoord, out SectorHandle handle) || handle.IsNull)
                {
                    return false;
                }

                int3 brickInSector = key.BrickCoord & Sector.SECTOR_MASK;
                int brickIdx = Sector.ToBrickIdx(brickInSector.x, brickInSector.y, brickInSector.z);

                ref Sector sector = ref handle.Get();
                if (sector.brickIdx[brickIdx] == Sector.BRICKID_EMPTY)
                {
                    return false;
                }

                sector.MarkBrickRequireUpdate(brickIdx, (DirtyFlags)flags);
                return true;
            }
        }
    }
}
