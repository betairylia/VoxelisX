using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace Caelix.Authoring
{
    public enum VoxelSplineMode
    {
        Single,
        ParallelRails,
    }

    /// <summary>
    /// Converts a Unity spline to one or two sampled center lines. Parallel paths use a
    /// rotation-minimizing frame, mitered corners, and a bevel fallback when a corner exceeds the
    /// miter limit.
    /// </summary>
    public static class VoxelSplinePathBuilder
    {
        /// <summary>Largest number of samples one spline may contribute.</summary>
        public const int MaxSamplesPerSpline = 1 << 16;

        private const int MaxSubdivisionsPerCurve = 1 << 12;
        private const float DirectionEpsilon = 1e-12f;

        // Below this |sin| between the spline up vector and the travel direction the up vector says
        // nothing useful about the offset plane, so the transported frame carries the rail instead.
        private const float UpConditionEpsilon = 0.2f;

        private readonly struct PathSample
        {
            public readonly Vector3 Position;
            public readonly Vector3 Up;

            public PathSample(Vector3 position, Vector3 up)
            {
                Position = position;
                Up = up;
            }
        }

        public static void Build(
            Spline spline,
            float sampleSpacing,
            VoxelSplineMode mode,
            float parallelSeparation,
            float miterLimit,
            List<List<Vector3>> output,
            bool snapSeparationToGrid = false)
        {
            if (spline == null || output == null || spline.Count < 2)
            {
                return;
            }

            var samples = new List<PathSample>();
            Sample(spline, Mathf.Max(0.05f, sampleSpacing), samples);
            if (samples.Count < 2)
            {
                return;
            }

            if (mode == VoxelSplineMode.Single)
            {
                var path = new List<Vector3>(samples.Count);
                for (int i = 0; i < samples.Count; i++)
                {
                    path.Add(samples[i].Position);
                }

                output.Add(path);
                return;
            }

            float halfSeparation = Mathf.Max(0.5f, parallelSeparation) * 0.5f;
            if (snapSeparationToGrid)
            {
                halfSeparation = SnapHalfSeparation(halfSeparation);
            }

            float safeMiterLimit = Mathf.Max(1f, miterLimit);
            BuildOffsetPath(samples, spline.Closed, halfSeparation, safeMiterLimit, output);
            BuildOffsetPath(samples, spline.Closed, -halfSeparation, safeMiterLimit, output);
        }

        /// <summary>
        /// Snaps a half separation onto the voxel-center lattice.
        /// </summary>
        /// <remarks>
        /// Voxel centers sit on half-integers, so a rail offset by a whole number of voxels from a
        /// grid-aligned path lands on a cell boundary: the two rails then round to the same side and
        /// the pair as a whole sits half a voxel off center, which reads as one rail pulled in and
        /// the other pushed out. An odd number of half-voxels puts both rails exactly on cell
        /// centers, mirrored — the same reason a symmetric pixel-art shape around a one-pixel center
        /// line has an odd width. The usable rail spacings are therefore the odd voxel counts.
        /// </remarks>
        public static float SnapHalfSeparation(float halfSeparation)
        {
            // floor + 0.5 is the nearest half-integer for every non-integer input and rounds the
            // even spacings consistently up, so the control never jitters between N-1 and N+1.
            return Mathf.Max(0.5f, Mathf.Floor(halfSeparation) + 0.5f);
        }

        private static void Sample(Spline spline, float sampleSpacing, List<PathSample> samples)
        {
            int curveCount = spline.GetCurveCount();
            for (int curveIndex = 0; curveIndex < curveCount; curveIndex++)
            {
                float curveLength = spline.GetCurveLength(curveIndex);
                if (float.IsNaN(curveLength) || float.IsInfinity(curveLength) || curveLength < 0f)
                {
                    curveLength = 0f;
                }

                int subdivisions = Mathf.Clamp(
                    Mathf.CeilToInt(curveLength / sampleSpacing),
                    1,
                    MaxSubdivisionsPerCurve);
                int firstStep = curveIndex == 0 ? 0 : 1;

                for (int step = firstStep; step <= subdivisions; step++)
                {
                    float curveT = step / (float)subdivisions;
                    float splineT = spline.CurveToSplineT(curveIndex + curveT);
                    if (!SplineUtility.Evaluate(spline, splineT, out float3 position, out _, out float3 up))
                    {
                        continue;
                    }

                    if (!IsFinite(position))
                    {
                        continue;
                    }

                    Vector3 safeUp = math.lengthsq(up) > DirectionEpsilon && IsFinite(up)
                        ? (Vector3)math.normalize(up)
                        : Vector3.up;
                    samples.Add(new PathSample(position, safeUp));

                    if (samples.Count >= MaxSamplesPerSpline)
                    {
                        return;
                    }
                }
            }
        }

        private static void BuildOffsetPath(
            List<PathSample> sourceSamples,
            bool closed,
            float offset,
            float miterLimit,
            List<List<Vector3>> output)
        {
            var samples = new List<PathSample>(sourceSamples);
            if (closed && samples.Count > 1 &&
                (samples[0].Position - samples[samples.Count - 1].Position).sqrMagnitude < DirectionEpsilon)
            {
                samples.RemoveAt(samples.Count - 1);
            }

            int pointCount = samples.Count;
            if (pointCount < 2)
            {
                return;
            }

            int segmentCount = closed ? pointCount : pointCount - 1;
            var rights = new Vector3[segmentCount];
            var directions = new Vector3[segmentCount];
            if (!BuildOffsetFrames(samples, rights, directions))
            {
                return;
            }

            var path = new List<Vector3>(pointCount + 4);
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                if (!closed && pointIndex == 0)
                {
                    path.Add(samples[pointIndex].Position + rights[0] * offset);
                    continue;
                }

                if (!closed && pointIndex == pointCount - 1)
                {
                    path.Add(samples[pointIndex].Position + rights[segmentCount - 1] * offset);
                    continue;
                }

                int incomingIndex = (pointIndex - 1 + segmentCount) % segmentCount;
                int outgoingIndex = pointIndex % segmentCount;
                AppendCorner(
                    path,
                    samples[pointIndex].Position,
                    rights[incomingIndex],
                    rights[outgoingIndex],
                    directions[incomingIndex],
                    offset,
                    miterLimit);
            }

            if (closed && path.Count > 0)
            {
                path.Add(path[0]);
            }

            output.Add(path);
        }

        /// <summary>
        /// Computes one unit offset direction per segment.
        /// </summary>
        /// <remarks>
        /// The frame is parallel transported along the path — each segment rotates the previous
        /// offset direction by the minimal rotation between the two travel directions — and the
        /// spline's own up vector then picks the exact direction whenever it is well conditioned.
        /// Transport is what disambiguates the sign: the up vector flips as the path passes through
        /// vertical, and comparing the fresh direction against the *previous segment's* direction
        /// instead (as a naive continuity fix does) inverts the rails on any turn sharper than 90
        /// degrees, which crosses the two rails over each other and folds a bar between them.
        /// </remarks>
        private static bool BuildOffsetFrames(List<PathSample> samples, Vector3[] rights, Vector3[] directions)
        {
            int pointCount = samples.Count;
            int segmentCount = rights.Length;

            int firstValid = -1;
            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                int nextIndex = (segmentIndex + 1) % pointCount;
                Vector3 direction = samples[nextIndex].Position - samples[segmentIndex].Position;
                if (direction.sqrMagnitude < DirectionEpsilon)
                {
                    directions[segmentIndex] = Vector3.zero;
                    continue;
                }

                directions[segmentIndex] = direction.normalized;
                if (firstValid < 0)
                {
                    firstValid = segmentIndex;
                }
            }

            if (firstValid < 0)
            {
                // Every sample sits on the same point; there is no path to offset.
                return false;
            }

            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                if (directions[segmentIndex] == Vector3.zero)
                {
                    directions[segmentIndex] = segmentIndex > 0
                        ? directions[segmentIndex - 1]
                        : directions[firstValid];
                }
            }

            Vector3 previousRight = Vector3.zero;
            Vector3 previousDirection = Vector3.zero;

            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                Vector3 direction = directions[segmentIndex];
                Vector3 right;

                if (previousRight == Vector3.zero)
                {
                    right = FromUpVector(SegmentUp(samples, segmentIndex, pointCount), direction, Vector3.zero);
                }
                else
                {
                    Vector3 transported = Quaternion.FromToRotation(previousDirection, direction) * previousRight;
                    transported = Vector3.ProjectOnPlane(transported, direction);
                    transported = transported.sqrMagnitude > DirectionEpsilon
                        ? transported.normalized
                        : AnyPerpendicular(direction);
                    right = FromUpVector(SegmentUp(samples, segmentIndex, pointCount), direction, transported);
                }

                rights[segmentIndex] = right;
                previousRight = right;
                previousDirection = direction;
            }

            return true;
        }

        private static Vector3 SegmentUp(List<PathSample> samples, int segmentIndex, int pointCount)
        {
            int nextIndex = (segmentIndex + 1) % pointCount;
            Vector3 up = samples[segmentIndex].Up + samples[nextIndex].Up;
            return up.sqrMagnitude > DirectionEpsilon ? up.normalized : samples[segmentIndex].Up;
        }

        /// <summary>
        /// Returns the offset direction for one segment, preferring the spline's up vector and
        /// falling back to <paramref name="transported"/> where up and travel are near parallel.
        /// </summary>
        private static Vector3 FromUpVector(Vector3 up, Vector3 direction, Vector3 transported)
        {
            Vector3 right = Vector3.Cross(up, direction);
            if (right.magnitude < UpConditionEpsilon)
            {
                return transported != Vector3.zero ? transported : AnyPerpendicular(direction);
            }

            right.Normalize();
            if (transported != Vector3.zero && Vector3.Dot(right, transported) < 0f)
            {
                right = -right;
            }

            return right;
        }

        private static Vector3 AnyPerpendicular(Vector3 direction)
        {
            Vector3 helper = Mathf.Abs(direction.y) < 0.9f ? Vector3.up : Vector3.forward;
            Vector3 perpendicular = Vector3.Cross(helper, direction);
            return perpendicular.sqrMagnitude > DirectionEpsilon
                ? perpendicular.normalized
                : Vector3.right;
        }

        private static void AppendCorner(
            List<Vector3> path,
            Vector3 center,
            Vector3 incomingRight,
            Vector3 outgoingRight,
            Vector3 incomingDirection,
            float offset,
            float miterLimit)
        {
            if (Vector3.Dot(incomingRight, outgoingRight) < 0f)
            {
                // The path turns back on itself. A straight bevel would cut across the center line
                // and weld this rail to its partner, so walk around the corner at offset distance.
                path.Add(center + incomingRight * offset);
                path.Add(center + incomingDirection * Mathf.Abs(offset));
                path.Add(center + outgoingRight * offset);
                return;
            }

            Vector3 miter = incomingRight + outgoingRight;
            if (miter.sqrMagnitude > DirectionEpsilon)
            {
                miter.Normalize();
                float denominator = Mathf.Abs(Vector3.Dot(miter, outgoingRight));
                if (denominator > 0.0001f)
                {
                    float miterRatio = 1f / denominator;
                    if (miterRatio <= miterLimit)
                    {
                        path.Add(center + miter * (offset / denominator));
                        return;
                    }
                }
            }

            // Bevel fallback. Two points keep each rail connected without an unbounded miter spike.
            path.Add(center + incomingRight * offset);
            path.Add(center + outgoingRight * offset);
        }

        private static bool IsFinite(float3 value)
        {
            return math.all(math.isfinite(value)) && math.all(math.abs(value) < 1e7f);
        }
    }
}
