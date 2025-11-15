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

        public bool isStatic = false;
        
        private Rigidbody body;
        private VoxelEntity _entity;

        public VoxelEntity entity
        {
            get
            {
                if (_entity == null)
                {
                    _entity = GetComponent<VoxelEntity>();
                }

                return _entity;
            }
        }

        private void Awake()
        {
            InitializeBody();
            _entity = GetComponent<VoxelEntity>();
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

            foreach(var kvp in entity.Sectors)
            {
                int3 sectorPos = kvp.Key;
                Sector sector = kvp.Value.Get();
                float3 f3thisSectorPos = VoxelEntity.GetSectorBlockPos(sectorPos);
                float4x4 mySectorToWorld =
                    math.mul(entity.ObjectToWorld(), float4x4.Translate(f3thisSectorPos));

                foreach (var otherKvp in other.entity.Sectors)
                {
                    int3 otherSectorPos = otherKvp.Key;
                    Sector otherSector = otherKvp.Value.Get();
                    float3 f3otherSectorPos = VoxelEntity.GetSectorBlockPos(otherSectorPos);
                    float4x4 otherSectorToWorld =
                        math.mul(other.entity.ObjectToWorld(), float4x4.Translate(f3otherSectorPos));

                    // Pick the smaller sector as src
                    int srcSize = sector.NonEmptyBrickCount;
                    int dstSize = otherSector.NonEmptyBrickCount;

                    resultBuf.Clear();

                    var sectorJob = new VoxelCollisionSolver.SectorJob
                    {
                        srcToDst = math.mul(math.fastinverse(otherSectorToWorld), mySectorToWorld),
                        src = sector,
                        dst = otherSector,
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

        public struct MassProperties
        {
            public float mass;
            public float3 centerOfMass;
            public float3 inertiaTensor;
        }

        public MassProperties massProperties { get; private set; }

        /// <summary>
        /// Computes mass properties (mass, center of mass, inertia tensor) for this voxel body.
        /// Uses the job system to accumulate properties across all sectors.
        /// </summary>
        public MassProperties ComputeMassProperties()
        {
            MassProperties result = new MassProperties();

            // Compute center of mass
            var CoM = new NativeArray<float4>(1, Allocator.TempJob);
            try
            {
                CoM[0] = new float4(0, 0, 0, 0);

                foreach (var kvp in entity.Sectors)
                {
                    int3 sectorPos = kvp.Key;
                    var sectorBPos = VoxelEntity.GetSectorBlockPos(sectorPos);

                    var sectorJob = new VoxelEntityPhysics.AccumulateSectorCenterOfMass
                    {
                        settings = PhysicsSettings.Settings,
                        sector = kvp.Value.Get(),
                        sectorPosition = sectorBPos,
                        accumulatedCenter = CoM
                    };

                    sectorJob.Schedule().Complete();
                }

                result.mass = CoM[0].w;
                if (CoM[0].w > 0)
                {
                    result.centerOfMass = CoM[0].xyz / CoM[0].w;
                }
                else
                {
                    result.centerOfMass = float3.zero;
                }
            }
            finally
            {
                CoM.Dispose();
            }

            // Compute inertia tensor
            var Inertia = new NativeArray<float3>(1, Allocator.TempJob);
            try
            {
                Inertia[0] = new float3(0, 0, 0);

                foreach (var kvp in entity.Sectors)
                {
                    int3 sectorPos = kvp.Key;
                    var sectorBPos = VoxelEntity.GetSectorBlockPos(sectorPos);

                    var sectorJob = new VoxelEntityPhysics.AccumulateSectorInertia
                    {
                        settings = PhysicsSettings.Settings,
                        sector = kvp.Value.Get(),
                        sectorPosition = sectorBPos,
                        centerOfMass = result.centerOfMass,
                        accumulatedInertia = Inertia,
                    };

                    sectorJob.Schedule().Complete();
                }

                result.inertiaTensor = Inertia[0];
            }
            finally
            {
                Inertia.Dispose();
            }
            
            // TODO: REMOVE ME
            massProperties = result;
            
            return result;
        }

        public unsafe void BeforePhysicsTick()
        {
            foreach (var kvp in entity.Sectors)
            {
                kvp.Value.Ptr->UpdateNonEmptyBricks();
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