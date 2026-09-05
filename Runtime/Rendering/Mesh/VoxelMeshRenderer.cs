using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;

namespace Caelix.Rendering.Meshing
{
    /// <summary>
    /// Main coordinator for mesh-based voxel rendering.
    /// Tracks the <see cref="EntityView"/>s of a <see cref="ClientWorld"/> and manages one
    /// <see cref="SectorMeshRenderer"/> per sector.
    /// </summary>
    public class VoxelMeshRenderer : IDisposable
    {
        // Settings
        private readonly int chunkSize;
        private readonly Material material;

        // View tracking
        private readonly HashSet<EntityView> trackedViews = new HashSet<EntityView>();
        private readonly Dictionary<(EntityView, int3), SectorMeshRenderer> sectorRenderers =
            new Dictionary<(EntityView, int3), SectorMeshRenderer>();

        // Cached lists to avoid allocations
        private readonly List<(EntityView, int3)> sectorsToRemove = new List<(EntityView, int3)>();
        private readonly List<EntityView> viewsToRemove = new List<EntityView>();
        private readonly HashSet<EntityView> currentViews = new HashSet<EntityView>();

        /// <summary>The client world whose views are rendered. Nothing renders until it is set.</summary>
        private ClientWorld source;
        public ClientWorld Source
        {
            get => source;
            set
            {
                if (ReferenceEquals(source, value)) return;
                if (source != null)
                {
                    source.ViewDespawning -= RemoveView;
                    source.SectorRemoving -= RemoveSectorRenderer;
                    source.Owner.WorldRemoving -= OnWorldRemoving;
                }
                ReleaseRenderers();
                source = value != null && !value.IsDisposed ? value : null;
                if (source != null)
                {
                    source.ViewDespawning += RemoveView;
                    source.SectorRemoving += RemoveSectorRenderer;
                    source.Owner.WorldRemoving += OnWorldRemoving;
                }
            }
        }

        private void OnWorldRemoving(ClientWorld world)
        {
            if (ReferenceEquals(source, world)) Source = null;
        }

        /// <summary>
        /// Gets the number of currently tracked views.
        /// </summary>
        public int TrackedEntityCount => trackedViews.Count;

        /// <summary>
        /// Gets the total number of sector renderers.
        /// </summary>
        public int SectorRendererCount => sectorRenderers.Count;

        public VoxelMeshRenderer(int chunkSize, Material material)
        {
            this.chunkSize = chunkSize;
            this.material = material;
        }

        /// <summary>
        /// Updates all mesh renderers. Called once per frame.
        /// Implements two-phase update: schedule jobs, then complete and apply.
        /// </summary>
        public void Update()
        {
            if (Source == null)
            {
                return;
            }

            // Discover and track new views
            DiscoverViews();
            RemoveMissingSectors();

            // Phase 1: Schedule mesh generation jobs for all invalidated chunks
            JobHandle meshJobs = default;
            bool hasMeshJobs = false;

            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.ScheduleJobs();
                if (kvp.Value.TryGetScheduledJobHandle(out JobHandle sectorJobs))
                {
                    meshJobs = JobHandle.CombineDependencies(meshJobs, sectorJobs);
                    hasMeshJobs = true;
                }
            }

            if (hasMeshJobs)
            {
                meshJobs.Complete();
            }

            // Phase 2: Apply meshes after the combined barrier
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.ApplyCompletedJobs();
            }

