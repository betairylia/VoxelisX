using System;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Utils
{
    /// <summary>
    /// Provides assertion utilities that are compatible with Burst-compiled code.
    /// </summary>
    /// <remarks>
    /// Standard Unity assertions cannot be used in Burst-compiled jobs.
    /// This class provides simple assertion methods that work within Burst constraints.
    /// Assertions are only active when UNITY_ASSERTIONS is defined.
    /// </remarks>
    static class BurstAssertSimpleExperssionsOnly
    {
        /// <summary>
        /// Asserts that a condition is true. Throws an exception if the condition is false.
        /// </summary>
        /// <param name="truth">The condition to assert.</param>
        /// <exception cref="Exception">Thrown when the condition is false and UNITY_ASSERTIONS is defined.</exception>
        /// <remarks>
        /// This method is designed to be used in Burst-compiled code where standard
        /// Unity assertions are not available. In release builds (when UNITY_ASSERTIONS
        /// is not defined), this method becomes a no-op and has zero overhead.
        /// </remarks>
        public static void IsTrue(bool truth)
        {
#if UNITY_ASSERTIONS
            if (!truth)
            {
                throw new Exception("Assertion failed");
            }
#endif
        }
    }

    /// <summary>
    /// Extension methods for converting between Vector3Int and int3 types.
    /// </summary>
    public static class VectorConversionExtensions
    {
        /// <summary>
        /// Converts an int3 to a Vector3Int.
        /// </summary>
        /// <param name="value">The int3 value to convert.</param>
        /// <returns>A Vector3Int with the same x, y, z components.</returns>
        public static Vector3Int ToVector3Int(this int3 value)
        {
            return new Vector3Int(value.x, value.y, value.z);
        }

        /// <summary>
        /// Converts a Vector3Int to an int3.
        /// </summary>
        /// <param name="value">The Vector3Int value to convert.</param>
        /// <returns>An int3 with the same x, y, z components.</returns>
        public static int3 ToInt3(this Vector3Int value)
        {
            return new int3(value.x, value.y, value.z);
        }
    }
}
