using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Caelix.Utils;

namespace Caelix.Rendering.Meshing
{
    /// <summary>
    /// Manages mesh rendering for a single sector.
    /// Subdivides the sector into chunks and creates GameObject hierarchy for rendering.
    /// </summary>
    public class SectorMeshRenderer : IDisposable
    {
        private readonly SectorHandle sectorHandle;
        private readonly int3 sectorPosition;
        private readonly int chunkSize;
        private readonly Material material;
        private readonly Transform parentTransform;

        private const DirtyFlags MeshUpdateFlags =
            DirtyFlags.BlockBrickAdded |
            DirtyFlags.BlockBrickRemoved |
            DirtyFlags.GeometryWithLocalNeighbor;

        // Chunk management
        private readonly int3 chunksPerAxis;
        private readonly int totalChunks;
        private readonly GameObject[] chunkObjects;
        private readonly MeshFilter[] meshFilters;
        private readonly MeshRenderer[] meshRenderers;
        private readonly Mesh[] meshes;

        // Job tracking
        private readonly List<ChunkMeshData> meshDataList = new List<ChunkMeshData>();
        private JobHandle jobHandle;
        private readonly List<int> chunkIndices = new List<int>();
        private readonly HashSet<int> dirtyChunks = new HashSet<int>();

        /// <summary>
        /// Parent GameObject containing all chunk renderers.
        /// </summary>
        public GameObject SectorObject { get; private set; }

        public SectorMeshRenderer(
            SectorHandle sectorHandle,
            int3 sectorPosition,
            int chunkSize,
            Material material,
            Transform worldTransform)
        {
            this.sectorHandle = sectorHandle;
            this.sectorPosition = sectorPosition;
            this.chunkSize = chunkSize;
            this.material = material;

            // Calculate chunk subdivision
            chunksPerAxis = new int3(
                Sector.SECTOR_SIZE_IN_BLOCKS / chunkSize,
                Sector.SECTOR_SIZE_IN_BLOCKS / chunkSize,
                Sector.SECTOR_SIZE_IN_BLOCKS / chunkSize
            );
            totalChunks = chunksPerAxis.x * chunksPerAxis.y * chunksPerAxis.z;

            // Create parent GameObject
            SectorObject = new GameObject($"Sector_{sectorPosition.x}_{sectorPosition.y}_{sectorPosition.z}");
            SectorObject.transform.SetParent(worldTransform, false);
            SectorObject.transform.localPosition = (sectorPosition * Sector.SECTOR_SIZE_IN_BLOCKS).ToVector3Int();

            // Allocate arrays
            chunkObjects = new GameObject[totalChunks];
            meshFilters = new MeshFilter[totalChunks];
            meshRenderers = new MeshRenderer[totalChunks];
            meshes = new Mesh[totalChunks];

            // Create chunk GameObjects
            InitializeChunks();

            // Mark all chunks as dirty for initial generation
            for (int i = 0; i < totalChunks; i++)
            {
                dirtyChunks.Add(i);
            }
        }

        /// <summary>
        /// Creates GameObject hierarchy for all chunks.
        /// </summary>
        private void InitializeChunks()
        {
            for (int z = 0; z < chunksPerAxis.z; z++)
            {
                for (int y = 0; y < chunksPerAxis.y; y++)
                {
                    for (int x = 0; x < chunksPerAxis.x; x++)
                    {
                        int chunkIdx = GetChunkIndex(x, y, z);
                        int3 chunkPos = new int3(x, y, z);

                        // Create GameObject
                        GameObject chunkObj = new GameObject($"Chunk_{x}_{y}_{z}");
                        chunkObj.transform.SetParent(SectorObject.transform, false);
                        chunkObj.transform.localPosition = (chunkPos * chunkSize).ToVector3Int();

                        // Add components
                        MeshFilter filter = chunkObj.AddComponent<MeshFilter>();
                        MeshRenderer renderer = chunkObj.AddComponent<MeshRenderer>();

                        // Create mesh
                        Mesh mesh = new Mesh();
                        mesh.name = $"ChunkMesh_{x}_{y}_{z}";
                        mesh.indexFormat = IndexFormat.UInt32; // Support large meshes

                        // Assign
                        filter.mesh = mesh;
                        renderer.sharedMaterial = material;
                        renderer.shadowCastingMode = ShadowCastingMode.On;
                        renderer.receiveShadows = true;

                        // Store references
                        chunkObjects[chunkIdx] = chunkObj;
                        meshFilters[chunkIdx] = filter;
                        meshRenderers[chunkIdx] = renderer;
                        meshes[chunkIdx] = mesh;
                    }
                }
            }
        }

