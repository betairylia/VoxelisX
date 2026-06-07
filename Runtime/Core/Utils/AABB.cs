using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Mathematics;

namespace Voxelis.Mathematics
{
    [BurstCompile]
    public struct AABB
    {
        public float3 Min;
        public float3 Max;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AABB Inflated(float amount)
        {
            return new AABB { Min = Min - amount, Max = Max + amount };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AABB Union(AABB a, AABB b)
        {
            return new AABB { Min = math.min(a.Min, b.Min), Max = math.max(a.Max, b.Max) };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Overlaps(AABB a, AABB b)
        {
            return math.all(a.Min <= b.Max) && math.all(a.Max >= b.Min);
        }

        public static AABB Transform(AABB from, RigidTransform transform)
        {
            float3 worldMin = new float3(float.PositiveInfinity);
            float3 worldMax = new float3(float.NegativeInfinity);

            for (int mask = 0; mask < 8; mask++)
            {
                float3 localCorner = new float3(
                    (mask & 1) == 0 ? from.Min.x : from.Max.x,
                    (mask & 2) == 0 ? from.Min.y : from.Max.y,
                    (mask & 4) == 0 ? from.Min.z : from.Max.z);
                float3 worldCorner = math.transform(transform, localCorner);
                worldMin = math.min(worldMin, worldCorner);
                worldMax = math.max(worldMax, worldCorner);
            }

            return new AABB { Min = worldMin, Max = worldMax };
        }
    }

    [BurstCompile]
    public struct AABBInt
    {
        public int3 Min;
        public int3 Max;

        public void Update(int3 point)
        {
            Min = math.min(Min, point);
            Max = math.max(Max, point);
        }
    }
}