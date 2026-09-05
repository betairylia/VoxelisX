using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Client
{
    /// <summary>
    /// The client's render replica of one server world. Applies replication messages into
    /// <see cref="EntityView"/> data, binds scene-authored components to their views, spawns view
    /// objects for entities the scene does not have, and runs the Geometry-only dirty propagation
    /// the renderers need. No automata, no physics.
    /// </summary>
    public sealed class ClientWorld : IDisposable
    {
        private readonly Dictionary<Guid128, EntityView> views = new();
        private readonly List<EntityView> viewList = new();
        private readonly Dictionary<Guid128, VoxelEntity> pendingAuthored = new();
        private bool disposed;

        public ushort Id { get; }
        public bool IsDisposed => disposed;
        public ushort ReplicatedSlotMask { get; internal set; }

        /// <summary>The client that owns this replica.</summary>
        public CaelixClient Owner { get; }

        /// <summary>Views in spawn order. Do not mutate the world while iterating.</summary>
        public IReadOnlyList<EntityView> Views => viewList;

        public event Action<EntityView> ViewSpawned;

        /// <summary>Raised before a view's data is disposed. Renderers release their resources here.</summary>
        public event Action<EntityView> ViewDespawning;

        /// <summary>Raised while the sector is still alive. Consumers must release jobs and handles here.</summary>
        public event Action<EntityView, int3> SectorRemoving;

        internal ClientWorld(CaelixClient owner, ushort id, ushort replicatedSlotMask)
        {
            Owner = owner;
            Id = id;
            ReplicatedSlotMask = replicatedSlotMask == 0 ? Sector.DefaultReplicatedSlotMask : replicatedSlotMask;
        }

        public bool TryGetView(Guid128 guid, out EntityView view) => views.TryGetValue(guid, out view);

        #region Authored components

        /// <summary>
        /// A scene component that wants to present the entity with its guid. Bound immediately if
        /// the view exists, otherwise when its spawn message arrives.
        /// </summary>
        public void RegisterAuthored(VoxelEntity component)
        {
            if (component == null) return;
            Guid128 guid = component.PersistentGuid;
            if (guid.IsZero) return;

            if (views.TryGetValue(guid, out EntityView view))
            {
                Bind(view, component, clientSpawned: false);
                return;
            }

            pendingAuthored[guid] = component;
        }

        public void UnregisterAuthored(VoxelEntity component)
        {
            if (component == null) return;
            Guid128 guid = component.PersistentGuid;
            if (pendingAuthored.TryGetValue(guid, out VoxelEntity pending) && pending == component)
            {
                pendingAuthored.Remove(guid);
            }

            if (views.TryGetValue(guid, out EntityView view) && view.Component == component)
            {
                component.UnbindView();
                view.Component = null;
            }
        }

        private void Bind(EntityView view, VoxelEntity component, bool clientSpawned)
        {
            view.Component = component;
            view.IsClientSpawned = clientSpawned;
            component.BindView(view);
            ApplyTransformToComponent(view);
        }

        #endregion

        #region Message application

        internal void OnSpawn(in EntitySpawnMessage message)
        {
            if (views.ContainsKey(message.Guid))
            {
                // A duplicate spawn is a server bug; keep the first.
                Debug.LogWarning($"[ClientWorld {Id}] Duplicate spawn for {message.Guid}; ignored.");
                return;
            }

            var data = new VoxelEntityData(Allocator.Persistent)
            {
                Guid = message.Guid,
                transform = message.Transform,
                previousTransform = message.Transform,
                isStatic = message.IsStatic != 0,
                isProtected = message.IsProtected != 0,
            };

            var view = new EntityView(message.Guid, data)
            {
                HasBody = message.HasBody != 0,
                // A born-static body settles its motion vectors on its first frame.
                ShouldResetMotionVectors = message.IsStatic != 0,
            };
            views.Add(message.Guid, view);
            viewList.Add(view);

            if (pendingAuthored.TryGetValue(message.Guid, out VoxelEntity authored) && authored != null)
            {
                pendingAuthored.Remove(message.Guid);
                Bind(view, authored, clientSpawned: false);
            }
            else
            {
                SpawnViewObject(view);
            }

            ViewSpawned?.Invoke(view);
        }

        private void SpawnViewObject(EntityView view)
        {
            var go = new GameObject($"VoxelEntity_{view.Guid}");
            go.SetActive(false);
            var component = go.AddComponent<VoxelEntity>();
            component.InitializeAsClientView(view.Guid, Owner?.Host);
            if (view.HasBody)
            {
                // View-only body: gives tools the same component surface as an authored body.
                go.AddComponent<VoxelBody>();
            }

            go.SetActive(true);
            Bind(view, component, clientSpawned: true);
        }

        internal void OnDespawn(in EntityDespawnMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            ViewDespawning?.Invoke(view);

            views.Remove(message.Guid);
            viewList.Remove(view);

            VoxelEntity component = view.Component;
            view.Component = null;
            if (component != null)
            {
                component.UnbindView();
                if (view.IsClientSpawned)
                {
                    DestroyViewObject(component.gameObject);
                }
                else if (component.isActiveAndEnabled)
                {
                    // A replacement with the same GUID must bind back to its authored component.
                    pendingAuthored[message.Guid] = component;
                }
            }

            view.Data.Dispose();
        }

        internal void OnState(in EntityStateMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            bool isStatic = message.IsStatic != 0;
            if (!view.Data.isStatic && isStatic)
            {
                view.ShouldResetMotionVectors = true;
            }

            view.Data.isStatic = isStatic;
            view.Data.isProtected = message.IsProtected != 0;
            view.HasBody = message.HasBody != 0;
        }

        internal void OnTransform(in EntityTransformMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            view.Data.previousTransform = view.Data.transform;
            view.Data.transform = message.Transform;
            ApplyTransformToComponent(view);
        }

        private static void ApplyTransformToComponent(EntityView view)
        {
            Transform t = view.Transform;
            if (t == null) return;
            RigidTransform pose = view.Data.transform;
            t.SetPositionAndRotation(pose.pos, pose.rot);
            // The pose came from the server; a later hasChanged means someone else moved it.
            t.hasChanged = false;
        }

        internal void OnSectorAdd(in SectorMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            if (view.Data.sectors.ContainsKey(message.SectorPos))
            {
                return;
            }

            view.Data.AddEmptySectorAt(message.SectorPos);
        }

        internal void OnSectorRemove(in SectorMessage message)
        {
            if (!views.TryGetValue(message.Guid, out EntityView view))
            {
                return;
            }

            if (!view.Data.sectors.ContainsKey(message.SectorPos)) return;
            SectorRemoving?.Invoke(view, message.SectorPos);
            view.Data.RemoveSectorAt(message.SectorPos);
        }

        internal void OnBrickBatch(in BrickBatchHeader header, ref NetMessageReader reader)
        {
            if (!views.TryGetValue(header.Guid, out EntityView view))
            {
                // Unknown entity: skip the payload so the reader stays consistent.
                SkipBrickBatch(header.BrickCount, ref reader);
                return;
            }

            if (!view.Data.sectors.TryGetValue(header.SectorPos, out SectorHandle handle))
            {
                view.Data.AddEmptySectorAt(header.SectorPos);
                handle = view.Data.sectors[header.SectorPos];
            }

            ref Sector sector = ref handle.Get();
            for (int i = 0; i < header.BrickCount; i++)
            {
                int brickIdx = reader.Read<ushort>();
                reader.Read<ushort>(); // server-side dirty flags; informational
                if (brickIdx < 0 || brickIdx >= Sector.BRICKS_IN_SECTOR)
                {
                    throw new System.IO.InvalidDataException($"Brick index {brickIdx} out of range.");
                }

                sector.ApplyReplicatedBrick(brickIdx, ref reader);
            }

            sector.UpdateNonEmptyBricks();
        }

        private static void SkipBrickBatch(int brickCount, ref NetMessageReader reader)
        {
            for (int i = 0; i < brickCount; i++)
            {
                reader.Read<ushort>();
                reader.Read<ushort>();
                int slots = reader.Read<byte>();
                for (int s = 0; s < slots; s++)
                {
                    reader.Read<byte>();
                    int stride = reader.Read<ushort>();
                    reader.Skip(stride * Sector.BLOCKS_IN_BRICK);
                }
            }
        }

        #endregion

        private static void DestroyViewObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        #region Frame

        /// <summary>Clears last frame's require-update flags. Call before applying messages.</summary>
        public void ClearRequireUpdate()
        {
            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                view.Data.ClearRequireUpdates();
            }
        }

        /// <summary>
        /// Converts this frame's applied bricks into require-update flags for the renderers.
        /// Only Geometry bits propagate, so no sector is ever allocated here.
        /// </summary>
        public void PropagateForRender()
        {
            const DirtyFlags renderFlags =
                DirtyFlags.Geometry | DirtyFlags.GeometryWithLocalNeighbor |
                DirtyFlags.BlockBrickAdded | DirtyFlags.BlockBrickRemoved;

            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                if (view.Data.entityDirtyFlags == 0 && !AnySectorDirty(view.Data))
                {
                    continue;
                }

                view.Data.PropagateDirtyFlags(renderFlags, async: false);
            }
        }

        /// <summary>Ends the dirty lifetime of this frame's applied bricks. Call after the renderers ran.</summary>
        public void EndFrame()
        {
            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                view.Data.ClearDirtyFlags();
            }
        }

        private static bool AnySectorDirty(in VoxelEntityData data)
        {
            foreach (var kvp in data.sectors)
            {
                if (kvp.Value.Get().sectorDirtyFlags != 0) return true;
            }

            return false;
        }

        #endregion

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            for (int i = 0; i < viewList.Count; i++)
            {
                EntityView view = viewList[i];
                ViewDespawning?.Invoke(view);
                VoxelEntity component = view.Component;
                view.Component = null;
                if (component != null)
                {
                    component.UnbindView();
                    if (view.IsClientSpawned && component.gameObject != null)
                    {
                        DestroyViewObject(component.gameObject);
                    }
                }

                view.Data.Dispose();
            }

            viewList.Clear();
            views.Clear();
            pendingAuthored.Clear();
        }
    }

}
