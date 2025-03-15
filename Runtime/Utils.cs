using System;

namespace Voxelis.Utils
{
    static class BurstAssertSimpleExperssionsOnly
    {
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
