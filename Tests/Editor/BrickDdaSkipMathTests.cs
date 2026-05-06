using NUnit.Framework;
using Unity.Mathematics;

namespace VoxelisX.Tests
{
    public class BrickDdaSkipMathTests
    {
        private const float GridEpsilon = 0.0001f;
        private const float MinRayDir = 1e-20f;
        private const int BrickSize = 8;
        private const int MicroShift = 2;
        private const int MicroSize = 1 << MicroShift;
        private const int MicroMask = MicroSize - 1;

        private struct Cursor
        {
            public int3 Cell;
            public int3 Step;
            public float3 DeltaDist;
            public float3 SideDist;
            public bool3 AxisMask;
        }

        [TestCase(1.1f, 1.1f, 1.1f, 0.92f, 0.82f, 0.76f)]
        [TestCase(6.8f, 6.2f, 1.3f, -0.60f, -0.90f, 0.40f)]
        [TestCase(2.0f, 2.0f, 2.0f, 1.00f, 1.00f, 1.00f)]
        [TestCase(4.2f, 2.4f, 5.7f, 0.35f, -0.70f, -0.20f)]
        public void EmptyCoarseSkipMatchesRepeatedFineSteps(
            float px, float py, float pz,
            float dx, float dy, float dz)
        {
            float3 rayDir = math.normalize(new float3(dx, dy, dz));
            Cursor repeated = CreateCursor(new float3(px, py, pz), rayDir);
            Cursor skipped = repeated;

            int3 initialCoarseCell = CoarseCell(repeated.Cell);
            do
            {
                FineStep(ref repeated);
            }
            while (IsInside(repeated.Cell) && math.all(CoarseCell(repeated.Cell) == initialCoarseCell));

            SkipEmptyCoarseCell(ref skipped, math.abs(rayDir));

            Assert.That(math.all(skipped.Cell == repeated.Cell), Is.True);
            Assert.That(math.all(skipped.AxisMask == repeated.AxisMask), Is.True);
            Assert.That(math.distance(skipped.SideDist, repeated.SideDist), Is.LessThan(0.0002f));
        }

        [Test]
        public void EmptyCoarseSkipDoesNotMoveZeroStepAxes()
        {
            float3 rayDir = new float3(1.0f, 0.0f, 0.0f);
            Cursor skipped = CreateCursor(new float3(1.25f, 2.5f, 3.5f), rayDir);

            SkipEmptyCoarseCell(ref skipped, math.abs(rayDir));

            Assert.That(skipped.Cell.x, Is.EqualTo(4));
            Assert.That(skipped.Cell.y, Is.EqualTo(2));
            Assert.That(skipped.Cell.z, Is.EqualTo(3));
            Assert.That(skipped.AxisMask.x, Is.True);
            Assert.That(skipped.AxisMask.y, Is.False);
            Assert.That(skipped.AxisMask.z, Is.False);
        }

        private static Cursor CreateCursor(float3 entryPositionInGrid, float3 rayDir)
        {
            float3 gridMax = new float3(BrickSize) - GridEpsilon;
            float3 entryPos = math.clamp(entryPositionInGrid, float3.zero, gridMax);
            float3 raySign = math.sign(rayDir);
            int3 cell = new int3((int)math.floor(entryPos.x), (int)math.floor(entryPos.y), (int)math.floor(entryPos.z));

            var cursor = new Cursor
            {
                Cell = cell,
                Step = new int3((int)raySign.x, (int)raySign.y, (int)raySign.z),
                DeltaDist = 1.0f / math.max(math.abs(rayDir), new float3(MinRayDir)),
                AxisMask = new bool3(false, false, false)
            };
            cursor.SideDist = (raySign * (new float3(cursor.Cell) - entryPos) + (raySign * 0.5f) + 0.5f) * cursor.DeltaDist;
            return cursor;
        }

        private static bool IsInside(int3 cell)
        {
            return math.all(cell >= int3.zero) && math.all(cell < new int3(BrickSize));
        }

        private static int3 CoarseCell(int3 cell)
        {
            return new int3(cell.x >> MicroShift, cell.y >> MicroShift, cell.z >> MicroShift);
        }

        private static void FineStep(ref Cursor cursor)
        {
            cursor.AxisMask = cursor.SideDist <= math.min(cursor.SideDist.yzx, cursor.SideDist.zxy);
            cursor.SideDist += math.select(float3.zero, cursor.DeltaDist, cursor.AxisMask);
            cursor.Cell += math.select(int3.zero, cursor.Step, cursor.AxisMask);
        }

        private static void SkipEmptyCoarseCell(ref Cursor cursor, float3 rayAbs)
        {
            int3 localCell = new int3(cursor.Cell.x & MicroMask, cursor.Cell.y & MicroMask, cursor.Cell.z & MicroMask);
            int3 crossingsToExit = new int3(
                cursor.Step.x > 0 ? MicroSize - localCell.x : localCell.x + 1,
                cursor.Step.y > 0 ? MicroSize - localCell.y : localCell.y + 1,
                cursor.Step.z > 0 ? MicroSize - localCell.z : localCell.z + 1);

            float3 coarseExitT = cursor.SideDist + (new float3(crossingsToExit) - 1.0f) * cursor.DeltaDist;
            float tExit = math.min(coarseExitT.x, math.min(coarseExitT.y, coarseExitT.z));
            float3 crossingsF = math.floor((tExit - cursor.SideDist) * rayAbs + GridEpsilon) + 1.0f;
            int3 crossings = math.clamp(
                new int3((int)crossingsF.x, (int)crossingsF.y, (int)crossingsF.z),
                int3.zero,
                crossingsToExit);

            cursor.Cell += crossings * cursor.Step;
            cursor.SideDist += new float3(crossings) * cursor.DeltaDist;

            float3 lastCrossT = cursor.SideDist - cursor.DeltaDist;
            cursor.AxisMask = new bool3(
                crossings.x > 0 && math.abs(lastCrossT.x - tExit) <= GridEpsilon,
                crossings.y > 0 && math.abs(lastCrossT.y - tExit) <= GridEpsilon,
                crossings.z > 0 && math.abs(lastCrossT.z - tExit) <= GridEpsilon);
        }
    }
}
