using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis;
using Voxelis.Simulation;
using Voxelis.Utils;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Covers the two halves of graph-driven alien propagation: which bricks become query
    /// sources, and how a published graph's neighbors get their RequireUpdate flags.
    /// </summary>
    public unsafe class BrickOverlapDirtyPropagationTests
    {
        static readonly Guid128 SourceGuid = new Guid128(1, 0, 0, 0);
        static readonly Guid128 TargetGuid = new Guid128(2, 0, 0, 0);

        const DirtyFlags SourceFlag = DirtyFlags.Reserved1;
        const DirtyFlags MotionFlag = DirtyFlags.Reserved2;

        // ---------------------------------------------------------------- query building

        [Test]
        public void QueryCollectsOnlyDirtyBricksOfAStaticEntity()
        {
            using var entity = new EntityDataTestScope();
            entity.Data.isStatic = true;
            AddAllocatedBrick(entity, int3.zero, new int3(1, 0, 0));
            AddAllocatedBrick(entity, int3.zero, new int3(2, 0, 0));
            entity.Data.ClearDirtyFlags();
            MarkBrickDirty(entity, int3.zero, new int3(1, 0, 0), SourceFlag);

            using var world = new TickBufferScope((SourceGuid, entity, 0));
            BrickOverlapQueryRequest request = world.BuildQuery(DefaultSettings());

            try
            {
                Assert.That(request.SourceBrickCount, Is.EqualTo(1));
                List<VoxelBrickOverlapQuery> sources = ReadSources(request, 0);
                Assert.That(sources[0].BrickCoord, Is.EqualTo(new int3(1, 0, 0)));
                Assert.That(sources[0].Flags & (ushort)SourceFlag, Is.Not.EqualTo(0));
            }
            finally
            {
                request.Dispose();
            }
        }

        [Test]
        public void QueryCollectsEveryAllocatedBrickOfAMovingEntity()
        {
            using var entity = new EntityDataTestScope();
            entity.Data.isStatic = false;
            AddAllocatedBrick(entity, int3.zero, new int3(1, 0, 0));
            AddAllocatedBrick(entity, new int3(1, 0, 0), new int3(0, 0, 0));
            entity.Data.ClearDirtyFlags();

            using var world = new TickBufferScope((SourceGuid, entity, 0));
            BrickOverlapQueryRequest request = world.BuildQuery(DefaultSettings());

            try
            {
                Assert.That(request.SourceBrickCount, Is.EqualTo(2));
                List<VoxelBrickOverlapQuery> sources = ReadSources(request, 0);
                var coords = new List<int3>();
                for (int i = 0; i < sources.Count; i++)
                {
                    coords.Add(sources[i].BrickCoord);
                    Assert.That(sources[i].Flags, Is.EqualTo((ushort)MotionFlag),
                        "A clean brick of a moving entity carries only the motion mask.");
                }

                // The second sector starts one sector (16 bricks) along x.
                Assert.That(coords, Is.EquivalentTo(new[]
                {
                    new int3(1, 0, 0),
                    new int3(Sector.SIZE_IN_BRICKS, 0, 0)
                }));
            }
            finally
            {
                request.Dispose();
            }
        }

        [Test]
        public void QueryDropsCleanBricksWhenMotionIsExcluded()
        {
            using var entity = new EntityDataTestScope();
            entity.Data.isStatic = false;
            AddAllocatedBrick(entity, int3.zero, int3.zero);
            entity.Data.ClearDirtyFlags();

            using var world = new TickBufferScope((SourceGuid, entity, 0));
            BrickOverlapQuerySettings settings = DefaultSettings();
            settings.IncludeMovingBodies = false;

            BrickOverlapQueryRequest request = world.BuildQuery(settings);
            Assert.That(request.IsCreated, Is.False);
        }

        [Test]
        public void QueryKeepsBothFlagSourcesOnADirtyMovingBrick()
        {
            using var entity = new EntityDataTestScope();
            entity.Data.isStatic = false;
            AddAllocatedBrick(entity, int3.zero, int3.zero);
            entity.Data.ClearDirtyFlags();
            MarkBrickDirty(entity, int3.zero, int3.zero, SourceFlag);

            using var world = new TickBufferScope((SourceGuid, entity, 0));
            BrickOverlapQueryRequest request = world.BuildQuery(DefaultSettings());

            try
            {
                List<VoxelBrickOverlapQuery> sources = ReadSources(request, 0);
                Assert.That(sources[0].Flags,
                    Is.EqualTo((ushort)(SourceFlag | MotionFlag)));
            }
            finally
            {
                request.Dispose();
            }
        }

        // ---------------------------------------------------------------- propagation

        [Test]
        public void PropagationMarksTheAlienBrickWithTheSourceFlags()
        {
            using var scope = new PropagationScope();
            AddAllocatedBrick(scope.Source, int3.zero, int3.zero);
            AddAllocatedBrick(scope.Target, int3.zero, new int3(3, 0, 0));
            scope.Target.Data.ClearDirtyFlags();

            BrickOverlapPropagationStats stats = scope.Propagate(
                source: (int3.zero, SourceFlag),
                targetBrick: new int3(3, 0, 0));

            Assert.That(stats.OverlappingSourceBricks, Is.EqualTo(1));
            Assert.That(stats.MarkedBricks, Is.EqualTo(1));
            Assert.That(
                scope.Target.RequireFlagsAt(int3.zero, new int3(3, 0, 0)) & (ushort)SourceFlag,
                Is.Not.EqualTo(0));
            Assert.That(
                scope.Target.SectorAt(int3.zero).Get().sectorRequireUpdateFlags & (ushort)SourceFlag,
                Is.Not.EqualTo(0));
        }

        [Test]
        public void PropagationLeavesTargetDirtyFlagsAlone()
        {
            using var scope = new PropagationScope();
            AddAllocatedBrick(scope.Source, int3.zero, int3.zero);
            AddAllocatedBrick(scope.Target, int3.zero, int3.zero);
            scope.Target.Data.ClearDirtyFlags();

            scope.Propagate(source: (int3.zero, SourceFlag), targetBrick: int3.zero);

            Assert.That(scope.Target.SectorAt(int3.zero).Get().sectorDirtyFlags, Is.EqualTo(0));
        }

        [Test]
        public void PropagationSplitsGlobalBrickCoordinatesOfNegativeSectors()
        {
            using var scope = new PropagationScope();
            AddAllocatedBrick(scope.Source, int3.zero, int3.zero);
            // Global brick (-1,-1,-1) is the last brick of sector (-1,-1,-1).
            int3 targetSector = new int3(-1, -1, -1);
            int3 brickInSector = new int3(Sector.SIZE_IN_BRICKS - 1);
            AddAllocatedBrick(scope.Target, targetSector, brickInSector);
            scope.Target.Data.ClearDirtyFlags();

            BrickOverlapPropagationStats stats = scope.Propagate(
                source: (int3.zero, SourceFlag),
                targetBrick: new int3(-1, -1, -1));

            Assert.That(stats.MarkedBricks, Is.EqualTo(1));
            Assert.That(
                scope.Target.RequireFlagsAt(targetSector, brickInSector) & (ushort)SourceFlag,
                Is.Not.EqualTo(0));
        }

        [Test]
        public void PropagationSkipsAnUnallocatedTargetBrickSlot()
        {
            using var scope = new PropagationScope();
            AddAllocatedBrick(scope.Source, int3.zero, int3.zero);
            // The sector exists but the addressed brick slot was never allocated.
            scope.Target.AddSector(int3.zero);
            scope.Target.Data.ClearDirtyFlags();

            BrickOverlapPropagationStats stats = scope.Propagate(
                source: (int3.zero, SourceFlag),
                targetBrick: new int3(5, 0, 0));

            Assert.That(stats.MarkedBricks, Is.EqualTo(0));
            Assert.That(scope.Target.RequireFlagsAt(int3.zero, new int3(5, 0, 0)), Is.EqualTo(0));
        }

        [Test]
        public void PropagationSkipsAMissingTargetSector()
        {
            using var scope = new PropagationScope();
            AddAllocatedBrick(scope.Source, int3.zero, int3.zero);

            BrickOverlapPropagationStats stats = scope.Propagate(
                source: (int3.zero, SourceFlag),
                targetBrick: new int3(0, 0, 0));

            Assert.That(stats.MarkedBricks, Is.EqualTo(0));
            Assert.That(scope.Target.Data.sectors.Count, Is.EqualTo(0),
                "Propagation never allocates a sector on the target.");
        }

        // ---------------------------------------------------------------- helpers

        static BrickOverlapQuerySettings DefaultSettings()
        {
            return new BrickOverlapQuerySettings
            {
                FlagsToPropagate = DirtyFlags.All,
                MotionDirtyMask = MotionFlag,
                IncludeMovingBodies = true
            };
        }

        /// <summary>
        /// Allocates one brick. The write dirties it, so every test clears the entity before it
        /// marks the dirtiness it actually wants to observe.
        /// </summary>
        static void AddAllocatedBrick(EntityDataTestScope entity, int3 sectorPos, int3 brickPos)
        {
            SectorHandle sector = entity.Data.sectors.ContainsKey(sectorPos)
                ? entity.SectorAt(sectorPos)
                : entity.AddSector(sectorPos);

            int3 blockPos = brickPos * Sector.SIZE_IN_BLOCKS;
            sector.SetBlock(blockPos.x, blockPos.y, blockPos.z, new Block(1));
        }

        static void MarkBrickDirty(
            EntityDataTestScope entity, int3 sectorPos, int3 brickPos, DirtyFlags flags)
        {
            entity.SectorAt(sectorPos).Get().MarkBrickDirty(
                Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z), flags);
        }

        static List<VoxelBrickOverlapQuery> ReadSources(BrickOverlapQueryRequest request, int batchIndex)
        {
            var result = new List<VoxelBrickOverlapQuery>();
            NativeStream.Reader reader = request.Bricks.AsReader();
            int count = reader.BeginForEachIndex(batchIndex);
            for (int i = 0; i < count; i++)
            {
                result.Add(reader.Read<VoxelBrickOverlapQuery>());
            }
            reader.EndForEachIndex();
            return result;
        }

        /// <summary> Minimal WorldStageInputs holding the given entities and one body each. </summary>
        sealed class TickBufferScope : IDisposable
        {
            public VoxelisXWorld.WorldStageInputs Buf;

            public TickBufferScope(params (Guid128 Id, EntityDataTestScope Entity, int BodyIndex)[] entities)
            {
                Buf.VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(
                    math.max(1, entities.Length), Allocator.Persistent);
                Buf.VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(
                    math.max(1, entities.Length), Allocator.Persistent);

                foreach (var e in entities)
                {
                    Buf.VoxelEntities.Add(e.Id, e.Entity.Data);
                    var body = new VoxelBodyData(Allocator.Persistent)
                    {
                        _cached_body_index = e.BodyIndex
                    };
                    Buf.VoxelBodies.Add(e.Id, body);
                    if (!e.Entity.Data.isStatic)
                    {
                        Buf.nDynamicBodies++;
                    }
                }
            }

            public BrickOverlapQueryRequest BuildQuery(BrickOverlapQuerySettings settings)
            {
                return BrickOverlapQueryBuilder.Build(ref Buf, settings);
            }

            public void Dispose()
            {
                foreach (var kvp in Buf.VoxelBodies)
                {
                    VoxelBodyData body = kvp.Value;
                    body.Dispose();
                }

                Buf.VoxelBodies.Dispose();
                Buf.VoxelEntities.Dispose();
            }
        }

        /// <summary>
        /// Two entities, a hand-published graph, and the request that names its source bricks.
        /// Body 0 is the source, body 1 the target.
        /// </summary>
        sealed class PropagationScope : IDisposable
        {
            public readonly EntityDataTestScope Source = new EntityDataTestScope();
            public readonly EntityDataTestScope Target = new EntityDataTestScope();

            readonly BrickOverlapGraphBuilder m_Builder = new BrickOverlapGraphBuilder();
            NativeHashMap<Guid128, VoxelEntityData> m_Entities;

            public BrickOverlapPropagationStats Propagate(
                (int3 Brick, DirtyFlags Flags) source, int3 targetBrick)
            {
                if (m_Entities.IsCreated) m_Entities.Dispose();
                m_Entities = new NativeHashMap<Guid128, VoxelEntityData>(2, Allocator.Persistent);
                m_Entities.Add(SourceGuid, Source.Data);
                m_Entities.Add(TargetGuid, Target.Data);

                using var bodyIndexToGuid = new NativeArray<Guid128>(2, Allocator.Persistent);
                bodyIndexToGuid[0] = SourceGuid;
                bodyIndexToGuid[1] = TargetGuid;

                using var candidates = new NativeStream(1, Allocator.TempJob);
                NativeStream.Writer writer = candidates.AsWriter();
                writer.BeginForEachIndex(0);
                writer.Write(new VoxelBrickOverlapCandidate
                {
                    BodyIndexA = 0,
                    BrickCoordsInA = source.Brick,
                    BodyIndexB = 1,
                    BrickCoordsInB = targetBrick
                });
                writer.EndForEachIndex();

                m_Builder.BuildAndPublish(candidates, bodyIndexToGuid);

                var request = new BrickOverlapQueryRequest
                {
                    Batches = new NativeArray<VoxelBrickOverlapQueryBatch>(1, Allocator.TempJob),
                    Bricks = new NativeStream(1, Allocator.TempJob),
                    BatchEntityIds = new NativeArray<Guid128>(1, Allocator.TempJob),
                    SourceBrickCount = 1
                };

                try
                {
                    request.Batches[0] = new VoxelBrickOverlapQueryBatch { SourceBodyIndex = 0 };
                    request.BatchEntityIds[0] = SourceGuid;
                    NativeStream.Writer sourceWriter = request.Bricks.AsWriter();
                    sourceWriter.BeginForEachIndex(0);
                    sourceWriter.Write(new VoxelBrickOverlapQuery(source.Brick, (ushort)source.Flags));
                    sourceWriter.EndForEachIndex();

                    return BrickOverlapDirtyPropagation.Propagate(
                        m_Builder.Graph, request, ref m_Entities);
                }
                finally
                {
                    request.Dispose();
                }
            }

            public void Dispose()
            {
                if (m_Entities.IsCreated) m_Entities.Dispose();
                m_Builder.Dispose();
                Target.Dispose();
                Source.Dispose();
            }
        }
    }
}
