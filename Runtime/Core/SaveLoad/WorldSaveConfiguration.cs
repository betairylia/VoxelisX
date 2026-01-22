using UnityEngine;

namespace VoxelisX.SaveLoad
{
    /// <summary>
    /// ScriptableObject for configuring world save/load settings
    /// </summary>
    [CreateAssetMenu(fileName = "WorldSaveConfiguration", menuName = "VoxelisX/World Save Configuration")]
    public class WorldSaveConfiguration : ScriptableObject
    {
        [Header("Spatial Partitioning")]
        [Tooltip("Number of sectors per region axis (default 16). Each region file will contain this many sectors cubed.")]
        [Range(4, 32)]
        public int regionSizeInSectors = 16;

        [Tooltip("Number of regions per entity index grid axis (default 16). Each index file covers this many regions cubed.")]
        [Range(4, 32)]
        public int indexGridSizeInRegions = 16;

        [Tooltip("Number of regions per entity archive grid axis (default 16). Should match indexGridSizeInRegions for alignment.")]
        [Range(4, 32)]
        public int archiveGridSizeInRegions = 16;

        [Header("Save Path")]
        [Tooltip("Directory where world data will be saved (relative to persistent data path)")]
        public string saveFolderName = "Worlds";

        [Tooltip("Name of the world save (will create subfolder)")]
        public string worldName = "DefaultWorld";

        [Header("Auto-Save")]
        [Tooltip("Enable automatic periodic saving")]
        public bool enableAutoSave = true;

        [Tooltip("Auto-save interval in seconds")]
        [Range(10f, 600f)]
        public float autoSaveInterval = 60f;

        [Tooltip("Save dirty sectors only (faster) or all sectors (slower but safer)")]
        public bool saveDirtyOnly = true;

        /// <summary>
        /// Get the full save path for this world
        /// </summary>
        public string GetSavePath()
        {
            return System.IO.Path.Combine(Application.persistentDataPath, saveFolderName, worldName);
        }

        /// <summary>
        /// Apply configuration to WorldSaveConstants
        /// Call this before creating WorldSaveManager
        /// </summary>
        public void ApplyConfiguration()
        {
            WorldSaveConstants.REGION_SIZE_IN_SECTORS = regionSizeInSectors;
            WorldSaveConstants.INDEX_GRID_SIZE_IN_REGIONS = indexGridSizeInRegions;
            WorldSaveConstants.ARCHIVE_GRID_SIZE_IN_REGIONS = archiveGridSizeInRegions;

            Debug.Log($"World save configuration applied: " +
                     $"Region size: {regionSizeInSectors}^3 sectors, " +
                     $"Index grid: {indexGridSizeInRegions}^3 regions, " +
                     $"Save path: {GetSavePath()}");
        }

        private void OnValidate()
        {
            // Keep archive and index grid sizes in sync
            if (archiveGridSizeInRegions != indexGridSizeInRegions)
            {
                archiveGridSizeInRegions = indexGridSizeInRegions;
            }
        }
    }
}
