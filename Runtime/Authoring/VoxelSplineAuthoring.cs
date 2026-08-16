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
        [Header("Spline Shape")]
        [SerializeField] private VoxelSplineMode mode = VoxelSplineMode.ParallelRails;
        [SerializeField, Min(0.5f)] private float radius = 0.5f;
        [SerializeField] private bool pixelPerfect = true;
        [SerializeField, Min(0.5f)] private float parallelSeparation = 3f;
        [SerializeField, Min(1f)] private float cornerMiterLimit = 4f;
        [SerializeField, Min(0.05f)] private float sampleSpacing = 0.25f;

        [Header("Voxel Material")]
        [SerializeField] private Block block = new Block(31, 12, 24, false);

        [Header("Live Editing")]
        [SerializeField] private bool autoRebuild = true;

        private readonly List<List<Vector3>> paths = new();
        private SplineContainer splineContainer;

        public SplineContainer Container => splineContainer != null
            ? splineContainer
            : splineContainer = GetComponent<SplineContainer>();

        public VoxelSplineMode Mode => mode;
        public float Radius => radius;
        public bool PixelPerfect => pixelPerfect;
        public float ParallelSeparation => parallelSeparation;
        public float CornerMiterLimit => cornerMiterLimit;
        public float SampleSpacing => sampleSpacing;
        public Block Block => block;
        public bool AutoRebuild => autoRebuild;

        protected override Block MaterialBlock => block;

        protected override void Reset()
        {
            base.Reset();
            EnsureDefaultSpline();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Spline.Changed += OnSplineChanged;
        }

        private void OnDisable()
        {
            Spline.Changed -= OnSplineChanged;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            radius = Mathf.Max(0.5f, radius);
            parallelSeparation = Mathf.Max(0.5f, parallelSeparation);
            cornerMiterLimit = Mathf.Max(1f, cornerMiterLimit);
            sampleSpacing = Mathf.Max(0.05f, sampleSpacing);
            base.OnValidate();
        }
#endif

        public override void CollectAuthoringStateComponents(List<Component> components)
        {
            base.CollectAuthoringStateComponents(components);
            components.Add(Container);
        }

        /// <summary>Builds local-space center lines for Scene view preview and voxelization.</summary>
        public void BuildPreviewPaths(List<List<Vector3>> output)
        {
            output.Clear();
            IReadOnlyList<Spline> splines = Container.Splines;
            for (int splineIndex = 0; splineIndex < splines.Count; splineIndex++)
            {
                VoxelSplinePathBuilder.Build(
                    splines[splineIndex],
                    sampleSpacing,
                    mode,
                    parallelSeparation,
                    cornerMiterLimit,
                    output);
            }
        }

        protected override void BuildVoxelSet(HashSet<Vector3Int> voxels)
        {
            BuildPreviewPaths(paths);
            VoxelSplineVoxelizer.Rasterize(paths, radius, pixelPerfect, voxels);
        }

        private void EnsureDefaultSpline()
        {
            Spline spline = Container.Spline;
            if (spline != null && spline.Count >= 2)
            {
                return;
            }

            Container.Spline = new Spline(
                new[]
                {
                    new float3(0f, 0f, 0f),
                    new float3(0f, 0f, 8f),
                },
                TangentMode.AutoSmooth);
        }

        private void OnSplineChanged(Spline changedSpline, int knotIndex, SplineModification modification)
        {
            if (!autoRebuild)
            {
                return;
            }

            IReadOnlyList<Spline> splines = Container.Splines;
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
