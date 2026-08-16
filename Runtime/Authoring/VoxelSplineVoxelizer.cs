using System.Collections.Generic;
using UnityEngine;

namespace Voxelis.Authoring
{
    /// <summary>Rasterizes sampled paths into solid voxel pencil strokes.</summary>
    public static class VoxelSplineVoxelizer
    {
        private const float SmoothStep = 0.25f;

        public static void Rasterize(
            IReadOnlyList<List<Vector3>> paths,
            float radius,
            bool pixelPerfect,
            HashSet<Vector3Int> output)
        {
            float safeRadius = Mathf.Max(0.5f, radius);
            for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
            {
                IReadOnlyList<Vector3> path = paths[pathIndex];
                if (path.Count == 0)
                {
                    continue;
                }

                if (path.Count == 1)
                {
                    if (pixelPerfect)
                    {
                        StampPixelBrush(Vector3Int.RoundToInt(path[0]), safeRadius, output);
                    }
                    else
                    {
                        StampSmoothBrush(path[0], safeRadius, output);
                    }
                    continue;
                }

                for (int pointIndex = 1; pointIndex < path.Count; pointIndex++)
                {
                    if (pixelPerfect)
                    {
                        RasterizePixelPerfectSegment(path[pointIndex - 1], path[pointIndex], safeRadius, output);
                    }
                    else
                    {
                        RasterizeSmoothSegment(path[pointIndex - 1], path[pointIndex], safeRadius, output);
                    }
                }
            }
        }

        private static void RasterizePixelPerfectSegment(
            Vector3 start,
            Vector3 end,
            float radius,
            HashSet<Vector3Int> output)
        {
            Vector3Int from = Vector3Int.RoundToInt(start);
            Vector3Int to = Vector3Int.RoundToInt(end);
            Vector3Int delta = to - from;
            int steps = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y), Mathf.Abs(delta.z));

            if (steps == 0)
            {
                StampPixelBrush(from, radius, output);
                return;
            }

            // One rounded cell per major-axis step is the 3D equivalent of a pixel pencil line.
            // It avoids the extra corner cells produced by a super-cover rasterization.
            for (int step = 0; step <= steps; step++)
            {
                float t = step / (float)steps;
                Vector3Int center = Vector3Int.RoundToInt(Vector3.Lerp((Vector3)from, (Vector3)to, t));
                StampPixelBrush(center, radius, output);
            }
        }

        private static void RasterizeSmoothSegment(
            Vector3 start,
            Vector3 end,
            float radius,
            HashSet<Vector3Int> output)
        {
            float length = Vector3.Distance(start, end);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / SmoothStep));
            for (int step = 0; step <= steps; step++)
            {
                StampSmoothBrush(Vector3.Lerp(start, end, step / (float)steps), radius, output);
            }
        }

        private static void StampPixelBrush(
            Vector3Int center,
            float radius,
            HashSet<Vector3Int> output)
        {
            int extent = Mathf.CeilToInt(radius);
            float radiusSquared = radius * radius + 0.0001f;
            for (int z = -extent; z <= extent; z++)
            for (int y = -extent; y <= extent; y++)
            for (int x = -extent; x <= extent; x++)
            {
                var offset = new Vector3Int(x, y, z);
                if (offset.sqrMagnitude <= radiusSquared)
                {
                    output.Add(center + offset);
                }
            }
        }

        private static void StampSmoothBrush(
            Vector3 center,
            float radius,
            HashSet<Vector3Int> output)
        {
            Vector3 radiusVector = Vector3.one * radius;
            Vector3Int min = Vector3Int.FloorToInt(center - radiusVector);
            Vector3Int max = Vector3Int.CeilToInt(center + radiusVector);
            float radiusSquared = radius * radius + 0.0001f;

            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var voxel = new Vector3Int(x, y, z);
                if (((Vector3)voxel - center).sqrMagnitude <= radiusSquared)
                {
                    output.Add(voxel);
                }
            }
        }
    }
}
