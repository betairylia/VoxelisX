using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Voxelis.Utils;

namespace Voxelis.Rendering.Meshing
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

        // Chunk management
        private readonly int3 chunksPerAxis;
        private readonly int totalChunks;
        private readonly GameObject[] chunkObjects;
        private readonly MeshFilter[] meshFilters;
        private readonly MeshRenderer[] meshRenderers;
        private readonly Mesh[] meshes;

        // Job tracking
        private readonly List<ChunkMeshData> meshDataList = new List<ChunkMeshData>();
        private readonly List<JobHandle> jobHandles = new List<JobHandle>();
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
                        renderer.material = material;
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

            // Check sector update records to mark dirty chunks
            for (int i = 0; i < sector.updateRecord.Length; i++)
            {
                short brickIdxAbs = sector.updateRecord[i];
                int3 brickPos = Sector.ToBrickPos(brickIdxAbs);

                // Determine which chunk(s) this brick affects
                int3 chunkIdx = (brickPos * Sector.SIZE_IN_BLOCKS) / chunkSize;

                if (IsValidChunkIndex(chunkIdx))
                {
                    dirtyChunks.Add(GetChunkIndex(chunkIdx.x, chunkIdx.y, chunkIdx.z));
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
                jobHandles.Add(handle);
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
            if (jobHandles.Count == 0)
                return;

            // Complete all jobs
            foreach (var handle in jobHandles)
            {
                handle.Complete();
            }

            // Apply meshes
            for (int i = 0; i < jobHandles.Count; i++)
            {
                int chunkIdx = chunkIndices[i];
                ChunkMeshData meshData = meshDataList[i];

                ApplyMeshData(chunkIdx, meshData);

                // Dispose mesh data
                meshData.Dispose();
            }

            // Clear tracking
            jobHandles.Clear();
            meshDataList.Clear();
            chunkIndices.Clear();
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
            var colors = new Color[meshData.vertices.Length];
            var uvs = new Vector2[meshData.vertices.Length];

            for (int i = 0; i < meshData.vertices.Length; i++)
            {
                VoxelVertex v = meshData.vertices[i];
                vertices[i] = v.position;
                normals[i] = v.normal;
                colors[i] = new Color(v.color.x, v.color.y, v.color.z, v.color.w);
                uvs[i] = v.uv;
            }

            var indices = meshData.indices.AsArray().ToArray();

            // Assign to mesh
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors = colors;
            mesh.uv = uvs;
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
            if (jobHandles.Count > 0)
            {
                foreach (var handle in jobHandles)
                {
                    handle.Complete();
                }
                foreach (var meshData in meshDataList)
                {
                    meshData.Dispose();
                }
                jobHandles.Clear();
                meshDataList.Clear();
            }

            // Destroy meshes
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] != null)
                {
                    UnityEngine.Object.Destroy(meshes[i]);
                }
            }

            // Destroy GameObjects
            if (SectorObject != null)
            {
                UnityEngine.Object.Destroy(SectorObject);
            }
        }
    }
}
