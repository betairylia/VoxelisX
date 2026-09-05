using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>
    /// Authoring and client view component of one voxel entity.
    ///
    /// The entity's data lives in a <see cref="CaelixWorld"/> on the server. This component
    /// registers a scene-authored entity with its host in <c>OnEnable</c>, and presents the
    /// entity's replicated view on the client. In a process that runs the server (Host role) the
    /// data API below writes straight into server data, so importers, generators, and gameplay
    /// code keep working unchanged. In a process that does not run the server, writes go through
    /// commands and reads come from the view.
    /// </summary>
    public unsafe partial class VoxelEntity : MonoBehaviour
    {
        private static Unity.Mathematics.Random globalEntityRandomState =
            new((uint)(DateTime.Now.Ticks & 0xFFFFFFFF) | 1u);

        public static void InitializeRandomState(uint seed)
        {
            globalEntityRandomState = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
        }

        [Tooltip("Marks this entity as never moving.")]
        [SerializeField] private bool isStatic = true;

        [Tooltip("Marks this entity as protected from gameplay interaction tools (freeze/drag/break). " +
                 "Persisted in .cxw so the designation survives save/load and travels with the entity by GUID.")]
        [SerializeField] private bool isProtected;

        [Tooltip("Exclude this scene-authored entity from Caelix world save files. Authoring tools set this automatically; baked entities leave it off.")]
        [SerializeField, HideInInspector] private bool excludeFromWorldSave;

        [Tooltip("Host this entity registers with. Leave empty to use a host on a parent, then the " +
                 "process host.")]
        [SerializeField] private CaelixHost host;

        // Stable identity across play sessions and remote clients. Zero means "assign at runtime".
        [SerializeField, HideInInspector] private uint4 persistentGuidValue;

        private Guid128 guid;
        private CaelixHost resolvedHost;
        private CaelixWorld serverWorld;
        private bool ownsServerEntity;
        private bool isClientView;
        private bool warnedNoHost;
        private EntityView view;

        #region Identity and state

        public Guid128 PersistentGuid
        {
            get => guid;
            set
            {
                guid = value;
                persistentGuidValue = value.Value;
            }
        }

        public CaelixHost Host => resolvedHost;

        /// <summary>The server world this entity lives in, when this process runs it.</summary>
        public CaelixWorld ServerWorld => serverWorld;

        /// <summary>The replicated view bound to this component, once the spawn arrived.</summary>
        public EntityView View => view;

        /// <summary>True for view objects the client spawned for entities the scene did not author.</summary>
        public bool IsClientView => isClientView;

        /// <summary>True when this process runs the server and the entity exists in it.</summary>
        public bool HasServerData =>
            serverWorld != null && !serverWorld.IsDisposed && serverWorld.HasEntity(guid);

        /// <summary>Whether this entity never moves. The runtime value lives on the server.</summary>
        public bool IsStatic
        {
            get
            {
                if (HasServerData) return serverWorld.GetEntity(guid).isStatic;
                if (view != null) return view.IsStatic;
                return isStatic;
            }
            set
            {
                isStatic = value;
                if (HasServerData)
                {
                    serverWorld.SetEntityStatic(guid, value);
                }
                else if (resolvedHost != null && resolvedHost.Client != null && !guid.IsZero)
                {
                    resolvedHost.Client.SetEntityStatic(guid, value);
                }
            }
        }

        /// <summary>
        /// Whether this entity is protected from gameplay interaction (e.g. the main world that must
        /// never be unfrozen or dragged). Persisted keyed by <see cref="PersistentGuid"/>.
        /// </summary>
        public bool IsProtected
        {
            get
            {
                if (HasServerData) return serverWorld.GetEntity(guid).isProtected;
                if (view != null) return view.IsProtected;
                return isProtected;
            }
            set
            {
                isProtected = value;
                if (HasServerData)
                {
                    serverWorld.SetEntityProtected(guid, value);
                }
            }
        }

        /// <summary>Whether world serialization must skip this entity.</summary>
        public bool ExcludeFromWorldSave
        {
            get => excludeFromWorldSave;
            set
            {
                excludeFromWorldSave = value;
                if (HasServerData)
                {
                    VoxelEntityData data = serverWorld.GetEntity(guid);
                    data.excludeFromSave = value;
                    serverWorld.SetEntity(guid, data);
                }
            }
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            ResolveGuid();
        }

        private void OnEnable()
        {
            if (isClientView)
            {
                return;
            }

            EnsureRegistered();
        }

        private void OnDisable()
        {
            Unregister();
        }

        private void Update()
        {
            // A Transform moved by something other than replication (inspector drag, animation)
            // teleports the server entity. Replication resets hasChanged after every pose it applies.
            if (ownsServerEntity && HasServerData && transform.hasChanged)
            {
                serverWorld.SetEntityTransform(guid, new RigidTransform(transform.rotation, transform.position), teleport: true);
                transform.hasChanged = false;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Pushes inspector edits of the authoring fields into the live server data during play mode.
        /// The inspector writes the serialized fields directly, bypassing the properties.
        /// </summary>
        private void OnValidate()
        {
            if (!Application.isPlaying || !HasServerData) return;
            VoxelEntityData data = serverWorld.GetEntity(guid);
            if (data.isStatic != isStatic) IsStatic = isStatic;
            if (data.isProtected != isProtected) IsProtected = isProtected;
        }
#endif

        private void ResolveGuid()
        {
            if (!guid.IsZero) return;
            var stored = new Guid128(persistentGuidValue);
            guid = stored.IsZero ? Guid128.Random(ref globalEntityRandomState) : stored;
        }

        /// <summary>
        /// The serialized host, else a host on a parent, else the process host. See
        /// <c>SERVER_CLIENT_ARCHITECTURE.md</c> section 4.
        /// </summary>
        private CaelixHost ResolveHost()
        {
            if (host != null) return host;
            CaelixHost parent = GetComponentInParent<CaelixHost>(true);
            if (parent != null) return parent;
            return CaelixHost.Current;
        }

        /// <summary>
        /// Registers this scene-authored entity with the server world of its host. Idempotent.
        /// Returns false when no host exists or this is a client view.
        /// </summary>
        public bool EnsureRegistered()
        {
            if (isClientView) return false;
            if (ownsServerEntity) return true;

            ResolveGuid();
            resolvedHost = ResolveHost();
            if (resolvedHost == null)
            {
                if (!warnedNoHost)
                {
                    warnedNoHost = true;
                    Debug.LogError($"{name}: no CaelixHost in the scene; the entity is not simulated.", this);
                }

                return false;
            }

            resolvedHost.EnsureInitialized();
            serverWorld = resolvedHost.World;
            if (serverWorld == null)
            {
                return false;
            }

            var pose = new RigidTransform(transform.rotation, transform.position);
            if (!serverWorld.CreateEntity(guid, pose, isStatic, isProtected, excludeFromWorldSave))
            {
                // The guid is taken: a duplicated scene object shares its serialized identity.
                Debug.LogWarning($"{name}: guid {guid} is already in the world; assigning a new one.", this);
                PersistentGuid = Guid128.Random(ref globalEntityRandomState);
                if (!serverWorld.CreateEntity(guid, pose, isStatic, isProtected, excludeFromWorldSave))
                {
                    return false;
                }
            }

            ownsServerEntity = true;
            transform.hasChanged = false;
            resolvedHost.Client?.RegisterAuthoredView(this, serverWorld.Id);
            return true;
        }

        private void Unregister()
        {
            if (isClientView)
            {
                return;
            }

            if (resolvedHost != null && resolvedHost.Client != null && serverWorld != null)
            {
                resolvedHost.Client.UnregisterAuthoredView(this, serverWorld.Id);
            }

            if (ownsServerEntity && serverWorld != null && !serverWorld.IsDisposed)
            {
                serverWorld.RemoveEntity(guid);
            }

            ownsServerEntity = false;
            serverWorld = null;
        }

        /// <summary>Called by the client before activation on a view object it spawned.</summary>
        internal void InitializeAsClientView(Guid128 viewGuid, CaelixHost owningHost)
        {
            isClientView = true;
            PersistentGuid = viewGuid;
            resolvedHost = owningHost;
            serverWorld = owningHost != null ? owningHost.World : null;
        }

        internal void BindView(EntityView boundView)
        {
            view = boundView;
        }

        internal void UnbindView()
        {
            view = null;
        }

        #endregion

        #region Server data API (host mode)

        private VoxelEntityData RequireServerData()
        {
            if (!HasServerData)
            {
                throw new InvalidOperationException(
                    $"{name}: server data is not available in this process. Read the view instead, or send a command.");
            }

            return serverWorld.GetEntity(guid);
        }

        /// <summary>A copy of the server record. Sector storage inside it is shared, scalars are a snapshot.</summary>
        public VoxelEntityData GetDataCopy() => RequireServerData();

        /// <summary>Writes scalar fields of a record back to the server.</summary>
        public void CopyDataFrom(VoxelEntityData srcData)
        {
            if (HasServerData)
            {
                serverWorld.SetEntity(guid, srcData);
            }
        }

        /// <summary>The server entity's sectors. Not created when this process does not run the server.</summary>
        public SharedHashMap<int3, SectorHandle> Sectors =>
            HasServerData ? serverWorld.GetEntity(guid).sectors : default;

        public SharedHashMap<int3, SectorNeighborHandles> Neighbors =>
            HasServerData ? serverWorld.GetEntity(guid).sectorNeighbors : default;

        public void AddEmptySectorAt(int3 pos) => RequireServerData().AddEmptySectorAt(pos);

        public void ShallowCopyAndAddSectorAt(int3 pos, Sector sector) => RequireServerData().ShallowCopyAndAddSectorAt(pos, sector);

        public void AddSectorAt(int3 pos, SectorHandle sector) => RequireServerData().AddSectorAt(pos, sector);

        public bool RemoveSectorAt(int3 pos) => RequireServerData().RemoveSectorAt(pos);

        /// <summary>Reads a block: from the server in host mode, otherwise from the replica.</summary>
        public Block GetBlock(int3 pos)
        {
            if (HasServerData) return serverWorld.GetBlock(guid, pos);
            if (view != null) return view.Data.GetBlock(pos);
            return Block.Empty;
        }

        /// <summary>Reads a slot: from the server in host mode, otherwise from the replica (replicated slots only).</summary>
        public T GetSlot<T>(SectorSlotId slotId, int3 pos) where T : unmanaged
        {
            if (HasServerData) return serverWorld.GetSlot<T>(guid, slotId, pos);
            if (view != null) return view.Data.GetSlot<T>(slotId, pos);
            return default;
        }

        /// <summary>Writes a block: directly in host mode, as a command otherwise.</summary>
        public void SetBlock(int3 pos, Block b)
        {
            if (HasServerData)
            {
                serverWorld.SetBlock(guid, pos, b);
            }
            else if (resolvedHost != null && resolvedHost.Client != null && !guid.IsZero)
            {
                resolvedHost.Client.SetBlock(guid, pos, b);
            }
        }

        /// <summary>Writes a slot value into server data. Host mode only.</summary>
        public void SetSlot<T>(SectorSlotId slotId, int3 pos, T value)
            where T : unmanaged, IEquatable<T>
        {
            RequireServerData().SetSlot(slotId, pos, value);
        }

        /// <summary>Appends the entity-local position of every non-empty block. See <see cref="VoxelEntityData.CollectSolidVoxelPositions"/>.</summary>
        public bool CollectSolidVoxelPositions(List<int3> into, int maxCount = 0)
        {
            if (HasServerData) return serverWorld.GetEntity(guid).CollectSolidVoxelPositions(into, maxCount);
            if (view != null) return view.Data.CollectSolidVoxelPositions(into, maxCount);
            return true;
        }

        public JobHandle PropagateDirtyFlags(DirtyFlags flags = DirtyFlags.All, bool async = false)
        {
            return RequireServerData().PropagateDirtyFlags(flags, async);
        }

        public void ClearDirtyFlags()
        {
            VoxelEntityData data = RequireServerData();
            data.ClearDirtyFlags();
            serverWorld.SetEntity(guid, data);
        }

        public void ClearRequireUpdates()
        {
            VoxelEntityData data = RequireServerData();
            data.ClearRequireUpdates();
            serverWorld.SetEntity(guid, data);
        }

        public void RefreshAllocatedBrickLists() => RequireServerData().RefreshAllocatedBrickLists();

        public ulong GetHostMemoryUsageKB()
        {
            if (HasServerData) return serverWorld.GetEntity(guid).GetHostMemoryUsageKB();
            if (view != null) return view.Data.GetHostMemoryUsageKB();
            return 0;
        }

        /// <summary>Teleports the server entity to this component's Transform.</summary>
        public void SyncTransformToData()
        {
            if (HasServerData)
            {
                serverWorld.SetEntityTransform(guid, new RigidTransform(transform.rotation, transform.position), teleport: true);
                transform.hasChanged = false;
            }
        }

        /// <summary>Moves this component's Transform to the server pose (host mode) or the view pose.</summary>
        public void SyncTransformFromData()
        {
            RigidTransform pose;
            if (HasServerData) pose = serverWorld.GetEntity(guid).transform;
            else if (view != null) pose = view.Data.transform;
            else return;
            transform.SetPositionAndRotation(pose.pos, pose.rot);
            transform.hasChanged = false;
        }

        public static int3 GetSectorBlockPos(int3 sectorPos) => VoxelEntityData.GetSectorBlockPos(sectorPos);

        public float4x4 ObjectToWorld() => transform.localToWorldMatrix;

        public float4x4 WorldToObject() => transform.worldToLocalMatrix;

        #endregion
    }
}
