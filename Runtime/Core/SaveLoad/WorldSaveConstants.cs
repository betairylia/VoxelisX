using Unity.Mathematics;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// Constants and configuration for the world save/load system
    /// </summary>
    public static class WorldSaveConstants
    {
        // Magic numbers for file identification
        public const uint MAGIC_WORLD_META = 0x584F5657;      // "VOXW"
        public const uint MAGIC_REGION = 0x53584F56;          // "VOXS"
        public const uint MAGIC_ENTITY_INDEX = 0x49455856;    // "VXEI"
        public const uint MAGIC_ENTITY_ARCHIVE = 0x41455856;  // "VXEA"

        // File format versions
        public const uint VERSION_WORLD_META = 1;
        public const uint VERSION_REGION = 1;
        public const uint VERSION_ENTITY_INDEX = 1;
        public const uint VERSION_ENTITY_ARCHIVE = 1;

        // Spatial partitioning configuration (configurable)
        /// <summary>
        /// Number of sectors per region axis (default 16 = 16^3 = 4096 sectors per region)
        /// Each sector is 128 blocks, so one region = 2048^3 blocks
        /// </summary>
        public static int REGION_SIZE_IN_SECTORS = 16;

        /// <summary>
        /// Number of regions per entity index grid axis (default 16 = 16^3 regions per index file)
        /// This means each index file covers (16*16*128)^3 = 32768^3 blocks
        /// </summary>
        public static int INDEX_GRID_SIZE_IN_REGIONS = 16;

        /// <summary>
        /// Number of regions per entity archive axis (same as index for alignment)
        /// </summary>
        public static int ARCHIVE_GRID_SIZE_IN_REGIONS = 16;

        // Helper functions
        public static int3 GetRegionCoords(int3 sectorPos)
        {
            return new int3(
                FloorDiv(sectorPos.x, REGION_SIZE_IN_SECTORS),
                FloorDiv(sectorPos.y, REGION_SIZE_IN_SECTORS),
                FloorDiv(sectorPos.z, REGION_SIZE_IN_SECTORS)
            );
        }

        public static int3 GetIndexGridCoords(int3 regionPos)
        {
            return new int3(
                FloorDiv(regionPos.x, INDEX_GRID_SIZE_IN_REGIONS),
                FloorDiv(regionPos.y, INDEX_GRID_SIZE_IN_REGIONS),
                FloorDiv(regionPos.z, INDEX_GRID_SIZE_IN_REGIONS)
            );
        }

        public static int3 GetArchiveGridCoords(int3 regionPos)
        {
            return new int3(
                FloorDiv(regionPos.x, ARCHIVE_GRID_SIZE_IN_REGIONS),
                FloorDiv(regionPos.y, ARCHIVE_GRID_SIZE_IN_REGIONS),
                FloorDiv(regionPos.z, ARCHIVE_GRID_SIZE_IN_REGIONS)
            );
        }

        public static int3 GetSectorLocalPos(int3 sectorPos, int3 regionPos)
        {
            return sectorPos - regionPos * REGION_SIZE_IN_SECTORS;
        }

        // Floor division (handles negatives correctly)
        private static int FloorDiv(int a, int b)
        {
            int d = a / b;
            int r = a % b;
            return (r != 0 && ((r < 0) != (b < 0))) ? d - 1 : d;
        }
    }
}
