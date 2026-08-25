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

        [Header("Air Friction")]
        [Tooltip("Global linear velocity damping applied to every dynamic body each step (per-second, " +
                 "exponential). Higher = lower terminal falling/sliding speed. 0 = no linear air friction.")]
        [Min(0f)] public float linearAirFriction = 0.05f;

        [Tooltip("Global angular velocity damping applied to every dynamic body each step (per-second, " +
                 "exponential). This is how rotation is damped: spinning structures reach a terminal " +
                 "angular speed just like linear drag gives a terminal linear speed. 0 = no angular air friction.")]
        [Min(0f)] public float angularAirFriction = 0.05f;

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
        public bool synchronizeCollisionWorld = true;

        [Header("Solver Stabilization")]
        [Tooltip("Enable stabilization heuristic")]
        public bool enableSolverStabilization = true;

        [Tooltip("Velocity clipping factor")]
        public float velocityClippingFactor = 0.5f;

        [Tooltip("Inertia scaling factor")]
        public float inertiaScalingFactor = 0.75f;

        [Header("Direct Solver")]
        [Tooltip("Tuning for the accurate (Direct) solver. Only applies to contacts where BOTH bodies " +
                 "have VoxelBody.accuratePhysics enabled. If Contact Stiffness is left at 0 these are " +
                 "ignored and Unity's defaults are used instead.")]
        public Solver.DirectSolverSettings directSolverSettings = Solver.DirectSolverSettings.Default;
        public bool enableDirectSolver = true;

        [Tooltip("Multithreading enabled")] public bool multiThreaded = true;

        [Header("Brick Overlap Graph")]
        [Tooltip("Directed-record count (2x raw candidates) at or below which the post-physics " +
                 "brick-overlap graph builds in one serial job. Larger inputs are unsupported.")]
        public int brickOverlapSerialThreshold = 2048;

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
            brickOverlapGraphBuilder = new BrickOverlapGraphBuilder();
        }

        internal void AddBodyToProperlySizedWorld(VoxelBody b)
        {
            
        }

        // This step's transient body index -> stable entity GUID. Lives from the world build
        // until the next one, so post-step brick-overlap queries can resolve their candidates.
        private NativeArray<Guid128> bodyIndexToGuid;

        private BrickOverlapGraphBuilder brickOverlapGraphBuilder;

        // Time step of the last simulated step, reused when a caller asks for a broadphase
        // rebuild before querying (motion expansion must match the step that produced the pose).
        private float lastTimeStep;

        /// <summary>
        /// Read-only view of the brick-overlap graph published by the last physics step.
        /// Empty (IsCreated false) before the first step. Re-fetch each step.
        /// </summary>
        public BrickOverlapGraph BrickOverlapGraph =>
            brickOverlapGraphBuilder != null ? brickOverlapGraphBuilder.Graph : default;

        /// <summary> Counters of the last brick-overlap graph build. </summary>
        public BrickOverlapGraphStats BrickOverlapGraphStats =>
            brickOverlapGraphBuilder != null ? brickOverlapGraphBuilder.LastBuildStats : default;

        /// <summary>
        /// Queries the stepped collision world for brick overlaps, then builds and publishes
        /// the graph. Returns the published graph, or a default (empty) one when no step has
        /// run yet.
        /// </summary>
        /// <param name="queryBatches">
        /// One batch per source body, addressing the bodies of the step that just ran.
        /// </param>
        /// <param name="queryBricksByBatch">
        /// Source bricks, one stream lane per batch. Neither input is consumed nor disposed
        /// here; the caller keeps both alive until it has finished reading the graph.
        /// </param>
        /// <param name="rebuildBroadphase">
        /// Rebuilds the dynamic broadphase tree first. Not needed after a step with
        /// <see cref="synchronizeCollisionWorld"/> set — that step already synchronized the
        /// solver-integrated transforms and the BVH. Use it when the caller moved bodies since.
        /// </param>
        public BrickOverlapGraph BuildBrickOverlapGraph(
            NativeArray<VoxelBrickOverlapQueryBatch> queryBatches,
            NativeStream queryBricksByBatch,
            bool rebuildBroadphase = false,
            JobHandle inputDeps = default)
        {
            if (brickOverlapGraphBuilder == null || !bodyIndexToGuid.IsCreated)
            {
                return default;
            }

            if (rebuildBroadphase)
            {
                Profiler.BeginSample("Brick Overlap Rebuild Broadphase");
                physicsWorld.CollisionWorld.ScheduleBuildBroadphaseJobs(
                    ref physicsWorld, lastTimeStep, gravity, haveStaticBodiesChanged,
                    inputDeps, multiThreaded).Complete();
                inputDeps = default;
                Profiler.EndSample();
            }

            Profiler.BeginSample("Brick Overlap Query");
            JobHandle queryHandle = physicsWorld.CollisionWorld.ScheduleVoxelBrickOverlaps(
                queryBatches, queryBricksByBatch, out NativeStream rawOverlaps, inputDeps);
            queryHandle.Complete();
            Profiler.EndSample();

            Profiler.BeginSample("Brick Overlap Graph Build");
            brickOverlapGraphBuilder.serialBuildThreshold = brickOverlapSerialThreshold;
            brickOverlapGraphBuilder.BuildAndPublish(rawOverlaps, bodyIndexToGuid);
            rawOverlaps.Dispose();
            Profiler.EndSample();

            return brickOverlapGraphBuilder.Graph;
        }

        public void SimulateStep(
            float dt,
            VoxelisXWorld.WorldStageInputs tickBuf)
        {
            lastTimeStep = dt;

            // The previous step's mapping stayed alive for that step's post-step queries.
            // It describes the body layout that is about to be replaced, so release it here.
            if (bodyIndexToGuid.IsCreated)
            {
                bodyIndexToGuid.Dispose();
            }

            Profiler.BeginSample("Physics Build World");
            var buildHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldBuild(
                ref tickBuf, ref physicsWorld, out bodyIndexToGuid,
                linearAirFriction, angularAirFriction, default,
                enableDirectSolver);
            buildHandle.Complete();
            haveStaticBodiesChanged.Value = 1;
            Profiler.EndSample();

            // Brick-overlap queries are not part of the step. The caller issues them against the
            // stepped, synchronized collision world through BuildBrickOverlapGraph.

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

            // A zero-initialised DirectSolverSettings means zero contact stiffness and damping, which
            // makes direct-solver contacts collapse. That is what a component serialized before this
            // field existed deserialises to, so treat it as "unset" and use Unity's defaults.
            Solver.DirectSolverSettings directSettings = directSolverSettings.ContactStiffness > 0f
                ? directSolverSettings
                : Solver.DirectSolverSettings.Default;

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
                DirectSolverSettings = directSettings,
                HaveStaticBodiesChanged = haveStaticBodiesChanged
            };

            if (synchronizeCollisionWorld == false)
            {
                Debug.LogWarning("Synchronize Collision World is disabled, brick overlap may stale");
            }
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

            // Narrowphase funnel counters: clear before the jobs that fill them are scheduled.
            BeginVoxelContactProfiling();

            Profiler.BeginSample("Physics Schedule Step Jobs");
            var handles = simulation.ScheduleStepJobs(stepInput, default, multiThreaded);
            Profiler.EndSample();

            Profiler.BeginSample("Physics Complete Step Jobs");
            handles.FinalExecutionHandle.Complete();
            Profiler.EndSample();

            // Read-only contact diagnostics: must run after Complete() and before the next
            // ResetSimulationContext, while this frame's voxel contact event stream is valid.
            Profiler.BeginSample("Physics Contact Debug Logging");
            LogVoxelContactsAfterStep(tickBuf.nDynamicBodies);
            LogVoxelContactProfileAfterStep();
            Profiler.EndSample();

            Profiler.BeginSample("Physics OnSimulationFinished");
            OnSimulationFinished();
            Profiler.EndSample();

            Profiler.BeginSample("Physics Export World");
            var exportHandle = VoxelisXPhysicsInterface.SchedulePhysicsWorldExport(
                ref tickBuf, ref physicsWorld, bodyIndexToGuid, default);
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
            if (bodyIndexToGuid.IsCreated)
            {
                bodyIndexToGuid.Dispose();
            }
            brickOverlapGraphBuilder?.Dispose();
            brickOverlapGraphBuilder = null;

            if (verboseLogging) Debug.Log("Physics Disposed!");
        }
    }
}
