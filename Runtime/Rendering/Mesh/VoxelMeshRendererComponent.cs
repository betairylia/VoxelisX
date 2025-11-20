using System;
using UnityEngine;

namespace Voxelis.Rendering.Meshing
{
    /// <summary>
    /// MonoBehaviour component for managing mesh-based voxel rendering.
    /// Automatically discovers and renders all VoxelEntity instances in the scene.
    /// This provides a fallback rendering method for platforms that don't support ray tracing (e.g., macOS).
    /// </summary>
    [AddComponentMenu("Voxelis/Voxel Mesh Renderer")]
    public class VoxelMeshRendererComponent : MonoBehaviour
    {
        [Header("Mesh Settings")]
        [Tooltip("Size of each mesh chunk (default 32). Sectors are subdivided into chunks of this size.")]
        [Range(8, 128)]
        [SerializeField] private int chunkSize = 32;

        [Tooltip("Material used for rendering voxel meshes. Should use VoxelMesh shader.")]
        [SerializeField] private Material material;

        [Header("Runtime Info")]
        [Tooltip("Number of VoxelEntity instances currently being rendered")]
        [SerializeField, ReadOnly] private int trackedEntityCount;

        [Tooltip("Total number of sector renderers active")]
        [SerializeField, ReadOnly] private int sectorRendererCount;

        [Header("Debug")]
        [Tooltip("Regenerate all meshes on the next frame")]
        [SerializeField] private bool regenerateAll = false;
        
        [SerializeField] private bool autoTick = false;

        private VoxelMeshRenderer meshRenderer;

        /// <summary>
        /// Gets or sets the chunk size. Changes will take effect after regenerating meshes.
        /// </summary>
        public int ChunkSize
        {
            get => chunkSize;
            set
            {
                if (chunkSize != value && value >= 8 && value <= 128)
                {
                    chunkSize = value;
                    Reinitialize();
                }
            }
        }

        /// <summary>
        /// Gets or sets the material used for rendering.
        /// </summary>
        public Material Material
        {
            get => material;
            set
            {
                if (material != value)
                {
                    material = value;
                    Reinitialize();
                }
            }
        }

        /// <summary>
        /// Gets the underlying mesh renderer.
        /// </summary>
        public VoxelMeshRenderer MeshRenderer => meshRenderer;

        private void OnValidate()
        {
            // Ensure chunk size is a valid value
            if (chunkSize < 8) chunkSize = 8;
            if (chunkSize > 128) chunkSize = 128;

            // Warn if no material assigned
            if (material == null)
            {
                Debug.LogWarning("VoxelMeshRendererComponent: No material assigned. Please assign a material that uses the VoxelMesh shader.", this);
            }
        }

        private void Awake()
        {
            Initialize();
        }

        private void OnEnable()
        {
            if (meshRenderer == null)
            {
                Initialize();
            }
        }

        private void Update()
        {
            if(autoTick) Tick();
        }

        public void Tick()
        {
            if (meshRenderer == null)
            {
                Debug.LogError("VoxelMeshRendererComponent: MeshRenderer is null. Reinitializing...", this);
                Initialize();
                return;
            }

            // Handle regenerate request
            if (regenerateAll)
            {
                regenerateAll = false;
                meshRenderer.RegenerateAll();
            }

            // Update mesh renderer
            meshRenderer.Update();

            // Update runtime info for inspector
            trackedEntityCount = meshRenderer.TrackedEntityCount;
            sectorRendererCount = meshRenderer.SectorRendererCount;
        }

        private void OnDisable()
        {
            Cleanup();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        /// <summary>
        /// Initializes the mesh renderer system.
        /// </summary>
        private void Initialize()
        {
            if (material == null)
            {
                Debug.LogError("VoxelMeshRendererComponent: Cannot initialize without a material assigned.", this);
                return;
            }

            Cleanup();

            meshRenderer = new VoxelMeshRenderer(chunkSize, material);

            Debug.Log($"VoxelMeshRendererComponent: Initialized with chunk size {chunkSize}");
        }

        /// <summary>
        /// Reinitializes the mesh renderer (used when settings change).
        /// </summary>
        private void Reinitialize()
        {
            if (!isActiveAndEnabled)
                return;

            Debug.Log("VoxelMeshRendererComponent: Reinitializing due to settings change...");
            Initialize();
        }

        /// <summary>
        /// Cleans up the mesh renderer system.
        /// </summary>
        private void Cleanup()
        {
            if (meshRenderer != null)
            {
                meshRenderer.Dispose();
                meshRenderer = null;
            }
        }

        /// <summary>
        /// Forces regeneration of all meshes.
        /// </summary>
        [ContextMenu("Regenerate All Meshes")]
        public void RegenerateAllMeshes()
        {
            if (meshRenderer != null)
            {
                meshRenderer.RegenerateAll();
                Debug.Log("VoxelMeshRendererComponent: Regenerating all meshes...");
            }
        }

        /// <summary>
        /// Custom attribute for read-only fields in inspector.
        /// </summary>
        private class ReadOnlyAttribute : PropertyAttribute { }
    }
}
