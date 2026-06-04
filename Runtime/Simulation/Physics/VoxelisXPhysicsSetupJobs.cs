using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
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
        public struct FillPhysicsWorldJob : IJob
        {
            public VoxelisXWorld.WorldStageInputs tickBuf;

            public NativeArray<Unity.Physics.RigidBody> rigidBodies;
            public NativeArray<Unity.Physics.MotionData> motionDatas;
            public NativeArray<Unity.Physics.MotionVelocity> motionVelocities;

            public NativeReference<int> numDynamic;

            public void Execute()
            {
                
            }
        }
        
        public static JobHandle SchedulePhysicsWorldBuild(
            ref VoxelisXWorld.WorldStageInputs tickBuf,
            ref PhysicsWorld world,
            JobHandle inputDeps)
        {
            // Count number of static and dynamic bodies
            int nStatic = 0, nDynamic = 0;
            foreach (var b in tickBuf.VoxelBodies.GetValueArray(Allocator.Temp))
            {
                if (b.isStatic) nStatic++;
                else nDynamic++;
            }

            // Reset world for rebuilding
            world.Reset(nStatic, nDynamic, 0);

            var fillWorldJob = new FillPhysicsWorldJob
            {
                tickBuf = tickBuf,
                rigidBodies = world.Bodies,
                motionDatas = world.MotionDatas,
                motionVelocities = world.MotionVelocities
            };

            return fillWorldJob.Schedule(inputDeps);
        }
    }
}
