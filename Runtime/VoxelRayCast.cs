#define PROFILE

using System;
using UnityEngine;
using Voxelis;

namespace Voxelis
{
    /// <summary>
    /// Performs voxel raycasting using the DDA (Digital Differential Analyzer) algorithm.
    /// This component casts a ray from the camera center and determines which voxel is being looked at,
    /// enabling block placement, destruction, and interaction in a voxel world.
    /// </summary>
    /// <remarks>
    /// The DDA algorithm efficiently traverses voxels along a ray by stepping through grid cells
    /// one at a time, avoiding the need to check every voxel in a bounding volume.
    /// Based on "A Fast Voxel Traversal Algorithm for Ray Tracing" by Amanatides & Woo (1987).
    /// </remarks>
    [RequireComponent(typeof(Camera))]
    public class VoxelRayCast : MonoBehaviour
    {
        #region Inspector Fields

        /// <summary>
        /// Visual indicator showing which block is currently targeted.
        /// </summary>
        [Tooltip("Transform that will be positioned at the targeted block")]
        public Transform pointed;

        /// <summary>
        /// The voxel world renderer containing all voxel entities to raycast against.
        /// </summary>
        [Tooltip("Reference to the VoxelisX world renderer")]
        public VoxelisXRenderer targetWorld;

        /// <summary>
        /// The block type ID currently held by the player for placement.
        /// </summary>
        [Tooltip("Block ID to place when right-clicking")]
        public uint handblock;

        /// <summary>
        /// Maximum distance in world units that raycasting will check.
        /// </summary>
        [Tooltip("Maximum raycast distance in world units")]
        [SerializeField] private float maxDistance = 20.0f;

        /// <summary>
        /// When enabled, holding right-click will continuously place blocks.
        /// </summary>
        [Tooltip("Allow continuous block placement while holding right-click")]
        [SerializeField] private bool placeContinuously = false;

        /// <summary>
        /// When enabled, raycasting will automatically run each LateUpdate.
        /// </summary>
        [Tooltip("Automatically perform raycasting each frame")]
        [SerializeField] private bool autoTick = false;

        #endregion

        #region Private Fields

        /// <summary>
        /// Whether the last raycast hit a voxel.
        /// </summary>
        private bool hitted = false;

        /// <summary>
        /// The voxel position that was hit by the raycast (in entity local space).
        /// </summary>
        private Vector3Int hit = Vector3Int.zero;

        /// <summary>
        /// The normal direction of the hit face (in entity local space).
        /// </summary>
        private Vector3Int hitNormal = Vector3Int.zero;

        /// <summary>
        /// The voxel entity that was hit.
        /// </summary>
        private VoxelEntity hitTarget;

        /// <summary>
        /// Cached camera component.
        /// </summary>
        private Camera mainCamera;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            mainCamera = GetComponent<Camera>();
        }

        public void LateUpdate()
        {
            if (autoTick)
            {
                Tick();
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Performs a single raycast tick, updating the targeted block and handling input.
        /// Call this manually if autoTick is disabled.
        /// </summary>
        public void Tick()
        {
#if PROFILE
            UnityEngine.Profiling.Profiler.BeginSample("VoxelRayCast.Tick");
#endif
            PerformRaycast();
            UpdateVisuals();

            if (hitted)
            {
                HandleInputs();
            }
#if PROFILE
            UnityEngine.Profiling.Profiler.EndSample();
#endif
        }

        #endregion

        #region Raycasting Logic

        /// <summary>
        /// Performs the voxel raycast using the DDA algorithm to find the first solid block hit.
        /// </summary>
        private void PerformRaycast()
        {
#if PROFILE
            UnityEngine.Profiling.Profiler.BeginSample("VoxelRayCast.PerformRaycast");
#endif
            hitted = false;
            float closestDistance = maxDistance;

            // Create ray from camera center
            Ray cameraRay = mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));

            // Check each voxel entity in the world
            foreach (var target in targetWorld.AllEntities)
            {
                // Transform ray to entity local space
                Ray localRay = new Ray(
                    target.transform.InverseTransformPoint(cameraRay.origin),
                    target.transform.InverseTransformDirection(cameraRay.direction)
                );

                // Perform DDA traversal
                if (RaycastVoxelEntity(target, localRay, closestDistance, out Vector3Int hitPos, out Vector3Int normal, out float distance))
                {
                    // Found a closer hit
                    if (distance < closestDistance)
                    {
                        hitted = true;
                        closestDistance = distance;
                        hit = hitPos;
                        hitNormal = normal;
                        hitTarget = target;
                    }
                }
            }
#if PROFILE
            UnityEngine.Profiling.Profiler.EndSample();
#endif
        }

