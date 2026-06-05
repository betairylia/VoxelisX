using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxelis.Simulation
{
    public class VoxelEntityPhysics
    {
        public struct SectorMassMomentInput
        {
            public int3 SectorPosition;
            public int3 SectorBlockPosition;
            public SectorHandle Sector;
        }

        public struct SectorMassMoments
        {
            public float Mass;
            public float3 FirstMoment;
            public float3 InertiaOrigin;

            public static SectorMassMoments operator +(SectorMassMoments a, SectorMassMoments b)
            {
                return new SectorMassMoments
                {
                    Mass = a.Mass + b.Mass,
                    FirstMoment = a.FirstMoment + b.FirstMoment,
                    InertiaOrigin = a.InertiaOrigin + b.InertiaOrigin
                };
            }

            public static SectorMassMoments operator -(SectorMassMoments a, SectorMassMoments b)
            {
                return new SectorMassMoments
                {
                    Mass = a.Mass - b.Mass,
                    FirstMoment = a.FirstMoment - b.FirstMoment,
                    InertiaOrigin = a.InertiaOrigin - b.InertiaOrigin
                };
            }
        }

        public struct SectorMassMomentResult
        {
            public int3 SectorPosition;
            public SectorMassMoments Moments;
        }

        [BurstCompile]
        public struct ComputeSectorMassMomentsJob : IJobParallelFor
        {
            public PhysicsSettings settings;
            [ReadOnly] public NativeArray<SectorMassMomentInput> inputs;
            [WriteOnly] public NativeArray<SectorMassMomentResult> results;

            public void Execute(int index)
            {
                SectorMassMomentInput input = inputs[index];
                results[index] = new SectorMassMomentResult
                {
                    SectorPosition = input.SectorPosition,
                    Moments = ComputeSectorMassMoments(input.Sector.Get(), input.SectorBlockPosition, settings)
                };
            }
        }

        /// <remarks>
        /// TODO: LIMITATION: only the diagonal of the inertia tensor is accumulated
        /// (Ixx, Iyy, Izz). The products of inertia (Ixy, Ixz, Iyz) are not computed,
        /// and the downstream rigid body forces its motion (principal-axis) frame to be
        /// axis-aligned with the body (BodyFromMotion rotation = identity). This is exact
        /// only when the mass distribution's principal axes coincide with the voxel-grid
        /// axes (e.g. symmetric bodies); an asymmetric voxel body (L-shape, diagonally
        /// weighted) will rotate without the correct inertial coupling. Computing the full
        /// symmetric tensor + eigendecomposition for BodyFromMotion is future work.
        /// </remarks>
        [BurstCompile]
        public static SectorMassMoments ComputeSectorMassMoments(
            Sector sector,
            int3 sectorBlockPosition,
            PhysicsSettings settings)
        {
            SectorMassMoments result = default;

            foreach (BlockIterator blockIter in new SectorNonEmptyBlockEnumerator(sector))
            {
                float mass = settings.GetBlockMass(blockIter.block);
                if (mass <= 0f)
                {
                    continue;
                }

                float3 position = new float3(sectorBlockPosition + blockIter.position) + 0.5f;
                result.Mass += mass;
                result.FirstMoment += mass * position;
                result.InertiaOrigin += mass * new float3(
                    position.y * position.y + position.z * position.z,
                    position.x * position.x + position.z * position.z,
                    position.x * position.x + position.y * position.y);
            }

            return result;
        }

        public static float3 InertiaAroundCenterOfMass(SectorMassMoments moments, float3 centerOfMass)
        {
            if (moments.Mass <= 0f)
            {
                return float3.zero;
            }

            return moments.InertiaOrigin - moments.Mass * new float3(
                centerOfMass.y * centerOfMass.y + centerOfMass.z * centerOfMass.z,
                centerOfMass.x * centerOfMass.x + centerOfMass.z * centerOfMass.z,
                centerOfMass.x * centerOfMass.x + centerOfMass.y * centerOfMass.y);
        }
    }
}
