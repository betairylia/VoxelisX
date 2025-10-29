using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Voxelis
{
    [RequireComponent(typeof(VoxelEntity))]
    public abstract class InfiniteLoader : MonoBehaviour
    {
        public bool allowParallelization = false;
        
        public Transform loadCenter;
        
        public abstract void LoadSector(Vector3Int sectorPos);
        public virtual void EndLoadTick() { }
        protected VoxelEntity entity;
        
        private List<Vector3Int> sectorLoadOrder = new();
        private int currentIndex;

        public Vector3Int sectorLoadBounds;
        public float sectorLoadRadiusInBlocks, sectorUnloadRadiusInBlocks;

        protected HashSet<Vector3Int> loadingSectors = new();
        
        /// <summary>
        /// Generates all integer points that are within both a bounding box and sphere, ordered by Manhattan distance.
        /// </summary>
        /// <param name="bounds">The box bounds, representing the maximum absolute value for each coordinate.</param>
        /// <param name="radius">The radius of the sphere centered at the origin.</param>
        /// <returns>A list of points ordered by Manhattan distance from the origin.</returns>
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

        private void Start()
        {
            entity = gameObject.GetComponent<VoxelEntity>();
            ResetSectorLoadOrder();
        }

        public Vector3Int loadCenterSectorPos => Vector3Int.FloorToInt(loadCenter.position / 16.0f);

        public bool ShouldUnload(Vector3Int sectorPos, Vector3Int centerSectorPos)
        {
            Vector3Int relativePos = sectorPos - loadCenterSectorPos;
            relativePos.y = 0;
            return !((relativePos.magnitude * 128.0f) <= sectorUnloadRadiusInBlocks
                && Mathf.Abs(relativePos.x) <= sectorLoadBounds.x
                && Mathf.Abs(relativePos.y) <= sectorLoadBounds.y
                && Mathf.Abs(relativePos.z) <= sectorLoadBounds.z);
        }
        
        public void ResetSectorLoadOrder()
        {
            // Fill sector load order list
            GeneratePointsInIntersection(sectorLoadBounds, sectorLoadRadiusInBlocks / 128.0f, ref sectorLoadOrder);
        }
        
        public virtual void Tick()
        {
            Vector3Int lsp = loadCenterSectorPos;
            
            // Unload sectors
            var list = entity.Voxels.Values.ToList();
            foreach (var sector in list)
            {
                if (ShouldUnload(sector.sectorPos, lsp))
                {
                    sector.Remove();
                    entity.Voxels.Remove(sector.sectorPos);
                    entity.sectorsToRemove.Enqueue(sector);
                }
            }
            
            // Load sectors
            // TODO: Split to frames
            for (currentIndex = 0; currentIndex < sectorLoadOrder.Count; currentIndex++)
            {
                Vector3Int targetSectorPos = lsp + sectorLoadOrder[currentIndex];
                // Debug.Log($"Loaded sector @ {targetSectorPos}");
                if (loadingSectors.Contains(targetSectorPos) || entity.Voxels.ContainsKey(targetSectorPos))
                {
                    continue;
                }

                loadingSectors.Add(targetSectorPos);
                LoadSector(targetSectorPos);
            }
            
            EndLoadTick();
        }

        public void MarkSectorLoaded(SectorRef sector)
        {
            loadingSectors.Remove(sector.sectorPos);
            entity.Voxels.Add(sector.sectorPos, sector);
        }
    }
}