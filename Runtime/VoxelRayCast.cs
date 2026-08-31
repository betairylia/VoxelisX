#define PROFILE

using System;
using Unity.Mathematics;
using UnityEngine;
using Caelix;
using Caelix.Utils;

namespace Caelix
{
    internal interface IVoxelRaycastTarget
    {
        bool IsSolid(int3 position);
    }

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
        private readonly struct EntityRaycastTarget : IVoxelRaycastTarget
        {
            private readonly VoxelEntity entity;

            public EntityRaycastTarget(VoxelEntity entity)
            {
                this.entity = entity;
            }

            public bool IsSolid(int3 position)
            {
                return !entity.GetBlock(position).isEmpty;
            }
        }

        #region Inspector Fields

        /// <summary>
        /// Visual indicator showing which block is currently targeted.
        /// </summary>
        [Tooltip("Transform that will be positioned at the targeted block")]
        public Transform pointed;

        /// <summary>
        /// The voxel world renderer containing all voxel entities to raycast against.
        /// </summary>
        [Tooltip("Reference to the Caelix world renderer")]
        public CaelixCoreWorld targetWorld;

        /// <summary>
        /// The block type ID currently held by the player for placement.
        /// </summary>
        [Tooltip("Block ID to place when right-clicking")]
        public ushort handblock;

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

        #region Protected Fields

        /// <summary>
        /// Whether the last raycast hit a voxel.
        /// </summary>
        protected bool hitted = false;

        /// <summary>
        /// The voxel position that was hit by the raycast (in entity local space).
        /// </summary>
        protected int3 hit = int3.zero;

        /// <summary>
        /// The normal direction of the hit face (in entity local space).
        /// </summary>
        protected int3 hitNormal = int3.zero;

        /// <summary>
        /// The voxel entity that was hit.
        /// </summary>
        protected VoxelEntity hitTarget;

        /// <summary>
        /// Cached camera component.
        /// </summary>
        protected Camera mainCamera;

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
                if (RaycastVoxelEntity(target, localRay, closestDistance, out int3 hitPos, out int3 normal, out float distance))
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
        private static bool RaycastVoxelEntity(
            VoxelEntity entity,
            Ray ray,
            float maxDist,
            out int3 hitPosition,
            out int3 hitNormal,
            out float distance)
        {
            return RaycastVoxelGrid(
                new EntityRaycastTarget(entity),
                ray,
                maxDist,
                out hitPosition,
                out hitNormal,
                out distance);
        }

