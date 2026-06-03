using Unity.Collections;
using UnityEngine;
using UnityEngine.Serialization;

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

        [FormerlySerializedAs("isStatic"), SerializeField] private bool _isStatic = false;

        public bool isStatic
        {
            get => _isStatic;
            set
            {
                _isStatic = value;
                data.isStatic = value;
            }
        }
        
        private Rigidbody body;
        private VoxelEntity _entity;
        private VoxelBodyData data;

        public VoxelBodyData GetDataCopy() => data;

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
            data.isStatic = _isStatic;
            InitializeBody();
            _entity = GetComponent<VoxelEntity>();
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

        /// <summary>
        /// Computes mass properties (mass, center of mass, inertia tensor) for this voxel body.
        /// Uses cached per-sector origin moments and only refreshes geometry-dirty sectors after the initial build.
        /// </summary>
        public VoxelBodyData.MassProperties ComputeMassProperties()
        {
            data.isStatic = _isStatic;
            return data.ComputeMassProperties(entity.Sectors);
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
    }
}
