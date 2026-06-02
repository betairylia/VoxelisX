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
        private NativeHashMap<int3, VoxelEntityPhysics.SectorMassMoments> sectorMassCache;
        private VoxelEntityPhysics.SectorMassMoments cachedMassMoments;
        private bool massCacheInitialized;

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
        /// Uses cached per-sector origin moments and only refreshes geometry-dirty sectors after the initial build.
        /// </summary>
        public MassProperties ComputeMassProperties()
        {
            RefreshMassPropertiesCache();
            return massProperties;
        }

        public MassProperties RebuildMassPropertiesCache()
        {
            RefreshMassPropertiesCache(forceRebuild: true);
            return massProperties;
        }

        public bool RefreshMassPropertiesCache(
            DirtyFlags dirtyMask = DirtyFlags.Geometry,
            bool forceRebuild = false)
        {
            if (isStatic)
            {
                ClearMassPropertiesCache();
                massProperties = default;
                return false;
            }

            int sectorCount = entity.Sectors.Count;
            if (sectorCount == 0)
            {
                ClearMassPropertiesCache();
                massProperties = default;
                return true;
            }

            bool resetCache = forceRebuild || !massCacheInitialized || !sectorMassCache.IsCreated;
            EnsureMassPropertiesCache(sectorCount, resetCache);

            bool changed = RemoveMissingSectorMoments();

            var inputs = new NativeList<VoxelEntityPhysics.SectorMassMomentInput>(Allocator.TempJob);
            try
            {
                foreach (var kvp in entity.Sectors)
                {
                    ref Sector sector = ref kvp.Value.Get();
                    bool cached = sectorMassCache.ContainsKey(kvp.Key);
                    if (cached && (sector.sectorDirtyFlags & (ushort)dirtyMask) == 0)
                    {
                        continue;
                    }

                    sector.UpdateNonEmptyBricks();
                    inputs.Add(new VoxelEntityPhysics.SectorMassMomentInput
                    {
                        SectorPosition = kvp.Key,
                        SectorBlockPosition = VoxelEntity.GetSectorBlockPos(kvp.Key),
                        Sector = kvp.Value
                    });
                }

                if (inputs.Length == 0)
                {
                    if (changed)
                    {
                        ApplyCachedMassProperties();
                    }
                    return changed;
                }

                using var results = new NativeArray<VoxelEntityPhysics.SectorMassMomentResult>(inputs.Length, Allocator.TempJob);
                var job = new VoxelEntityPhysics.ComputeSectorMassMomentsJob
                {
                    settings = PhysicsSettings.Settings,
                    inputs = inputs.AsArray(),
                    results = results
                };
                job.Schedule(inputs.Length, 1).Complete();

                for (int i = 0; i < results.Length; i++)
                {
                    VoxelEntityPhysics.SectorMassMomentResult result = results[i];
                    VoxelEntityPhysics.SectorMassMoments oldMoments = default;
                    bool hadCachedSector = sectorMassCache.TryGetValue(result.SectorPosition, out oldMoments);

                    if (hadCachedSector)
                    {
                        sectorMassCache[result.SectorPosition] = result.Moments;
                    }
                    else
                    {
                        sectorMassCache.Add(result.SectorPosition, result.Moments);
                    }

                    cachedMassMoments += result.Moments - oldMoments;
                    changed = true;
                }

                ApplyCachedMassProperties();
                return changed;
            }
            finally
            {
                if (inputs.IsCreated)
                {
                    inputs.Dispose();
                }
            }
        }

        private void EnsureMassPropertiesCache(int sectorCount, bool rebuild)
        {
            if (rebuild)
            {
                ClearMassPropertiesCache();
            }

            if (!sectorMassCache.IsCreated)
            {
                sectorMassCache = new NativeHashMap<int3, VoxelEntityPhysics.SectorMassMoments>(math.max(1, sectorCount), Allocator.Persistent);
            }
            else if (sectorMassCache.Capacity < sectorCount)
            {
                sectorMassCache.Capacity = sectorCount;
            }

            massCacheInitialized = true;
        }

        private bool RemoveMissingSectorMoments()
        {
            if (!sectorMassCache.IsCreated || sectorMassCache.Count == 0)
            {
                return false;
            }

            bool changed = false;
            using var cachedKeys = sectorMassCache.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < cachedKeys.Length; i++)
            {
                int3 sectorPosition = cachedKeys[i];
                if (entity.Sectors.ContainsKey(sectorPosition))
                {
                    continue;
                }

                cachedMassMoments -= sectorMassCache[sectorPosition];
                sectorMassCache.Remove(sectorPosition);
                changed = true;
            }

            return changed;
        }

        private void ClearMassPropertiesCache()
        {
            if (sectorMassCache.IsCreated)
            {
                sectorMassCache.Dispose();
            }

            sectorMassCache = default;
            cachedMassMoments = default;
            massCacheInitialized = false;
        }

        private void ApplyCachedMassProperties()
        {
            MassProperties result = default;
            result.mass = cachedMassMoments.Mass;
            if (cachedMassMoments.Mass > 0f)
            {
                result.centerOfMass = cachedMassMoments.FirstMoment / cachedMassMoments.Mass;
                result.inertiaTensor = VoxelEntityPhysics.InertiaAroundCenterOfMass(cachedMassMoments, result.centerOfMass);
            }

            massProperties = result;
        }

        public static void ResolveContact(IEnumerable<VoxelCollisionSolver.ContactPoint> wsContactsB, VoxelEntity Ae, VoxelEntity Be)
        {
            throw new NotImplementedException();
        }

        private void OnDestroy()
        {
            ClearMassPropertiesCache();
        }

        private void OnDrawGizmos()
        {
            if (body == null) return;
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.TransformPoint(body.centerOfMass), 0.25f);
        }
    }
}
