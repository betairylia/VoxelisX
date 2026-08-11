using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using Voxelis.Utils;

namespace Voxelis
{
    [RequireComponent(typeof(VoxelEntity))]
    public class VoxelBody : MonoBehaviour//, IDisposable
    {
        /// <summary>
        /// Indicates whether physics simulation is enabled for this voxel entity.
        /// When enabled, the entity will participate in collision detection and response.
        /// </summary>
        [FormerlySerializedAs("collisionEnabled")] public bool physicsEnabled = false;

        /// <summary>
        /// Selects which Unity Physics solver resolves contacts involving this body.
        /// On (default) uses the Direct solver, which is more accurate but more expensive;
        /// off falls back to the cheaper Iterative solver.
        /// </summary>
        /// <remarks>
        /// The solver for a contact pair is the <i>least</i> accurate of the two bodies involved
        /// (Unity Physics combines them with a min), so a pair only gets the direct solver when
        /// <b>both</b> bodies have this enabled — including the static body in a dynamic-vs-static pair.
        /// </remarks>
        [Tooltip("Use the accurate (Direct) solver for contacts involving this body instead of the " +
                 "cheaper Iterative solver.\n\nA contact pair only uses the Direct solver when BOTH " +
                 "bodies have this enabled.")]
        [SerializeField] private bool _accuratePhysics = true;

        public bool accuratePhysics
        {
            get => _accuratePhysics;
            set
            {
                _accuratePhysics = value;
                data.accuratePhysics = value;
            }
        }

        private Rigidbody body;
        private VoxelEntity _entity;
        private VoxelBodyData data;

        public VoxelBodyData GetDataCopy() => data;

        public Guid128 PersistentGuid => entity.PersistentGuid;

        public void CopyDataFrom(VoxelBodyData srcData)
        {
            data = srcData;
            // Re-assert from the component so inspector edits (which write the backing field
            // directly, bypassing the property setter) take effect on the next tick.
            data.accuratePhysics = _accuratePhysics;
        }

        /// <summary>
        /// Overwrites the body's physics velocity. Used by world load to resume a dynamic body's
        /// motion (the solver reads motionVelocity when building the physics world each tick).
        /// Harmless on static bodies, whose velocity the solver ignores.
        /// </summary>
        public void SetVelocity(float3 linearVelocity, float3 angularVelocity)
        {
            data.motionVelocity.LinearVelocity = linearVelocity;
            data.motionVelocity.AngularVelocity = angularVelocity;
        }

        public VoxelEntity entity
        {
            get
            {
                if (_entity == null)
                {
                    _entity = GetComponent<VoxelEntity>();
                }

                return _entity;
            }
        }

        private void Awake()
        {
            data = new VoxelBodyData(Allocator.Persistent);
            data.accuratePhysics = _accuratePhysics;
            CreateCollider();
            InitializeBody();
            _entity = GetComponent<VoxelEntity>();
        }

        private void CreateCollider()
        {
            var material = new Unity.Physics.Material
            {
                Friction = 0.1f,
                Restitution = 0.0f,
                FrictionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                RestitutionCombinePolicy = Unity.Physics.Material.CombinePolicy.GeometricMean,
                CollisionResponse = Unity.Physics.CollisionResponsePolicy.CollideRaiseCollisionEvents
            };

            data.collider = Unity.Physics.VoxelCollider.Create(
                null,
                Unity.Physics.CollisionFilter.Default,
                material
            );
        }

        private void InitializeBody()
        {
            if (!physicsEnabled)
            {
                return;
            }

            body = gameObject.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
            }
            
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
        }

        public VoxelBodyData.MassProperties massProperties => data.massProperties;

        public void AddForce(Vector3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            CurrentForceCommands()?.AddForce(PersistentGuid, ToFloat3(force), mode);
        }

        public void AddTorque(Vector3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            CurrentForceCommands()?.AddTorque(PersistentGuid, ToFloat3(torque), mode);
        }

        public void AddForceAtPosition(
            Vector3 force,
            Vector3 worldPosition,
            VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            CurrentForceCommands()?.AddForceAtPosition(PersistentGuid, ToFloat3(force), ToFloat3(worldPosition), mode);
        }

        /// <summary>
        /// Computes mass properties (mass, center of mass, inertia tensor) for this voxel body.
        /// Uses cached per-sector origin moments and only refreshes geometry-dirty sectors after the initial build.
        /// </summary>
        public void ComputeMassProperties()
        {
            data.accuratePhysics = _accuratePhysics;
            data.ComputePhysicsProperties(entity.GetDataCopy());
        }

        private void OnDestroy()
        {
            data.Dispose();
        }

        private void OnEnable()
        {
            (VoxelisXCoreWorld.instance as VoxelisXWorld)?.AddBody(this);
        }

        private void OnDisable()
        {
            (VoxelisXCoreWorld.instance as VoxelisXWorld)?.RemoveBody(this);
        }

        private void OnDrawGizmos()
        {
            if (body == null) return;
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.TransformPoint(body.centerOfMass), 0.25f);
        }

        private static VoxelBodyForceCommandStream CurrentForceCommands()
        {
            return (VoxelisXCoreWorld.instance as VoxelisXWorld)?.BodyForceCommands;
        }

        private static float3 ToFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }
    }
}
