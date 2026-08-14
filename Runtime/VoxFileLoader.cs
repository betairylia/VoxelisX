using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using VoxReader.Interfaces;

namespace Voxelis
{
    /// <summary>
    /// Maps a MagicaVoxel palette entry to a VoxelisX block ID.
    /// </summary>
    [Serializable]
    public struct VoxBlockConversion
    {
        [SerializeField] private int voxPaletteId;
        [SerializeField] private Color32 voxColor;
        // Unity serializes int fields consistently and the inspector constrains this to ushort range.
        [SerializeField] private int targetBlockId;

        /// <summary>
        /// The palette ID shown by MagicaVoxel (1-255).
        /// </summary>
        public int VoxPaletteId => voxPaletteId;

        /// <summary>
        /// The source color stored at this palette ID.
        /// </summary>
        public Color32 VoxColor => voxColor;

        /// <summary>
        /// The VoxelisX block ID used when importing voxels with this palette ID.
        /// </summary>
        public ushort TargetBlockId => (ushort)Mathf.Clamp(targetBlockId, ushort.MinValue, ushort.MaxValue);

        internal VoxBlockConversion(int voxPaletteId, Color32 voxColor, ushort targetBlockId)
        {
            this.voxPaletteId = voxPaletteId;
            this.voxColor = voxColor;
            this.targetBlockId = targetBlockId;
        }
    }

    /// <summary>
    /// Loads voxel data from MagicaVoxel .vox files into a <see cref="VoxelEntity"/>.
    /// </summary>
    /// <remarks>
    /// The Y and Z axes are swapped during import to match Unity's coordinate system.
    /// Used VOX palette IDs can be mapped to arbitrary VoxelisX block IDs. Colors without
    /// a mapping retain the loader's legacy stone/RGB555 fallback behavior.
    /// </remarks>
    [RequireComponent(typeof(VoxelEntity))]
    public class VoxFileLoader : MonoBehaviour
    {
        internal const int MinimumVoxPaletteId = 1;
        internal const int MaximumVoxPaletteId = 255;

        /// <summary>
        /// Path to the .vox file to load.
        /// </summary>
        [SerializeField] private string voxFilePath;
        [SerializeField] private bool LoadAsStone = false;
        [SerializeField, HideInInspector] private List<VoxBlockConversion> blockConversionPalette = new();

        private VoxelEntity entity;

        /// <summary>
        /// The currently configured VOX-to-VoxelisX palette mappings.
        /// </summary>
        public IReadOnlyList<VoxBlockConversion> BlockConversionPalette => blockConversionPalette;

        /// <summary>
        /// Reads the configured VOX file and refreshes mappings for every palette ID used by its voxels.
        /// Existing target block IDs are preserved; newly discovered colors default to their RGB555 ID.
        /// </summary>
        public void RefreshBlockConversionPalette()
        {
            if (string.IsNullOrWhiteSpace(voxFilePath))
            {
                throw new InvalidOperationException("A VOX file path must be specified before reading its palette.");
            }

            IVoxFile voxFile = VoxReader.VoxReader.Read(voxFilePath);
            var usedColors = new Dictionary<int, Color32>();

            foreach (IModel model in voxFile.Models)
            {
                foreach (VoxReader.Voxel voxel in model.Voxels)
                {
                    int paletteId = voxel.ColorIndex + 1;
                    usedColors[paletteId] = ToColor32(voxel.Color);
                }
            }

            blockConversionPalette = CreateUpdatedBlockConversionPalette(usedColors, blockConversionPalette);
        }

        /// <summary>
        /// Loads the .vox file and populates the <see cref="VoxelEntity"/> with its voxel data.
        /// </summary>
        public void Initialize()
        {
            entity = GetComponent<VoxelEntity>();

            IVoxFile voxFile = VoxReader.VoxReader.Read(voxFilePath);
            Dictionary<int, ushort> blockIdsByPaletteId = BuildBlockIdLookup(blockConversionPalette);

            foreach (IModel model in voxFile.Models)
            {
                foreach (VoxReader.Voxel voxel in model.Voxels)
                {
                    int paletteId = voxel.ColorIndex + 1;
                    Block block;

                    if (blockIdsByPaletteId.TryGetValue(paletteId, out ushort targetBlockId))
                    {
                        block = new Block(targetBlockId);
                    }
                    else
                    {
                        // Preserve the loader's pre-palette behavior for files that have not been scanned
                        // or for colors added after the last scan.
                        block = LoadAsStone
                            ? new Block(voxel.Color.R >> 3, voxel.Color.G >> 3, voxel.Color.B >> 3, false)
                            : new Block(0x8000);
                    }

                    entity.SetBlock(
                        new int3(
                            voxel.GlobalPosition.X,
                            voxel.GlobalPosition.Z,
                            voxel.GlobalPosition.Y),
                        block);
                }
            }
        }

        internal static ushort EncodeRgb555BlockId(byte r, byte g, byte b)
        {
            return BlockConversionPaletteUtility.EncodeRgb555BlockId(new Color32(r, g, b, byte.MaxValue));
        }

        internal static List<VoxBlockConversion> CreateUpdatedBlockConversionPalette(
            IEnumerable<KeyValuePair<int, Color32>> usedColors,
            IReadOnlyList<VoxBlockConversion> existingPalette)
        {
            return BlockConversionPaletteUtility.CreateUpdated(
                usedColors,
                existingPalette,
                entry => entry.VoxPaletteId,
                entry => entry.TargetBlockId,
                IsValidPaletteId,
                (paletteId, color, targetBlockId) =>
                    new VoxBlockConversion(paletteId, color, targetBlockId),
                $"VOX palette IDs must be between {MinimumVoxPaletteId} and {MaximumVoxPaletteId}.");
        }

        private static Dictionary<int, ushort> BuildBlockIdLookup(IReadOnlyList<VoxBlockConversion> palette)
        {
            return BlockConversionPaletteUtility.BuildTargetLookup(
                palette,
                entry => entry.VoxPaletteId,
                entry => entry.TargetBlockId,
                IsValidPaletteId);
        }

        private static bool IsValidPaletteId(int paletteId)
        {
            return paletteId >= MinimumVoxPaletteId && paletteId <= MaximumVoxPaletteId;
        }

        private static Color32 ToColor32(VoxReader.Color color)
        {
            return new Color32(color.R, color.G, color.B, color.A);
        }

        private void Start()
        {
            Initialize();
        }
    }
}
