using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Physics;
using UnityEngine;

namespace Voxelis.Simulation
{
    public class VoxelisXPhysicsWorld : MonoSingleton<VoxelisXPhysicsWorld>
    {
        protected List<VoxelBody> bodies = new();

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

        [HideInInspector] public NativeReference<int> haveStaticBodiesChanged;

        public override void Init()
        {
            base.Init();
            physicsWorld = new PhysicsWorld(0, 0, 0);
            simulation = Unity.Physics.Simulation.Create();
            haveStaticBodiesChanged = new NativeReference<int>(0, Allocator.Persistent);
        }

        public void AddBody(VoxelBody b)
        {
            bodies.Add(b);
        }

        internal void AddBodyToProperlySizedWorld(VoxelBody b)
        {
            
        }

        public void RemoveBody(VoxelBody b)
        {
            bodies.Remove(b);
        }

        public void SimulateStep(float dt)
        {
            // TODO: Update physics World
            // PhysicsWorldBuilder.cs:88
            
            BeforeSimulationStart();

            // Create solver stabilization settings
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

            debugFrameCount++;

            // Debug: Check collision world before simulation (first 10 frames only)
            if (debugFrameCount <= 10)
            {
                UnityEngine.Debug.Log($"[SimStep {debugFrameCount}] CollisionWorld NumBodies: {physicsWorld.CollisionWorld.NumBodies}, NumDynamic: {physicsWorld.CollisionWorld.NumDynamicBodies}, NumStatic: {physicsWorld.CollisionWorld.NumStaticBodies}");
            }

            // Build the broadphase BVH trees before simulation
            var buildBroadphaseHandle = physicsWorld.CollisionWorld.ScheduleBuildBroadphaseJobs(
                ref physicsWorld, dt, gravity, haveStaticBodiesChanged, default, multiThreaded);
            buildBroadphaseHandle.Complete();

            if (debugFrameCount <= 10)
            {
                UnityEngine.Debug.Log($"[SimStep {debugFrameCount}] Broadphase built successfully");
            }

            simulation.ResetSimulationContext(stepInput);
            var handles = simulation.ScheduleStepJobs(stepInput, default, multiThreaded);

            handles.FinalExecutionHandle.Complete();

            // Debug: Check for collision events (first 10 frames only)
            if (debugFrameCount <= 10)
            {
                var collisionEvents = simulation.CollisionEvents;
                if (true)
                {
                    int eventCount = 0;
                    foreach (var collisionEvent in collisionEvents)
                    {
                        eventCount++;
                        if (eventCount <= 5) // Only log first 5 collision events per frame
                        {
                            UnityEngine.Debug.Log($"[Collision] BodyA: {collisionEvent.BodyIndexA}, BodyB: {collisionEvent.BodyIndexB}, Normal: {collisionEvent.Normal}");
                        }
                    }
                    if (eventCount > 0)
                    {
                        UnityEngine.Debug.Log($"[Collision] Total events in frame {debugFrameCount}: {eventCount}");
                    }
                    else
                    {
                        UnityEngine.Debug.Log($"[Collision] No collision events in frame {debugFrameCount}");
                    }
                }
            }

            OnSimulationFinished();
            handles.FinalDisposeHandle.Complete();

            // Reset the static bodies changed flag after simulation
            if (haveStaticBodiesChanged.Value > 0)
            {
                haveStaticBodiesChanged.Value = 0;
            }
        }

        public virtual void BeforeSimulationStart() { }
        public virtual void OnSimulationFinished() { }

        private void OnDisable()
        {
            simulation.Dispose();
            physicsWorld.Dispose();
            haveStaticBodiesChanged.Dispose();
            
            Debug.Log("Physics Disposed!");
        }
    }
}