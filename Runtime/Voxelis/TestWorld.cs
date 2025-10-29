using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Serialization;

namespace Voxelis
{
    public class TestWorld : VoxelEntity
    {
        [BurstCompile]
        struct FillWorldSectorJob : IJob
        {
            public Vector3Int sectorPos;
            public Sector sector;

            public void Execute()
            {
                for (int x = 0; x < Sector.SIZE_IN_BRICKS_X * Sector.SIZE_IN_BLOCKS_X; x++)
                {
                    for (int y = 0; y < Sector.SIZE_IN_BRICKS_Y * Sector.SIZE_IN_BLOCKS_Y; y++)
                    {
                        for (int z = 0; z < Sector.SIZE_IN_BRICKS_Z * Sector.SIZE_IN_BLOCKS_Z; z++)
                        {
                            int wx = x + sectorPos.x * 128;
                            int wy = y + sectorPos.y * 128;
                            int wz = z + sectorPos.z * 128;
                            
                            // if (wy > (math.sin(wx) + math.cos(wz)))
                            // if((wx & wy & wz) == 0 && wy < (600 + 200 * math.sin(wx / 100.0)))
                            // if(wy < (600 + 200 * math.sin(wx / 100.0) + 100 * math.cos(wz / 150.0)))
                            // if(wy < 128)
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

        public Vector3Int numSectors;

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
                        if (!Voxels.ContainsKey(secPos))
                        {
                            var sec = new SectorRef(
                                this,
                                Sector.New(Allocator.Persistent, 128),
                                secPos);
                            Voxels.Add(secPos, sec);
                        }

                        var job = new FillWorldSectorJob()
                        {
                            sectorPos = secPos,
                            sector = Voxels[secPos].sector
                        };

                        fillWorldJobs.Add(job.Schedule());
                    }
                }
            }
            
            JobHandle.CompleteAll(fillWorldJobs);
            fillWorldJobs.Dispose();
            Debug.Log("Done!");

            int totalBricks = 0;
            foreach (var sector in Voxels.Values)
            {
                totalBricks += sector.sector.NonEmptyBrickCount;
            }
            Debug.Log($"Total: {totalBricks} Bricks ({totalBricks * 2 / 1024} MiB)");
        }

        void Start()
        {
            Initialize();
        }

        [FormerlySerializedAs("Tick")] public bool CorruptionTick = false;

        [BurstCompile]
        struct TestUpdate : IJob
        {
            public Sector sector;
            public int p;
            
            public void Execute()
            {
                const int Zs = 128;
                for (int x = 0; x < 128; x++)
                {
                    for (int i = 0; i < Zs; i++)
                    {
                        int y = p % 128;
                        int z = ((p / 128) % (128 / Zs)) * Zs + i;
                        sector.SetBlock(
                            x, y, z, new Block((ushort)(sector.GetBlock(x, y, z).isEmpty ? 1 : 0)));
                    }
                }
            }
        }

        public void Update()
        {
            if (CorruptionTick)
            {
                NativeList<JobHandle> jobs = new NativeList<JobHandle>(Allocator.Temp);
                
                foreach (var sec in Voxels.Values)
                {
                    int p = Time.frameCount;

                    jobs.Add(new TestUpdate()
                    {
                        p = p,
                        sector = sec.sector
                    }.Schedule());
                }
                
                JobHandle.CompleteAll(jobs);
                jobs.Dispose();
            }
        }
    }
}