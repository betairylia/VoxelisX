using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Caelix.Rendering.RayQuery
{
    /// <summary>
    /// Reorders one entity's change list so that the entries of a render group are contiguous, and
    /// reports where each group's slice starts and how long it is.
    /// </summary>
    /// <remarks>
    /// A counting pass, a prefix sum and a scatter pass, so a group's entries keep their original
    /// relative order. That matters: within one group a removal must still be seen before an
    /// addition that reuses its slot. The renderer hands each group renderer its own slice, and
    /// several of those jobs then read this one array at once.
    /// </remarks>
    [BurstCompile]
    internal struct BucketChangesJob : IJob
    {
        /// <summary>The cycle's changes, in publication order.</summary>
        [ReadOnly] public NativeArray<BrickChange> changes;

        /// <summary>How many entries of <see cref="changes"/> are real; the array itself may be padded.</summary>
        public int changeCount;

        /// <summary>Output: every entry, reordered by group.</summary>
        public NativeList<BrickChange> sorted;

        /// <summary>Output: one key per group that has at least one entry, in first-seen order.</summary>
        public NativeList<int3> groupKeys;

        /// <summary>Output: index into <see cref="sorted"/> of each group's first entry.</summary>
        public NativeList<int> groupStarts;

        /// <summary>Output: number of entries of each group.</summary>
        public NativeList<int> groupCounts;

        public void Execute()
        {
            if (changeCount <= 0)
            {
                return;
            }

            var groupIndex = new NativeHashMap<int3, int>(changeCount, Allocator.Temp);

            for (int i = 0; i < changeCount; i++)
            {
                int3 group = RenderGroup.Of(changes[i].Key);
                if (groupIndex.TryGetValue(group, out int slot))
                {
                    groupCounts[slot] = groupCounts[slot] + 1;
                }
                else
                {
                    groupIndex.Add(group, groupKeys.Length);
                    groupKeys.Add(group);
                    groupCounts.Add(1);
                }
            }

            groupStarts.Resize(groupKeys.Length, NativeArrayOptions.UninitializedMemory);
            var cursor = new NativeArray<int>(groupKeys.Length, Allocator.Temp);

            int running = 0;
            for (int g = 0; g < groupKeys.Length; g++)
            {
                groupStarts[g] = running;
                cursor[g] = running;
                running += groupCounts[g];
            }

            sorted.Resize(changeCount, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < changeCount; i++)
            {
                int slot = groupIndex[RenderGroup.Of(changes[i].Key)];
                sorted[cursor[slot]] = changes[i];
                cursor[slot] = cursor[slot] + 1;
            }

            cursor.Dispose();
            groupIndex.Dispose();
        }
    }
}
