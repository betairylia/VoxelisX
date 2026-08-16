using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace Voxelis.Authoring
{
    public enum VoxelSplineMode
    {
        Single,
        ParallelRails,
    }

    /// <summary>
    /// Converts a Unity spline to one or two sampled center lines. Parallel paths use a stable
    /// frame, mitered corners, and a bevel fallback when a corner exceeds the miter limit.
    /// </summary>
    public static class VoxelSplinePathBuilder
    {
        private const float DirectionEpsilon = 0.000001f;

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
            List<List<Vector3>> output)
        {
            if (spline == null || spline.Count < 2)
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
            float safeMiterLimit = Mathf.Max(1f, miterLimit);
            BuildOffsetPath(samples, spline.Closed, halfSeparation, safeMiterLimit, output);
            BuildOffsetPath(samples, spline.Closed, -halfSeparation, safeMiterLimit, output);
        }

        private static void Sample(Spline spline, float sampleSpacing, List<PathSample> samples)
        {
            int curveCount = spline.GetCurveCount();
            for (int curveIndex = 0; curveIndex < curveCount; curveIndex++)
            {
                float curveLength = spline.GetCurveLength(curveIndex);
                int subdivisions = Mathf.Max(1, Mathf.CeilToInt(curveLength / sampleSpacing));
                int firstStep = curveIndex == 0 ? 0 : 1;

                for (int step = firstStep; step <= subdivisions; step++)
                {
                    float curveT = step / (float)subdivisions;
                    float splineT = spline.CurveToSplineT(curveIndex + curveT);
                    if (!SplineUtility.Evaluate(spline, splineT, out float3 position, out _, out float3 up))
                    {
                        continue;
                    }

                    Vector3 safeUp = math.lengthsq(up) > DirectionEpsilon
                        ? (Vector3)math.normalize(up)
                        : Vector3.up;
                    samples.Add(new PathSample(position, safeUp));
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
            Vector3 previousRight = Vector3.zero;

            for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
            {
                int nextIndex = (segmentIndex + 1) % pointCount;
                Vector3 direction = samples[nextIndex].Position - samples[segmentIndex].Position;
                if (direction.sqrMagnitude < DirectionEpsilon)
                {
                    rights[segmentIndex] = previousRight == Vector3.zero ? Vector3.right : previousRight;
                    continue;
                }

                direction.Normalize();
                Vector3 up = samples[segmentIndex].Up + samples[nextIndex].Up;
                if (up.sqrMagnitude < DirectionEpsilon)
                {
                    up = samples[segmentIndex].Up;
                }

                Vector3 right = Vector3.Cross(up.normalized, direction);
                if (right.sqrMagnitude < DirectionEpsilon && previousRight != Vector3.zero)
                {
                    right = Vector3.ProjectOnPlane(previousRight, direction);
                }
                if (right.sqrMagnitude < DirectionEpsilon)
                {
                    Vector3 fallbackUp = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) < 0.99f
                        ? Vector3.up
                        : Vector3.forward;
                    right = Vector3.Cross(fallbackUp, direction);
                }

                right.Normalize();
                if (previousRight != Vector3.zero && Vector3.Dot(right, previousRight) < 0f)
                {
                    right = -right;
                }

                rights[segmentIndex] = right;
                previousRight = right;
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
                    offset,
                    miterLimit);
            }

            if (closed && path.Count > 0)
            {
                path.Add(path[0]);
            }

            output.Add(path);
        }

        private static void AppendCorner(
            List<Vector3> path,
            Vector3 center,
            Vector3 incomingRight,
            Vector3 outgoingRight,
            float offset,
            float miterLimit)
        {
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
    }
}