        /// <summary>
        /// Casts a ray through a voxel entity using the DDA (Digital Differential Analyzer) algorithm.
        /// This efficiently traverses voxels along the ray path without checking every voxel in a volume.
        /// </summary>
        /// <param name="entity">The voxel entity to raycast against</param>
        /// <param name="ray">The ray in entity local space</param>
        /// <param name="maxDist">Maximum distance to check</param>
        /// <param name="hitPosition">Output: the voxel position that was hit</param>
        /// <param name="hitNormal">Output: the face normal of the hit</param>
        /// <param name="distance">Output: the distance to the hit</param>
        /// <returns>True if a solid voxel was hit</returns>
        private bool RaycastVoxelEntity(
            VoxelEntity entity,
            Ray ray,
            float maxDist,
            out Vector3Int hitPosition,
            out Vector3Int hitNormal,
            out float distance)
        {
            hitPosition = Vector3Int.zero;
            hitNormal = Vector3Int.zero;
            distance = 0f;

            // DDA initialization
            Vector3 origin = ray.origin;
            Vector3 direction = ray.direction.normalized;

            // Current voxel position
            Vector3Int voxelPos = new Vector3Int(
                Mathf.FloorToInt(origin.x),
                Mathf.FloorToInt(origin.y),
                Mathf.FloorToInt(origin.z)
            );

            // Step direction for each axis (-1, 0, or 1)
            Vector3Int step = new Vector3Int(
                direction.x > 0 ? 1 : (direction.x < 0 ? -1 : 0),
                direction.y > 0 ? 1 : (direction.y < 0 ? -1 : 0),
                direction.z > 0 ? 1 : (direction.z < 0 ? -1 : 0)
            );

            // tDelta: how far along the ray we must move (in units of t) to cross one voxel boundary in each axis
            Vector3 tDelta = new Vector3(
                Mathf.Abs(direction.x) > 0.0001f ? 1.0f / Mathf.Abs(direction.x) : float.MaxValue,
                Mathf.Abs(direction.y) > 0.0001f ? 1.0f / Mathf.Abs(direction.y) : float.MaxValue,
                Mathf.Abs(direction.z) > 0.0001f ? 1.0f / Mathf.Abs(direction.z) : float.MaxValue
            );

            // tMax: t-value at which the ray crosses the next voxel boundary in each axis
            Vector3 tMax = new Vector3(
                CalculateInitialTMax(origin.x, direction.x, step.x),
                CalculateInitialTMax(origin.y, direction.y, step.y),
                CalculateInitialTMax(origin.z, direction.z, step.z)
            );

            // Track which face we entered through (for normal calculation)
            Vector3Int lastStep = Vector3Int.zero;

            // Maximum number of steps to prevent infinite loops
            int maxSteps = Mathf.CeilToInt(maxDist) + 1;

            // DDA main loop: step through voxels along the ray
            for (int step_count = 0; step_count < maxSteps; step_count++)
            {
                // Check if current voxel is solid
                Block block = entity.GetBlock(voxelPos);
                if (!block.isEmpty)
                {
                    // Hit a solid block!
                    hitPosition = voxelPos;
                    hitNormal = -lastStep; // Normal points outward from the face we entered

                    // Calculate accurate hit distance
                    distance = CalculateHitDistance(origin, direction, voxelPos, lastStep);

                    return true;
                }

                // Move to next voxel along the ray
                // Choose the axis where we'll cross the boundary soonest
                if (tMax.x < tMax.y)
                {
                    if (tMax.x < tMax.z)
                    {
                        // Step in X
                        if (tMax.x > maxDist) break;
                        voxelPos.x += step.x;
                        tMax.x += tDelta.x;
                        lastStep = new Vector3Int(step.x, 0, 0);
                    }
                    else
                    {
                        // Step in Z
                        if (tMax.z > maxDist) break;
                        voxelPos.z += step.z;
                        tMax.z += tDelta.z;
                        lastStep = new Vector3Int(0, 0, step.z);
                    }
                }
                else
                {
                    if (tMax.y < tMax.z)
                    {
                        // Step in Y
                        if (tMax.y > maxDist) break;
                        voxelPos.y += step.y;
                        tMax.y += tDelta.y;
                        lastStep = new Vector3Int(0, step.y, 0);
                    }
                    else
                    {
                        // Step in Z
                        if (tMax.z > maxDist) break;
                        voxelPos.z += step.z;
                        tMax.z += tDelta.z;
                        lastStep = new Vector3Int(0, 0, step.z);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Calculates the initial tMax value for one axis.
        /// This is the t-value at which the ray first crosses a voxel boundary on this axis.
        /// </summary>
        /// <param name="origin">Ray origin coordinate on this axis</param>
        /// <param name="direction">Ray direction on this axis</param>
        /// <param name="step">Step direction on this axis (-1 or 1)</param>
        /// <returns>The initial tMax value</returns>
        private float CalculateInitialTMax(float origin, float direction, int step)
        {
            if (Mathf.Abs(direction) < 0.0001f)
                return float.MaxValue;

            // Calculate which voxel boundary we're heading toward
            float voxelBoundary;
            if (step > 0)
            {
                // Moving positive: next boundary is at ceiling
                voxelBoundary = Mathf.Ceil(origin);
            }
            else
            {
                // Moving negative: next boundary is at floor
                voxelBoundary = Mathf.Floor(origin);
            }

            // Calculate t value to reach that boundary
            return (voxelBoundary - origin) / direction;
        }

        /// <summary>
        /// Calculates the accurate distance from ray origin to the hit point on a voxel face.
        /// </summary>
        /// <param name="origin">Ray origin</param>
        /// <param name="direction">Ray direction (normalized)</param>
        /// <param name="voxelPos">The voxel that was hit</param>
        /// <param name="faceNormal">The normal of the face that was hit</param>
        /// <returns>Distance along the ray to the hit point</returns>
        private float CalculateHitDistance(Vector3 origin, Vector3 direction, Vector3Int voxelPos, Vector3Int faceNormal)
        {
            // Calculate which face of the voxel was hit
            Vector3 hitFaceCenter = (Vector3)voxelPos + Vector3.one * 0.5f + (Vector3)faceNormal * 0.5f;

            // Find the distance to the plane of that face
            // Using the face normal and a point on the plane
            float t;
            if (faceNormal.x != 0)
            {
                t = (hitFaceCenter.x - origin.x) / direction.x;
            }
            else if (faceNormal.y != 0)
            {
                t = (hitFaceCenter.y - origin.y) / direction.y;
            }
            else
            {
                t = (hitFaceCenter.z - origin.z) / direction.z;
            }

            return Mathf.Max(0, t);
        }

        #endregion

        #region Visuals

        /// <summary>
        /// Updates the visual indicator showing the targeted block.
        /// </summary>
        private void UpdateVisuals()
        {
            if (hitted && pointed != null)
            {
                pointed.gameObject.SetActive(true);
                pointed.position = hitTarget.transform.TransformPoint(hit);
                pointed.rotation = hitTarget.transform.rotation;
            }
            else if (pointed != null)
            {
                pointed.gameObject.SetActive(false);
            }
        }

        #endregion

        #region Input Handling

        /// <summary>
        /// Handles player input for block interaction (place, destroy, pick).
        /// </summary>
        private void HandleInputs()
        {
            // Left click: destroy block
            if (Input.GetMouseButtonDown(0))
            {
                hitTarget.SetBlock(hit, Block.Empty);
            }

            // Right click: place block
            bool shouldPlace = placeContinuously
                ? Input.GetMouseButton(1)
                : Input.GetMouseButtonDown(1);

            if (shouldPlace)
            {
                Vector3Int placePosition = hit + hitNormal;
                hitTarget.SetBlock(placePosition, new Block { data = handblock });
            }

            // Middle click: pick block
            if (Input.GetMouseButtonDown(2))
            {
                Block block = hitTarget.GetBlock(hit);
                handblock = block.data;
                Debug.Log($"Picked block: {block.data}");
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Gets whether the last raycast hit a voxel.
        /// </summary>
        public bool IsHitting => hitted;

        /// <summary>
        /// Gets the position of the voxel that was hit (in world space).
        /// Only valid if IsHitting is true.
        /// </summary>
        public Vector3 HitPositionWorld => hitTarget != null
            ? hitTarget.transform.TransformPoint(hit)
            : Vector3.zero;

        /// <summary>
        /// Gets the position of the voxel that was hit (in entity local space).
        /// Only valid if IsHitting is true.
        /// </summary>
        public Vector3Int HitPositionLocal => hit;

        /// <summary>
        /// Gets the normal of the face that was hit (in entity local space).
        /// Only valid if IsHitting is true.
        /// </summary>
        public Vector3Int HitNormal => hitNormal;

        /// <summary>
        /// Gets the voxel entity that was hit.
        /// Only valid if IsHitting is true.
        /// </summary>
        public VoxelEntity HitEntity => hitTarget;

        #endregion
    }
}
