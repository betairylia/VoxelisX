using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxelis.Simulation
{
    public class VoxelEntityPhysics
    {
        [BurstCompile]
        public struct AccumulateSectorCenterOfMass : IJob
        {
            public PhysicsSettings settings;
            public NativeArray<float4> accumulatedCenter;
            
            public Sector sector;
            public int3 sectorPosition;
            
            public void Execute()
            {
                foreach (BlockIterator blockIter in new SectorNonEmptyBlockEnumerator(sector))
                {
                    float mass = settings.GetBlockMass(blockIter.block);
                    float3 position = new float3(sectorPosition + blockIter.position) + 0.5f;
                    accumulatedCenter[0] += new float4(mass * position, mass);
                }
            }
        }
        
        [BurstCompile]
        public struct AccumulateSectorInertia : IJob
        {
            public PhysicsSettings settings;
            public float3 centerOfMass;
            public NativeArray<float3> accumulatedInertia;
            
            public Sector sector;
            public int3 sectorPosition;
            
            public void Execute()
            {
                float3 currentInertia = accumulatedInertia[0];
                
                foreach (BlockIterator blockIter in new SectorNonEmptyBlockEnumerator(sector))
                {
                    float mass = settings.GetBlockMass(blockIter.block);
                    float3 position = new float3(sectorPosition + blockIter.position) + 0.5f - centerOfMass;

                    currentInertia.x += mass * (position.y * position.y + position.z * position.z);
                    currentInertia.y += mass * (position.x * position.x + position.z * position.z);
                    currentInertia.z += mass * (position.x * position.x + position.y * position.y);
                }
                
                accumulatedInertia[0] = currentInertia;
            }
        }
    }
}