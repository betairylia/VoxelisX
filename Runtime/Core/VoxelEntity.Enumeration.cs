using System.Collections.Generic;
using Unity.Mathematics;

namespace Voxelis
{
    /// <summary>
    /// Read-only bulk enumeration of an entity's voxels.
    ///
    /// Gameplay code that needs "every solid voxel of this entity" (copy/paste tools, exporters,
    /// analysis) cannot walk the sector/brick tables itself: those live behind raw pointers and
    /// callers usually compile without unsafe code. This partial supplies the walk once, in the
    /// engine, and hands back plain positions in entity-local space. Blocks and side-table slots
    /// are then read through the normal <see cref="GetBlock"/> / <see cref="GetSlot{T}"/> API.
    ///
    /// The walk allocates nothing beyond the caller's list and touches no dirty/requireUpdate state.
    /// </summary>
    public unsafe partial class VoxelEntity
    {
        /// <summary>
        /// Appends the entity-local position of every non-empty block to <paramref name="into"/>.
        /// </summary>
        /// <param name="into">Destination list. Not cleared — positions are appended.</param>
        /// <param name="maxCount">
        /// Stop after this many positions. Use it to bail out of accidentally enumerating a whole
        /// streamed world. A value of 0 or less means "no limit".
        /// </param>
        /// <returns>
        /// True when the whole entity was enumerated, false when <paramref name="maxCount"/> cut the
        /// walk short (the list then holds exactly <paramref name="maxCount"/> positions).
        /// </returns>
        /// <remarks>
        /// Only allocated bricks are visited, so the cost scales with occupied volume, not with the
        /// entity's coordinate range. An allocated brick may still be entirely empty; those blocks
        /// are filtered out here, so every returned position is genuinely non-empty.
        /// </remarks>
        public bool CollectSolidVoxelPositions(List<int3> into, int maxCount = 0)
        {
            if (into == null)
            {
                return true;
            }

            foreach (var sectorEntry in data.sectors)
            {
                int3 sectorOrigin = sectorEntry.Key * Sector.SECTOR_SIZE_IN_BLOCKS;
                Sector* sector = sectorEntry.Value.Ptr;
                if (sector == null)
                {
                    continue;
                }

                foreach (SectorNonEmptyBrickEnumerator.BrickRef brick in sector->EnumerateNonEmptyBricks())
                {
                    int3 brickOrigin = Sector.ToBrickPos((short)brick.BrickAbs) * Sector.SIZE_IN_BLOCKS;
                    Block* blocks = sector->GetBrick<Block>(SectorSlotId.Block, brick.Bid);
                    if (blocks == null)
                    {
                        continue;
                    }

                    for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                    for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                    for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                    {
                        if (blocks[Sector.ToBlockIdx(x, y, z)].isEmpty)
                        {
                            continue;
                        }

                        if (maxCount > 0 && into.Count >= maxCount)
                        {
                            return false;
                        }

                        into.Add(sectorOrigin + brickOrigin + new int3(x, y, z));
                    }
                }
            }

            return true;
        }
    }
}