        /// <summary>
        /// Schedules mesh generation jobs for dirty chunks.
        /// Called during update phase 1.
        /// </summary>
        public void ScheduleJobs()
        {
            ref Sector sector = ref sectorHandle.Get();

            // Dirty source flags have already been propagated and cleared before the renderer tick.
            // Consume the resulting target-side requireUpdate flags, just like the ray renderer.
            unsafe
            {
                for (int brickIdx = 0; brickIdx < Sector.BRICKS_IN_SECTOR; brickIdx++)
                {
                    if (RequiresRemesh(sector.brickRequireUpdateFlags[brickIdx]))
                    {
                        int3 brickPos = Sector.ToBrickPos((short)brickIdx);
                        int3 chunkIdx = (brickPos * Sector.SIZE_IN_BLOCKS) / chunkSize;

                        if (IsValidChunkIndex(chunkIdx))
                        {
                            dirtyChunks.Add(GetChunkIndex(chunkIdx.x, chunkIdx.y, chunkIdx.z));
                        }
                    }
                }
            }

            // Schedule jobs for dirty chunks
            foreach (int chunkIdx in dirtyChunks)
            {
                int3 chunkCoord = GetChunkCoord(chunkIdx);
                int3 chunkMin = chunkCoord * chunkSize;
                int3 chunkSizeVec = new int3(chunkSize, chunkSize, chunkSize);

                // Create mesh data
                var meshData = new ChunkMeshData(
                    vertexCapacity: 16384,
                    indexCapacity: 32768,
                    Allocator.TempJob
                );

                // Create job
                var job = new MeshGenerationJob
                {
                    sector = sector,
                    chunkMin = chunkMin,
                    chunkSize = chunkSizeVec,
                    vertices = meshData.vertices,
                    indices = meshData.indices
                };

                // Schedule
                JobHandle handle = job.Schedule();

                // Track
                meshDataList.Add(meshData);
                jobHandle = JobHandle.CombineDependencies(jobHandle, handle);
                chunkIndices.Add(chunkIdx);
            }

            dirtyChunks.Clear();
        }

        /// <summary>
        /// Completes jobs and applies meshes to renderers.
        /// Called during update phase 2.
        /// </summary>
        public void CompleteJobs()
        {
            if (meshDataList.Count == 0)
                return;

            jobHandle.Complete();
            ApplyCompletedJobs();
        }

        internal bool TryGetScheduledJobHandle(out JobHandle handle)
        {
            handle = jobHandle;
            return meshDataList.Count > 0;
        }

        /// <summary>
        /// Applies mesh data after the renderer coordinator has completed the combined job handle.
        /// </summary>
        internal void ApplyCompletedJobs()
        {
            if (meshDataList.Count == 0)
                return;

            // Apply meshes
            for (int i = 0; i < meshDataList.Count; i++)
            {
                int chunkIdx = chunkIndices[i];
                ChunkMeshData meshData = meshDataList[i];

                ApplyMeshData(chunkIdx, meshData);

                // Dispose mesh data
                meshData.Dispose();
            }

            // Clear tracking
            jobHandle = default;
            meshDataList.Clear();
            chunkIndices.Clear();

            // Clear sector state
            ref Sector sector = ref sectorHandle.Get();
            sector.ReorderBricks();
        }

        /// <summary>
        /// Applies mesh data to a chunk's MeshFilter.
        /// </summary>
        private void ApplyMeshData(int chunkIdx, ChunkMeshData meshData)
        {
            Mesh mesh = meshes[chunkIdx];
            mesh.Clear();

            if (meshData.IsEmpty)
            {
                // Hide empty chunks
                chunkObjects[chunkIdx].SetActive(false);
                return;
            }

            // Show chunk
            chunkObjects[chunkIdx].SetActive(true);

            // Convert NativeList to arrays
            var vertices = new Vector3[meshData.vertices.Length];
            var normals = new Vector3[meshData.vertices.Length];
            var blockIds = new Vector2[meshData.vertices.Length];

            for (int i = 0; i < meshData.vertices.Length; i++)
            {
                VoxelVertex v = meshData.vertices[i];
                vertices[i] = v.position;
                normals[i] = v.normal;
                blockIds[i] = new Vector2(v.blockID, 0f);
            }

            var indices = meshData.indices.AsArray().ToArray();

            // Assign to mesh
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = blockIds;
            mesh.triangles = indices;

            // Calculate bounds
            mesh.RecalculateBounds();

            // Optimize
            mesh.Optimize();
        }

        /// <summary>
        /// Forces regeneration of all chunks.
        /// </summary>
        public void MarkAllDirty()
        {
            for (int i = 0; i < totalChunks; i++)
            {
                dirtyChunks.Add(i);
            }
        }

        internal static bool RequiresRemesh(ushort requireUpdateFlags)
        {
            return (requireUpdateFlags & (ushort)MeshUpdateFlags) != 0;
        }

        /// <summary>
        /// Converts chunk coordinates to flat index.
        /// </summary>
        private int GetChunkIndex(int x, int y, int z)
        {
            return x + y * chunksPerAxis.x + z * chunksPerAxis.x * chunksPerAxis.y;
        }

        /// <summary>
        /// Converts flat index to chunk coordinates.
        /// </summary>
        private int3 GetChunkCoord(int index)
        {
            int z = index / (chunksPerAxis.x * chunksPerAxis.y);
            int rem = index % (chunksPerAxis.x * chunksPerAxis.y);
            int y = rem / chunksPerAxis.x;
            int x = rem % chunksPerAxis.x;
            return new int3(x, y, z);
        }

        /// <summary>
        /// Checks if chunk index is valid.
        /// </summary>
        private bool IsValidChunkIndex(int3 chunkIdx)
        {
            return chunkIdx.x >= 0 && chunkIdx.x < chunksPerAxis.x &&
                   chunkIdx.y >= 0 && chunkIdx.y < chunksPerAxis.y &&
                   chunkIdx.z >= 0 && chunkIdx.z < chunksPerAxis.z;
        }

        /// <summary>
        /// Cleanup resources.
        /// </summary>
        public void Dispose()
        {
            // Complete any pending jobs
            if (meshDataList.Count > 0)
            {
                jobHandle.Complete();
                foreach (var meshData in meshDataList)
                {
                    meshData.Dispose();
                }
                jobHandle = default;
                meshDataList.Clear();
            }

            // Destroy meshes
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] != null)
                {
                    DestroyObject(meshes[i]);
                }
            }

            // Destroy GameObjects
            if (SectorObject != null)
            {
                DestroyObject(SectorObject);
                SectorObject = null;
            }
        }

        private static void DestroyObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
