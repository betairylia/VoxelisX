#define PROFILE

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Voxelis;

[RequireComponent(typeof(Camera))]
public class VoxelRayCast : MonoBehaviour
{
    public Transform pointed;
    public VoxelisXRenderer targetWorld;

    public uint handblock;

    bool hitted = false;
    Vector3Int hit = new Vector3Int(0, 0, 0), dirc = new Vector3Int(0, 0, 0);
    private VoxelEntity hitTarget;

    [SerializeField] private bool placeContinuously = false;
    [SerializeField] private bool autoTick = false;

    public void LateUpdate()
    {
        if (autoTick) Tick();
    }

    // Update is called once per frame
    // TODO: DDA
    public void Tick()
    {
#if PROFILE
        UnityEngine.Profiling.Profiler.BeginSample($"VoxelRayCast");
#endif
        float maxDistance = 20.0f;
        float _minD = maxDistance;
        hitted = false;
        
        Ray cameraRay = GetComponent<Camera>().ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        foreach (var target in targetWorld.AllEntities)
        {
            // RayCast for voxels
            var ray = new Ray(
                target.transform.InverseTransformPoint(cameraRay.origin),
                target.transform.InverseTransformDirection(cameraRay.direction)
            );
            Vector3 current = ray.origin;
            Vector3 terminal = ray.origin + ray.direction * maxDistance;

            Vector3Int minV = Vector3Int.RoundToInt(Vector3.Min(current, terminal)) - Vector3Int.one;
            Vector3Int maxV = Vector3Int.RoundToInt(Vector3.Max(current, terminal)) + Vector3Int.one;

            for (int x = minV.x; x <= maxV.x; x++)
            {
                for (int y = minV.y; y <= maxV.y; y++)
                {
                    for (int z = minV.z; z <= maxV.z; z++)
                    {
                        Vector3Int p = new Vector3Int(x, y, z);
                        if (target.GetBlock(p).isEmpty) // TODO: Selectable nonsolid blocks
                        {
                            continue;
                        }
                        Bounds b = new Bounds(p + Vector3.one * 0.5f, Vector3.one);

                        float d;
                        if (b.IntersectRay(ray, out d))
                        {
                            if (d < _minD)
                            {
                                hitted = true;
                                _minD = d;
                                hit = p;
                                hitTarget = target;
                            }
                        }
                    }
                }
            }
        }

        // Actual cast
        if (hitted)
        {
            GameObject obj = new GameObject();
            obj.transform.position = Vector3.zero;
            obj.transform.rotation = Quaternion.identity;

            BoxCollider coll = obj.AddComponent<BoxCollider>();
            coll.center = hitTarget.transform.TransformPoint(hit + Vector3.one * 0.5f);
            coll.size = Vector3.one;

            RaycastHit info;
            if (coll.Raycast(cameraRay, out info, maxDistance))
            {
                dirc = Vector3Int.RoundToInt(hitTarget.transform.InverseTransformDirection(info.normal));
                pointed.gameObject.SetActive(true);
                pointed.position = hitTarget.transform.TransformPoint(hit);
                pointed.rotation = hitTarget.transform.rotation;

                HandleInputs();
            }

            Destroy(coll);
            Destroy(obj);
        }
        else
        {
            pointed.gameObject.SetActive(false);
        }
#if PROFILE
        UnityEngine.Profiling.Profiler.EndSample();
#endif
    }

    private void HandleInputs()
    {
        if(Input.GetMouseButtonDown(0))
        {
            hitTarget.SetBlock(hit, Block.Empty);
        }
        // if(Input.GetMouseButtonDown(1))
        if((Input.GetMouseButton(1) && placeContinuously) || (Input.GetMouseButtonDown(1) && (!placeContinuously)))
        {
            hitTarget.SetBlock(hit + dirc, new Block{data = handblock});
        }
        if (Input.GetMouseButtonDown(2))
        {
            var blk = hitTarget.GetBlock(hit);
            Debug.Log($"Block: {blk.data}");
            handblock = blk.data;
        }
    }
}
