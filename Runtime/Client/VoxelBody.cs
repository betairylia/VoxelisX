using Unity.Mathematics;
using UnityEngine;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>
    /// Authoring and client view component of a voxel rigid body. The body record lives in the
    /// server world; this component adds it when enabled and removes it when disabled. Forces go
    /// through the client as commands, so gameplay code reads the same in every role.
    /// </summary>
    [RequireComponent(typeof(VoxelEntity))]
    public class VoxelBody : MonoBehaviour
    {
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

        private VoxelEntity _entity;
        private bool ownsServerBody;

        public bool accuratePhysics
        {
            get => _accuratePhysics;
            set
            {
                _accuratePhysics = value;
                if (HasServerData)
                {
                    World.SetBodyAccuratePhysics(PersistentGuid, value);
                }
            }
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

        public Guid128 PersistentGuid => entity.PersistentGuid;

        private CaelixWorld World => entity.ServerWorld;

        /// <summary>True when this process runs the server and the body exists in it.</summary>
        public bool HasServerData => entity.HasServerData && World.HasBody(PersistentGuid);

        private void OnEnable()
        {
            if (entity.IsClientView)
            {
                return; // the server already has this body; the view only presents it
            }

            if (!entity.EnsureRegistered())
            {
                return;
            }

            if (World.AddBody(PersistentGuid, _accuratePhysics))
            {
                ownsServerBody = true;
            }
        }

        private void OnDisable()
        {
            if (ownsServerBody && World != null && !World.IsDisposed)
            {
                World.RemoveBody(PersistentGuid);
            }

            ownsServerBody = false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying || !HasServerData) return;
            World.SetBodyAccuratePhysics(PersistentGuid, _accuratePhysics);
        }
#endif

        #region Server data API (host mode)

        public VoxelBodyData GetDataCopy()
        {
            if (!HasServerData)
            {
                throw new System.InvalidOperationException(
                    $"{name}: body data is not available in this process.");
            }

            World.TryGetBody(PersistentGuid, out VoxelBodyData data);
            return data;
        }

        public void CopyDataFrom(VoxelBodyData srcData)
        {
            if (HasServerData)
            {
                srcData.accuratePhysics = _accuratePhysics;
                World.SetBody(PersistentGuid, srcData);
            }
        }

        /// <summary>Overwrites the body's physics velocity. Host mode only.</summary>
        public void SetVelocity(float3 linearVelocity, float3 angularVelocity)
        {
            if (HasServerData)
            {
                World.SetBodyVelocity(PersistentGuid, linearVelocity, angularVelocity);
            }
        }

        public VoxelBodyData.MassProperties massProperties =>
            HasServerData ? GetDataCopy().massProperties : default;

        /// <summary>
        /// Recomputes mass properties from the entity's current voxels. The tick does this every
        /// step; call it only when a value is needed between ticks.
        /// </summary>
        public void ComputeMassProperties()
        {
            if (!HasServerData) return;
            VoxelBodyData data = GetDataCopy();
            data.accuratePhysics = _accuratePhysics;
            data.ComputePhysicsProperties(entity.GetDataCopy());
            World.SetBody(PersistentGuid, data);
        }

        #endregion

        #region Forces (commands)

        public void AddForce(Vector3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Send(VoxelBodyForceCommand.Force(PersistentGuid, ToFloat3(force), mode));
        }

        public void AddTorque(Vector3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Send(VoxelBodyForceCommand.Torque(PersistentGuid, ToFloat3(torque), mode));
        }

        public void AddForceAtPosition(
            Vector3 force,
            Vector3 worldPosition,
            VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Send(VoxelBodyForceCommand.ForceAtPosition(PersistentGuid, ToFloat3(force), ToFloat3(worldPosition), mode));
        }

        private void Send(in VoxelBodyForceCommand command)
        {
            CaelixHost host = entity.Host != null ? entity.Host : CaelixHost.Any;
            if (host == null || host.Client == null)
            {
                return;
            }

            ushort worldId = World != null ? World.Id : (ushort)0;
            host.Client.AddForce(in command, worldId);
        }

        private static float3 ToFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }

        #endregion
    }
}
