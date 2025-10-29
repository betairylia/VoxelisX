using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.PlayerLoop;

namespace Voxelis.Simulation
{
    public class UnityPhysicsCollider : MonoBehaviour
    {
        public bool isStatic;

        public struct Box
        {
            public float lifespan;
            public Collider coll;

            public void ReduceLife(float dt)
            {
                lifespan -= dt;
            }

            public void ResetLife(float t)
            {
                lifespan = t;
            }
        }

        public Dictionary<Vector3Int, Box> boxes = new();
        public float lifeMax = 1.0f;
        
        public void UpdateSelfRegister()
        {
            // TODO
        }

        public void AddAt(Vector3Int position)
        {
            if (boxes.ContainsKey(position))
            {
                var existing = boxes[position];
                boxes[position] = new Box
                {
                    lifespan = lifeMax,
                    coll = existing.coll
                };
            }
            else
            {
                // var box = gameObject.AddComponent<BoxCollider>();
                // box.center = position + Vector3.one * 0.5f;
                // box.size = Vector3.one * 0.8f;
                
                var box = gameObject.AddComponent<SphereCollider>();
                box.center = position + Vector3.one * 0.5f;
                box.radius = 0.45f;
                
                boxes[position] = new Box
                {
                    lifespan = lifeMax,
                    coll = box
                };
            }
        }

        private void Update()
        {
            List<Vector3Int> toDelete = new();
            foreach (var box in boxes.ToList())
            {
                if (box.Value.lifespan < Time.deltaTime)
                {
                    toDelete.Add(box.Key);
                    continue;
                }
                
                // Reduce time
                boxes[box.Key] = new Box
                {
                    lifespan = box.Value.lifespan - Time.deltaTime,
                    coll = box.Value.coll
                };
            }

            toDelete.ForEach(x =>
            {
                Destroy(boxes[x].coll);
                boxes.Remove(x);
            });
        }

        public void Tick()
        {
            // var box = gameObject.AddComponent<BoxCollider>();
        }
    }
}