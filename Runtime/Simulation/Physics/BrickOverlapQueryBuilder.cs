using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    /// <summary>
    /// Selects the source bricks of one brick-overlap query and the flags each source
    /// carries to its alien neighbors.
    /// </summary>
    /// <remarks>
    /// A brick becomes a source when it is dirty, or when its entity moves and
    /// <see cref="IncludeMovingBodies"/> is set. Both conditions can hold; the emitted flags
    /// are then the union. Only alien-propagatable flag bits survive
    /// (<see cref="DirtyPropagationSettings.FilterCanPropagateToAlien"/>).
    ///
    /// Motion is the benchmark knob: it makes every allocated brick of every non-static
    /// entity a source, which is the heaviest input the graph can receive.
    /// </remarks>
    public struct BrickOverlapQuerySettings
    {
        /// <summary> Dirty bits a dirty source brick may hand to its alien neighbors. </summary>
        public DirtyFlags FlagsToPropagate;

        /// <summary> Flags a moving entity's bricks carry, independent of their dirty state. </summary>
        public DirtyFlags MotionDirtyMask;

        /// <summary>
        /// When set, every allocated brick of a non-static entity is a source. When clear,
        /// only dirty bricks are.
        /// </summary>
        public bool IncludeMovingBodies;

        public static BrickOverlapQuerySettings Default => new BrickOverlapQuerySettings
        {
            FlagsToPropagate = DirtyFlags.All,
            MotionDirtyMask = DirtyFlags.GeneralAutomata,
            IncludeMovingBodies = true
        };

        /// <summary> Clamps both masks to the alien-propagatable bits. </summary>
        public BrickOverlapQuerySettings Normalized()
        {
            return new BrickOverlapQuerySettings
            {
                FlagsToPropagate = (DirtyFlags)DirtyPropagationSettings.FilterCanPropagateToAlien(
                    (ushort)FlagsToPropagate),
                MotionDirtyMask = (DirtyFlags)DirtyPropagationSettings.FilterCanPropagateToAlien(
                    (ushort)MotionDirtyMask),
                IncludeMovingBodies = IncludeMovingBodies
            };
        }
    }

    /// <summary>
    /// One built query: the batch array and brick stream that
    /// <c>CollisionWorld.ScheduleVoxelBrickOverlaps</c> consumes, plus the GUID of each batch's
    /// source entity.
    /// </summary>
    /// <remarks>
    /// One batch per physics body, in body-index order. The stream survives the query so the
    /// propagation pass can re-read it: lane <c>i</c> holds the source bricks of
    /// <see cref="BatchEntityIds"/><c>[i]</c>, each with the flags that brick propagates.
    /// The caller owns and must dispose it.
    /// </remarks>
    public struct BrickOverlapQueryRequest : IDisposable
    {
        public NativeArray<VoxelBrickOverlapQueryBatch> Batches;
        public NativeStream Bricks;
        public NativeArray<Guid128> BatchEntityIds;

        /// <summary> Total source bricks across all batches. </summary>
        public int SourceBrickCount;

        public bool IsCreated => Bricks.IsCreated;

        public void Dispose()
        {
            if (Batches.IsCreated) Batches.Dispose();
            if (Bricks.IsCreated) Bricks.Dispose();
            if (BatchEntityIds.IsCreated) BatchEntityIds.Dispose();
        }
    }

    /// <summary>
    /// Builds the brick-overlap query input from the tick's voxel bodies.
    /// </summary>
    public static class BrickOverlapQueryBuilder
    {
        struct QueryBodyRef
        {
            public Guid128 EntityId;
            public int BodyIndex;
        }

        /// <summary>
        /// Collects this tick's source bricks into one batch per body. Returns a default
        /// (not created) request when there is nothing to query.
        /// </summary>
        /// <remarks>
        /// Body indices come from <c>VoxelBodyData._cached_body_index</c>, the same assignment
        /// the physics world build used, so the batches address the bodies of the step that
        /// just ran. Call between the physics step and the next world build.
        /// </remarks>
        public static BrickOverlapQueryRequest Build(
            ref VoxelisXWorld.WorldStageInputs tickBuf,
            BrickOverlapQuerySettings settings,
            Allocator allocator = Allocator.TempJob)
        {
            settings = settings.Normalized();
            if (settings.FlagsToPropagate == DirtyFlags.None &&
                (!settings.IncludeMovingBodies || settings.MotionDirtyMask == DirtyFlags.None))
            {
                return default;
            }

            int bodyCount = tickBuf.VoxelBodies.Count;
            if (bodyCount == 0)
            {
                return default;
            }

            var bodyRefs = new NativeArray<QueryBodyRef>(bodyCount, allocator);
            int batchCount = 0;
            foreach (var kvp in tickBuf.VoxelBodies)
            {
                if (!tickBuf.VoxelEntities.ContainsKey(kvp.Key))
                {
                    continue;
                }

                bodyRefs[batchCount++] = new QueryBodyRef
                {
                    EntityId = kvp.Key,
                    BodyIndex = kvp.Value._cached_body_index
                };
            }

            if (batchCount == 0)
            {
                bodyRefs.Dispose();
                return default;
            }

            var request = new BrickOverlapQueryRequest
            {
                Batches = new NativeArray<VoxelBrickOverlapQueryBatch>(batchCount, allocator),
                Bricks = new NativeStream(batchCount, allocator),
                BatchEntityIds = new NativeArray<Guid128>(batchCount, allocator)
            };

            var brickCounts = new NativeArray<int>(batchCount, Allocator.TempJob);
            new CollectSourceBricksJob
            {
                Bodies = bodyRefs,
                Entities = tickBuf.VoxelEntities,
                DirtyMask = (ushort)settings.FlagsToPropagate,
                MotionMask = settings.IncludeMovingBodies ? (ushort)settings.MotionDirtyMask : (ushort)0,
                Batches = request.Batches,
                BatchEntityIds = request.BatchEntityIds,
                BrickWriter = request.Bricks.AsWriter(),
                BrickCounts = brickCounts
            }.Schedule(batchCount, 1).Complete();

            int total = 0;
            for (int i = 0; i < batchCount; i++)
            {
                total += brickCounts[i];
            }

            request.SourceBrickCount = total;
            brickCounts.Dispose();
            bodyRefs.Dispose();

            if (total == 0)
            {
                // Nothing to query. Release the empty buffers rather than making the caller
                // schedule a traversal that cannot produce a candidate.
                request.Dispose();
                return default;
            }

            return request;
        }

        [BurstCompile]
        unsafe struct CollectSourceBricksJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<QueryBodyRef> Bodies;
            [ReadOnly] public NativeHashMap<Guid128, VoxelEntityData> Entities;
            [ReadOnly] public ushort DirtyMask;
            [ReadOnly] public ushort MotionMask;

            [WriteOnly] public NativeArray<VoxelBrickOverlapQueryBatch> Batches;
            [WriteOnly] public NativeArray<Guid128> BatchEntityIds;
            [WriteOnly] public NativeArray<int> BrickCounts;
            public NativeStream.Writer BrickWriter;

            public void Execute(int batchIndex)
            {
                QueryBodyRef body = Bodies[batchIndex];
                Batches[batchIndex] = new VoxelBrickOverlapQueryBatch
                {
                    SourceBodyIndex = body.BodyIndex
                };
                BatchEntityIds[batchIndex] = body.EntityId;

                NativeStream.Writer writer = BrickWriter;
                writer.BeginForEachIndex(batchIndex);
                BrickCounts[batchIndex] = WriteSourceBricks(body, ref writer);
                writer.EndForEachIndex();
            }

            int WriteSourceBricks(QueryBodyRef body, ref NativeStream.Writer writer)
            {
                if (!Entities.TryGetValue(body.EntityId, out VoxelEntityData entity))
                {
                    return 0;
                }

                // A static entity never contributes motion sources; its bricks must be dirty.
                ushort motionFlags = entity.isStatic ? (ushort)0 : MotionMask;
                int written = 0;

                foreach (var kvp in entity.sectors)
                {
                    if (kvp.Value.IsNull)
                    {
                        continue;
                    }

                    ref Sector sector = ref kvp.Value.Get();
                    ushort sectorDirty = (ushort)(sector.sectorDirtyFlags & DirtyMask);
                    if (sectorDirty == 0 && motionFlags == 0)
                    {
                        continue;
                    }

                    int3 sectorBrickOrigin = kvp.Key * Sector.SIZE_IN_BRICKS;
                    foreach (SectorNonEmptyBrickEnumerator.BrickRef brickRef in sector.EnumerateNonEmptyBricks())
                    {
                        int brickAbs = brickRef.BrickAbs;
                        ushort flags = motionFlags;
                        if (sectorDirty != 0)
                        {
                            flags |= (ushort)(sector.brickDirtyFlags[brickAbs] & DirtyMask);
                        }

                        if (flags == 0)
                        {
                            continue;
                        }

                        writer.Write(new VoxelBrickOverlapQuery(
                            sectorBrickOrigin + Sector.ToBrickPos((short)brickAbs), flags));
                        written++;
                    }
                }

                return written;
            }
        }
    }
}
