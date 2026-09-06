using Unity.Mathematics;
using UnityEngine;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>
    /// Authors a voxel rigid body in the local server world. This component adds the body when
    /// enabled and removes it when disabled. Client tools read EntityView.HasBody and send
    /// force and drag commands through CaelixClient; replicated views do not need this component.
    /// Force convenience methods on authored bodies also enqueue client commands.
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

        private VoxelEntity entity
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

        private Guid128 PersistentGuid => entity.PersistentGuid;

        private CaelixWorld World => entity.ServerWorld;

        /// <summary>True when this process runs the server and the body exists in it.</summary>
        private bool HasServerData => entity.HasServerData && World.HasBody(PersistentGuid);

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

        /// <summary>Queues a force command for the server's next step.</summary>
        public void AddForce(Vector3 force, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Send(VoxelBodyForceCommand.Force(PersistentGuid, ToFloat3(force), mode));
        }

        /// <summary>Queues a torque command for the server's next step.</summary>
        public void AddTorque(Vector3 torque, VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Send(VoxelBodyForceCommand.Torque(PersistentGuid, ToFloat3(torque), mode));
        }

        /// <summary>Queues a force at a world-space position for the server's next step.</summary>
        public void AddForceAtPosition(
            Vector3 force,
            Vector3 worldPosition,
            VoxelBodyForceMode mode = VoxelBodyForceMode.Force)
        {
            Send(VoxelBodyForceCommand.ForceAtPosition(PersistentGuid, ToFloat3(force), ToFloat3(worldPosition), mode));
        }

        private void Send(in VoxelBodyForceCommand command)
        {
            CaelixHost host = entity.Host != null ? entity.Host : CaelixHost.Current;
            if (host == null || host.Client == null) return;

            ushort worldId = World != null ? World.Id : (ushort)0;
            host.Client.AddForce(in command, worldId);
        }

        private static float3 ToFloat3(Vector3 value) => new(value.x, value.y, value.z);
    }
}
