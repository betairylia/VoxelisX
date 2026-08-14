using System;
using System.Collections.Generic;
using UnityEngine;

namespace Voxelis
{
    /// <summary>
    /// Shared mechanics for file-loader palettes whose source keys map to VoxelisX block IDs.
    /// Format-specific loaders remain responsible for choosing and validating their source keys.
    /// </summary>
    internal static class BlockConversionPaletteUtility
    {
        public static ushort EncodeRgb555BlockId(Color32 color)
        {
            return new Block(color.r >> 3, color.g >> 3, color.b >> 3, false).id;
        }

        public static List<TEntry> CreateUpdated<TEntry>(
            IEnumerable<KeyValuePair<int, Color32>> usedColors,
            IReadOnlyList<TEntry> existingPalette,
            Func<TEntry, int> getSourceKey,
            Func<TEntry, ushort> getTargetBlockId,
            Predicate<int> isValidSourceKey,
            Func<int, Color32, ushort, TEntry> createEntry,
            string validSourceKeyDescription)
        {
            if (usedColors == null)
            {
                throw new ArgumentNullException(nameof(usedColors));
            }

            var existingTargets = BuildTargetLookup(
                existingPalette,
                getSourceKey,
                getTargetBlockId,
                isValidSourceKey);

            var sortedColors = new SortedDictionary<int, Color32>();
            foreach (KeyValuePair<int, Color32> pair in usedColors)
            {
                if (!isValidSourceKey(pair.Key))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(usedColors),
                        pair.Key,
                        validSourceKeyDescription);
                }

                sortedColors[pair.Key] = pair.Value;
            }

            var updatedPalette = new List<TEntry>(sortedColors.Count);
            foreach (KeyValuePair<int, Color32> pair in sortedColors)
            {
                ushort targetBlockId = existingTargets.TryGetValue(pair.Key, out ushort existingTarget)
                    ? existingTarget
                    : EncodeRgb555BlockId(pair.Value);

                updatedPalette.Add(createEntry(pair.Key, pair.Value, targetBlockId));
            }

            return updatedPalette;
        }

        public static Dictionary<int, ushort> BuildTargetLookup<TEntry>(
            IReadOnlyList<TEntry> palette,
            Func<TEntry, int> getSourceKey,
            Func<TEntry, ushort> getTargetBlockId,
            Predicate<int> isValidSourceKey)
        {
            var lookup = new Dictionary<int, ushort>();
            if (palette == null)
            {
                return lookup;
            }

            for (int i = 0; i < palette.Count; i++)
            {
                TEntry entry = palette[i];
                int sourceKey = getSourceKey(entry);
                if (isValidSourceKey(sourceKey))
                {
                    lookup[sourceKey] = getTargetBlockId(entry);
                }
            }

            return lookup;
        }
    }
}
