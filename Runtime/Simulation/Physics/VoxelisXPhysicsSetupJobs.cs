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

            public int nDynamic;

            public void Execute()
            {
                int dynamicIdx = 0;
                int staticIdx = nDynamic;

                var keys = tickBuf.VoxelBodies.GetKeyArray(Allocator.Temp);

                for (int i = 0; i < keys.Length; i++)
                {
                    Guid128 guid = keys[i];
                    VoxelBodyData body = tickBuf.VoxelBodies[guid];
                    VoxelEntityData entity = tickBuf.VoxelEntities[guid];

                    int bodyIndex = body.isStatic ? staticIdx++ : dynamicIdx++;

                    bodyIndexToGuid[bodyIndex] = guid;

                    rigidBodies[bodyIndex] = new Unity.Physics.RigidBody
                    {
                        WorldFromBody = entity.transform,
                        Collider = body.collider,
                        Entity = Entity.Null,
                        Scale = 1.0f,
                        CustomTags = 0
                    };

                    if (!body.isStatic)
                    {
                        var massProps = body.massProperties;

                        RigidTransform bodyFromMotion = new RigidTransform(
                            quaternion.identity,
                            massProps.centerOfMass
                        );
                        RigidTransform worldFromMotion = math.mul(entity.transform, bodyFromMotion);

                        Unity.Physics.MotionData persistedMotionData = body.motionData;
                        motionDatas[bodyIndex] = new Unity.Physics.MotionData
                        {
                            WorldFromMotion = worldFromMotion,
                            BodyFromMotion = bodyFromMotion,
                            LinearDamping = persistedMotionData.LinearDamping,
                            AngularDamping = persistedMotionData.AngularDamping
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

            [ReadOnly] public NativeArray<Unity.Physics.MotionData> motionDatas;
            [ReadOnly] public NativeArray<Unity.Physics.MotionVelocity> motionVelocities;
            [ReadOnly] public NativeArray<Guid128> bodyIndexToGuid;

            public int nDynamic;

            public void Execute()
            {
                for (int i = 0; i < nDynamic; i++)
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
            out int nDynamic,
            JobHandle inputDeps)
        {
            // Count number of static and dynamic bodies
            int nStatic = 0;
            nDynamic = 0;
            foreach (var b in tickBuf.VoxelBodies.GetValueArray(Allocator.Temp))
            {
                if (b.isStatic) nStatic++;
                else nDynamic++;
            }

            // Reset world for rebuilding
            world.Reset(nStatic, nDynamic, 0);

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
                nDynamic = nDynamic
            };

            return fillWorldJob.Schedule(inputDeps);
        }

        public static JobHandle SchedulePhysicsWorldExport(
            ref VoxelisXWorld.WorldStageInputs tickBuf,
            ref PhysicsWorld world,
            NativeArray<Guid128> bodyIndexToGuid,
            int nDynamic,
            JobHandle inputDeps)
        {
            var exportJob = new ExportPhysicsWorldJob
            {
                tickBuf = tickBuf,
                motionDatas = world.MotionDatas,
                motionVelocities = world.MotionVelocities,
                bodyIndexToGuid = bodyIndexToGuid,
                nDynamic = nDynamic
            };

            var handle = exportJob.Schedule(inputDeps);
            bodyIndexToGuid.Dispose(handle);
            return handle;
        }
    }
}
