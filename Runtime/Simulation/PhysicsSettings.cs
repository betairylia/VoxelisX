using Unity.Collections;

namespace Voxelis.Simulation
{
    public struct PhysicsSettings
    {
        public static PhysicsSettings Settings;
        // public NativeHashMap<Block, float> massTable;

        // TODO: FIXME: DEBUG PURPOSE
        public float GetBlockMass(Block b)
        {
            return 1.0f;
            // return massTable[b];
        }
    }
}