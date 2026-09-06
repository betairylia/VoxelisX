using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix
{
    /// <summary>
    /// Abstract base class for implementing infinite voxel world loading systems.
    /// Manages loading and unloading of sectors based on a center point and configurable radius.
    /// </summary>
    /// <remarks>
    /// This component automatically loads sectors near the load center and unloads distant sectors.
    /// Sectors are loaded in order of Manhattan distance from the center for optimal streaming.
    /// Derived classes must implement LoadSector to define how sector data is generated or loaded.
    /// </remarks>
    [RequireComponent(typeof(VoxelEntity))]
    public abstract class InfiniteLoader : MonoBehaviour
    {
        /// <summary>
        /// The transform whose position determines which sectors to load.
        /// Typically set to the player's transform.
        /// </summary>
        public Transform loadCenter;

        /// <summary>
        /// Called to load a sector at the specified position.
        /// Must be implemented by derived classes to define loading behavior.
        /// </summary>
        /// <param name="sectorPos">The sector position to load in sector coordinates.</param>
        public abstract void LoadSector(int3 sectorPos);

        /// <summary>
        /// The voxel entity that owns the loaded sectors.
        /// </summary>
        protected VoxelEntity entity;

        private List<int3> sectorLoadOrder = new();
        private bool initialized;

        /// <summary>
        /// Maximum bounds for sector loading in each axis, in sector units.
        /// </summary>
        public int3 sectorLoadBounds;

        /// <summary>
        /// Radius in blocks for loading sectors (converted to sector units internally).
        /// </summary>
        public float sectorLoadRadiusInBlocks;

        /// <summary>
        /// Radius in blocks for unloading sectors. Should be larger than load radius to prevent thrashing.
        /// </summary>
        public float sectorUnloadRadiusInBlocks;

        /// <summary>
        /// Set of sector positions currently being loaded to prevent duplicate load requests.
        /// </summary>
        protected HashSet<int3> loadingSectors = new();
        
        /// <summary>
        /// Initializes the loader by finding the VoxelEntity component and calculating load order.
        /// </summary>
        // Streaming is not driven by the world tick in v1 (see SERVER_CLIENT_ARCHITECTURE.md).
        // Call Tick() from your own loop. The loader writes into server data through the
        // entity component, so it only works in a process that runs the server.
        protected virtual void OnEnable()
        {
            InitializeLoader();
        }

        protected virtual void Start()
        {
            InitializeLoader();
        }

        protected void InitializeLoader()
        {
            if (initialized)
            {
                return;
            }

            entity = gameObject.GetComponent<VoxelEntity>();
            ResetSectorLoadOrder();
            initialized = true;
        }

        /// <summary>
        /// Gets the current sector position of the load center.
        /// </summary>
        /// <remarks>
        /// Divides the load center's world position by SIZE_IN_BRICKS (16) to convert from world space to sector space.
        /// Note: This divides by SIZE_IN_BRICKS instead of SECTOR_SIZE_IN_BLOCKS (128), which means
        /// the sector positions returned are scaled by a factor of 8. This is intentional design
        /// to match the sector coordinate system used throughout the engine.
        /// </remarks>
        public int3 loadCenterSectorPos => (int3)math.floor(loadCenter.position / Sector.SIZE_IN_BRICKS);

        /// <summary>
        /// Determines whether a sector should be unloaded based on its distance from the center.
        /// </summary>
        /// <param name="sectorPos">The sector position to check.</param>
        /// <param name="centerSectorPos">The current center sector position.</param>
        /// <returns>True if the sector is outside the unload radius or bounds.</returns>
        public bool ShouldUnload(int3 sectorPos, int3 centerSectorPos)
        {
            int3 relativePos = sectorPos - loadCenterSectorPos;
            relativePos.y = 0;
            return !((math.length(relativePos) * Sector.SECTOR_SIZE_IN_BLOCKS) <= sectorUnloadRadiusInBlocks
                && math.abs(relativePos.x) <= sectorLoadBounds.x
                && math.abs(relativePos.y) <= sectorLoadBounds.y
                && math.abs(relativePos.z) <= sectorLoadBounds.z);
        }
        
        /// <summary>
        /// Recalculates the sector loading order based on current bounds and radius settings.
        /// </summary>
        /// <remarks>
        /// This should be called whenever sectorLoadBounds or sectorLoadRadiusInBlocks changes.
        /// The load order is cached for efficiency and reused each tick.
        /// </remarks>
        public void ResetSectorLoadOrder()
        {
            // Fill sector load order list
            SectorLoadGeometry.GeneratePointsInIntersection(sectorLoadBounds, sectorLoadRadiusInBlocks / Sector.SECTOR_SIZE_IN_BLOCKS, ref sectorLoadOrder);
        }

        /// <summary>
        /// Performs one update tick: unloads distant sectors and loads nearby sectors.
        /// </summary>
        /// <remarks>
        /// This method first unloads sectors outside the unload radius, then loads sectors
        /// within the load radius that aren't already loaded or being loaded.
        /// TODO: Split sector loading across multiple frames for better performance.
        /// </remarks>
        public virtual void Tick()
        {
            int3 lsp = loadCenterSectorPos;

            // Unload sectors
            var list = entity.Sectors.GetKeyArray(Allocator.Temp);
            foreach (var _sectorPos in list)
            {
                int3 sectorPos = new int3(_sectorPos.x, _sectorPos.y, _sectorPos.z);
                if (ShouldUnload(sectorPos, lsp))
                {
                    entity.RemoveSectorAt(sectorPos);
                }
            }

            // Load sectors
            // TODO: Split to frames
            for (int currentIndex = 0; currentIndex < sectorLoadOrder.Count; currentIndex++)
            {
                int3 targetSectorPos = lsp + sectorLoadOrder[currentIndex];
                // Debug.Log($"Loaded sector @ {targetSectorPos}");
                if (loadingSectors.Contains(targetSectorPos) || entity.Sectors.ContainsKey(targetSectorPos))
                {
                    continue;
                }

                loadingSectors.Add(targetSectorPos);
                LoadSector(targetSectorPos);
            }
        }

        /// <summary>
        /// Marks a sector as fully loaded and adds it to the entity's sector collection.
        /// Should be called by derived classes when LoadSector completes.
        /// </summary>
        /// <param name="sectorPos">The position of the sector that has finished loading.</param>
        /// <param name="sector">The sector data that has finished loading.</param>
        public unsafe void MarkSectorLoaded(int3 sectorPos, SectorHandle sector)
        {
            Debug.Log($"Sector Added at {sectorPos}");
            loadingSectors.Remove(sectorPos);
            entity.AddSectorAt(sectorPos, sector);
        }
    }
}
