using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Serialization;

namespace Voxelis
{
    /// <summary>
    /// Test implementation of VoxelEntity that generates procedural voxel terrain using noise functions.
    /// Includes a corruption test mode for testing dynamic voxel updates.
    /// </summary>
    public class TestWorld : VoxelEntity
    {
        /// <summary>
        /// Burst-compiled job that fills a sector with procedurally generated voxel data.
        /// Uses layered Simplex noise to create organic terrain shapes.
        /// </summary>
        [BurstCompile]
        struct FillWorldSectorJob : IJob
        {
            /// <summary>
            /// Position of the sector being generated in sector coordinates.
            /// </summary>
            public Vector3Int sectorPos;

            /// <summary>
            /// The sector to fill with generated voxel data.
            /// </summary>
            public SectorHandle sector;

            /// <summary>
            /// Executes the job, filling the sector with noise-based voxel data.
            /// </summary>
            /// <remarks>
            /// Uses two octaves of Simplex noise at different scales to create terrain.
            /// Blocks are colored based on their height (Y position) with a gradient effect.
            /// </remarks>
            public void Execute()
            {
                for (int x = 0; x < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BLOCKS; x++)
                {
                    for (int y = 0; y < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BLOCKS; y++)
                    {
                        for (int z = 0; z < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BLOCKS; z++)
                        {
                            int wx = x + sectorPos.x * Sector.SECTOR_SIZE_IN_BLOCKS;
                            int wy = y + sectorPos.y * Sector.SECTOR_SIZE_IN_BLOCKS;
                            int wz = z + sectorPos.z * Sector.SECTOR_SIZE_IN_BLOCKS;

                            // if (wy > (math.sin(wx) + math.cos(wz)))
                            // if((wx & wy & wz) == 0 && wy < (600 + 200 * math.sin(wx / 100.0)))
                            // if(wy < (600 + 200 * math.sin(wx / 100.0) + 100 * math.cos(wz / 150.0)))
                            // if(wy < Sector.SECTOR_SIZE_IN_BLOCKS)
                            // {
                            //     sector.SetBlock(x, y, z, new Block(1));
                            // }
                            // else
                            // {
                            //     sector.SetBlock(x, y, z, Block.Empty);
                            // }

                            // var n = Unity.Mathematics.noise.snoise(
                            //     new float3(wx / 32.0f, wy / 32.0f, wz / 32.0f));
                            var n = Unity.Mathematics.noise.snoise(
                                new float3(wx / 72.0f, wy / 72.0f, wz / 72.0f)) +
                                    noise.snoise(
                                        new float3(wx / 24.0f, wy / 24.0f, wz / 24.0f)) * 0.5f;
                            // float n = (float)(600 + 200 * math.sin(wx / 100.0) + 100 * math.cos(wz / 150.0)) - wy;
                            if (n > 0)
                            {
                                // sector.SetBlock(x, y, z, new Block(1));
                                // sector.SetBlock(x, y, z, new Block(n, wy / 512.0f, 0.5f, ((wx & wy & wz) == 0) ? 1.0f : 0.0f));
                                sector.SetBlock(x, y, z, new Block(n, wy / 512.0f, 0.5f, 0f));
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Number of sectors to generate in each dimension.
        /// </summary>
        public Vector3Int numSectors;

        /// <summary>
        /// Initializes the test world by generating all sectors with procedural terrain.
        /// Can be called from the context menu in the Unity Editor.
        /// </summary>
        /// <remarks>
        /// Generates sectors in parallel using Unity Jobs for performance.
        /// Logs total brick count and memory usage when complete.
        /// </remarks>
        [ContextMenu("Initialize")]
        public void Initialize()
        {
            Dispose();
            
            NativeList<JobHandle> fillWorldJobs = new NativeList<JobHandle>(Allocator.Temp);
            
            for (int i = 0; i < numSectors.x; i++)
            {
                for (int j = 0; j < numSectors.z; j++)
                {
                    for (int k = 0; k < numSectors.y; k++)
                    {
                        var secPos = new Vector3Int(i, k, j);
                        if (!Sectors.ContainsKey(secPos))
                        {
                            AddEmptySectorAt(secPos);
                        }

                        var job = new FillWorldSectorJob()
                        {
                            sectorPos = secPos,
                            sector = Sectors[secPos]
                        };

                        fillWorldJobs.Add(job.Schedule());
                    }
                }
            }
            
            JobHandle.CompleteAll(fillWorldJobs);
            fillWorldJobs.Dispose();
            Debug.Log("Done!");

            int totalBricks = 0;
            foreach (var kvp in Sectors)
            {
                totalBricks += kvp.Value.NonEmptyBrickCount;
            }
            Debug.Log($"Total: {totalBricks} Bricks ({totalBricks * 2 / 1024} MiB)");
        }

        /// <summary>
        /// Initializes the test world on scene start.
        /// </summary>
        void Start()
        {
            Initialize();
        }

        /// <summary>
        /// When enabled, continuously modifies voxels every frame to test dynamic updates.
        /// </summary>
        [FormerlySerializedAs("Tick")] public bool CorruptionTick = false;

        /// <summary>
        /// Burst-compiled job that toggles blocks in a sector for testing dynamic voxel updates.
        /// </summary>
        [BurstCompile]
        struct TestUpdate : IJob
        {
            /// <summary>
            /// The sector to modify.
            /// </summary>
            public SectorHandle sector;

            /// <summary>
            /// Frame counter used to determine which blocks to toggle.
            /// </summary>
            public int p;

            /// <summary>
            /// Toggles a horizontal slice of blocks in the sector.
            /// </summary>
            public void Execute()
            {
                const int Zs = Sector.SECTOR_SIZE_IN_BLOCKS;
                for (int x = 0; x < Sector.SECTOR_SIZE_IN_BLOCKS; x++)
                {
                    for (int i = 0; i < Zs; i++)
                    {
                        int y = p % Sector.SECTOR_SIZE_IN_BLOCKS;
                        int z = ((p / Sector.SECTOR_SIZE_IN_BLOCKS) % (Sector.SECTOR_SIZE_IN_BLOCKS / Zs)) * Zs + i;
                        sector.SetBlock(
                            x, y, z, new Block((ushort)(sector.GetBlock(x, y, z).isEmpty ? 1 : 0)));
                    }
                }
            }
        }

        /// <summary>
        /// If CorruptionTick is enabled, modifies voxels every frame to test rendering updates.
        /// </summary>
        public void Update()
        {
            if (CorruptionTick)
            {
                NativeList<JobHandle> jobs = new NativeList<JobHandle>(Allocator.Temp);

                foreach (var kvp in Sectors)
                {
                    int p = Time.frameCount;

                    jobs.Add(new TestUpdate()
                    {
                        p = p,
                        sector = kvp.Value
                    }.Schedule());
                }

                JobHandle.CompleteAll(jobs);
                jobs.Dispose();
            }
        }
    }
}