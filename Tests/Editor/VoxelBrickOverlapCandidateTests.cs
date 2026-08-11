using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Tests the explicit post-simulation brick query API. These queries are not part of
    /// narrowphase or the regular simulation step.
    /// </summary>
    public unsafe class VoxelBrickOverlapCandidateTests
    {
        const float TimeStep = 1f / 60f;

        sealed class VoxelColliderFixture : IDisposable
        {
            readonly EntityDataTestScope m_Scope = new EntityDataTestScope();
            readonly Dictionary<int3, SectorHandle> m_Sectors =
                new Dictionary<int3, SectorHandle>();

            public BlobAssetReference<Collider> Collider { get; private set; }

            public SectorHandle AddSector(int3 sectorCoord)
            {
                if (m_Sectors.TryGetValue(sectorCoord, out SectorHandle existing))
                {
                    return existing;
                }

                SectorHandle handle = m_Scope.AddSector(sectorCoord);
                m_Sectors.Add(sectorCoord, handle);
                return handle;
            }

            public void SetBlock(int3 sectorCoord, int3 localBlock, Block block)
            {
                SectorHandle sector = AddSector(sectorCoord);
                sector.SetBlock(localBlock.x, localBlock.y, localBlock.z, block);
            }

            public void Build()
            {
                Build(CollisionFilter.Default, Material.Default);
            }

            public void Build(CollisionFilter filter, Material material)
            {
                if (Collider.IsCreated)
                {
                    throw new InvalidOperationException("The voxel collider fixture was already built.");
                }

                // Match production: the overlap query consumes allocated bricks, while regular
                // physics continues to consume refreshed occupancy and physics-key masks.
                foreach (SectorHandle handle in m_Sectors.Values)
                {
                    ref Sector sector = ref handle.Get();
                    for (int brick = 0; brick < Sector.BRICKS_IN_SECTOR; brick++)
                    {
                        sector.MarkBrickRequireUpdate(
                            brick, DirtyFlags.GeometryWithLocalNeighbor);
                    }
                }

                m_Scope.Data.RefreshNonEmptyMask(DirtyFlags.GeometryWithLocalNeighbor);

                var bodyData = new VoxelBodyData(Allocator.Persistent);
                try
                {
                    bodyData.ComputePhysicsProperties(m_Scope.Data);
                    bodyData.RefreshPhysicsKeyMask(m_Scope.Data.sectors);
                }
                finally
                {
                    bodyData.Dispose();
                }

                Collider = VoxelCollider.Create(m_Sectors, filter, material);
            }

            public void Dispose()
            {
                if (Collider.IsCreated)
                {
                    // VoxelCollider owns a persistent hash map stored inside its blob. Blob
                    // disposal alone cannot invoke the collider's custom Dispose method.
                    var voxel = (VoxelCollider*)Collider.GetUnsafePtr();
                    if (voxel->m_Sectors.IsCreated)
                    {
                        voxel->Dispose();
                    }
                    Collider.Dispose();
                }

                m_Scope.Dispose();
            }
        }

        sealed class PhysicsWorldFixture : IDisposable
        {
            public PhysicsWorld World;

            public PhysicsWorldFixture(int staticBodies, int dynamicBodies)
            {
                World = new PhysicsWorld(staticBodies, dynamicBodies, 0);
            }

            public void Dispose()
            {
                World.Dispose();
            }
        }

        struct BuildZeroBrickQueryBatchesJob : IJobParallelFor
        {
            public NativeArray<VoxelBrickOverlapQueryBatch> Batches;
            public NativeStream.Writer QueryWriter;

            public void Execute(int batchIndex)
            {
                Batches[batchIndex] = new VoxelBrickOverlapQueryBatch
                {
                    SourceBodyIndex = batchIndex
                };

                NativeStream.Writer writer = QueryWriter;
                writer.BeginForEachIndex(batchIndex);
                writer.Write(new VoxelBrickOverlapQuery(int3.zero, (ushort)(batchIndex + 1)));
                writer.EndForEachIndex();
            }
        }

        static RigidBody Body(
            BlobAssetReference<Collider> collider,
            RigidTransform worldFromBody,
            int entityIndex)
        {
            return new RigidBody
            {
                Collider = collider,
                WorldFromBody = worldFromBody,
                Entity = new Entity { Index = entityIndex, Version = 1 },
                Scale = 1f,
                SolverType = SolverType.Iterative
            };
        }

        static void SetDynamicMotion(
            PhysicsWorld world,
            int bodyIndex,
            RigidTransform worldFromBody)
        {
            NativeArray<MotionData> motionDatas = world.MotionDatas;
            motionDatas[bodyIndex] = new MotionData
            {
                WorldFromMotion = worldFromBody,
                BodyFromMotion = RigidTransform.identity,
                LinearDamping = 0f,
                AngularDamping = 0f
            };

            NativeArray<MotionVelocity> motionVelocities = world.MotionVelocities;
            motionVelocities[bodyIndex] = new MotionVelocity
            {
                LinearVelocity = float3.zero,
                AngularVelocity = float3.zero,
                InverseInertia = new float3(1f),
                InverseMass = 1f,
                AngularExpansionFactor = 0f,
                GravityFactor = 0f
            };
        }

        static void BuildBroadphase(PhysicsWorldFixture fixture)
        {
            ref PhysicsWorld world = ref fixture.World;
            world.CollisionWorld.BuildBroadphase(
                ref world, TimeStep, float3.zero, buildStaticTree: true);
        }

        static List<VoxelBrickOverlapCandidate> RunQuery(
            PhysicsWorldFixture fixture,
            params (int BodyIndex, int3 Brick)[] queries)
        {
            using var batches = new NativeArray<VoxelBrickOverlapQueryBatch>(
                queries.Length, Allocator.TempJob);
            NativeArray<VoxelBrickOverlapQueryBatch> batchWriter = batches;
            using var queryStream = new NativeStream(
                math.max(1, queries.Length), Allocator.TempJob);
            NativeStream.Writer writer = queryStream.AsWriter();

            for (int batchIndex = 0; batchIndex < queryStream.ForEachCount; batchIndex++)
            {
                writer.BeginForEachIndex(batchIndex);
                if (batchIndex < queries.Length)
                {
                    batchWriter[batchIndex] = new VoxelBrickOverlapQueryBatch
                    {
                        SourceBodyIndex = queries[batchIndex].BodyIndex
                    };
                    writer.Write(new VoxelBrickOverlapQuery(
                        queries[batchIndex].Brick, (ushort)(batchIndex + 1)));
                }
                writer.EndForEachIndex();
            }

            return RunQuery(fixture, batches, queryStream, default);
        }

        static List<VoxelBrickOverlapCandidate> RunQueryBatch(
            PhysicsWorldFixture fixture,
            int sourceBodyIndex,
            params VoxelBrickOverlapQuery[] queries)
        {
            using var batches = new NativeArray<VoxelBrickOverlapQueryBatch>(
                1, Allocator.TempJob);
            NativeArray<VoxelBrickOverlapQueryBatch> batchWriter = batches;
            batchWriter[0] = new VoxelBrickOverlapQueryBatch
            {
                SourceBodyIndex = sourceBodyIndex
            };

            using var queryStream = new NativeStream(1, Allocator.TempJob);
            NativeStream.Writer writer = queryStream.AsWriter();
            writer.BeginForEachIndex(0);
            for (int i = 0; i < queries.Length; i++)
            {
                writer.Write(queries[i]);
            }
            writer.EndForEachIndex();

            return RunQuery(fixture, batches, queryStream, default);
        }

        static List<VoxelBrickOverlapCandidate> RunQuery(
            PhysicsWorldFixture fixture,
            NativeArray<VoxelBrickOverlapQueryBatch> queryBatches,
            NativeStream queryStream,
            JobHandle inputDeps)
        {
            JobHandle handle = fixture.World.CollisionWorld.ScheduleVoxelBrickOverlaps(
                queryBatches, queryStream, out NativeStream stream, inputDeps);
            handle.Complete();

            try
            {
                var result = new List<VoxelBrickOverlapCandidate>();
                NativeStream.Reader reader = stream.AsReader();
                for (int lane = 0; lane < stream.ForEachCount; lane++)
                {
                    int count = reader.BeginForEachIndex(lane);
                    for (int i = 0; i < count; i++)
                    {
                        result.Add(reader.Read<VoxelBrickOverlapCandidate>());
                    }
                    reader.EndForEachIndex();
                }
                return result;
            }
            finally
            {
                stream.Dispose();
            }
        }

        [Test]
        public void QueryRecordHasStableSixteenByteLayout()
        {
            Assert.That(UnsafeUtility.SizeOf<VoxelBrickOverlapQuery>(), Is.EqualTo(16));
        }

        static void AssertCandidate(
            VoxelBrickOverlapCandidate candidate,
            int sourceBody,
            int3 sourceBrick,
            int targetBody,
            int3 targetBrick)
        {
            Assert.That(candidate.BodyIndexA, Is.EqualTo(sourceBody));
            Assert.That(candidate.BrickCoordsInA, Is.EqualTo(sourceBrick));
            Assert.That(candidate.BodyIndexB, Is.EqualTo(targetBody));
            Assert.That(candidate.BrickCoordsInB, Is.EqualTo(targetBrick));
        }

        [Test]
        public void StaticStatic_ZeroDynamicBodies_QueriesAllocatedEmptyTarget()
        {
            using var source = new VoxelColliderFixture();
            using var target = new VoxelColliderFixture();

            // Sector -1, local block 120 is global block -8 and global brick -1.
            source.SetBlock(new int3(-1, 0, 0), new int3(120, 0, 0), new Block(1));

            // Allocated-but-empty target bricks intentionally participate.
            target.SetBlock(int3.zero, int3.zero, new Block(1));
            target.SetBlock(int3.zero, int3.zero, Block.Empty);
            source.Build();
            target.Build();

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(source.Collider,
                new RigidTransform(quaternion.identity, new float3(8f, 1f, 0f)), 1);
            bodies[1] = Body(target.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            List<VoxelBrickOverlapCandidate> result =
                RunQuery(world, (0, new int3(-1, 0, 0)));

            Assert.That(result, Has.Count.EqualTo(1));
            AssertCandidate(result[0], 0, new int3(-1, 0, 0), 1, int3.zero);
        }

        [Test]
        public void QueryOnlyEmitsPairsForSubmittedSourceBricks()
        {
            using var source = new VoxelColliderFixture();
            using var target = new VoxelColliderFixture();
            source.SetBlock(int3.zero, int3.zero, new Block(1));
            source.SetBlock(int3.zero, new int3(8, 0, 0), new Block(1));
            target.SetBlock(int3.zero, int3.zero, new Block(1));
            target.SetBlock(int3.zero, new int3(8, 0, 0), new Block(1));
            source.Build();
            target.Build();

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(source.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(target.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            List<VoxelBrickOverlapCandidate> result =
                RunQuery(world, (0, int3.zero));

            Assert.That(result, Is.Not.Empty);
            for (int i = 0; i < result.Count; i++)
            {
                Assert.That(result[i].BodyIndexA, Is.EqualTo(0));
                Assert.That(result[i].BrickCoordsInA, Is.EqualTo(int3.zero));
            }
        }

        [Test]
        public void OneBatchMayContainArbitrarilyGroupedBricksFromOneBody()
        {
            using var source = new VoxelColliderFixture();
            using var target = new VoxelColliderFixture();
            source.SetBlock(int3.zero, int3.zero, new Block(1));
            source.SetBlock(int3.zero, new int3(16, 0, 0), new Block(1));
            target.SetBlock(int3.zero, int3.zero, new Block(1));
            target.SetBlock(int3.zero, new int3(16, 0, 0), new Block(1));
            source.Build();
            target.Build();

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(source.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(target.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            List<VoxelBrickOverlapCandidate> result = RunQueryBatch(
                world,
                0,
                new VoxelBrickOverlapQuery(int3.zero, 0x0001),
                new VoxelBrickOverlapQuery(new int3(2, 0, 0), 0x8000));

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.Exists(candidate =>
                candidate.BodyIndexA == 0 &&
                candidate.BodyIndexB == 1 &&
                candidate.BrickCoordsInA.Equals(int3.zero) &&
                candidate.BrickCoordsInB.Equals(int3.zero)), Is.True);
            Assert.That(result.Exists(candidate =>
                candidate.BodyIndexA == 0 &&
                candidate.BodyIndexB == 1 &&
                candidate.BrickCoordsInA.Equals(new int3(2, 0, 0)) &&
                candidate.BrickCoordsInB.Equals(new int3(2, 0, 0))), Is.True);
        }

        [Test]
        public void UnallocatedSourceBrickMayOverlapAllocatedTargetBrick()
        {
            using var source = new VoxelColliderFixture();
            using var target = new VoxelColliderFixture();

            // Brick 1 keeps the source collider valid while queried brick 0 stays unallocated.
            source.SetBlock(int3.zero, new int3(8, 0, 0), new Block(1));
            target.SetBlock(int3.zero, int3.zero, new Block(1));
            source.Build();
            target.Build();

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(source.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(target.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            List<VoxelBrickOverlapCandidate> result = RunQueryBatch(
                world, 0, new VoxelBrickOverlapQuery(int3.zero, 0x0040));

            Assert.That(result, Has.Count.EqualTo(1));
            AssertCandidate(result[0], 0, int3.zero, 1, int3.zero);
        }

        [Test]
        public void ParallelBatchInputMayEmitReversedRawDuplicatesWithoutProducerCompletion()
        {
            using var body0 = new VoxelColliderFixture();
            using var body1 = new VoxelColliderFixture();
            body0.SetBlock(int3.zero, int3.zero, new Block(1));
            body1.SetBlock(int3.zero, int3.zero, new Block(1));
            body0.Build();
            body1.Build();

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(body0.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(body1.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            using var batches = new NativeArray<VoxelBrickOverlapQueryBatch>(
                2, Allocator.TempJob);
            using var queryStream = new NativeStream(2, Allocator.TempJob);
            JobHandle producer = new BuildZeroBrickQueryBatchesJob
            {
                Batches = batches,
                QueryWriter = queryStream.AsWriter()
            }.Schedule(2, 1);

            // ScheduleVoxelBrickOverlaps consumes both inputs behind the producer dependency;
            // no producer.Complete() is required at this boundary.
            List<VoxelBrickOverlapCandidate> result =
                RunQuery(world, batches, queryStream, producer);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.Exists(candidate =>
                candidate.BodyIndexA == 0 && candidate.BodyIndexB == 1), Is.True);
            Assert.That(result.Exists(candidate =>
                candidate.BodyIndexA == 1 && candidate.BodyIndexB == 0), Is.True);
        }

        [Test]
        public void CollisionFiltersSuppressQueryTargets()
        {
            using var source = new VoxelColliderFixture();
            using var target = new VoxelColliderFixture();
            source.SetBlock(int3.zero, int3.zero, new Block(1));
            target.SetBlock(int3.zero, int3.zero, new Block(1));

            source.Build(new CollisionFilter
            {
                BelongsTo = 1u,
                CollidesWith = 1u,
                GroupIndex = 0
            }, Material.Default);
            target.Build(new CollisionFilter
            {
                BelongsTo = 2u,
                CollidesWith = 2u,
                GroupIndex = 0
            }, Material.Default);

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(source.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(target.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            Assert.That(RunQuery(world, (0, int3.zero)), Is.Empty);
        }

        [Test]
        public void CollisionResponseNoneSuppressesQueryTargets()
        {
            using var source = new VoxelColliderFixture();
            using var target = new VoxelColliderFixture();
            source.SetBlock(int3.zero, int3.zero, new Block(1));
            target.SetBlock(int3.zero, int3.zero, new Block(1));

            Material noResponse = Material.Default;
            noResponse.CollisionResponse = CollisionResponsePolicy.None;
            source.Build();
            target.Build(CollisionFilter.Default, noResponse);

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(source.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(target.Collider, RigidTransform.identity, 2);
            BuildBroadphase(world);

            Assert.That(RunQuery(world, (0, int3.zero)), Is.Empty);
        }

        [Test]
        public void DynamicTreeUpdateMakesPostSolverPoseQueryable()
        {
            using var dynamicTarget = new VoxelColliderFixture();
            using var staticSource = new VoxelColliderFixture();
            dynamicTarget.SetBlock(int3.zero, int3.zero, new Block(1));
            staticSource.SetBlock(int3.zero, int3.zero, new Block(1));
            dynamicTarget.Build();
            staticSource.Build();

            using var world = new PhysicsWorldFixture(1, 1);
            var farAway = new RigidTransform(
                quaternion.identity, new float3(256f, 0f, 0f));

            // Dynamic bodies precede static bodies in PhysicsWorld.Bodies.
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(dynamicTarget.Collider, farAway, 1);
            bodies[1] = Body(staticSource.Collider, RigidTransform.identity, 2);
            SetDynamicMotion(world.World, 0, farAway);
            BuildBroadphase(world);

            Assert.That(RunQuery(world, (1, int3.zero)), Is.Empty);

            NativeArray<MotionData> motionDatas = world.World.MotionDatas;
            MotionData motion = motionDatas[0];
            motion.WorldFromMotion = RigidTransform.identity;
            motionDatas[0] = motion;

            ref PhysicsWorld physicsWorld = ref world.World;
            physicsWorld.CollisionWorld.UpdateDynamicTree(
                ref physicsWorld, TimeStep, float3.zero);

            List<VoxelBrickOverlapCandidate> result =
                RunQuery(world, (1, int3.zero));
            Assert.That(result, Has.Count.EqualTo(1));
            AssertCandidate(result[0], 1, int3.zero, 0, int3.zero);
        }
    }
}