        internal static bool RaycastVoxelGrid<TTarget>(
            TTarget target,
            Ray ray,
            float maxDist,
            out int3 hitPosition,
            out int3 hitNormal,
            out float distance)
            where TTarget : struct, IVoxelRaycastTarget
        {
            hitPosition = int3.zero;
            hitNormal = int3.zero;
            distance = 0f;

            // DDA initialization
            Vector3 origin = ray.origin;
            Vector3 direction = ray.direction.normalized;
            if (direction.sqrMagnitude < 0.00000001f || maxDist < 0f)
            {
                return false;
            }

            // Current voxel position
            int3 voxelPos = new int3(
                (int)math.floor(origin.x),
                (int)math.floor(origin.y),
                (int)math.floor(origin.z)
            );

            // Step direction for each axis (-1, 0, or 1)
            int3 step = new int3(
                direction.x > 0 ? 1 : (direction.x < 0 ? -1 : 0),
                direction.y > 0 ? 1 : (direction.y < 0 ? -1 : 0),
                direction.z > 0 ? 1 : (direction.z < 0 ? -1 : 0)
            );

            // A point on a grid plane belongs to the cell entered by the ray. floor() already
            // chooses that cell for a positive step; a negative step needs the cell below it.
            bool startsOnXBoundary = IsGridBoundary(origin.x) && step.x != 0;
            bool startsOnYBoundary = IsGridBoundary(origin.y) && step.y != 0;
            bool startsOnZBoundary = IsGridBoundary(origin.z) && step.z != 0;
            if (startsOnXBoundary && step.x < 0) voxelPos.x--;
            if (startsOnYBoundary && step.y < 0) voxelPos.y--;
            if (startsOnZBoundary && step.z < 0) voxelPos.z--;

            // tDelta: how far along the ray we must move (in units of t) to cross one voxel boundary in each axis
            Vector3 tDelta = new Vector3(
                Mathf.Abs(direction.x) > 0.0001f ? 1.0f / Mathf.Abs(direction.x) : float.MaxValue,
                Mathf.Abs(direction.y) > 0.0001f ? 1.0f / Mathf.Abs(direction.y) : float.MaxValue,
                Mathf.Abs(direction.z) > 0.0001f ? 1.0f / Mathf.Abs(direction.z) : float.MaxValue
            );

            // tMax: t-value at which the ray crosses the next voxel boundary in each axis
            Vector3 tMax = new Vector3(
                CalculateInitialTMax(origin.x, direction.x, step.x, voxelPos.x),
                CalculateInitialTMax(origin.y, direction.y, step.y, voxelPos.y),
                CalculateInitialTMax(origin.z, direction.z, step.z, voxelPos.z)
            );

            // Track which face we entered through (for normal calculation)
            int3 lastStep = SelectPrimaryStep(
                direction,
                step,
                startsOnXBoundary,
                startsOnYBoundary,
                startsOnZBoundary);
            float currentDistance = 0f;

            // A diagonal ray crosses more than one voxel plane per unit of distance. Size the
            // safety limit from all three direction components instead of distance alone.
            float crossingsPerUnit = Mathf.Abs(direction.x) + Mathf.Abs(direction.y) + Mathf.Abs(direction.z);
            int maxSteps = Mathf.CeilToInt(maxDist * crossingsPerUnit) + 3;

            // DDA main loop: step through voxels along the ray
            for (int step_count = 0; step_count < maxSteps; step_count++)
            {
                // Check if current voxel is solid
                if (target.IsSolid(voxelPos))
                {
                    // Hit a solid block!
                    hitPosition = voxelPos;
                    if (math.all(lastStep == int3.zero))
                    {
                        lastStep = SelectPrimaryStep(direction, step, true, true, true);
                    }
                    hitNormal = -lastStep; // Normal points outward from the face we entered
                    distance = currentDistance;

                    return true;
                }

                float nextDistance = Mathf.Min(tMax.x, Mathf.Min(tMax.y, tMax.z));
                if (nextDistance > maxDist)
                {
                    break;
                }

                // Cross every plane reached at this distance. Stepping only one axis at an exact
                // edge or corner makes the traversal inspect cells that the ray never enters.
                bool crossX = SameTraversalDistance(tMax.x, nextDistance);
                bool crossY = SameTraversalDistance(tMax.y, nextDistance);
                bool crossZ = SameTraversalDistance(tMax.z, nextDistance);
                float crossingDistance = nextDistance;
                if (crossX) crossingDistance = Mathf.Max(crossingDistance, tMax.x);
                if (crossY) crossingDistance = Mathf.Max(crossingDistance, tMax.y);
                if (crossZ) crossingDistance = Mathf.Max(crossingDistance, tMax.z);
                if (crossingDistance > maxDist)
                {
                    break;
                }
                lastStep = SelectPrimaryStep(direction, step, crossX, crossY, crossZ);

                if (crossX)
                {
                    voxelPos.x += step.x;
                    tMax.x += tDelta.x;
                }
                if (crossY)
                {
                    voxelPos.y += step.y;
                    tMax.y += tDelta.y;
                }
                if (crossZ)
                {
                    voxelPos.z += step.z;
                    tMax.z += tDelta.z;
                }

                currentDistance = Mathf.Max(0f, crossingDistance);
            }

            return false;
        }

