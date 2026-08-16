using System.Collections.Generic;
using UnityEngine;

namespace Voxelis.Authoring
{
    /// <summary>Rasterizes sampled paths into solid voxel pencil strokes.</summary>
    /// <remarks>
    /// Voxel (i, j, k) covers the entity-local box [i, i + 1) on every axis, so the cell that owns a
    /// point is floor(point) and that cell's center sits at (i + 0.5). Rounding instead of flooring
    /// shifts every stroke half a voxel and makes symmetric offsets (parallel rails) land
    /// asymmetrically, one rail a full voxel further out than the other.
    /// </remarks>
    public static class VoxelSplineVoxelizer
    {
        /// <summary>Largest number of voxels a single rasterization may produce.</summary>
        public const int MaxVoxels = 2_000_000;

        /// <summary>Largest usable brush radius, in voxels.</summary>
        public const float MaxRadius = 64f;

        private const float SmoothStep = 0.25f;
        private const int MaxStepsPerSegment = 1 << 16;

        /// <summary>Returns the voxel that contains a local-space point.</summary>
        public static Vector3Int ToCell(Vector3 position)
        {
            return Vector3Int.FloorToInt(position);
        }

        /// <summary>Returns the local-space center of a voxel.</summary>
        public static Vector3 CellCenter(Vector3Int cell)
        {
            return new Vector3(cell.x + 0.5f, cell.y + 0.5f, cell.z + 0.5f);
        }

        /// <summary>
        /// Fills <paramref name="output"/> with the voxels covered by every path.
        /// Returns false when the result was truncated by <see cref="MaxVoxels"/> or by a
        /// degenerate path, so callers can warn instead of hanging on runaway input.
        /// </summary>
        public static bool Rasterize(
            IReadOnlyList<List<Vector3>> paths,
            float radius,
            bool pixelPerfect,
            HashSet<Vector3Int> output)
        {
            if (paths == null || output == null)
            {
                return true;
            }

            float safeRadius = Mathf.Clamp(radius, 0.5f, MaxRadius);
            List<Vector3Int> brush = pixelPerfect ? BuildBrushOffsets(safeRadius) : null;
            var line = pixelPerfect ? new List<Vector3Int>() : null;
            bool complete = true;

            for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
            {
                IReadOnlyList<Vector3> path = paths[pathIndex];
                if (path == null || path.Count == 0)
                {
                    continue;
                }

                if (pixelPerfect)
                {
                    complete &= BuildPixelPerfectLine(path, line);
                    complete &= StampBrushAlong(line, brush, output);
                }
                else
                {
                    complete &= RasterizeSmoothPath(path, safeRadius, output);
                }

                if (output.Count >= MaxVoxels)
                {
                    return false;
                }
            }

            return complete;
        }

        /// <summary>
        /// Converts a continuous path into a clean one-voxel-wide digital line.
        /// </summary>
        /// <remarks>
        /// This is the pixel-art pencil rule (Aseprite's "pixel perfect"): a cell is dropped as soon
        /// as its neighbours on both sides already touch each other, which removes the doubled
        /// corner of every L-shaped step and any one-cell spur left by a curve wobbling across a
        /// cell boundary. The result is a 26-connected staircase with no redundant cells.
        /// </remarks>
        public static bool BuildPixelPerfectLine(IReadOnlyList<Vector3> path, List<Vector3Int> line)
        {
            if (line == null)
            {
                return true;
            }

            line.Clear();
            if (path == null || path.Count == 0)
            {
                return true;
            }

            int firstIndex = FindFiniteIndex(path, 0);
            if (firstIndex < 0)
            {
                return false;
            }

            Vector3Int previousCell = ToCell(path[firstIndex]);
            AppendCell(line, previousCell);

            bool complete = firstIndex == 0;
            for (int pointIndex = firstIndex + 1; pointIndex < path.Count; pointIndex++)
            {
                Vector3 point = path[pointIndex];
                if (!IsFinite(point))
                {
                    complete = false;
                    continue;
                }

                Vector3Int cell = ToCell(point);
                complete &= AppendSegment(line, previousCell, cell);
                previousCell = cell;

                if (line.Count >= MaxVoxels)
                {
                    return false;
                }
            }

            return complete;
        }

        /// <summary>Appends one voxel per major-axis step, excluding <paramref name="from"/>.</summary>
        private static bool AppendSegment(List<Vector3Int> line, Vector3Int from, Vector3Int to)
        {
            Vector3Int delta = to - from;
            int steps = Mathf.Max(Mathf.Abs(delta.x), Mathf.Max(Mathf.Abs(delta.y), Mathf.Abs(delta.z)));
            if (steps == 0)
            {
                return true;
            }

            bool complete = steps <= MaxStepsPerSegment;
            steps = Mathf.Min(steps, MaxStepsPerSegment);

            for (int step = 1; step <= steps; step++)
            {
                AppendCell(line, new Vector3Int(
                    from.x + RoundedDivide((long)delta.x * step, steps),
                    from.y + RoundedDivide((long)delta.y * step, steps),
                    from.z + RoundedDivide((long)delta.z * step, steps)));
            }

            return complete;
        }

