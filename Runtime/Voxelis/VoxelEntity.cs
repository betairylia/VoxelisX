using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxelis
{
    public class VoxelEntity : MonoBehaviour, IDisposable
    {
        public Dictionary<Vector3Int, SectorRef> Voxels = new Dictionary<Vector3Int, SectorRef>();

        private void OnEnable()
        {
            FindFirstObjectByType<VoxelisXRenderer>().AddEntity(this);
        }

        private void OnDisable()
        {
            FindFirstObjectByType<VoxelisXRenderer>().RemoveEntity(this);
        }

        private void OnDestroy()
        {
            Dispose();
        }

        public void Dispose()
        {
            foreach (var sector in Voxels.Values)
            {
                sector?.Dispose();
            }
            Voxels.Clear();
        }

        public ulong GetHostMemoryUsageKB()
        {
            ulong result = 0;
            foreach (var s in Voxels.Values)
            {
                result += (ulong)(s.MemoryUsage / 1024);
            }

            return result;
        }

        public ulong GetGPUMemoryUsageKB()
        {
            ulong result = 0;
            foreach (var s in Voxels.Values)
            {
                result += (ulong)(s.VRAMUsage / 1024);
            }

            return result;
        }
    }
}
