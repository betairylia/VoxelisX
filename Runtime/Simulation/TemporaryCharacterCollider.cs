using System.Collections.Generic;
using UnityEngine;
using System.Linq;

namespace Voxelis.Simulation
{
    [RequireComponent(typeof(VoxelEntity))]
    public class TemporaryCharacterCollider : MonoBehaviour
    {
        public Transform player;
        
        private VoxelEntity entity;
        private Dictionary<Vector3Int, Collider> activeColliders = new();
        
        [SerializeField] private float thresholdDistance;
        public BoundsInt playerBounds;

        public void Start()
        {
            entity = GetComponent<VoxelEntity>();
        }

        public float Distance(Vector3Int a, Vector3Int b)
        {
            var diff = a - b;
            // return Mathf.Abs(a.x) + Mathf.Abs(a.y) + Mathf.Abs(a.z);
            return diff.magnitude;
        }
        
        public void Update()
        {
            Vector3Int playerBlockPos =
                Vector3Int.RoundToInt(transform.worldToLocalMatrix *
                                      new Vector4(player.position.x, player.position.y, player.position.z, 1.0f) * 8);

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
                        Vector3Int pos = playerBlockPos + new Vector3Int(px, py, pz);
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
                        newCollider.center = (pos + Vector3.one * 0.5f) * 0.125f;
                        newCollider.size = Vector3.one * 0.125f;
                        
                        activeColliders.Add(pos, newCollider);
                    }
                }
            }
        }
    }
}