        /// <summary>
        /// Calculates the initial tMax value for one axis.
        /// This is the t-value at which the ray first crosses a voxel boundary on this axis.
        /// </summary>
        /// <param name="origin">Ray origin coordinate on this axis</param>
        /// <param name="direction">Ray direction on this axis</param>
        /// <param name="step">Step direction on this axis (-1, 0, or 1)</param>
        /// <param name="voxelCoordinate">Current voxel coordinate on this axis</param>
        /// <returns>The initial tMax value</returns>
        private static float CalculateInitialTMax(float origin, float direction, int step, int voxelCoordinate)
        {
            if (step == 0 || Mathf.Abs(direction) < 0.0001f)
                return float.MaxValue;

            float voxelBoundary = step > 0 ? voxelCoordinate + 1 : voxelCoordinate;

            // Calculate t value to reach that boundary
            return (voxelBoundary - origin) / direction;
        }

        private static bool IsGridBoundary(float coordinate)
        {
            return coordinate == Mathf.Floor(coordinate);
        }

        private static bool SameTraversalDistance(float a, float b)
        {
            // Camera and entity transforms lose a small amount of precision at large world
            // coordinates. Treat crossings within one thousandth of a voxel as the same edge so
            // a grazing ray does not flicker into a cell for a microscopic segment.
            return Mathf.Abs(a - b) <= 0.001f;
        }

        private static int3 SelectPrimaryStep(
            Vector3 direction,
            int3 step,
            bool includeX,
            bool includeY,
            bool includeZ)
        {
            float x = includeX ? Mathf.Abs(direction.x) : -1f;
            float y = includeY ? Mathf.Abs(direction.y) : -1f;
            float z = includeZ ? Mathf.Abs(direction.z) : -1f;

            if (x >= y && x >= z && step.x != 0) return new int3(step.x, 0, 0);
            if (y >= z && step.y != 0) return new int3(0, step.y, 0);
            if (step.z != 0) return new int3(0, 0, step.z);
            return int3.zero;
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
                pointed.position = hitTarget.transform.TransformPoint(hit.ToVector3Int());
                pointed.rotation = hitTarget.transform.rotation;
            }
            else if (pointed != null)
            {
                pointed.gameObject.SetActive(false);
            }
        }

        #endregion

        #region Input Handling

        protected virtual void HandleLeftClick()
        {
            hitTarget.SetBlock(hit, Block.Empty);
        }

        protected virtual void HandleRightClick()
        {
            int3 placePosition = hit + hitNormal;
            hitTarget.SetBlock(placePosition, new Block(handblock));
        }

        protected virtual void HandleMiddleClick()
        {
            Block block = hitTarget.GetBlock(hit);
            handblock = block.data;
            Debug.Log($"Picked block: {block.data}");
        }

        // TODO: Modern interface
        /// <summary>
        /// Handles player input for block interaction (place, destroy, pick).
        /// </summary>
        private void HandleInputs()
        {
            // Left click: destroy block
            if (Input.GetMouseButtonDown(0))
            {
                HandleLeftClick();
            }

            // Right click: place block
            bool shouldPlace = placeContinuously
                ? Input.GetMouseButton(1)
                : Input.GetMouseButtonDown(1);

            if (shouldPlace)
            {
                HandleRightClick();
            }

            // Middle click: pick block
            if (Input.GetMouseButtonDown(2))
            {
                HandleMiddleClick();
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
            ? hitTarget.transform.TransformPoint(hit.ToVector3Int())
            : Vector3.zero;

        /// <summary>
        /// Gets the position of the voxel that was hit (in entity local space).
        /// Only valid if IsHitting is true.
        /// </summary>
        public int3 HitPositionLocal => hit;

        /// <summary>
        /// Gets the normal of the face that was hit (in entity local space).
        /// Only valid if IsHitting is true.
        /// </summary>
        public int3 HitNormal => hitNormal;

        /// <summary>
        /// Gets the voxel entity that was hit.
        /// Only valid if IsHitting is true.
        /// </summary>
        public VoxelEntity HitEntity => hitTarget;

        #endregion
    }
}
