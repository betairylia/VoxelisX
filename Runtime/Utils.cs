using System;

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
}
