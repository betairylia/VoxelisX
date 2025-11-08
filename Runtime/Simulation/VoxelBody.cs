using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Voxelis.Simulation;

namespace Voxelis
{
    [RequireComponent(typeof(VoxelEntity))]
    public class VoxelBody : MonoBehaviour//, IDisposable
    {
        /// <summary>
        /// Indicates whether physics simulation is enabled for this voxel entity.
        /// When enabled, the entity will participate in collision detection and response.
        /// </summary>
        [FormerlySerializedAs("collisionEnabled")] public bool physicsEnabled = false;
        private Rigidbody body;
        private UnityPhysicsCollider debugBody;

        private VoxelEntity entity;

        private void Awake()
        {
            InitializeBody();
            entity = GetComponent<VoxelEntity>();
        }

        public void InitializeBody()
        {
            if (!physicsEnabled)
            {
                return;
            }

            body = gameObject.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
            }
            debugBody = gameObject.AddComponent<UnityPhysicsCollider>();
            
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
        }
        
        /// <summary>
        /// Tests collision between this voxel entity and another voxel entity.
        /// Calculates contact points and resolves penetrations between the two entities.
        /// </summary>
        /// <param name="other">The other voxel entity to test collision against.</param>
        /// <param name="contactBuf">List buffer to store contact points (managed).</param>
        /// <param name="nativeContactBuf">Native list buffer for intermediate contact point calculations.</param>
        /// <remarks>
        /// This method performs sector-by-sector collision detection between both entities.
        /// It chooses the smaller sector as the source for optimization and accumulates all contact points.
        /// The collision resolution is performed via VoxelEntity.ResolveContact.
        /// </remarks>
        public void TestCollision(
            VoxelBody other, 
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
            
            foreach(SectorRef sector in entity.Voxels.Values)
            {
                Vector3 f3thisSectorPos = sector.sectorBlockPos;
                float4x4 mySectorToWorld =
                    math.mul(entity.ObjectToWorld(), float4x4.Translate(f3thisSectorPos));
                
                foreach (SectorRef otherSector in other.entity.Voxels.Values)
                {
                    Vector3 f3otherSectorPos = otherSector.sectorBlockPos;
                    float4x4 otherSectorToWorld =
                        math.mul(other.entity.ObjectToWorld(), float4x4.Translate(f3otherSectorPos));
                    
                    // Pick the smaller sector as src
                    int srcSize = sector.sector.NonEmptyBrickCount;
                    int dstSize = otherSector.sector.NonEmptyBrickCount;
                    
                    resultBuf.Clear();

                    var sectorJob = new VoxelCollisionSolver.SectorJob
                    {
                        srcToDst = math.mul(math.fastinverse(otherSectorToWorld), mySectorToWorld),
                        src = sector.sector,
                        dst = otherSector.sector,
                        dstSpaceResults = resultBuf
                    };

                    sectorJob.Schedule().Complete();

                    // var wsContacts = resultBuf.ToArrayNBC()
                    //     .Select(x => x
                    //         .TranslateVia(otherSectorToWorld)
                    //         .ApplySectorPos(sector.sectorBlockPos, otherSector.sectorBlockPos)).ToList();
                    // otherContacts.AddRange(wsContacts);
                }
            }

            totalContacts = otherContacts.Count;
            Debug.Log($"Total Contacts: {totalContacts}");

            foreach (var cp in otherContacts)
            {
                Debug.DrawRay(cp.position, cp.normal, Color.red);
            }
        }

        // TODO: Incremental update with parallel axis theorem for inertia
        public void UpdateBody()
        {
            if (body == null) return;
            
            // Center of Mass
            // Temp array for results
            var CoM = new NativeArray<float4>(1, Allocator.TempJob);
            try
            {
                CoM[0] = new float4(0, 0, 0, 0);

                foreach (SectorRef sector in entity.Voxels.Values)
                {
                    var sectorBPos = sector.sectorBlockPos;

                    var sectorJob = new VoxelEntityPhysics.AccumulateSectorCenterOfMass
                    {
                        settings = PhysicsSettings.Settings,
                        sector = sector.sector,
                        sectorPosition = new int3(sectorBPos.x, sectorBPos.y, sectorBPos.z),

                        accumulatedCenter = CoM
                    };

                    sectorJob.Schedule().Complete();
                }

                body.mass = CoM[0].w;
                if (CoM[0].w > 0)
                {
                    body.centerOfMass = CoM[0].xyz / CoM[0].w;
                }
                else
                {
                    body.centerOfMass = Vector3.zero;
                }
            }
            finally
            {
                CoM.Dispose();
            }

            // Inertia
            var Inertia = new NativeArray<float3>(1, Allocator.TempJob);
            try
            {
                Inertia[0] = new float3(0, 0, 0);
                foreach (SectorRef sector in entity.Voxels.Values)
                {
                    var sectorBPos = sector.sectorBlockPos;

                    var sectorJob = new VoxelEntityPhysics.AccumulateSectorInertia
                    {
                        settings = PhysicsSettings.Settings,
                        sector = sector.sector,
                        sectorPosition = new int3(sectorBPos.x, sectorBPos.y, sectorBPos.z),

                        centerOfMass = body.centerOfMass,
                        accumulatedInertia = Inertia,
                    };

                    sectorJob.Schedule().Complete();
                }

                body.inertiaTensor = Inertia[0];
                body.inertiaTensorRotation = Quaternion.identity;
            }
            finally
            {
                Inertia.Dispose();
            }
        }

        public static void ResolveContact(IEnumerable<VoxelCollisionSolver.ContactPoint> wsContactsB, VoxelEntity Ae, VoxelEntity Be)
        {
            throw new NotImplementedException();
        }

        private void OnDrawGizmos()
        {
            if (body == null) return;
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.TransformPoint(body.centerOfMass), 0.25f);
        }
    }
}