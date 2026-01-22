using NUnit.Framework;
using Unity.Mathematics;
using Voxelis;

namespace VoxelisX.Tests
{
    /// <summary>
    /// Unit tests for NeighborhoodSettings.
    /// Tests the 26-direction Moore neighborhood vectors and opposite direction indices.
    /// </summary>
    [TestFixture]
    public class NeighborhoodSettingsTests
    {
        #region Direction Vector Tests

        [Test]
        public void Directions_HasCorrectCount()
        {
            // Assert
            Assert.AreEqual(26, NeighborhoodSettings.Directions.Length, "Directions array should have 26 elements");
        }

        [Test]
        public void Directions_FaceNeighbors_AreUnitVectors()
        {
            // The first 6 directions should be face-adjacent (Von Neumann neighborhood)
            // Indices 0-5: ±X, ±Y, ±Z

            for (int i = 0; i < NeighborhoodSettings.VON_NEUMANN_COUNT; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];
                int magnitude = math.abs(dir.x) + math.abs(dir.y) + math.abs(dir.z);

                Assert.AreEqual(1, magnitude, $"Face neighbor at index {i} should be a unit vector (magnitude 1)");
            }
        }

        [Test]
        public void Directions_EdgeNeighbors_HaveMagnitudeTwo()
        {
            // Edge neighbors (indices 6-17) should have 2 non-zero components
            for (int i = 6; i < 18; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];
                int magnitude = math.abs(dir.x) + math.abs(dir.y) + math.abs(dir.z);

                Assert.AreEqual(2, magnitude, $"Edge neighbor at index {i} should have Manhattan magnitude 2");
            }
        }

        [Test]
        public void Directions_CornerNeighbors_HaveMagnitudeThree()
        {
            // Corner neighbors (indices 18-25) should have all 3 components non-zero
            for (int i = 18; i < 26; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];
                int magnitude = math.abs(dir.x) + math.abs(dir.y) + math.abs(dir.z);

                Assert.AreEqual(3, magnitude, $"Corner neighbor at index {i} should have Manhattan magnitude 3");
            }
        }

        [Test]
        public void Directions_AllComponents_AreWithinValidRange()
        {
            // Each component should be -1, 0, or 1
            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];

                Assert.IsTrue(dir.x >= -1 && dir.x <= 1, $"Direction {i} X component should be -1, 0, or 1");
                Assert.IsTrue(dir.y >= -1 && dir.y <= 1, $"Direction {i} Y component should be -1, 0, or 1");
                Assert.IsTrue(dir.z >= -1 && dir.z <= 1, $"Direction {i} Z component should be -1, 0, or 1");
            }
        }

        [Test]
        public void Directions_NoZeroVectors()
        {
            // None of the directions should be (0,0,0)
            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];
                bool isZero = (dir.x == 0 && dir.y == 0 && dir.z == 0);

                Assert.IsFalse(isZero, $"Direction {i} should not be a zero vector");
            }
        }

        #endregion

        #region Opposite Direction Tests

        [Test]
        public void OppositeDirectionIndices_HasCorrectCount()
        {
            // Assert
            Assert.AreEqual(26, NeighborhoodSettings.OppositeDirectionIndices.Length,
                "OppositeDirectionIndices array should have 26 elements");
        }

        [Test]
        public void OppositeDirectionIndices_AllIndicesWithinValidRange()
        {
            // All opposite indices should be in range [0, 25]
            for (int i = 0; i < NeighborhoodSettings.OppositeDirectionIndices.Length; i++)
            {
                int oppositeIdx = NeighborhoodSettings.OppositeDirectionIndices[i];

                Assert.IsTrue(oppositeIdx >= 0 && oppositeIdx < 26,
                    $"Opposite index for direction {i} should be in range [0, 25], but was {oppositeIdx}");
            }
        }

        [Test]
        public void OppositeDirectionIndices_PointToActualOppositeDirections()
        {
            // For each direction, its opposite should have negated components
            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                var dir = NeighborhoodSettings.Directions[i];
                int oppositeIdx = NeighborhoodSettings.OppositeDirectionIndices[i];
                var oppositeDir = NeighborhoodSettings.Directions[oppositeIdx];

                Assert.AreEqual(-dir.x, oppositeDir.x,
                    $"Direction {i} X and its opposite should be negated");
                Assert.AreEqual(-dir.y, oppositeDir.y,
                    $"Direction {i} Y and its opposite should be negated");
                Assert.AreEqual(-dir.z, oppositeDir.z,
                    $"Direction {i} Z and its opposite should be negated");
            }
        }

        [Test]
        public void OppositeDirectionIndices_IsSymmetric()
        {
            // If j is the opposite of i, then i should be the opposite of j
            for (int i = 0; i < NeighborhoodSettings.OppositeDirectionIndices.Length; i++)
            {
                int oppositeIdx = NeighborhoodSettings.OppositeDirectionIndices[i];
                int oppositeOfOpposite = NeighborhoodSettings.OppositeDirectionIndices[oppositeIdx];

                Assert.AreEqual(i, oppositeOfOpposite,
                    $"Opposite relation should be symmetric: opposite of opposite of {i} should be {i}");
            }
        }

        [Test]
        public void OppositeDirectionIndices_FacePairs_AreCorrect()
        {
            // Verify specific face pairs according to the implementation
            // 0<->1, 2<->3, 4<->5
            Assert.AreEqual(1, NeighborhoodSettings.OppositeDirectionIndices[0], "Face 0 opposite should be 1");
            Assert.AreEqual(0, NeighborhoodSettings.OppositeDirectionIndices[1], "Face 1 opposite should be 0");
            Assert.AreEqual(3, NeighborhoodSettings.OppositeDirectionIndices[2], "Face 2 opposite should be 3");
            Assert.AreEqual(2, NeighborhoodSettings.OppositeDirectionIndices[3], "Face 3 opposite should be 2");
            Assert.AreEqual(5, NeighborhoodSettings.OppositeDirectionIndices[4], "Face 4 opposite should be 5");
            Assert.AreEqual(4, NeighborhoodSettings.OppositeDirectionIndices[5], "Face 5 opposite should be 4");
        }

        #endregion

        #region Specific Direction Tests

        [Test]
        public void Directions_FaceNeighbors_CoverAllAxes()
        {
            // Verify we have +X, -X, +Y, -Y, +Z, -Z
            var faceDirections = new int3[6];
            for (int i = 0; i < 6; i++)
            {
                faceDirections[i] = NeighborhoodSettings.Directions[i];
            }

            // Check we have ±1 in each axis exactly twice
            int xCount = 0, yCount = 0, zCount = 0;
            foreach (var dir in faceDirections)
            {
                if (dir.x != 0) xCount++;
                if (dir.y != 0) yCount++;
                if (dir.z != 0) zCount++;
            }

            Assert.AreEqual(2, xCount, "Should have 2 face directions along X axis");
            Assert.AreEqual(2, yCount, "Should have 2 face directions along Y axis");
            Assert.AreEqual(2, zCount, "Should have 2 face directions along Z axis");
        }

        [Test]
        public void Directions_AllAreUnique()
        {
            // No two directions should be the same
            for (int i = 0; i < NeighborhoodSettings.Directions.Length; i++)
            {
                for (int j = i + 1; j < NeighborhoodSettings.Directions.Length; j++)
                {
                    bool areEqual = math.all(NeighborhoodSettings.Directions[i] == NeighborhoodSettings.Directions[j]);
                    Assert.IsFalse(areEqual, $"Directions {i} and {j} should be unique");
                }
            }
        }

        #endregion

        #region Direction Lookup Tests

        [Test]
        public void DirectionToIndexMinusOne_AllValidDirections_ReturnCorrectIndex()
        {
            // Test that the lookup function returns correct indices for all 26 directions
            for (int expectedIndex = 0; expectedIndex < 26; expectedIndex++)
            {
                int3 direction = NeighborhoodSettings.Directions[expectedIndex];
                int actualIndex = NeighborhoodSettings.DirectionToIndexMinusOne(direction);

                Assert.AreEqual(expectedIndex, actualIndex,
                    $"Direction {direction} should map to index {expectedIndex}");
            }
        }

        [Test]
        public void DirectionToIndexMinusOne_ZeroVector_ReturnsMinusOne()
        {
            int result = NeighborhoodSettings.DirectionToIndexMinusOne(new int3(0, 0, 0));

            Assert.AreEqual(-1, result, "Zero vector (center) should return -1");
        }

        [Test]
        public void DirectionToIndexMinusOne_InvalidDirection_ReturnsMinusOne()
        {
            // Test directions outside valid range {-1, 0, 1}
            Assert.AreEqual(-1, NeighborhoodSettings.DirectionToIndexMinusOne(new int3(2, 0, 0)),
                "Direction with component > 1 should return -1");
            Assert.AreEqual(-1, NeighborhoodSettings.DirectionToIndexMinusOne(new int3(0, -2, 0)),
                "Direction with component < -1 should return -1");
            Assert.AreEqual(-1, NeighborhoodSettings.DirectionToIndexMinusOne(new int3(5, 5, 5)),
                "Invalid direction should return -1");
        }

        [Test]
        public void DirectionToIndexMinusOne_IsInverseOfDirections()
        {
            // Verify that DirectionToIndexMinusOne is the inverse of indexing into Directions array
            for (int i = 0; i < 26; i++)
            {
                int3 direction = NeighborhoodSettings.Directions[i];
                int retrievedIndex = NeighborhoodSettings.DirectionToIndexMinusOne(direction);

                Assert.AreEqual(i, retrievedIndex,
                    $"DirectionToIndexMinusOne should be inverse of Directions array access for index {i}");
            }
        }

        #endregion

        #region Direction Mask Tests

        [Test]
        public void HasDirection_SetBit_ReturnsTrue()
        {
            // Set bit 5
            uint mask = 1u << 5;

            Assert.IsTrue(NeighborhoodSettings.HasDirection(mask, 5),
                "HasDirection should return true for set bit");
            Assert.IsFalse(NeighborhoodSettings.HasDirection(mask, 4),
                "HasDirection should return false for unset bit");
        }

        [Test]
        public void SetDirection_SetsCorrectBit()
        {
            uint mask = 0;
            mask = NeighborhoodSettings.SetDirection(mask, 10);

            Assert.AreEqual(1u << 10, mask, "SetDirection should set correct bit");
            Assert.IsTrue(NeighborhoodSettings.HasDirection(mask, 10),
                "Set direction should be detectable with HasDirection");
        }

        [Test]
        public void HasAnyDirection_ZeroMask_ReturnsFalse()
        {
            Assert.IsFalse(NeighborhoodSettings.HasAnyDirection(0),
                "Zero mask should have no directions");
        }

        [Test]
        public void HasAnyDirection_NonZeroMask_ReturnsTrue()
        {
            Assert.IsTrue(NeighborhoodSettings.HasAnyDirection(1),
                "Non-zero mask should have at least one direction");
            Assert.IsTrue(NeighborhoodSettings.HasAnyDirection(0xFFFFFFFF),
                "All-bits-set mask should have directions");
        }

        #endregion
    }
}
