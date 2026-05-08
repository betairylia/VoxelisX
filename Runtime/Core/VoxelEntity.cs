using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections.NotBurstCompatible;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis
{
    public unsafe struct VoxelEntityData
    {
        /// <summary>
        /// Dictionary mapping sector positions to their corresponding sector data.
        /// Key is the sector position in sector-space coordinates.
        /// </summary>
        // public Dictionary<Vector3Int, Sector> sectors = new Dictionary<Vector3Int, Sector>();
        public LockableUnsafeHashMap<int3, SectorHandle> sectors;
        public LockableUnsafeHashMap<int3, SectorNeighborHandles> sectorNeighbors;
        public NativeQueue<int3> sectorsToRemove;

        /// <summary>
        /// The rigid transform representing position and rotation of this entity.
        /// Synced with Unity Transform via SyncTransformToData/SyncTransformFromData.
        /// </summary>
        public RigidTransform transform;
        public RigidTransform previousTransform;
        public float3 linearVelocity;
        public float3 angularVelocity;

        // Dirty propagation
        public ushort entityDirtyFlags;
        public ushort entityRequireUpdateFlags;

        public VoxelEntityData(Allocator allocator)
        {
            sectors = new(1, allocator);
            sectorNeighbors = new(1, allocator);
            sectorsToRemove = new(allocator);
            transform = RigidTransform.identity;
            previousTransform = RigidTransform.identity;
            linearVelocity = float3.zero;
            angularVelocity = float3.zero;
            entityDirtyFlags = 0;
            entityRequireUpdateFlags = 0;
        }

        public VoxelEntityData(Allocator allocator, Transform transform)
        {
            sectors = new(1, allocator);
            sectorNeighbors = new(1, allocator);
            sectorsToRemove = new(allocator);
            this.transform = new RigidTransform(transform.rotation, transform.position);
            previousTransform = this.transform;
            linearVelocity = float3.zero;
            angularVelocity = float3.zero;
            entityDirtyFlags = 0;
            entityRequireUpdateFlags = 0;
        }

        /// <summary>
        /// Adds an empty sector at the specified position.
        /// </summary>
        public void AddEmptySectorAt(int3 pos)
        {
            AddSectorAt(pos, SectorHandle.AllocEmpty());
        }

        /// <summary>
        /// Copies a sector and adds it at the specified position.
        /// </summary>
        public void CopyAndAddSectorAt(int3 pos, Sector sector)
        {
            // Allocate memory for the Sector struct itself
            Sector* sectorPtr = (Sector*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<Sector>(),
                UnsafeUtility.AlignOf<Sector>(),
                Allocator.Persistent);

            // Initialize the sector in-place
            *sectorPtr = sector;

            AddSectorAt(pos, new SectorHandle(sectorPtr));
        }

        /// <summary>
        /// Adds a sector handle at the specified position.
        /// </summary>
        public void AddSectorAt(int3 pos, SectorHandle sector)
        {
            sectors.Add(pos, sector);
            SectorNeighborHandles newHandles = SectorNeighborHandles.Create();
            sectorNeighbors.Add(pos, newHandles);

            // Bidirectional linking for all 26 neighbors
            for (int d = 0; d < NeighborhoodSettings.neighborhoodCount; d++)
            {
                int3 dir = NeighborhoodSettings.Directions[d];
                int3 neighborPos = pos + dir;

                if (sectors.TryGetValue(neighborPos, out SectorHandle neighborSector))
                {
                    // Link new sector to existing neighbor
                    newHandles.Neighbors[d] = neighborSector;
                    
                    // Link neighbor back to new sector (find opposite direction)
                    if (sectorNeighbors.TryGetValue(neighborPos, out SectorNeighborHandles neighborHandles))
                    {
                        int oppD = NeighborhoodSettings.OppositeDirectionIndices[d];
                        neighborHandles.Neighbors[oppD] = sector;
                    }
                }
            }
        }

        /// <summary>
        /// Removes and disposes a sector at the specified position.
        /// </summary>
        /// <param name="pos">The sector position to remove.</param>
        /// <returns>True if the sector was found and removed, false otherwise.</returns>
        public bool RemoveSectorAt(int3 pos)
        {
            if (!sectors.ContainsKey(pos))
            {
                return false;
            }

            // Update neighbors
            for (int d = 0; d < NeighborhoodSettings.Directions.Length; d++)
            {
                int3 dir = NeighborhoodSettings.Directions[d];
                if (sectorNeighbors.TryGetValue(pos - dir, out SectorNeighborHandles handles))
                {
                    handles.Neighbors[d] = new SectorHandle(null);
                }
            }
            
            sectors[pos].Dispose(Allocator.Persistent);
            sectorNeighbors[pos].Dispose(Allocator.Persistent);
            sectors.Remove(pos);
            sectorNeighbors.Remove(pos);
            return true;
        }

        /// <summary>
        /// Gets the block at the specified world position.
        /// </summary>
        /// <param name="pos">World position of the block in block coordinates.</param>
        /// <returns>The block at the specified position, or Block.Empty if no sector exists at that location.</returns>
        public Block GetBlock(int3 pos)
        {
            int3 sectorPos = new int3(
                pos.x >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.y >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.z >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS));

            if (!sectors.ContainsKey(sectorPos))
            {
                return Block.Empty;
            }

            return sectors[sectorPos].GetBlock(
                pos.x & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.y & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.z & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS))
            );
        }

        /// <summary>
        /// Sets the block at the specified world position. Creates a new sector if one doesn't exist.
        /// </summary>
        /// <param name="pos">World position of the block in block coordinates.</param>
        /// <param name="b">The block data to set.</param>
        /// <remarks>
        /// If the sector doesn't exist at the calculated sector position, a new sector will be automatically created.
        /// </remarks>
        public void SetBlock(int3 pos, Block b)
        {
            int3 sectorPos = new int3(
                pos.x >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.y >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS),
                pos.z >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS));

            if (!sectors.ContainsKey(sectorPos))
            {
                AddEmptySectorAt(sectorPos);
            }

            // Modify sector directly in dictionary
            sectors[sectorPos].SetBlock(
                pos.x & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.y & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                pos.z & (Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS)),
                b
            );
        }

        /// <summary>
        /// Calculates the total host (CPU) memory usage of all sectors in this entity.
        /// </summary>
        /// <returns>Total memory usage in kilobytes.</returns>
        public ulong GetHostMemoryUsageKB()
        {
            ulong result = 0;
            foreach (var kvp in sectors)
            {
                result += (ulong)(kvp.Value.Get().MemoryUsage / 1024);
            }

            return result;
        }

        /// <summary>
        /// Propagates dirty flags to neighbors. Iterates ALL sectors, pulls dirty flags from neighbors.
        /// </summary>
        public JobHandle PropagateDirtyFlags(DirtyFlags flagsToPropagate = DirtyFlags.All, bool async = false)
        {
            if (sectors.Count == 0) return default;

            // First pass: Ensure neighbor sectors exist for all dirty sectors at boundaries
            // This allows dirty flags to propagate even to sectors that don't exist yet
            EnsureNeighborSectorsForDirtyBoundaries(flagsToPropagate);

            // Get all sector positions
            NativeArray<int3> allPositions = sectors.GetKeyArray(Allocator.TempJob);

            var job = new DirtyPropagationJob
            {
                allSectorPositions = allPositions,
                sectors = sectors.AsReadOnly(),
                sectorNeighbors = sectorNeighbors.AsReadOnly(),
                neighborhoodType = NeighborhoodSettings.neighborhoodType,
                flagsToPropagate = flagsToPropagate
            };

            JobHandle handle;
            if (async)
            {
                handle = job.Schedule(allPositions.Length, 1);
                allPositions.Dispose(handle);
            }
            else
            {
                job.Run(allPositions.Length);
                allPositions.Dispose();
                handle = default;
            }

            return handle;
        }

        /// <summary>
        /// Ensures that neighbor sectors exist for all dirty bricks at sector boundaries.
        /// Uses sectorNeighborsToCreate as a coarse direction mask, then checks brick flags so
        /// only flags allowed to allocate local sectors create missing neighbors.
        /// Must be called from main thread (not thread-safe).
        /// </summary>
        private void EnsureNeighborSectorsForDirtyBoundaries(DirtyFlags flagsToPropagate)
        {
            ushort requestedFlags = (ushort)flagsToPropagate;
            ushort allocationFlags = DirtyPropagationSettings.FilterCanAllocateLocalBricks(
                DirtyPropagationSettings.FilterCanPropagateToNeighbor(requestedFlags));

            if (allocationFlags == 0)
            {
                return;
            }

            // Collect which neighbor sectors need to be created
            HashSet<int3> sectorsToCreate = new HashSet<int3>();

            foreach (var kvp in sectors)
            {
                int3 sectorPos = kvp.Key;
                ref Sector sector = ref kvp.Value.Get();

                // O(1) check: Skip if sector has no cross-sector propagation needs
                if (sector.sectorNeighborsToCreate == 0) continue;

                // O(26) loop: Check each of 26 possible neighbor sectors
                for (int dir = 0; dir < NeighborhoodSettings.neighborhoodCount; dir++)
                {
                    // Skip if this neighbor sector doesn't need creation
                    if (!NeighborhoodSettings.HasDirection(sector.sectorNeighborsToCreate, dir)) continue;
                    if (!HasDirtyBrickThatCanAllocateNeighbor(ref sector, dir, allocationFlags)) continue;

                    int3 neighborSectorPos = sectorPos + NeighborhoodSettings.Directions[dir];

                    // Add to creation list if it doesn't exist
                    if (!sectors.ContainsKey(neighborSectorPos))
                    {
                        sectorsToCreate.Add(neighborSectorPos);
                    }
                }
            }

            // Create all needed sectors
            foreach (int3 pos in sectorsToCreate)
            {
                AddEmptySectorAt(pos);
            }
        }

        private static bool HasDirtyBrickThatCanAllocateNeighbor(ref Sector sector, int dir, ushort allocationFlags)
        {
            int totalBricks = Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS;

            for (int brickIdx = 0; brickIdx < totalBricks; brickIdx++)
            {
                if ((sector.brickDirtyFlags[brickIdx] & allocationFlags) == 0) continue;
                if (!NeighborhoodSettings.HasDirection(sector.brickDirtyDirectionMask[brickIdx], dir)) continue;
                if (!NeighborhoodSettings.HasDirection(Sector.GetBrickSectorNeighborMask(brickIdx), dir)) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Clears dirty flags for all sectors. Should be called after propagation completes.
        /// </summary>
        public void ClearDirtyFlags()
        {
            entityDirtyFlags = 0;
            foreach (var kvp in sectors)
            {
                ref Sector sector = ref kvp.Value.Get();
                sector.ClearAllDirtyFlags();
            }
        }

        /// <summary>
        /// Clears require-update flags for all sectors. Should be called after systems have processed the updates.
        /// </summary>
        public void ClearRequireUpdates()
        {
            entityDirtyFlags = 0;
            foreach (var kvp in sectors)
            {
                ref Sector sector = ref kvp.Value.Get();
                sector.ClearAllRequireUpdateFlags();
            }
        }

        public void RefreshAllocatedBrickLists()
        {
            foreach (var kvp in sectors)
            {
                kvp.Value.Ptr->UpdateNonEmptyBricks();
            }
        }

        public void SetTransformForDirtyPropagation(RigidTransform nextTransform, float deltaTime)
        {
            previousTransform = transform;
            UpdateCurrentTransformForDirtyPropagation(nextTransform, deltaTime);
        }

        public void UpdateCurrentTransformForDirtyPropagation(RigidTransform nextTransform, float deltaTime)
        {
            transform = nextTransform;

            if (deltaTime <= 0f)
            {
                linearVelocity = float3.zero;
                angularVelocity = float3.zero;
                return;
            }

            linearVelocity = (transform.pos - previousTransform.pos) / deltaTime;
            quaternion deltaRotation = math.mul(transform.rot, math.inverse(previousTransform.rot));
            float4 deltaValue = deltaRotation.value;
            if (deltaValue.w < 0f)
            {
                deltaValue = -deltaValue;
            }

            float sinHalfAngle = math.length(deltaValue.xyz);
            if (sinHalfAngle < 1e-6f)
            {
                angularVelocity = float3.zero;
                return;
            }

            float angle = 2f * math.atan2(sinHalfAngle, deltaValue.w);
            angularVelocity = deltaValue.xyz / sinHalfAngle * (angle / deltaTime);
        }

        /// <summary>
        /// Disposes all resources used by this voxel entity data, including all sectors and the native collections.
        /// </summary>
        public void Dispose()
        {
            if (!sectors.IsCreated)
            {
                return;
            }

            foreach (var kvp in sectors)
            {
                Sector* sectorPtr = kvp.Value.Ptr;

                // First dispose the sector's internal allocations
                sectorPtr->Dispose(Allocator.Persistent);

                // Then free the Sector struct memory itself
                UnsafeUtility.Free(sectorPtr, Allocator.Persistent);
            }

            // Clear and dispose the hashmap
            sectors.Clear();

            // Dispose the native collections
            if (sectors.IsCreated)
            {
                sectors.Dispose();
                sectors = default;
            }
            if (sectorNeighbors.IsCreated)
            {
                foreach (var kvp in sectorNeighbors)
                {
                    kvp.Value.Dispose();
                }
                sectorNeighbors.Dispose();
                sectorNeighbors = default;
            }
            if (sectorsToRemove.IsCreated)
            {
                sectorsToRemove.Dispose();
                sectorsToRemove = default;
            }
        }
    }
    
    /// <summary>
    /// Represents a voxel-based entity in the game world. This is the main component for managing
    /// collections of voxel sectors and interfacing with the physics system.
    /// </summary>
    /// <remarks>
    /// VoxelEntity organizes voxels into sectors for efficient storage. Each sector
    /// contains a fixed-size grid of bricks, which in turn contain individual voxel blocks.
    /// Rendering is managed separately by VoxelisXRenderer.
    /// </remarks>
    public unsafe partial class VoxelEntity : MonoBehaviour, IDisposable
    {
        private VoxelEntityData data;
        public VoxelEntityData GetDataCopy() => data;

        private void Awake()
        {
            data = new VoxelEntityData(Allocator.Persistent, transform);
        }

        public void CopyDataFrom(VoxelEntityData srcData)
        {
            data = srcData;
        }
        
        public LockableUnsafeHashMap<int3, SectorHandle> Sectors => data.sectors;

        /// <summary>
        /// Adds an empty sector at the specified position.
        /// </summary>
        public void AddEmptySectorAt(int3 pos)
        {
            data.AddEmptySectorAt(pos);
        }

        /// <summary>
        /// Copies a sector and adds it at the specified position.
        /// </summary>
        public void CopyAndAddSectorAt(int3 pos, Sector sector)
        {
            data.CopyAndAddSectorAt(pos, sector);
        }

        /// <summary>
        /// Adds a sector handle at the specified position.
        /// </summary>
        public void AddSectorAt(int3 pos, SectorHandle sector)
        {
            data.AddSectorAt(pos, sector);
        }

        /// <summary>
        /// Removes and disposes a sector at the specified position.
        /// </summary>
        /// <param name="pos">The sector position to remove.</param>
        /// <returns>True if the sector was found and removed, false otherwise.</returns>
        public bool RemoveSectorAt(int3 pos)
        {
            return data.RemoveSectorAt(pos);
        }

        /// <summary>
        /// Syncs the Unity Transform to the VoxelEntityData struct (inward sync).
        /// Copies position and rotation from Unity's Transform component to the native RigidTransform.
        /// </summary>
        public void SyncTransformToData()
        {
            float deltaTime = Time.deltaTime > 0f ? Time.deltaTime : Time.fixedDeltaTime;
            SyncTransformToData(deltaTime);
        }

        public void SyncTransformToData(float deltaTime)
        {
            data.SetTransformForDirtyPropagation(new RigidTransform(transform.rotation, transform.position), deltaTime);
        }

        public void SyncCurrentTransformToData(float deltaTime)
        {
            data.UpdateCurrentTransformForDirtyPropagation(new RigidTransform(transform.rotation, transform.position), deltaTime);
        }

        /// <summary>
        /// Syncs the VoxelEntityData struct to the Unity Transform (outward sync).
        /// Copies position and rotation from the native RigidTransform to Unity's Transform component.
        /// </summary>
        public void SyncTransformFromData()
        {
            transform.SetPositionAndRotation(data.transform.pos, data.transform.rot);
        }

        /// <summary>
        /// Queue of sector positions that are scheduled for removal and cleanup.
        /// Used to defer disposal of sectors until after rendering is complete.
        /// </summary>
        public Queue<int3> sectorsToRemove = new Queue<int3>();

        /// <summary>
        /// Called when the component is enabled. Registers this entity with the renderer and initializes physics body.
        /// </summary>
        private void OnEnable()
        {
            VoxelisXCoreWorld.instance.AddEntity(this);
        }

        /// <summary>
        /// Called when the component is disabled. Unregisters this entity from the renderer.
        /// </summary>
        private void OnDisable()
        {
            VoxelisXCoreWorld.instance.RemoveEntity(this);
            Dispose();
        }

        /// <summary>
        /// Disposes all resources used by this voxel entity, including all sectors and their data.
        /// </summary>
        public void Dispose()
        {
            data.Dispose();
        }

        /// <summary>
        /// Calculates the total host (CPU) memory usage of all sectors in this entity.
        /// </summary>
        /// <returns>Total memory usage in kilobytes.</returns>
        public ulong GetHostMemoryUsageKB()
        {
            return data.GetHostMemoryUsageKB();
        }

        /// <summary>
        /// Helper to get sector block position from sector position.
        /// </summary>
        public static int3 GetSectorBlockPos(int3 sectorPos)
        {
            return sectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
        }

        /// <summary>
        /// Gets the block at the specified world position.
        /// </summary>
        /// <param name="pos">World position of the block in block coordinates.</param>
        /// <returns>The block at the specified position, or Block.Empty if no sector exists at that location.</returns>
        public Block GetBlock(int3 pos)
        {
            return data.GetBlock(pos);
        }

        /// <summary>
        /// Sets the block at the specified world position. Creates a new sector if one doesn't exist.
        /// </summary>
        /// <param name="pos">World position of the block in block coordinates.</param>
        /// <param name="b">The block data to set.</param>
        /// <remarks>
        /// If the sector doesn't exist at the calculated sector position, a new sector will be automatically created.
        /// </remarks>
        public void SetBlock(int3 pos, Block b)
        {
            data.SetBlock(pos, b);
        }

        /// <summary>
        /// Propagates dirty flags to neighbor bricks. Call this once per tick after modifications.
        /// </summary>
        public JobHandle PropagateDirtyFlags(DirtyFlags flags = DirtyFlags.All, bool async = false)
        {
            return data.PropagateDirtyFlags(flags, async);
        }

        /// <summary>
        /// Clears dirty flags for all sectors (call after propagation completes).
        /// </summary>
        public void ClearDirtyFlags() => data.ClearDirtyFlags();
        
        /// <summary>
        /// Clears dirty flags for all sectors (call before propagation completes).
        /// </summary>
        public void ClearRequireUpdates() => data.ClearRequireUpdates();

        public void RefreshAllocatedBrickLists() => data.RefreshAllocatedBrickLists();

        /// <summary>
        /// Gets the object-to-world transformation matrix for this entity.
        /// </summary>
        /// <returns>The local-to-world matrix of this entity's transform.</returns>
        public float4x4 ObjectToWorld()
        {
            return transform.localToWorldMatrix;
        }

        /// <summary>
        /// Gets the world-to-object transformation matrix for this entity.
        /// </summary>
        /// <returns>The world-to-local matrix of this entity's transform.</returns>
        public float4x4 WorldToObject()
        {
            return transform.worldToLocalMatrix;
        }
    }
}