        private static void AppendCell(List<Vector3Int> line, Vector3Int cell)
        {
            while (true)
            {
                int count = line.Count;
                if (count == 0)
                {
                    break;
                }

                if (line[count - 1] == cell)
                {
                    return;
                }

                // The cell before the last one is not adjacent yet, so the last one carries the line.
                if (count < 2 || ChebyshevDistance(line[count - 2], cell) > 1)
                {
                    break;
                }

                line.RemoveAt(count - 1);
            }

            line.Add(cell);
        }

        private static bool StampBrushAlong(
            IReadOnlyList<Vector3Int> line,
            IReadOnlyList<Vector3Int> brush,
            HashSet<Vector3Int> output)
        {
            for (int cellIndex = 0; cellIndex < line.Count; cellIndex++)
            {
                Vector3Int cell = line[cellIndex];
                for (int offsetIndex = 0; offsetIndex < brush.Count; offsetIndex++)
                {
                    output.Add(cell + brush[offsetIndex]);
                }

                if (output.Count >= MaxVoxels)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RasterizeSmoothPath(
            IReadOnlyList<Vector3> path,
            float radius,
            HashSet<Vector3Int> output)
        {
            int firstIndex = FindFiniteIndex(path, 0);
            if (firstIndex < 0)
            {
                return false;
            }

            bool complete = firstIndex == 0;
            Vector3 previous = path[firstIndex];
            StampSmoothBrush(previous, radius, output);

            for (int pointIndex = firstIndex + 1; pointIndex < path.Count; pointIndex++)
            {
                Vector3 point = path[pointIndex];
                if (!IsFinite(point))
                {
                    complete = false;
                    continue;
                }

                float length = Vector3.Distance(previous, point);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / SmoothStep));
                if (steps > MaxStepsPerSegment)
                {
                    steps = MaxStepsPerSegment;
                    complete = false;
                }

                for (int step = 1; step <= steps; step++)
                {
                    StampSmoothBrush(Vector3.Lerp(previous, point, step / (float)steps), radius, output);
                    if (output.Count >= MaxVoxels)
                    {
                        return false;
                    }
                }

                previous = point;
            }

            return complete;
        }

        /// <summary>Builds the voxel offsets of a discrete ball brush centred on one cell.</summary>
        private static List<Vector3Int> BuildBrushOffsets(float radius)
        {
            var offsets = new List<Vector3Int>();
            int extent = Mathf.CeilToInt(radius);
            float radiusSquared = radius * radius + 0.0001f;

            for (int z = -extent; z <= extent; z++)
            for (int y = -extent; y <= extent; y++)
            for (int x = -extent; x <= extent; x++)
            {
                var offset = new Vector3Int(x, y, z);
                if (offset.sqrMagnitude <= radiusSquared)
                {
                    offsets.Add(offset);
                }
            }

            return offsets;
        }

        private static void StampSmoothBrush(Vector3 center, float radius, HashSet<Vector3Int> output)
        {
            // Cell i is covered when its center, i + 0.5, is inside the ball.
            Vector3Int min = Vector3Int.FloorToInt(center - new Vector3(radius, radius, radius) - 0.5f * Vector3.one);
            Vector3Int max = Vector3Int.CeilToInt(center + new Vector3(radius, radius, radius) - 0.5f * Vector3.one);
            float radiusSquared = radius * radius + 0.0001f;

            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if ((CellCenter(cell) - center).sqrMagnitude <= radiusSquared)
                {
                    output.Add(cell);
                }
            }
        }

        private static int ChebyshevDistance(Vector3Int a, Vector3Int b)
        {
            return Mathf.Max(
                Mathf.Abs(a.x - b.x),
                Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
        }

        private static int RoundedDivide(long numerator, int denominator)
        {
            long half = denominator / 2;
            return (int)(numerator >= 0
                ? (numerator + half) / denominator
                : -((-numerator + half) / denominator));
        }

        private static int FindFiniteIndex(IReadOnlyList<Vector3> path, int startIndex)
        {
            for (int i = startIndex; i < path.Count; i++)
            {
                if (IsFinite(path[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
                   Mathf.Abs(value.x) < 1e7f && Mathf.Abs(value.y) < 1e7f && Mathf.Abs(value.z) < 1e7f;
        }
    }
}