            // requireUpdate cleanup is owned by the client frame after consumers have read it.

        }

        /// <summary>
        /// Tracks the views of the source world and drops the ones that despawned.
        /// </summary>
        private void DiscoverViews()
        {
            currentViews.Clear();
            IReadOnlyList<EntityView> views = Source.Views;
            for (int i = 0; i < views.Count; i++)
            {
                EntityView view = views[i];
                if (view.Transform == null)
                {
                    continue; // no scene object to parent meshes under yet
                }

                currentViews.Add(view);
                if (!trackedViews.Contains(view))
                {
                    AddView(view);
                }
            }

            viewsToRemove.Clear();
            foreach (EntityView view in trackedViews)
            {
                if (!currentViews.Contains(view))
                {
                    viewsToRemove.Add(view);
                }
            }

            for (int i = 0; i < viewsToRemove.Count; i++)
            {
                RemoveView(viewsToRemove[i]);
            }
        }

        private void AddView(EntityView view)
        {
            trackedViews.Add(view);

            // Create renderers for all existing sectors
            var sectorPositions = view.Data.sectors.GetKeyArray(Unity.Collections.Allocator.Temp);
            foreach (var sectorPos in sectorPositions)
            {
                if (view.Data.sectors.TryGetValue(sectorPos, out var sectorHandle))
                {
                    AddSectorRenderer(view, sectorPos, sectorHandle);
                }
            }

            sectorPositions.Dispose();
        }

        private void RemoveView(EntityView view)
        {
            trackedViews.Remove(view);

            // Remove all sector renderers for this view
            sectorsToRemove.Clear();
            foreach (var kvp in sectorRenderers)
            {
                if (kvp.Key.Item1 == view)
                {
                    sectorsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in sectorsToRemove)
            {
                RemoveSectorRenderer(key.Item1, key.Item2);
            }
        }

        private void AddSectorRenderer(EntityView view, int3 sectorPos, SectorHandle sectorHandle)
        {
            var key = (view, sectorPos);
            if (sectorRenderers.ContainsKey(key))
                return;

            var renderer = new SectorMeshRenderer(
                sectorHandle,
                sectorPos,
                chunkSize,
                material,
                view.Transform
            );

            sectorRenderers[key] = renderer;
        }

        private void RemoveSectorRenderer(EntityView view, int3 sectorPos)
        {
            var key = (view, sectorPos);
            if (sectorRenderers.TryGetValue(key, out var renderer))
            {
                renderer.Dispose();
                sectorRenderers.Remove(key);
            }
        }

        /// <summary>
        /// Checks for new sectors in tracked views and removes sectors that no longer exist.
        /// </summary>
        private void RemoveMissingSectors()
        {
            sectorsToRemove.Clear();
            foreach (var kvp in sectorRenderers)
            {
                EntityView view = kvp.Key.Item1;
                int3 sectorPos = kvp.Key.Item2;

                if (!trackedViews.Contains(view) || !view.Data.sectors.IsCreated || !view.Data.sectors.ContainsKey(sectorPos))
                {
                    sectorsToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in sectorsToRemove)
            {
                RemoveSectorRenderer(key.Item1, key.Item2);
            }

            // Add new sectors from tracked views
            foreach (EntityView view in trackedViews)
            {
                if (!view.Data.sectors.IsCreated)
                    continue;

                var sectorPositions = view.Data.sectors.GetKeyArray(Unity.Collections.Allocator.Temp);
                foreach (var sectorPos in sectorPositions)
                {
                    var key = (view, sectorPos);
                    if (!sectorRenderers.ContainsKey(key))
                    {
                        if (view.Data.sectors.TryGetValue(sectorPos, out var sectorHandle))
                        {
                            AddSectorRenderer(view, sectorPos, sectorHandle);
                        }
                    }
                }

                sectorPositions.Dispose();
            }
        }

        /// <summary>
        /// Forces regeneration of all meshes.
        /// </summary>
        public void RegenerateAll()
        {
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.MarkAllDirty();
            }
        }

        /// <summary>
        /// Cleanup all resources.
        /// </summary>
        public void Dispose()
        {
            Source = null;
            ReleaseRenderers();
        }

        private void ReleaseRenderers()
        {
            foreach (var kvp in sectorRenderers)
            {
                kvp.Value.Dispose();
            }

            sectorRenderers.Clear();
            trackedViews.Clear();
        }
    }
}
