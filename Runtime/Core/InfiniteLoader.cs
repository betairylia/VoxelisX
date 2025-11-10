using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Voxelis
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
        /// Whether to allow parallel loading of sectors (not yet implemented).
        /// </summary>
        public bool allowParallelization = false;

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
        public abstract void LoadSector(Vector3Int sectorPos);

        /// <summary>
        /// Called at the end of each load tick after all sectors have been processed.
        /// Can be overridden to perform cleanup or batch operations.
        /// </summary>
        public virtual void EndLoadTick() { }

        /// <summary>
        /// The voxel entity that owns the loaded sectors.
        /// </summary>
        protected VoxelEntity entity;

        private List<Vector3Int> sectorLoadOrder = new();
        private int currentIndex;

        /// <summary>
        /// Maximum bounds for sector loading in each axis, in sector units.
        /// </summary>
        public Vector3Int sectorLoadBounds;

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
        protected HashSet<Vector3Int> loadingSectors = new();
        
        /// <summary>
        /// Generates all integer points that are within both a bounding box and a cylindrical volume (ignoring Y),
        /// ordered by Manhattan distance from the origin.
        /// </summary>
        /// <param name="bounds">The box bounds, representing the maximum absolute value for each coordinate.</param>
        /// <param name="radius">The radius of the cylinder in the XZ plane centered at the origin.</param>
        /// <param name="result">Output list to populate with generated points, ordered by Manhattan distance.</param>
        /// <remarks>
        /// This method generates points by iterating through Manhattan distance shells, ensuring
        /// points closer to the origin are added first. The Y component is ignored for distance checks,
        /// creating a cylindrical loading volume which is typical for voxel games.
        /// </remarks>
        public static void GeneratePointsInIntersection(Vector3Int bounds, float radius, ref List<Vector3Int> result)
        {
            result.Clear();
            float radiusSqr = radius * radius;
            
            // Calculate the maximum Manhattan distance we need to check
            int maxDistance = bounds.x + bounds.y + bounds.z;
            
            // Process points by increasing Manhattan distance (ensures ordering by Manhattan distance)
            for (int manhattanDist = 0; manhattanDist <= maxDistance; manhattanDist++)
            {
                // For each possible Manhattan distance, generate all points with that distance
                for (int x = -Mathf.Min(manhattanDist, bounds.x); x <= Mathf.Min(manhattanDist, bounds.x); x++)
                {
                    int absX = Mathf.Abs(x);
                    int remainingDist = manhattanDist - absX;
                    
                    for (int y = -Mathf.Min(remainingDist, bounds.y); y <= Mathf.Min(remainingDist, bounds.y); y++)
                    {
                        int absY = Mathf.Abs(y);
                        int zValue = remainingDist - absY;
                        
                        // Check if z is within bounds
                        if (zValue >= 0 && zValue <= bounds.z)
                        {
                            // Create point with positive z
                            Vector3Int point = new Vector3Int(x, y, zValue);
                            var point_noY = point;
                            point_noY.y = 0;
                            
                            // Check if within sphere (Euclidean distance < radius)
                            if (point_noY.sqrMagnitude < radiusSqr)
                            {
                                result.Add(point);
                            }
                            
                            // Create point with negative z (if z != 0 to avoid duplicates)
                            if (zValue > 0)
                            {
                                Vector3Int negZPoint = new Vector3Int(x, y, -zValue);
                                
                                // Check if within sphere
                                if (negZPoint.sqrMagnitude < radiusSqr)
                                {
                                    result.Add(negZPoint);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Initializes the loader by finding the VoxelEntity component and calculating load order.
        /// </summary>
        private void Start()
        {
            entity = gameObject.GetComponent<VoxelEntity>();
            ResetSectorLoadOrder();
        }

        /// <summary>
        /// Gets the current sector position of the load center.
        /// </summary>
        /// <remarks>
        /// Divides the load center's world position by SIZE_IN_BRICKS to convert from world space to sector space.
        /// Note: This divides by SIZE_IN_BRICKS (16), not SECTOR_SIZE_IN_BLOCKS (128).
        /// This may be a bug or intentional design choice.
        /// </remarks>
        public Vector3Int loadCenterSectorPos => Vector3Int.FloorToInt(loadCenter.position / Sector.SIZE_IN_BRICKS);

        /// <summary>
        /// Determines whether a sector should be unloaded based on its distance from the center.
        /// </summary>
        /// <param name="sectorPos">The sector position to check.</param>
        /// <param name="centerSectorPos">The current center sector position.</param>
        /// <returns>True if the sector is outside the unload radius or bounds.</returns>
        public bool ShouldUnload(Vector3Int sectorPos, Vector3Int centerSectorPos)
        {
            Vector3Int relativePos = sectorPos - loadCenterSectorPos;
            relativePos.y = 0;
            return !((relativePos.magnitude * Sector.SECTOR_SIZE_IN_BLOCKS) <= sectorUnloadRadiusInBlocks
                && Mathf.Abs(relativePos.x) <= sectorLoadBounds.x
                && Mathf.Abs(relativePos.y) <= sectorLoadBounds.y
                && Mathf.Abs(relativePos.z) <= sectorLoadBounds.z);
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
            GeneratePointsInIntersection(sectorLoadBounds, sectorLoadRadiusInBlocks / Sector.SECTOR_SIZE_IN_BLOCKS, ref sectorLoadOrder);
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
            Vector3Int lsp = loadCenterSectorPos;

            // Unload sectors
            var list = entity.sectors.Keys.ToList();
            foreach (var sectorPos in list)
            {
                if (ShouldUnload(sectorPos, lsp))
                {
                    entity.sectors[sectorPos].Dispose();
                    entity.sectors.Remove(sectorPos);
                    entity.sectorsToRemove.Enqueue(sectorPos);
                }
            }

            // Load sectors
            // TODO: Split to frames
            for (currentIndex = 0; currentIndex < sectorLoadOrder.Count; currentIndex++)
            {
                Vector3Int targetSectorPos = lsp + sectorLoadOrder[currentIndex];
                // Debug.Log($"Loaded sector @ {targetSectorPos}");
                if (loadingSectors.Contains(targetSectorPos) || entity.sectors.ContainsKey(targetSectorPos))
                {
                    continue;
                }

                loadingSectors.Add(targetSectorPos);
                LoadSector(targetSectorPos);
            }

            EndLoadTick();
        }

        /// <summary>
        /// Marks a sector as fully loaded and adds it to the entity's sector collection.
        /// Should be called by derived classes when LoadSector completes.
        /// </summary>
        /// <param name="sectorPos">The position of the sector that has finished loading.</param>
        /// <param name="sector">The sector data that has finished loading.</param>
        public void MarkSectorLoaded(Vector3Int sectorPos, Sector sector)
        {
            Debug.Log($"Sector Added at {sectorPos}");
            loadingSectors.Remove(sectorPos);
            entity.sectors.Add(sectorPos, sector);
        }
    }
}