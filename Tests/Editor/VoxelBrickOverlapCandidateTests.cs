using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis;
using VoxelisX.Tests.TestSupport;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Scheduled-pipeline tests for the raw brick-overlap candidates produced by Unity Physics.
    /// These deliberately read the candidates after FinalExecutionHandle and before the next
    /// simulation reset, matching the public stream lifetime contract.
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

                // Match the production refresh order used by VoxelisXWorld. The overlap marker
                // consumes the refreshed allocated-brick list; voxel contacts additionally consume
                // PhysicsInfo and the physics-key bitmap.
                foreach (SectorHandle handle in m_Sectors.Values)
                {
                    ref Sector sector = ref handle.Get();
                    for (int brick = 0; brick < Sector.BRICKS_IN_SECTOR; brick++)
                    {
                        sector.MarkBrickRequireUpdate(brick, DirtyFlags.GeometryWithLocalNeighbor);
                    }
                }

                m_Scope.Data.RefreshNonEmptyMask(DirtyFlags.GeometryWithLocalNeighbor);

                var bodyData = new VoxelBodyData(Allocator.Persistent);
                try
                {
                    bodyData.ComputePhysicsProperties(
                        m_Scope.Data.sectors, m_Scope.Data.sectorNeighbors);
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
                    // VoxelCollider owns a persistent hash map stored inside its blob. Blob disposal
                    // alone cannot invoke the collider's custom Dispose method.
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

        sealed class StepObservation
        {
            public readonly List<VoxelBrickOverlapCandidate> Candidates =
                new List<VoxelBrickOverlapCandidate>();
            public int DynamicCandidateCount;
            public int StaticCandidateCount;
            public int VoxelContactCount;
            public bool DynamicStreamCreated;
            public bool StaticStreamCreated;
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

        static RigidBody Body(BlobAssetReference<Collider> collider, RigidTransform worldFromBody,
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

        static void SetDynamicMotion(PhysicsWorld world, int bodyIndex,
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

        static StepObservation RunScheduledStep(PhysicsWorldFixture fixture,
            bool multiThreaded)
        {
            ref PhysicsWorld world = ref fixture.World;
            world.CollisionWorld.BuildBroadphase(
                ref world, TimeStep, float3.zero, buildStaticTree: true);

            using var haveStaticBodiesChanged =
                new NativeReference<int>(1, Allocator.TempJob);
            var input = new SimulationStepInput
            {
                World = world,
                TimeStep = TimeStep,
                Gravity = float3.zero,
                NumSolverIterations = 4,
                NumSubsteps = 1,
                DirectSolverSettings = Solver.DirectSolverSettings.Default,
                HaveStaticBodiesChanged = haveStaticBodiesChanged
            };

            Unity.Physics.Simulation simulation = Unity.Physics.Simulation.Create();
            SimulationJobHandles handles = default;
            bool scheduled = false;
            try
            {
                handles = simulation.ScheduleStepJobs(input, default, multiThreaded);
                scheduled = true;
                handles.FinalExecutionHandle.Complete();

                VoxelBrickOverlapCandidates candidates =
                    simulation.VoxelBrickOverlapCandidates;
                var result = new StepObservation
                {
                    DynamicStreamCreated = candidates.DynamicStream.IsCreated,
                    StaticStreamCreated = candidates.StaticStream.IsCreated,
                    DynamicCandidateCount = candidates.DynamicStream.IsCreated
                        ? candidates.DynamicStream.Count()
                        : 0,
                    StaticCandidateCount = candidates.StaticStream.IsCreated
                        ? candidates.StaticStream.Count()
                        : 0
                };

                AppendCandidates(candidates.DynamicStream, result.Candidates);
                AppendCandidates(candidates.StaticStream, result.Candidates);

                // Simulation.Contacts is consumed while the full step builds Jacobians.
                // VoxelContactEvents is the post-step evidence that contact handling ran.
                foreach (VoxelContactEvent unused in simulation.VoxelContactEvents)
                {
                    result.VoxelContactCount++;
                }

                return result;
            }
            finally
            {
                if (scheduled)
                {
                    handles.FinalDisposeHandle.Complete();
                }
                simulation.Dispose();
            }
        }

        static void AppendCandidates(NativeStream stream,
            List<VoxelBrickOverlapCandidate> destination)
        {
            if (!stream.IsCreated)
            {
                return;
            }

            NativeStream.Reader reader = stream.AsReader();
            for (int workItem = 0; workItem < stream.ForEachCount; workItem++)
            {
                int count = reader.BeginForEachIndex(workItem);
                for (int i = 0; i < count; i++)
                {
                    destination.Add(reader.Read<VoxelBrickOverlapCandidate>());
                }
                reader.EndForEachIndex();
            }
        }

        static void AssertEndpoint(VoxelBrickOverlapCandidate candidate, int bodyIndex,
            int3 expectedBrick)
        {
            int3 actual;
            if (candidate.BodyIndexA == bodyIndex)
            {
                actual = candidate.BrickCoordsInA;
            }
            else if (candidate.BodyIndexB == bodyIndex)
            {
                actual = candidate.BrickCoordsInB;
            }
            else
            {
                Assert.Fail($"Candidate does not reference body {bodyIndex}.");
                return;
            }

            Assert.That(actual, Is.EqualTo(expectedBrick),
                $"Brick coordinate was not kept with body {bodyIndex}.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaticStatic_ZeroDynamicBodies_EmitsAllocatedBrickCandidateWithoutContacts(
            bool multiThreaded)
        {
            using var body0 = new VoxelColliderFixture();
            using var body1 = new VoxelColliderFixture();

            // Sector -1, local block 120 is global block -8 and therefore global brick -1.
            body0.SetBlock(new int3(-1, 0, 0), new int3(120, 0, 0), new Block(1));

            // Allocate body1's brick, then empty it. Allocated-but-empty bricks intentionally
            // participate in the conservative graph.
            body1.SetBlock(int3.zero, int3.zero, new Block(1));
            body1.SetBlock(int3.zero, int3.zero, Block.Empty);
            body0.Build();
            body1.Build();

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(body0.Collider,
                new RigidTransform(quaternion.identity, new float3(8f, 1f, 0f)), 1);
            bodies[1] = Body(body1.Collider, RigidTransform.identity, 2);

            StepObservation result = RunScheduledStep(world, multiThreaded);

            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.DynamicStreamCreated, Is.False,
                "The zero-dynamic path should expose an absent dynamic stream as empty.");
            Assert.That(result.StaticStreamCreated, Is.True);
            Assert.That(result.DynamicCandidateCount, Is.Zero);
            Assert.That(result.StaticCandidateCount, Is.EqualTo(1));
            Assert.That(result.VoxelContactCount, Is.Zero);

            VoxelBrickOverlapCandidate candidate = result.Candidates[0];
            Assert.That(new[] { candidate.BodyIndexA, candidate.BodyIndexB },
                Is.EquivalentTo(new[] { 0, 1 }));
            AssertEndpoint(candidate, 0, new int3(-1, 0, 0));
            AssertEndpoint(candidate, 1, int3.zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DynamicPairs_EmitCandidatesAndRetainVoxelContacts(bool secondBodyIsStatic)
        {
            using var body0 = new VoxelColliderFixture();
            using var body1 = new VoxelColliderFixture();

            // Body 0's occupied cell is in global brick 1. Its additional empty sector makes
            // it larger by the implementation's sector-count heuristic and forces the internal
            // A/B role swap; the emitted endpoint must still remain attached to body index 0.
            body0.SetBlock(int3.zero, new int3(8, 0, 0), new Block(1));
            body0.AddSector(new int3(2, 0, 0));
            body1.SetBlock(int3.zero, int3.zero, new Block(1));
            body0.Build();
            body1.Build();

            int staticBodies = secondBodyIsStatic ? 1 : 0;
            int dynamicBodies = secondBodyIsStatic ? 1 : 2;
            using var world = new PhysicsWorldFixture(staticBodies, dynamicBodies);

            var body0Transform = new RigidTransform(
                quaternion.identity, new float3(-8f, 1f, 0f));
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(body0.Collider, body0Transform, 1);
            bodies[1] = Body(body1.Collider, RigidTransform.identity, 2);
            SetDynamicMotion(world.World, 0, body0Transform);
            if (!secondBodyIsStatic)
            {
                SetDynamicMotion(world.World, 1, RigidTransform.identity);
            }

            StepObservation result = RunScheduledStep(world, multiThreaded: false);

            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.DynamicStreamCreated, Is.True);
            Assert.That(result.DynamicCandidateCount, Is.EqualTo(1));
            Assert.That(result.StaticCandidateCount, Is.Zero);
            Assert.That(result.VoxelContactCount, Is.GreaterThan(0),
                "Candidate emission must not replace the normal voxel contact path.");

            VoxelBrickOverlapCandidate candidate = result.Candidates[0];
            AssertEndpoint(candidate, 0, new int3(1, 0, 0));
            AssertEndpoint(candidate, 1, int3.zero);
        }

        [Test]
        public void StaticStatic_CollisionFiltersSuppressCandidates()
        {
            using var body0 = new VoxelColliderFixture();
            using var body1 = new VoxelColliderFixture();
            body0.SetBlock(int3.zero, int3.zero, new Block(1));
            body1.SetBlock(int3.zero, int3.zero, new Block(1));

            var filter0 = new CollisionFilter
            {
                BelongsTo = 1u,
                CollidesWith = 1u,
                GroupIndex = 0
            };
            var filter1 = new CollisionFilter
            {
                BelongsTo = 2u,
                CollidesWith = 2u,
                GroupIndex = 0
            };
            body0.Build(filter0, Material.Default);
            body1.Build(filter1, Material.Default);

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(body0.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(body1.Collider, RigidTransform.identity, 2);

            StepObservation result = RunScheduledStep(world, multiThreaded: false);

            Assert.That(result.Candidates, Is.Empty);
            Assert.That(result.StaticCandidateCount, Is.Zero);
        }

        [Test]
        public void StaticStatic_CollisionResponseNoneSuppressesCandidates()
        {
            using var body0 = new VoxelColliderFixture();
            using var body1 = new VoxelColliderFixture();
            body0.SetBlock(int3.zero, int3.zero, new Block(1));
            body1.SetBlock(int3.zero, int3.zero, new Block(1));

            Material noResponse = Material.Default;
            noResponse.CollisionResponse = CollisionResponsePolicy.None;
            body0.Build();
            body1.Build(CollisionFilter.Default, noResponse);

            using var world = new PhysicsWorldFixture(2, 0);
            NativeArray<RigidBody> bodies = world.World.Bodies;
            bodies[0] = Body(body0.Collider, RigidTransform.identity, 1);
            bodies[1] = Body(body1.Collider, RigidTransform.identity, 2);

            StepObservation result = RunScheduledStep(world, multiThreaded: false);

            Assert.That(result.Candidates, Is.Empty);
            Assert.That(result.StaticCandidateCount, Is.Zero);
        }
    }
}
