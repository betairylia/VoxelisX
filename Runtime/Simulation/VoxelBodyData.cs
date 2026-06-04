using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Simulation;

namespace Voxelis
{
    public struct VoxelBodyData : IDisposable
    {
        public struct MassProperties
        {
            public float mass;
            public float3 centerOfMass;
            public float3 inertiaTensor;
        }

        private Allocator allocator;
        private UnsafeHashMap<int3, VoxelEntityPhysics.SectorMassMoments> sectorMassCache;
        private VoxelEntityPhysics.SectorMassMoments cachedMassMoments;
        private bool massCacheInitialized;

        public bool isStatic;
        public BlobAssetReference<Collider> collider;
        public MassProperties massProperties { get; private set; }

        public VoxelBodyData(Allocator allocator)
        {
            this.allocator = allocator;
            sectorMassCache = default;
            cachedMassMoments = default;
            massCacheInitialized = false;
            isStatic = false;
            collider = default;
            massProperties = default;
        }

        public MassProperties ComputeMassProperties(LockableUnsafeHashMap<int3, SectorHandle> sectors)
        {
            RefreshMassPropertiesCache(sectors);
            return massProperties;
        }

        private void RefreshMassPropertiesCache(
            LockableUnsafeHashMap<int3, SectorHandle> sectors,
            DirtyFlags dirtyMask = DirtyFlags.Geometry)
        {
            if (isStatic)
            {
                ClearMassPropertiesCache();
                massProperties = default;
                return;
            }

            int sectorCount = sectors.Count;
            if (sectorCount == 0)
            {
                ClearMassPropertiesCache();
                massProperties = default;
                return;
            }

            bool resetCache = !massCacheInitialized || !sectorMassCache.IsCreated;
            EnsureMassPropertiesCache(sectorCount, resetCache);

            bool changed = RemoveMissingSectorMoments(sectors);

            var inputs = new NativeList<VoxelEntityPhysics.SectorMassMomentInput>(Allocator.TempJob);
            try
            {
                foreach (var kvp in sectors)
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
                    return;
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
                }

                ApplyCachedMassProperties();
            }
            finally
            {
                if (inputs.IsCreated)
                {
                    inputs.Dispose();
                }
            }
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

        public void Dispose()
        {
            ClearMassPropertiesCache();

            if (collider.IsCreated)
            {
                unsafe
                {
                    if (collider.Value.Type == ColliderType.Voxel)
                    {
                        var vc = (VoxelCollider*)collider.GetUnsafePtr();
                        vc->Dispose();
                    }
                }
                collider.Dispose();
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
                sectorMassCache = new UnsafeHashMap<int3, VoxelEntityPhysics.SectorMassMoments>(
                    math.max(1, sectorCount),
                    allocator == Allocator.Invalid ? Allocator.Persistent : allocator);
            }
            else if (sectorMassCache.Capacity < sectorCount)
            {
                sectorMassCache.Capacity = sectorCount;
            }

            massCacheInitialized = true;
        }

        private bool RemoveMissingSectorMoments(LockableUnsafeHashMap<int3, SectorHandle> sectors)
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
                if (sectors.ContainsKey(sectorPosition))
                {
                    continue;
                }

                cachedMassMoments -= sectorMassCache[sectorPosition];
                sectorMassCache.Remove(sectorPosition);
                changed = true;
            }

            return changed;
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
    }
}
