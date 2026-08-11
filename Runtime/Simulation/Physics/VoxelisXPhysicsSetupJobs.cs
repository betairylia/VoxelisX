using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine;
using UnityEngine.Profiling;
using Voxelis.Tick;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    public static class VoxelisXPhysicsInterface
    {
        [BurstCompile]
        public struct BuildBodyIndexByGuidJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Guid128> BodyIndexToGuid;
            public NativeParallelHashMap<Guid128, int>.ParallelWriter BodyIndexByGuid;

            public void Execute(int bodyIndex)
            {
                BodyIndexByGuid.TryAdd(BodyIndexToGuid[bodyIndex], bodyIndex);
            }
        }

        /// <summary>
        /// Maps stable voxel-side brick keys to this step's transient physics body indices.
        /// The multi-map is intentionally parallel-writable because future dirty/persistent
        /// collectors can feed it directly without first producing a serial flat list.
        /// </summary>
        [BurstCompile]
        public struct BuildBrickOverlapQueriesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<VoxelisXWorld.BrickInfo> SourceBricks;
            [ReadOnly] public NativeParallelHashMap<Guid128, int> BodyIndexByGuid;
            public NativeParallelMultiHashMap<int, int3>.ParallelWriter Queries;

            public void Execute(int index)
            {
                VoxelisXWorld.BrickInfo source = SourceBricks[index];
                if (!BodyIndexByGuid.TryGetValue(source.EntityId, out int bodyIndex))
                {
                    return;
                }

                int3 brickCoord = source.SectorPos * Sector.SIZE_IN_BRICKS +
                    (source.BrickOrigin >> Sector.SHIFT_IN_BLOCKS);
                Queries.Add(bodyIndex, brickCoord);
            }
        }

        // TODO: Parallelization
        [BurstCompile]
        public struct FillPhysicsWorldJob : IJob
        {
            [ReadOnly] public VoxelisXWorld.WorldStageInputs tickBuf;

            [NativeDisableParallelForRestriction]
            public NativeArray<Unity.Physics.RigidBody> rigidBodies;
            [NativeDisableParallelForRestriction]
            public NativeArray<Unity.Physics.MotionData> motionDatas;
            [NativeDisableParallelForRestriction]
            public NativeArray<Unity.Physics.MotionVelocity> motionVelocities;

            [WriteOnly] public NativeArray<Guid128> bodyIndexToGuid;

            // Global air friction (velocity damping) applied uniformly to every dynamic body this step.
            // Overrides each body's persisted MotionData damping so the value is a single live-tunable knob.
            public float linearDamping;
            public float angularDamping;

            public void Execute()
            {
                var keys = tickBuf.VoxelBodies.GetKeyArray(Allocator.Temp);

                for (int i = 0; i < keys.Length; i++)
                {
                    Guid128 guid = keys[i];
                    VoxelBodyData body = tickBuf.VoxelBodies[guid];
                    VoxelEntityData entity = tickBuf.VoxelEntities[guid];

                    // Use pre-computed indices at the beginning of the tick.
                    int bodyIndex = body._cached_body_index;

                    bodyIndexToGuid[bodyIndex] = guid;

                    rigidBodies[bodyIndex] = new Unity.Physics.RigidBody
                    {
                        WorldFromBody = entity.transform,
                        Collider = body.collider,
                        Entity = Entity.Null,
                        Scale = 1.0f,
                        CustomTags = 0,
                        // Unity Physics combines a pair's two solver types with a min, so a contact is
                        // only handed to the direct solver when both bodies opted in.
                        SolverType = body.accuratePhysics
                            ? Unity.Physics.SolverType.Direct
                            : Unity.Physics.SolverType.Iterative
                    };

                    if (!entity.isStatic)
                    {
                        var massProps = body.massProperties;

                        RigidTransform bodyFromMotion = new RigidTransform(
                            quaternion.identity,
                            massProps.centerOfMass
                        );
                        RigidTransform worldFromMotion = math.mul(entity.transform, bodyFromMotion);

                        motionDatas[bodyIndex] = new Unity.Physics.MotionData
                        {
                            WorldFromMotion = worldFromMotion,
                            BodyFromMotion = bodyFromMotion,
                            // Global air friction overrides the per-body persisted damping so friction
                            // is one live-tunable knob. Angular damping is what gives spinning bodies a
                            // terminal angular speed (the rotation analogue of linear drag).
                            LinearDamping = linearDamping,
                            AngularDamping = angularDamping
                        };

                        float inverseMass = massProps.mass > 0 ? 1.0f / massProps.mass : 0.0f;
                        float3 inverseInertia = float3.zero;
                        if (massProps.inertiaTensor.x > 0) inverseInertia.x = 1.0f / massProps.inertiaTensor.x;
                        if (massProps.inertiaTensor.y > 0) inverseInertia.y = 1.0f / massProps.inertiaTensor.y;
                        if (massProps.inertiaTensor.z > 0) inverseInertia.z = 1.0f / massProps.inertiaTensor.z;

                        Unity.Physics.MotionVelocity persistedMotionVelocity = body.motionVelocity;
                        motionVelocities[bodyIndex] = new Unity.Physics.MotionVelocity
                        {
                            LinearVelocity = persistedMotionVelocity.LinearVelocity,
                            AngularVelocity = persistedMotionVelocity.AngularVelocity,
                            InverseInertia = inverseInertia,
                            InverseMass = inverseMass,
                            AngularExpansionFactor = persistedMotionVelocity.AngularExpansionFactor,
                            GravityFactor = persistedMotionVelocity.GravityFactor
                        };
                    }
                }

                keys.Dispose();
            }
        }

        [BurstCompile]
        public struct ExportPhysicsWorldJob : IJob
        {
            public VoxelisXWorld.WorldStageInputs tickBuf;

            // Export walks every dynamic element from one IJob invocation. Disable the scheduler's
            // per-job-index range patching for these read-only views so indices after zero remain
            // accessible when more than one dynamic body exists.
            [ReadOnly, NativeDisableParallelForRestriction]
            public NativeArray<Unity.Physics.MotionData> motionDatas;
            [ReadOnly, NativeDisableParallelForRestriction]
            public NativeArray<Unity.Physics.MotionVelocity> motionVelocities;
            [ReadOnly] public NativeArray<Guid128> bodyIndexToGuid;
            public int nDynamicBodies;

            public void Execute()
            {
                for (int i = 0; i < nDynamicBodies; i++)
                {
                    Unity.Physics.MotionData md = motionDatas[i];
                    Unity.Physics.MotionVelocity mv = motionVelocities[i];
                    Guid128 guid = bodyIndexToGuid[i];

                    RigidTransform worldFromBody = math.mul(
                        md.WorldFromMotion,
                        math.inverse(md.BodyFromMotion)
                    );

                    var entity = tickBuf.VoxelEntities[guid];
                    entity.transform = worldFromBody;
                    tickBuf.VoxelEntities[guid] = entity;

                    var body = tickBuf.VoxelBodies[guid];
                    body.motionData = md;
                    body.motionVelocity = mv;
                    tickBuf.VoxelBodies[guid] = body;
                }
            }
        }

        /// <summary>
        /// Reloads sector data into each body's VoxelCollider. Must be called on main thread
        /// before scheduling the physics world build job.
        /// </summary>
        public static unsafe void ReloadColliderSectors(ref VoxelisXWorld.WorldStageInputs tickBuf)
        {
            var keys = tickBuf.VoxelBodies.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                Guid128 guid = keys[i];
                VoxelBodyData body = tickBuf.VoxelBodies[guid];
                if (!body.collider.IsCreated) continue;
                if (!tickBuf.VoxelEntities.TryGetValue(guid, out var entityData)) continue;

                VoxelCollider* vc = (VoxelCollider*)body.collider.GetUnsafePtr();
                using var sectors = entityData.sectors.ToNativeHashMap(Allocator.Temp);
                vc->ReloadSectors(sectors);
            }
            keys.Dispose();
        }

        public static JobHandle SchedulePhysicsWorldBuild(
            ref VoxelisXWorld.WorldStageInputs tickBuf,
            ref PhysicsWorld world,
            out NativeArray<Guid128> bodyIndexToGuid,
            float linearDamping,
            float angularDamping,
            JobHandle inputDeps,
            bool enableDirectSolver = false)
        {
            int nDynamic = tickBuf.nDynamicBodies;
            int nStatic = tickBuf.VoxelBodies.Count - nDynamic;

            // Reset world for rebuilding
            world.Reset(nStatic, nDynamic, 0);
            world.DynamicsWorld.EnableDirectSolver = enableDirectSolver;

            // Reload sector data into colliders (unsafe, must run on main thread)
            ReloadColliderSectors(ref tickBuf);

            bodyIndexToGuid = new NativeArray<Guid128>(nStatic + nDynamic, Allocator.TempJob);

            var fillWorldJob = new FillPhysicsWorldJob
            {
                tickBuf = tickBuf,
                rigidBodies = world.Bodies,
                motionDatas = world.MotionDatas,
                motionVelocities = world.MotionVelocities,
                bodyIndexToGuid = bodyIndexToGuid,
                linearDamping = linearDamping,
                angularDamping = angularDamping
            };

            return fillWorldJob.Schedule(inputDeps);
        }

        public static JobHandle SchedulePhysicsWorldExport(
            ref VoxelisXWorld.WorldStageInputs tickBuf,
            ref PhysicsWorld world,
            NativeArray<Guid128> bodyIndexToGuid,
            JobHandle inputDeps)
        {
            var exportJob = new ExportPhysicsWorldJob
            {
                tickBuf = tickBuf,
                motionDatas = world.MotionDatas,
                motionVelocities = world.MotionVelocities,
                bodyIndexToGuid = bodyIndexToGuid,
                nDynamicBodies = tickBuf.nDynamicBodies
            };

            var handle = exportJob.Schedule(inputDeps);
            return bodyIndexToGuid.Dispose(handle);
        }
    }
}
