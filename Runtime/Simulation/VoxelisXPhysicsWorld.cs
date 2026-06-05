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
    public partial class VoxelisXPhysicsWorld : MonoSingleton<VoxelisXPhysicsWorld>
    {
        protected PhysicsWorld physicsWorld;
        protected Unity.Physics.Simulation simulation;

        private int debugFrameCount = 0;

        [Header("Gravity")]
        public Vector3 gravity = new Vector3(0, -9.81f, 0);

        [Header("Simulation Parameters")]
        [Tooltip("Number of substeps per simulation step")]
        public int substepCount = 1;

        [Tooltip("Number of Gauss-Seidel solver iterations")]
        public int solverIterationCount = 4;

        [Tooltip("Maximum relative velocity when separating intersecting dynamic bodies")]
        public float maxDynamicDepenetrationVelocity = 10f;

        [Tooltip("Maximum relative velocity when separating dynamic bodies from static bodies")]
        public float maxStaticDepenetrationVelocity = 10f;

        [Tooltip("Synchronize collision world after physics step (enable for precise queries within same frame)")]
        public bool synchronizeCollisionWorld = false;

        [Header("Solver Stabilization")]
        [Tooltip("Enable stabilization heuristic")]
        public bool enableSolverStabilization = true;

        [Tooltip("Velocity clipping factor")]
        public float velocityClippingFactor = 0.5f;

        [Tooltip("Inertia scaling factor")]
        public float inertiaScalingFactor = 0.75f;

        [Tooltip("Multithreading enabled")] public bool multiThreaded = true;

        [Header("Debug")]
        [Tooltip("Log per-step collision-world / broadphase diagnostics for the first few frames.")]
        public bool verboseLogging = false;

        [HideInInspector] public NativeReference<int> haveStaticBodiesChanged;

        public override void Init()
        {
            base.Init();
            physicsWorld = new PhysicsWorld(0, 0, 0);
            simulation = Unity.Physics.Simulation.Create();
            haveStaticBodiesChanged = new NativeReference<int>(0, Allocator.Persistent);
        }

        internal void AddBodyToProperlySizedWorld(VoxelBody b)
        {
            
        }

        private NativeArray<Guid128> bodyIndexToGuid;
        private int nDynamic;

        public void SimulateStep(float dt, VoxelisXWorld.WorldStageInputs tickBuf)
        {
            Profiler.BeginSample("Physics Build World");
            var buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                ref tickBuf, ref physicsWorld, out bodyIndexToGuid, out nDynamic, default);
            buildHandle.Complete();
            haveStaticBodiesChanged.Value = 1;
            Profiler.EndSample();

            Profiler.BeginSample("Physics BeforeSimulationStart");
            BeforeSimulationStart();
            Profiler.EndSample();

            // Create solver stabilization settings
            Profiler.BeginSample("Physics Build Step Input");
            Solver.StabilizationHeuristicSettings stabilizationSettings = enableSolverStabilization
                ? new Solver.StabilizationHeuristicSettings
                {
                    EnableSolverStabilization = true,
                    VelocityClippingFactor = velocityClippingFactor,
                    InertiaScalingFactor = inertiaScalingFactor
                }
                : Solver.StabilizationHeuristicSettings.Default;

            SimulationStepInput stepInput = new SimulationStepInput()
            {
                World = physicsWorld,
                TimeStep = dt,
                Gravity = gravity,
                SynchronizeCollisionWorld = synchronizeCollisionWorld,
                NumSubsteps = substepCount,
                NumSolverIterations = solverIterationCount,
                MaxDynamicDepenetrationVelocity = maxDynamicDepenetrationVelocity,
                MaxStaticDepenetrationVelocity = maxStaticDepenetrationVelocity,
                SolverStabilizationHeuristicSettings = stabilizationSettings,
                HaveStaticBodiesChanged = haveStaticBodiesChanged
            };
            Profiler.EndSample();

            Profiler.BeginSample("Physics Debug Pre-Step");
            debugFrameCount++;

            // Debug: Check collision world before simulation (first 10 frames only)
            if (verboseLogging && debugFrameCount <= 10)
            {
                UnityEngine.Debug.Log($"[SimStep {debugFrameCount}] CollisionWorld NumBodies: {physicsWorld.CollisionWorld.NumBodies}, NumDynamic: {physicsWorld.CollisionWorld.NumDynamicBodies}, NumStatic: {physicsWorld.CollisionWorld.NumStaticBodies}");
            }
            Profiler.EndSample();

            // Build the broadphase BVH trees before simulation
            Profiler.BeginSample("Physics Build Broadphase");
            var buildBroadphaseHandle = physicsWorld.CollisionWorld.ScheduleBuildBroadphaseJobs(
                ref physicsWorld, dt, gravity, haveStaticBodiesChanged, default, multiThreaded);
            Profiler.BeginSample("Physics Complete Broadphase");
            buildBroadphaseHandle.Complete();
            Profiler.EndSample();
            Profiler.EndSample();

            Profiler.BeginSample("Physics Debug Post-Broadphase");
            if (verboseLogging && debugFrameCount <= 10)
            {
                UnityEngine.Debug.Log($"[SimStep {debugFrameCount}] Broadphase built successfully");
            }
            Profiler.EndSample();

            Profiler.BeginSample("Physics Reset Simulation Context");
            simulation.ResetSimulationContext(stepInput);
            Profiler.EndSample();

            Profiler.BeginSample("Physics Schedule Step Jobs");
            var handles = simulation.ScheduleStepJobs(stepInput, default, multiThreaded);
            Profiler.EndSample();

            Profiler.BeginSample("Physics Complete Step Jobs");
            handles.FinalExecutionHandle.Complete();
            Profiler.EndSample();

            // Debug: Check for collision events (first 10 frames only)
            // if (debugFrameCount <= 10)
            // {
            //     // var collisionEvents = simulation.CollisionEvents;
            //     var voxelContactEvents = simulation.VoxelContactEvents;
            //     if (true)
            //     {
            //         int eventCount = 0;
            //         foreach (var contactEvent in voxelContactEvents)
            //         {
            //             eventCount++;
            //             if (eventCount <= 5) // Only log first 5 collision events per frame
            //             {
            //                 UnityEngine.Debug.Log($"[Collision] BodyA: {contactEvent.BodyIndexA}:{contactEvent.VoxelCoordsInA}, BodyB: {contactEvent.BodyIndexB}:{contactEvent.VoxelCoordsInB}, Normal: {contactEvent.Normal}");
            //             }
            //         }
            //         if (eventCount > 0)
            //         {
            //             UnityEngine.Debug.Log($"[Collision] Total events in frame {debugFrameCount}: {eventCount}");
            //         }
            //         else
            //         {
            //             UnityEngine.Debug.Log($"[Collision] No collision events in frame {debugFrameCount}");
            //         }
            //     }
            // }

            Profiler.BeginSample("Physics OnSimulationFinished");
            OnSimulationFinished();
            Profiler.EndSample();

            Profiler.BeginSample("Physics Export World");
            var exportHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldExport(
                ref tickBuf, ref physicsWorld, bodyIndexToGuid, nDynamic, default);
            exportHandle.Complete();
            Profiler.EndSample();

            Profiler.BeginSample("Physics Complete Dispose Jobs");
            handles.FinalDisposeHandle.Complete();
            Profiler.EndSample();

            // Reset the static bodies changed flag after simulation
            Profiler.BeginSample("Physics Reset Static Changed Flag");
            if (haveStaticBodiesChanged.Value > 0)
            {
                haveStaticBodiesChanged.Value = 0;
            }
            Profiler.EndSample();
        }

        public virtual void BeforeSimulationStart() { }
        public virtual void OnSimulationFinished() { }

        private void OnDisable()
        {
            simulation.Dispose();
            physicsWorld.Dispose();
            haveStaticBodiesChanged.Dispose();

            if (verboseLogging) Debug.Log("Physics Disposed!");
        }
    }
}
