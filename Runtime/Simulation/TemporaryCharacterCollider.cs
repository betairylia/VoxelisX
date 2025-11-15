using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using System.Linq;
using Voxelis.Utils;

namespace Voxelis.Simulation
{
    [RequireComponent(typeof(VoxelEntity))]
    public class TemporaryCharacterCollider : MonoBehaviour
    {
        public Transform player;

        private VoxelEntity entity;
        private Dictionary<int3, Collider> activeColliders = new();

        [SerializeField] private float thresholdDistance;
        public BoundsInt playerBounds;

        public void Start()
        {
            entity = GetComponent<VoxelEntity>();
        }

        public float Distance(int3 a, int3 b)
        {
            var diff = a - b;
            // return Mathf.Abs(a.x) + Mathf.Abs(a.y) + Mathf.Abs(a.z);
            return math.length(diff);
        }
        
        public void Update()
        {
            int3 playerBlockPos =
                (int3)math.round(math.mul(transform.worldToLocalMatrix,
                                      new float4(player.position.x, player.position.y, player.position.z, 1.0f)).xyz * 8);

            // Remove unused colliders
            var currentList = activeColliders.ToList();
            foreach (var obj in currentList)
            {
                if (Distance(obj.Key, playerBlockPos) > thresholdDistance || entity.GetBlock(obj.Key).isEmpty)
                {
                    Destroy(activeColliders[obj.Key]);
                    activeColliders.Remove(obj.Key);
                }
            }
            
            // Add colliders
            for (int px = playerBounds.min.x; px < playerBounds.max.x; px++)
            {
                for (int py = playerBounds.min.x; py < playerBounds.max.x; py++)
                {
                    for (int pz = playerBounds.min.x; pz < playerBounds.max.x; pz++)
                    {
                        int3 pos = playerBlockPos + new int3(px, py, pz);
                        Block b = entity.GetBlock(pos);

                        if (b.isEmpty)
                        {
                            continue;
                        }

                        if (activeColliders.ContainsKey(pos))
                        {
                            continue;
                        }

                        // Add collider
                        BoxCollider newCollider = gameObject.AddComponent<BoxCollider>();
                        newCollider.center = (pos.ToVector3Int() + Vector3.one * 0.5f) * 0.125f;
                        newCollider.size = Vector3.one * 0.125f;

                        activeColliders.Add(pos, newCollider);
                    }
                }
            }
        }
    }
}