using System;
using System.Collections.Generic;
using System.IO;
using Simulation.Utils;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Profiling;
using Voxelis;
using Voxelis.IO;
using Voxelis.Rendering.Meshing;
using Voxelis.Simulation;
using Voxelis.Tick;
using Voxelis.Utils;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Voxelis
{
    public class VoxelisXWorld : VoxelisXCoreWorld
    {
        /// <summary>
        /// Exclusive CPU timing buckets from the last completed world tick. The brick graph
        /// bucket covers the whole post-physics alien propagation (query, graph build, and
        /// marking), and Tick excludes the other three, so the four times sum to
        /// TotalMilliseconds.
        /// </summary>
        public struct TickTimingStats
        {
            public bool IsCreated;
            public bool UsedRayTracing;
            public bool UsedMeshing;
            public double TickMilliseconds;
            public double PhysicsMilliseconds;
            public double BrickGraphMilliseconds;
            public double RenderingMilliseconds;
            public double TotalMilliseconds;
        }

        public Dictionary<Guid128, VoxelBody> bodies = new();

        public TickStage<WorldStageInputs> physicsStage;
        public TickStage<AutomataStageInputs> automataStage;

        // ---------------- COMPONENTS ------------------
        [Header("Components")]
        [SerializeField] protected VoxelisXPhysicsWorld physicsWorld;

        [SerializeField] protected VoxelRayCast rayCaster;
        [SerializeField] protected VoxelisXRenderer rayTracedRenderer;
        [SerializeField] protected VoxelMeshRendererComponent meshingRenderer;

        /// <summary>
        /// Read-only view of the brick-overlap graph published by the alien propagation stage,
        /// which runs right after the physics step. Earlier Tick stages therefore see the graph
        /// of the previous tick. Empty (IsCreated false) until the first publish, which needs
        /// <see cref="doAlienPropagation"/>.
        /// </summary>
        public BrickOverlapGraph BrickOverlapGraph =>
            physicsWorld != null ? physicsWorld.BrickOverlapGraph : default;

        /// <summary>Exclusive CPU timing buckets from the last completed world tick.</summary>
        public TickTimingStats LastTickTimings { get; private set; }

        // ---------------- PERFORMANCE ------------------
        [Header("Performance")]
        public float targetTPS = 100.0f;
        public float slowmo = 1.0f;

        // ---------------- ALIEN DIRTY PROPAGATION ------------------
        [Header("Alien Dirty Propagation")]
        [Tooltip("Propagate dirtiness between entities through the post-physics brick-overlap graph.")]
        public bool doAlienPropagation = false;

        [Tooltip("Flags a moving (non-static) entity's bricks hand to their alien neighbors.")]
        [SerializeField] private DirtyFlags alienMotionDirtyMask = DirtyFlags.GeneralAutomata;

        [Tooltip("Query every allocated brick of every non-static entity, not only the dirty ones. " +
                 "This is the heaviest input the graph can get; keep it on to benchmark motion.")]
        [SerializeField] private bool alienIncludeMovingBricks = true;

        /// <summary> Counters of the last alien propagation pass. </summary>
        public BrickOverlapPropagationStats LastBrickOverlapPropagationStats { get; private set; }


        // ---------------- DEBUG ------------------
        [Header("Debug")]
        public bool freeze = true;
        public bool isFirst = true;

        private float timer = 0.0f;

        private const string DefaultSaveLoadFileName = "voxelisx-world.vxw";

        // ---------------- SAVE / LOAD ------------------
        [Header("Save / Load")]
        [SerializeField] private bool autoLoadOnStart = false;
        [SerializeField] private string saveLoadPath = DefaultSaveLoadFileName;
        
        public struct WorldStageInputs
        {
            public NativeHashMap<Guid128, VoxelEntityData> VoxelEntities;
            public NativeHashMap<Guid128, VoxelBodyData> VoxelBodies;
            public int nDynamicBodies;
        }
        
        public struct AutomataStageInputs
        {
            public NativeHashMap<Guid128, VoxelEntityData> VoxelEntities;
            public NativeList<BrickInfo> BricksRequiredUpdate;
            public AutomataReadContext ReadContext;
        }
        
        // Ticking
        private WorldStageInputs tickBuf;
        private AutomataStageInputs automataTickBuf;
        private NativeList<AlienEntityView> alienEntityViews;
        private VoxelBodyForceCommandStream bodyForceCommands;

        public VoxelBodyForceCommandStream BodyForceCommands => bodyForceCommands;

        public override void Init()
        {
            base.Init();

            physicsStage = new();
            automataStage = new();

            tickBuf.VoxelEntities = new NativeHashMap<Guid128, VoxelEntityData>(1, Allocator.Persistent);
            tickBuf.VoxelBodies = new NativeHashMap<Guid128, VoxelBodyData>(1, Allocator.Persistent);
            automataTickBuf.BricksRequiredUpdate = new NativeList<BrickInfo>(Allocator.Persistent);
            alienEntityViews = new NativeList<AlienEntityView>(Allocator.Persistent);
            bodyForceCommands = new VoxelBodyForceCommandStream(Allocator.Persistent);
        }

        protected void Start()
        {
            if(autoLoadOnStart)
            {
                Load();
            }
        }

        /// <summary>
        /// Registers a voxel entity with this world.
        /// The entity's Guid cannot be changed after registration.
        /// </summary>
        /// <param name="e">The entity to add.</param>
        public void AddBody(VoxelBody b)
        {
            if (bodies.ContainsKey(b.entity.PersistentGuid))
            {
                return;
            }

            bodies.Add(b.entity.PersistentGuid, b);
        }

        /// <summary>
        /// Unregisters a voxel entity from this world.
        /// </summary>
        /// <param name="e">The entity to remove.</param>
        public void RemoveBody(VoxelBody b)
        {
            bodies.Remove(b.entity.PersistentGuid);
        }

        protected override void ReleaseResources()
        {
            tickBuf.VoxelEntities.Dispose();
            tickBuf.VoxelBodies.Dispose();
            automataTickBuf.BricksRequiredUpdate.Dispose();
            alienEntityViews.Dispose();
            bodyForceCommands?.Dispose();
            base.ReleaseResources();
        }

        public override void Tick()
        {
            // TEMP CODE -- Tick logic
            if ((!isFirst) && freeze) return;
            long tickStartTicks = Stopwatch.GetTimestamp();

            // TODO: FIXME: Check entity prevTransform lifespan; currently maybe treated as moved to current location from 0,0,0 in first frame, causing severe performance issues
            isFirst = false;

            float deltaTime = targetTPS > 0f ? 1.0f / targetTPS : Time.deltaTime;
            
            // timer -= Time.deltaTime;
            // if (timer > 0)
            // {
            //     return;
            // }
            // else
            // {
            //     timer = slowmo * 1.0f / targetTPS;
            // }

            // Ticking
            // TODO: FIXME: Currently inf loaders will not work due to no proper sector loading transition
            // TickWorldLoaders();
            
            /////////////////////////////////////////////////////////////////////////
            // TOPOLOGY STAGE
            //  DO
            //   - Add / Remove VoxelEntities
            //   - Switch entities' `IsStatic`
            //   - Can communicate with the managed world (1 entity = 1 GameObj)
            /////////////////////////////////////////////////////////////////////////
            
Profiler.BeginSample("Player Ray Cast");
            rayCaster?.Tick();
Profiler.EndSample();

            /////////////////////////////////////////////////////////////////////////
            // T-V Boundary
            //  Fix entity ordering
            //  Handover to unmanaged world / tickBuf
            //
            // DO NOT modify entity topology after here
            /////////////////////////////////////////////////////////////////////////

            // Fill native list by copying
            // TODO: Keep the unique instance in world and let VoxelEntity ref it?
Profiler.BeginSample("Fill TickBuffer");
            tickBuf.VoxelEntities.Clear();
            tickBuf.VoxelBodies.Clear();

            // Count dynamic body count
            // TODO: Arrange this to manage body indices properly with persistence
            tickBuf.nDynamicBodies = 0;
            foreach (var kvp in entities)
            {
                if (bodies.TryGetValue(kvp.Key, out var b))
                {
                    if (!b.entity.IsStatic)
                    {
                        tickBuf.nDynamicBodies++;
                    }
                }
            }

            int nDynamic = 0, nStatic = 0;
            foreach(var kvp in entities)
            {
                var e = kvp.Value;
                e.SyncTransformToData();
                tickBuf.VoxelEntities.Add(e.PersistentGuid, e.GetDataCopy());

                if (bodies.TryGetValue(kvp.Key, out var b))
                {
                    var bodyData = b.GetDataCopy();
                    if (e.IsStatic)
                    {
                        bodyData._cached_body_index = tickBuf.nDynamicBodies + nStatic;
                        nStatic++;
                    }
                    else
                    {
                        bodyData._cached_body_index = nDynamic;
                        nDynamic++;
                    }
                    tickBuf.VoxelBodies.Add(kvp.Key, bodyData);
                }
            }
Profiler.EndSample();

            /////////////////////////////////////////////////////////////////////////
            // VOXEL STAGE
            //  random access voxel stage
            //  TODO
            /////////////////////////////////////////////////////////////////////////

Profiler.BeginSample("WorldStage");
            DoTick(tickBuf);
Profiler.EndSample();
            
            // Tick
            JobHandle tickHandle = new JobHandle();

            /////// Voxel update stage
            // Random tick stage
            
            /////////////////////////////////////////////////////////////////////////
            // VOXEL STAGE
            //  automata stage
            //  DO
            //   - Modify voxel data within 1-voxel/t information propagation limit
            //   - Add forces to body
            //  DON'T
            //   - Add / remove / toggle `IsStatic` of VoxelEntities
            //   - Move entities transform
            /////////////////////////////////////////////////////////////////////////

            // Automata stage
            // TODO: Wrap this up and handle this properly
            // Activate sector snapshotting for modifications
Profiler.BeginSample("Activate Sector Snapshots");
            foreach (var e in entities.Values)
            {
                foreach (var kvp in e.Sectors)
                {
                    if (kvp.Value.Get().sectorRequireUpdateFlags > 0)
                        kvp.Value.ActivateSnapshot();
                }
            }
Profiler.EndSample();

            // Collect bricks to update
Profiler.BeginSample("Collect RequireUpdate Bricks");
            automataTickBuf.BricksRequiredUpdate.Clear();
            BrickCollector.Collect(ref tickBuf.VoxelEntities, ref automataTickBuf.BricksRequiredUpdate);
Profiler.EndSample();
Profiler.BeginSample("Build Alien Read Context");
            BuildAlienReadContext();
Profiler.EndSample();

Profiler.BeginSample("Automata Stage Schedule");
            tickHandle = automataStage.Schedule(automataTickBuf, tickHandle);
Profiler.EndSample();

            // Random access updating stage

            // Propagate dirtiness up, from brick(sector) to VoxelEntityData
            // Update physics info (MassProperties, VoxelType (Corner/Edge/Surface))

            /////// Physics update stage

            // Resolve voxel contact events into Sectors (AlienVoxelPairs)

            /////// Rendering update stage

            /////// End Tick stage
            // Clear dirtiness and propagate RequireBrickUpdate to self & neighbors

Profiler.BeginSample("Work Dispatch");
            tickHandle.Complete();
Profiler.EndSample();

            // TODO: Wrap this up and handle this properly
            // Apply sector modifications
Profiler.BeginSample("Apply Sector Snapshots");
            foreach(var e in entities.Values)
            {
                foreach (var kvp in e.Sectors)
                {
                    kvp.Value.ApplySnapshot();
                }
            }
Profiler.EndSample();

            /////////////////////////////////////////////////////////////////////////
            // V-P Boundary
            //  Dirty propagation
            /////////////////////////////////////////////////////////////////////////
 
            // Dirty propagation — operates on tickBuf to preserve physics-exported transforms
Profiler.BeginSample("Dirty Propagation");
    Profiler.BeginSample("Update Velocity");
            var entityKeys = tickBuf.VoxelEntities.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < entityKeys.Length; i++)
            {
                var entity = tickBuf.VoxelEntities[entityKeys[i]];
                entity.ComputeVelocityForDirtyPropagation(deltaTime);
                tickBuf.VoxelEntities[entityKeys[i]] = entity;
            }
    Profiler.EndSample();

    Profiler.BeginSample("Clear Require Updates");
            for (int i = 0; i < entityKeys.Length; i++)
            {
                var entity = tickBuf.VoxelEntities[entityKeys[i]];
                entity.ClearRequireUpdates();
                tickBuf.VoxelEntities[entityKeys[i]] = entity;
            }
    Profiler.EndSample();

    Profiler.BeginSample("Propagate Dirty Flags");
            JobHandle handle = new JobHandle();
            for (int i = 0; i < entityKeys.Length; i++)
            {
                var entity = tickBuf.VoxelEntities[entityKeys[i]];
                handle = JobHandle.CombineDependencies(handle, entity.PropagateDirtyFlags(DirtyFlags.All, true));

                // Persist sector growth from EnsureNeighborSectorsForDirtyBoundaries into the working
                // copy. This previously ran on the managed entities, whose sectors hashmap could
                // realloc and free the buffer that tickBuf — read just below by Alien Propagation and
                // by the final copy-back — still pointed at (a use-after-free that only surfaced when a
                // boundary brick spawned a new neighbor sector mid-tick). Operating on tickBuf keeps a
                // single consistent sectors map across the whole propagation phase.
                //
                // INVARIANT (load-bearing): the propagation phase may only ADD sectors to this working
                // copy — it must never free or relocate an existing Sector* — so the managed entity's
                // still-aliased pre-realloc sectors entries keep pointing at live Sector structs until
                // copy-back adopts the grown map. Don't introduce RemoveSectorAt / Sector disposal here.
                tickBuf.VoxelEntities[entityKeys[i]] = entity;
            }

        Profiler.BeginSample("Burst");
            handle.Complete();
        Profiler.EndSample();
    Profiler.EndSample();

            // TODO: At least make the jobs below Complete() o(1) times by chaining them
            // TODO: Refine the tick to job scheduling best practices
Profiler.EndSample();

            // Dirty flags stay set through the physics step: alien propagation runs on the
            // stepped poses (see below) and selects its source bricks from them. entityKeys
            // stays alive until that clear.

            // Mark non-empty blocks: rebuild the Block slot's occupancy aux from settled voxel data,
            // for every entity, before physics consumes it.
Profiler.BeginSample("Mark Non-empty Blocks");
            foreach (var e in tickBuf.VoxelEntities.GetValueArray(Allocator.Temp))
            {
                e.RefreshNonEmptyMask();
            }
Profiler.EndSample();

            // Physics after dirty propagation
Profiler.BeginSample("Recompute body mass properties");
            foreach (var b in tickBuf.VoxelBodies.GetKeyArray(Allocator.Temp))
            {
                var body = tickBuf.VoxelBodies[b];
                var entityData = tickBuf.VoxelEntities[b];
                body.ComputePhysicsProperties(entityData);
                tickBuf.VoxelBodies[b] = body;
            }
Profiler.EndSample();

            // Mark physics-key blocks: rebuild the PhysicsInfo slot's Corner/Edge aux from the
            // PhysicsInfo data just written above, before the physics step consumes it.
Profiler.BeginSample("Mark Physics-key Blocks");
            foreach (var b in tickBuf.VoxelBodies.GetKeyArray(Allocator.Temp))
            {
                var body = tickBuf.VoxelBodies[b];
                var entityData = tickBuf.VoxelEntities[b];
                body.RefreshPhysicsKeyMask(entityData.sectors);
            }
Profiler.EndSample();

Profiler.BeginSample("Apply Body Force Commands");
            bodyForceCommands?.ApplyTo(ref tickBuf, deltaTime);
Profiler.EndSample();

            /////////////////////////////////////////////////////////////////////////
            // PHYSICS STAGE
            //  DO
            //   - Move entities
            //  DON'T
            //   - Modify voxel data
            /////////////////////////////////////////////////////////////////////////

Profiler.BeginSample("Physics Step");
            long physicsStartTicks = Stopwatch.GetTimestamp();
            physicsWorld.SimulateStep(
                deltaTime, tickBuf);
            long physicsElapsedTicks = Stopwatch.GetTimestamp() - physicsStartTicks;
Profiler.EndSample();

            /////////////////////////////////////////////////////////////////////////
            // P-T Boundary
            //  Collect key overlapping bricks for alien propagation / reading
            //  Back to managed world
            /////////////////////////////////////////////////////////////////////////

            // Alien dirty propagation over the post-physics brick-overlap graph. The step
            // synchronized the collision world, so the BVH already describes the stepped poses
            // and needs no explicit rebuild.
Profiler.BeginSample("Alien Propagation");
            long alienStartTicks = Stopwatch.GetTimestamp();
            LastBrickOverlapPropagationStats = default;
            if (doAlienPropagation)
            {
                var request = BrickOverlapQueryBuilder.Build(ref tickBuf, new BrickOverlapQuerySettings
                {
                    FlagsToPropagate = DirtyFlags.All,
                    MotionDirtyMask = alienMotionDirtyMask,
                    IncludeMovingBodies = alienIncludeMovingBricks
                });

                if (request.IsCreated)
                {
                    try
                    {
                        BrickOverlapGraph graph = physicsWorld.BuildBrickOverlapGraph(
                            request.Batches, request.Bricks, rebuildBroadphase: false);

                        LastBrickOverlapPropagationStats = BrickOverlapDirtyPropagation.Propagate(
                            graph, request, ref tickBuf.VoxelEntities);
                    }
                    finally
                    {
                        request.Dispose();
                    }
                }
            }
            long alienElapsedTicks = Stopwatch.GetTimestamp() - alienStartTicks;
Profiler.EndSample();

            // End of the dirty lifetime: every consumer of this tick's dirty flags has run.
Profiler.BeginSample("Clear Dirty Flags");
            for (int i = 0; i < entityKeys.Length; i++)
            {
                var entity = tickBuf.VoxelEntities[entityKeys[i]];
                entity.ClearDirtyFlags();
                tickBuf.VoxelEntities[entityKeys[i]] = entity;
            }
            entityKeys.Dispose();
Profiler.EndSample();

            // Copy data back to VoxelEntities
Profiler.BeginSample("Burst -> Managed Boundary Copy Back");
            foreach(var kvp in entities)
            {
                kvp.Value.CopyDataFrom(tickBuf.VoxelEntities[kvp.Key]);
                kvp.Value.SyncTransformFromData();

                if (bodies.TryGetValue(kvp.Key, out var body))
                {
                    body.CopyDataFrom(tickBuf.VoxelBodies[kvp.Key]);
                }
            }
Profiler.EndSample();

            /////////////////////////////////////////////////////////////////////////
            // Renderer (client) work
            /////////////////////////////////////////////////////////////////////////
            
            // Tick renderer
Profiler.BeginSample("Renderer Tick");
            bool usedRayTracing = rayTracedRenderer?.enabled ?? false;
            bool usedMeshing = meshingRenderer?.enabled ?? false;
            long renderingStartTicks = Stopwatch.GetTimestamp();
            if (usedRayTracing) rayTracedRenderer.Tick();
            if (usedMeshing) meshingRenderer.Tick();
            long renderingElapsedTicks = Stopwatch.GetTimestamp() - renderingStartTicks;
Profiler.EndSample();

            long totalElapsedTicks = Stopwatch.GetTimestamp() - tickStartTicks;
            double totalMilliseconds = TicksToMilliseconds(totalElapsedTicks);
            double physicsMilliseconds = TicksToMilliseconds(physicsElapsedTicks);
            double renderingMilliseconds = TicksToMilliseconds(renderingElapsedTicks);
            // The brick graph is built outside the physics step now, so its cost is already
            // excluded from physicsMilliseconds and only has to come out of the tick bucket.
            double brickGraphMilliseconds = TicksToMilliseconds(alienElapsedTicks);

            LastTickTimings = new TickTimingStats
            {
                IsCreated = true,
                UsedRayTracing = usedRayTracing,
                UsedMeshing = usedMeshing,
                TickMilliseconds = Math.Max(
                    0.0,
                    totalMilliseconds - physicsMilliseconds - renderingMilliseconds -
                    brickGraphMilliseconds),
                PhysicsMilliseconds = physicsMilliseconds,
                BrickGraphMilliseconds = brickGraphMilliseconds,
                RenderingMilliseconds = renderingMilliseconds,
                TotalMilliseconds = totalMilliseconds
            };
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        public virtual void DoTick(
            WorldStageInputs world) { }

        private void BuildAlienReadContext()
        {
            alienEntityViews.Clear();

            foreach(var kvp in tickBuf.VoxelEntities)
            {
                VoxelEntityData entity = kvp.Value;
                float4x4 localToWorldMatrix = float4x4.TRS(entity.transform.pos, entity.transform.rot, 1f);
                float4x4 worldToLocal = math.inverse(localToWorldMatrix);
                float3 worldAabbMin = entity.transform.pos;
                float3 worldAabbMax = entity.transform.pos;

                if (entity.sectors.Count > 0)
                {
                    NativeArray<int3> sectorKeys = entity.sectors.GetKeyArray(Allocator.Temp);
                    int3 minSector = sectorKeys[0];
                    int3 maxSector = sectorKeys[0];

                    for (int k = 1; k < sectorKeys.Length; k++)
                    {
                        minSector = math.min(minSector, sectorKeys[k]);
                        maxSector = math.max(maxSector, sectorKeys[k]);
                    }

                    sectorKeys.Dispose();

                    float3 localMin = minSector * Sector.SECTOR_SIZE_IN_BLOCKS;
                    float3 localMax = (maxSector + 1) * Sector.SECTOR_SIZE_IN_BLOCKS;
                    for (int mask = 0; mask < 8; mask++)
                    {
                        float3 localCorner = new float3(
                            (mask & 1) == 0 ? localMin.x : localMax.x,
                            (mask & 2) == 0 ? localMin.y : localMax.y,
                            (mask & 4) == 0 ? localMin.z : localMax.z);
                        float3 worldCorner = math.transform(entity.transform, localCorner);
                        worldAabbMin = math.min(worldAabbMin, worldCorner);
                        worldAabbMax = math.max(worldAabbMax, worldCorner);
                    }
                }

                alienEntityViews.Add(new AlienEntityView
                {
                    EntityId = kvp.Key,
                    LocalToWorld = entity.transform,
                    WorldToLocal = worldToLocal,
                    Sectors = entity.sectors.AsReadOnly(),
                    WorldAabbMin = worldAabbMin,
                    WorldAabbMax = worldAabbMax
                });
            }

            automataTickBuf.ReadContext = new AutomataReadContext
            {
                AlienQuery = new AlienOccupancyQuery
                {
                    EntitiesInDeterministicOrder = alienEntityViews.AsArray()
                }
            };
        }

        /// <summary>
        /// Saves every registered <see cref="VoxelEntity"/> to a <c>.vxw</c> file at <paramref name="path"/>.
        /// Each entity's current Unity transform is synced into its native data prior to serialization.
        /// </summary>
        public void Save(string path)
        {
            var list = new List<(Guid128, VoxelEntity, bool, float3, float3)>(entities.Count);
            foreach(var e in entities.Values)
            {
                // TODO: FIXME: Subtle bug -- will this break tick continuity? (this overwrites prevTransform)
                e.SyncTransformToData();
                var (hasBody, linearVelocity, angularVelocity) = CaptureBodyState(e);
                list.Add((e.PersistentGuid, e, hasBody, linearVelocity, angularVelocity));
            }
            WorldSaver.Save(path, list);
        }

        /// <summary>
        /// Captures the part of an entity's physics state that lives on <see cref="VoxelBody"/>:
        /// whether an enabled component is present, and (for a moving entity) its velocity.
        /// Staticness is not captured here — it belongs to the entity and <see cref="WorldSaver"/>
        /// reads it from <see cref="VoxelEntityData.isStatic"/>.
        /// physicsEnabled is deliberately NOT consulted: it only controls Unity Rigidbody
        /// creation in VoxelBody.Awake — participation in the voxel physics world is purely
        /// registration (enabled component) + the entity's isStatic, and static colliders are
        /// typically authored with physicsEnabled = false.
        /// Uses GetComponent rather than the <see cref="bodies"/> dictionary so bodies on
        /// entities that are not currently registered are still captured.
        /// </summary>
        private static (bool HasBody, float3 LinearVelocity, float3 AngularVelocity) CaptureBodyState(VoxelEntity e)
        {
            if (!e.TryGetComponent<VoxelBody>(out var body) || !body.enabled)
            {
                Debug.LogWarning($"Captured no VoxelBody for {e.name}");
                return (false, float3.zero, float3.zero);
            }

            if (e.IsStatic)
            {
                // Static entities never move; velocity is meaningless, so persist zero.
                return (true, float3.zero, float3.zero);
            }

            // Persist the current physics velocity so the body resumes its motion on load rather
            // than restarting from rest. GetDataCopy reflects the latest tick's exported velocity.
            var motionVelocity = body.GetDataCopy().motionVelocity;
            return (true, motionVelocity.LinearVelocity, motionVelocity.AngularVelocity);
        }

        /// <summary>
        /// Saves the world using the inspector-configured save/load path.
        /// </summary>
        [InspectorButton("Save World", PlayModeOnly = true)]
        public void Save()
        {
            string path = ResolveSaveLoadPath();
            Save(path);
            Debug.Log($"Saved VoxelisX world to {path}", this);
        }

        /// <summary>
        /// Loads the world using the inspector-configured save/load path.
        /// </summary>
        [InspectorButton("Load World", PlayModeOnly = true)]
        public void Load()
        {
            Load(ResolveSaveLoadPath());
        }

        /// <summary>
        /// Loads every <see cref="VoxelEntity"/> stored in the <c>.vxw</c> file at <paramref name="path"/>.
        /// Mirrors <see cref="Save(string)"/> so callers (e.g. tooling / a dev console) can target an
        /// arbitrary path instead of the inspector-configured one.
        /// </summary>
        public void Load(string path)
        {
            WorldLoader.Load(path, rec =>
            {
                var go = new GameObject($"VoxelEntity_{rec.Guid}");

                go.SetActive(false);

                var e = go.AddComponent<VoxelEntity>();
                if (e != null)
                {
                    e.PersistentGuid = rec.Guid;
                    // Restore the protected designation so interaction tools keep refusing to
                    // unfreeze/drag this entity after load (keyed by GUID, not a stale scene ref).
                    e.IsProtected = rec.Protected;
                    e.IsStatic = rec.IsStatic;
                }

                Debug.Log($"{rec.Guid}: {rec.Flags}");

                if (rec.HasBody)
                {
                    // Fields must be assigned while the GameObject is still inactive:
                    // VoxelBody.Awake consumes physicsEnabled (Rigidbody creation).
                    // physicsEnabled must stay OFF: it only makes VoxelBody.Awake spawn a Unity
                    // Rigidbody, which the voxel physics never reads (participation is registration
                    // + the entity's isStatic). Authoring (e.g. CreateAlignedDetachedEntity) leaves it
                    // false and lets the voxel sim drive the body. Deriving it as `Dynamic -> true` here
                    // spawned a rogue PhysX Rigidbody that free-fell under gravity and fought the sim's
                    // per-frame transform writes, so loaded dynamic bodies drifted off and looked
                    // like they "failed to load" while static bodies (no Rigidbody) stayed put.
                    var body = go.AddComponent<VoxelBody>();
                    body.physicsEnabled = false;

                    // Leave this to ON regardless of the save file (it does not exist in vxw so far).
                    // TODO: Maybe decide if we should always do accuratePhysics or save to vxw.
                    body.accuratePhysics = true;
                }

                go.SetActive(true);

                // Restore physics velocity AFTER activation — VoxelBody.Awake reinitializes its data
                // (motionVelocity back to zero), so this must run once the component is live. Only
                // dynamic bodies carry meaningful velocity; the solver ignores a static body's.
                if (rec.HasBody && !rec.IsStatic && go.TryGetComponent<VoxelBody>(out var loadedBody))
                {
                    loadedBody.SetVelocity(rec.LinearVelocity, rec.AngularVelocity);
                }

                return e;
            });

            Debug.Log($"Loaded VoxelisX world from {path}", this);
        }

        [InspectorButton("Choose Save/Load Path")]
        private void ChooseSaveLoadPath()
        {
#if UNITY_EDITOR
            string currentPath = ResolveSaveLoadPath();
            string directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Application.persistentDataPath;
            }

            string fileName = Path.GetFileName(currentPath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = DefaultSaveLoadFileName;
            }

            string selectedPath = EditorUtility.SaveFilePanel(
                "Choose VoxelisX world file",
                directory,
                fileName,
                "vxw");

            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            saveLoadPath = selectedPath;
            EditorUtility.SetDirty(this);
#endif
        }

        private string ResolveSaveLoadPath()
        {
            string path = string.IsNullOrWhiteSpace(saveLoadPath)
                ? DefaultSaveLoadFileName
                : saveLoadPath;

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(Application.persistentDataPath, path);
        }
    }
}
