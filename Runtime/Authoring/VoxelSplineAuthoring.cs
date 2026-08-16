using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace Voxelis.Authoring
{
    /// <summary>Scene-authored voxel spline or parallel rail generator.</summary>
    [AddComponentMenu("VoxelisX/Authoring/Voxel Spline")]
    [RequireComponent(typeof(SplineContainer))]
    public sealed class VoxelSplineAuthoring : VoxelEntityAuthoringTool
    {
        /// <summary>Opaque default material, matching the voxel file loaders' plain block.</summary>
        public const ushort DefaultBlockId = 0x8000;

        [Header("Spline Shape")]
        [SerializeField] private VoxelSplineMode mode = VoxelSplineMode.ParallelRails;
        [SerializeField, Min(0.5f)] private float radius = 0.5f;
        [SerializeField] private bool pixelPerfect = true;
        [SerializeField, Min(0.5f)] private float parallelSeparation = 3f;
        [Tooltip("Place both rails exactly on voxel centers, mirrored around the spline. Voxel centers sit on half-integers, so this snaps the spacing to an odd number of voxels.")]
        [SerializeField] private bool snapRailsToGrid = true;
        [SerializeField, Min(1f)] private float cornerMiterLimit = 4f;
        [SerializeField, Min(0.05f)] private float sampleSpacing = 0.25f;

        [Header("Voxel Material")]
        [Tooltip("Raw 16-bit block id written into the entity. Bit 15 (0x8000) marks an opaque block; 0 is empty.")]
        [SerializeField] private ushort blockId = DefaultBlockId;

        [Header("Live Editing")]
        [SerializeField] private bool autoRebuild = true;

        private readonly List<List<Vector3>> paths = new();
        private SplineContainer splineContainer;
        private bool splineChangedSubscribed;

        public SplineContainer Container
        {
            get
            {
                if (splineContainer == null)
                {
                    splineContainer = GetComponent<SplineContainer>();
                }

                return splineContainer;
            }
        }

        public VoxelSplineMode Mode => mode;
        public float Radius => radius;
        public bool PixelPerfect => pixelPerfect;
        public float ParallelSeparation => parallelSeparation;
        public bool SnapRailsToGrid => snapRailsToGrid;
        public float CornerMiterLimit => cornerMiterLimit;

        /// <summary>The rail spacing the generator actually uses, in voxels.</summary>
        public float EffectiveRailSeparation
        {
            get
            {
                float half = Mathf.Max(0.5f, parallelSeparation) * 0.5f;
                return 2f * (snapRailsToGrid ? VoxelSplinePathBuilder.SnapHalfSeparation(half) : half);
            }
        }
        public float SampleSpacing => sampleSpacing;
        public ushort BlockId => blockId;
        public bool AutoRebuild => autoRebuild;

        protected override Block MaterialBlock => new Block(blockId);

        protected override void Reset()
        {
            base.Reset();
            EnsureDefaultSpline();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (!splineChangedSubscribed)
            {
                Spline.Changed += OnSplineChanged;
                splineChangedSubscribed = true;
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void OnDestroy()
        {
            // Spline.Changed is a static event; a leaked subscription would keep invoking this
            // component after Unity destroyed it.
            Unsubscribe();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            radius = Mathf.Clamp(radius, 0.5f, VoxelSplineVoxelizer.MaxRadius);
            parallelSeparation = Mathf.Max(0.5f, parallelSeparation);
            cornerMiterLimit = Mathf.Max(1f, cornerMiterLimit);
            sampleSpacing = Mathf.Max(0.05f, sampleSpacing);
            base.OnValidate();
        }
#endif

        public override void CollectAuthoringStateComponents(List<Component> components)
        {
            base.CollectAuthoringStateComponents(components);

            SplineContainer container = Container;
            if (container != null)
            {
                components.Add(container);
            }
        }

        /// <summary>Builds local-space center lines for Scene view preview and voxelization.</summary>
        public void BuildPreviewPaths(List<List<Vector3>> output)
        {
            if (output == null)
            {
                return;
            }

            output.Clear();

            SplineContainer container = Container;
            if (container == null)
            {
                return;
            }

            IReadOnlyList<Spline> splines = container.Splines;
            if (splines == null)
            {
                return;
            }

            for (int splineIndex = 0; splineIndex < splines.Count; splineIndex++)
            {
                Spline spline = splines[splineIndex];
                if (spline == null)
                {
                    continue;
                }

                VoxelSplinePathBuilder.Build(
                    spline,
                    sampleSpacing,
                    mode,
                    parallelSeparation,
                    cornerMiterLimit,
                    output,
                    snapRailsToGrid);
            }
        }

        protected override void BuildVoxelSet(HashSet<Vector3Int> voxels)
        {
            BuildPreviewPaths(paths);
            if (!VoxelSplineVoxelizer.Rasterize(paths, radius, pixelPerfect, voxels))
            {
                Debug.LogWarning(
                    $"'{name}' produced more than {VoxelSplineVoxelizer.MaxVoxels} voxels or contains a degenerate " +
                    "spline; the generated result is truncated. Reduce the radius, the spline length, or the sample spacing.",
                    this);
            }
        }

        private void Unsubscribe()
        {
            if (!splineChangedSubscribed)
            {
                return;
            }

            Spline.Changed -= OnSplineChanged;
            splineChangedSubscribed = false;
        }

        private void EnsureDefaultSpline()
        {
            SplineContainer container = Container;
            if (container == null)
            {
                return;
            }

            Spline spline = container.Spline;
            if (spline != null && spline.Count >= 2)
            {
                return;
            }

#if UNITY_EDITOR
            // Reset runs inside the editor's undo group for adding this component. Recording the
            // container keeps the default spline part of that same single undo step.
            UnityEditor.Undo.RecordObject(container, "Create Voxel Spline");
#endif

            container.Spline = new Spline(
                new[]
                {
                    new float3(0f, 0f, 0f),
                    new float3(0f, 0f, 8f),
                },
                TangentMode.AutoSmooth);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(container);
#endif
        }

        private void OnSplineChanged(Spline changedSpline, int knotIndex, SplineModification modification)
        {
            if (this == null || !autoRebuild || changedSpline == null)
            {
                return;
            }

            SplineContainer container = Container;
            if (container == null)
            {
                return;
            }

            IReadOnlyList<Spline> splines = container.Splines;
            if (splines == null)
            {
                return;
            }

            for (int splineIndex = 0; splineIndex < splines.Count; splineIndex++)
            {
                if (ReferenceEquals(splines[splineIndex], changedSpline))
                {
                    RequestRebuild();
                    return;
                }
            }
        }
    }
}
