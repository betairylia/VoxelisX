using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.NotBurstCompatible;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Voxelis.Simulation;

namespace Voxelis
{
    public partial class VoxelEntity : MonoBehaviour, IDisposable
    {
        public Dictionary<Vector3Int, SectorRef> Voxels = new Dictionary<Vector3Int, SectorRef>();
        public Queue<SectorRef> sectorsToRemove = new Queue<SectorRef>();

        [FormerlySerializedAs("collisionEnabled")] public bool physicsEnabled = false;
        private Rigidbody body;
        private UnityPhysicsCollider debugBody;

        private void OnEnable()
        {
            FindFirstObjectByType<VoxelisXRenderer>()?.AddEntity(this);
            InitializeBody();
        }

        private void OnDisable()
        {
            FindFirstObjectByType<VoxelisXRenderer>()?.RemoveEntity(this);
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

        public Block GetBlock(Vector3Int pos)
        {
            Vector3Int sectorPos = new Vector3Int(
                pos.x >> (Sector.SHIFT_IN_BLOCKS_X + Sector.SHIFT_IN_BRICKS_X),
                pos.y >> (Sector.SHIFT_IN_BLOCKS_Y + Sector.SHIFT_IN_BRICKS_Y),
                pos.z >> (Sector.SHIFT_IN_BLOCKS_Z + Sector.SHIFT_IN_BRICKS_Z));

            var found = Voxels.TryGetValue(sectorPos, out SectorRef sector);
            if (!found)
            {
                return Block.Empty;
            }

            return sector.sector.GetBlock(
                pos.x & (Sector.BRICK_MASK_X | (Sector.SECTOR_MASK_X << Sector.SHIFT_IN_BLOCKS_X)),
                pos.y & (Sector.BRICK_MASK_Y | (Sector.SECTOR_MASK_Y << Sector.SHIFT_IN_BLOCKS_Y)),
                pos.z & (Sector.BRICK_MASK_Z | (Sector.SECTOR_MASK_Z << Sector.SHIFT_IN_BLOCKS_Z))
            );
        }

        public void SetBlock(Vector3Int pos, Block b)
        {
            Vector3Int sectorPos = new Vector3Int(
                pos.x >> (Sector.SHIFT_IN_BLOCKS_X + Sector.SHIFT_IN_BRICKS_X),
                pos.y >> (Sector.SHIFT_IN_BLOCKS_Y + Sector.SHIFT_IN_BRICKS_Y),
                pos.z >> (Sector.SHIFT_IN_BLOCKS_Z + Sector.SHIFT_IN_BRICKS_Z));

            var found = Voxels.TryGetValue(sectorPos, out SectorRef sector);
            if (!found)
            {
                sector = new SectorRef(
                    this,
                    Sector.New(Allocator.Persistent, 1),
                    sectorPos);
                Voxels.Add(sectorPos, sector);
            }

            sector.sector.SetBlock(
                pos.x & (Sector.BRICK_MASK_X | (Sector.SECTOR_MASK_X << Sector.SHIFT_IN_BLOCKS_X)),
                pos.y & (Sector.BRICK_MASK_Y | (Sector.SECTOR_MASK_Y << Sector.SHIFT_IN_BLOCKS_Y)),
                pos.z & (Sector.BRICK_MASK_Z | (Sector.SECTOR_MASK_Z << Sector.SHIFT_IN_BLOCKS_Z)),
                b
            );
            
            // TODO: TEST ONLY: Remove me
            UpdateBody();
        }

        public float4x4 ObjectToWorld()
        {
            return transform.localToWorldMatrix;
        }
        
        public float4x4 WorldToObject()
        {
            return transform.worldToLocalMatrix;
        }

        // The hell this is mess
        public void TestCollision(
            VoxelEntity other, 
            List<VoxelCollisionSolver.ContactPoint> contactBuf,
            NativeList<VoxelCollisionSolver.ContactPoint> nativeContactBuf)
        {
            if (!physicsEnabled || !other.physicsEnabled)
            {
                return;
            }

            // alias
            var otherContacts = contactBuf;
            otherContacts.Clear();
            
            // Temp array for results
            var resultBuf = nativeContactBuf;
            int totalContacts = 0;
            
            foreach(SectorRef sector in Voxels.Values)
            {
                Vector3 f3thisSectorPos = sector.sectorBlockPos;
                float4x4 mySectorToWorld =
                    math.mul(ObjectToWorld(), float4x4.Translate(f3thisSectorPos));
                
                foreach (SectorRef otherSector in other.Voxels.Values)
                {
                    Vector3 f3otherSectorPos = otherSector.sectorBlockPos;
                    float4x4 otherSectorToWorld =
                        math.mul(other.ObjectToWorld(), float4x4.Translate(f3otherSectorPos));
                    
                    // Pick the smaller sector as src
                    int srcSize = sector.sector.NonEmptyBrickCount;
                    int dstSize = otherSector.sector.NonEmptyBrickCount;
                    
                    resultBuf.Clear();

                    // this => other
                    if (srcSize <= dstSize)
                    {
                        var sectorJob = new VoxelCollisionSolver.SectorJob
                        {
                            srcToDst = math.mul(math.fastinverse(otherSectorToWorld), mySectorToWorld),
                            src = sector.sector,
                            dst = otherSector.sector,
                            dstSpaceResults = resultBuf
                        };

                        sectorJob.Schedule().Complete();

                        var wsContacts = resultBuf.ToArrayNBC()
                            .Select(x => x
                                .TranslateVia(otherSectorToWorld)
                                .ApplySectorPos(sector.sectorBlockPos, otherSector.sectorBlockPos)).ToList();
                        otherContacts.AddRange(wsContacts);
                        // thisContacts.AddRange(wsContacts.Select(x => x.Invert()));
                    }
                    // other => this
                    else
                    {
                        var sectorJob = new VoxelCollisionSolver.SectorJob
                        {
                            srcToDst = math.mul(math.fastinverse(mySectorToWorld), otherSectorToWorld),
                            dst = sector.sector,
                            src = otherSector.sector,
                            dstSpaceResults = resultBuf
                        };

                        sectorJob.Schedule().Complete();
                        
                        var wsContacts = resultBuf.ToArrayNBC()
                            .Select(x => x
                                .TranslateVia(mySectorToWorld)
                                .ApplySectorPos(otherSector.sectorBlockPos, sector.sectorBlockPos)).ToList();
                        // thisContacts.AddRange(wsContacts);
                        otherContacts.AddRange(wsContacts.Select(x => x.Invert().Flip()));
                    }
                }
            }

            totalContacts = otherContacts.Count;
            Debug.Log($"Total Contacts: {totalContacts}");

            foreach (var cp in otherContacts)
            {
                Debug.DrawRay(cp.position, cp.normal, Color.red);
            }
            
            VoxelEntity.ResolveContact(otherContacts, this, other);
        }
    }
}
