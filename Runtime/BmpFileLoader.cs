using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace Caelix
{
    /// <summary>
    /// Maps a BMP palette entry or direct pixel color to a Caelix block ID.
    /// </summary>
    [Serializable]
    public struct BmpBlockConversion
    {
        [SerializeField] private int bmpPaletteIndex;
        [SerializeField] private Color32 bmpColor;
        // Unity serializes int fields consistently and the inspector constrains this to ushort range.
        [SerializeField] private int targetBlockId;

        /// <summary>
        /// The zero-based BMP palette index, or -1 when the BMP stores colors directly.
        /// </summary>
        public int BmpPaletteIndex => bmpPaletteIndex;

        /// <summary>
        /// The source color represented by this conversion.
        /// </summary>
        public Color32 BmpColor => bmpColor;

        /// <summary>
        /// The Caelix block ID used when importing matching pixels.
        /// </summary>
        public ushort TargetBlockId => (ushort)Mathf.Clamp(targetBlockId, ushort.MinValue, ushort.MaxValue);

        internal BmpBlockConversion(int bmpPaletteIndex, Color32 bmpColor, ushort targetBlockId)
        {
            this.bmpPaletteIndex = bmpPaletteIndex;
            this.bmpColor = bmpColor;
            this.targetBlockId = targetBlockId;
        }
    }

    /// <summary>
    /// Loads a BMP image as a one-voxel-tall <see cref="VoxelEntity"/> on the entity's X/Z plane.
    /// </summary>
    /// <remarks>
    /// Source X maps to local X, the bottom image row maps to local Z = 0, and every voxel is
    /// written at local Y = 0. Uncompressed Windows BMPs with 1, 4, 8, 16, 24, or 32 bits per
    /// pixel are supported. Indexed BMP mappings are keyed by palette index; direct-color BMP
    /// mappings are keyed by RGB color. Unmapped colors use RGB555 unless <see cref="loadAsStone"/>
    /// is enabled.
    /// </remarks>
    [RequireComponent(typeof(VoxelEntity))]
    public class BmpFileLoader : MonoBehaviour
    {
        internal const int DirectColorPaletteIndex = -1;
        internal const int MinimumBmpPaletteIndex = 0;
        internal const int MaximumBmpPaletteIndex = 255;

        /// <summary>
        /// Path to the .bmp file to load.
        /// </summary>
        [SerializeField] private string bmpFilePath;
        [SerializeField] private bool loadAsStone = false;
        [SerializeField, HideInInspector] private List<BmpBlockConversion> blockConversionPalette = new();

        private VoxelEntity entity;

        /// <summary>
        /// The currently configured BMP-to-Caelix palette mappings.
        /// </summary>
        public IReadOnlyList<BmpBlockConversion> BlockConversionPalette => blockConversionPalette;

        /// <summary>
        /// Reads the configured BMP and refreshes mappings for every source entry or color it uses.
        /// Existing target block IDs are preserved; newly discovered colors default to RGB555.
        /// </summary>
        public void RefreshBlockConversionPalette()
        {
            BmpImageData image = ReadConfiguredBmp();
            blockConversionPalette = CreateUpdatedBlockConversionPalette(image, blockConversionPalette);
        }

        /// <summary>
        /// Loads the configured BMP and populates the entity at local Y = 0.
        /// </summary>
        public void Initialize()
        {
            entity = GetComponent<VoxelEntity>();

            BmpImageData image = ReadConfiguredBmp();
            BlockIdLookup blockIds = BuildBlockIdLookup(blockConversionPalette);

            for (int z = 0; z < image.Height; z++)
            {
                int rowOffset = z * image.Width;
                for (int x = 0; x < image.Width; x++)
                {
                    int pixelIndex = rowOffset + x;
                    Color32 color = image.Pixels[pixelIndex];

                    bool hasMapping = image.IsIndexed
                        ? blockIds.ByPaletteIndex.TryGetValue(image.PaletteIndices[pixelIndex], out ushort targetBlockId)
                        : blockIds.ByColor.TryGetValue(ToColorKey(color), out targetBlockId);

                    Block block = hasMapping
                        ? new Block(targetBlockId)
                        : loadAsStone
                            ? new Block(0x8000)
                            : new Block(color.r >> 3, color.g >> 3, color.b >> 3, false);

                    entity.SetBlock(new int3(x, 0, z), block);
                }
            }
        }

        internal static ushort EncodeRgb555BlockId(byte r, byte g, byte b)
        {
            return BlockConversionPaletteUtility.EncodeRgb555BlockId(new Color32(r, g, b, byte.MaxValue));
        }

        internal static List<BmpBlockConversion> CreateUpdatedBlockConversionPalette(
            BmpImageData image,
            IReadOnlyList<BmpBlockConversion> existingPalette)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            if (image.IsIndexed)
            {
                var usedColors = new Dictionary<int, Color32>();
                for (int i = 0; i < image.PaletteIndices.Length; i++)
                {
                    int paletteIndex = image.PaletteIndices[i];
                    usedColors[paletteIndex] = image.Palette[paletteIndex];
                }

                return BlockConversionPaletteUtility.CreateUpdated(
                    usedColors,
                    existingPalette,
                    entry => entry.BmpPaletteIndex,
                    entry => entry.TargetBlockId,
                    IsValidPaletteIndex,
                    (paletteIndex, color, targetBlockId) =>
                        new BmpBlockConversion(paletteIndex, color, targetBlockId),
                    $"BMP palette indices must be between {MinimumBmpPaletteIndex} and {MaximumBmpPaletteIndex}.");
            }

            var directColors = new Dictionary<int, Color32>();
            for (int i = 0; i < image.Pixels.Length; i++)
            {
                Color32 color = image.Pixels[i];
                directColors[ToColorKey(color)] = color;
            }

            return BlockConversionPaletteUtility.CreateUpdated(
                directColors,
                existingPalette,
                GetDirectColorKey,
                entry => entry.TargetBlockId,
                IsValidColorKey,
                (colorKey, color, targetBlockId) =>
                    new BmpBlockConversion(DirectColorPaletteIndex, color, targetBlockId),
                "Direct BMP color keys must be 24-bit RGB values.");
        }

        private BmpImageData ReadConfiguredBmp()
        {
            if (string.IsNullOrWhiteSpace(bmpFilePath))
            {
                throw new InvalidOperationException("A BMP file path must be specified before loading it.");
            }

            return BmpDecoder.Decode(File.ReadAllBytes(bmpFilePath));
        }

        private static BlockIdLookup BuildBlockIdLookup(IReadOnlyList<BmpBlockConversion> palette)
        {
            Dictionary<int, ushort> byPaletteIndex = BlockConversionPaletteUtility.BuildTargetLookup(
                palette,
                entry => entry.BmpPaletteIndex,
                entry => entry.TargetBlockId,
                IsValidPaletteIndex);

            Dictionary<int, ushort> byColor = BlockConversionPaletteUtility.BuildTargetLookup(
                palette,
                GetDirectColorKey,
                entry => entry.TargetBlockId,
                IsValidColorKey);

            return new BlockIdLookup(byPaletteIndex, byColor);
        }

        private static bool IsValidPaletteIndex(int paletteIndex)
        {
            return paletteIndex >= MinimumBmpPaletteIndex && paletteIndex <= MaximumBmpPaletteIndex;
        }

        private static int ToColorKey(Color32 color)
        {
            return (color.r << 16) | (color.g << 8) | color.b;
        }

        private static int GetDirectColorKey(BmpBlockConversion entry)
        {
            return entry.BmpPaletteIndex == DirectColorPaletteIndex
                ? ToColorKey(entry.BmpColor)
                : DirectColorPaletteIndex;
        }

        private static bool IsValidColorKey(int colorKey)
        {
            return colorKey >= 0 && colorKey <= 0xFFFFFF;
        }

        private void Start()
        {
            Initialize();
        }

        private sealed class BlockIdLookup
        {
            public readonly Dictionary<int, ushort> ByPaletteIndex;
            public readonly Dictionary<int, ushort> ByColor;

            public BlockIdLookup(
                Dictionary<int, ushort> byPaletteIndex,
                Dictionary<int, ushort> byColor)
            {
                ByPaletteIndex = byPaletteIndex;
                ByColor = byColor;
            }
        }
    }

    internal sealed class BmpImageData
    {
        public int Width { get; }
        public int Height { get; }
        public Color32[] Pixels { get; }
        public Color32[] Palette { get; }
        public byte[] PaletteIndices { get; }
        public bool IsIndexed => PaletteIndices != null;

        public BmpImageData(
            int width,
            int height,
            Color32[] pixels,
            Color32[] palette,
            byte[] paletteIndices)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
            Palette = palette;
            PaletteIndices = paletteIndices;
        }
    }

    internal static class BmpDecoder
    {
        private const uint BiRgb = 0;
        private const int BitmapFileHeaderSize = 14;
        private const int MinimumInfoHeaderSize = 40;

        public static BmpImageData Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            RequireRange(bytes, 0, BitmapFileHeaderSize + MinimumInfoHeaderSize, "BMP header");
            if (bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            {
                throw new FormatException("The file does not have a BMP signature.");
            }

            uint pixelDataOffset = ReadUInt32(bytes, 10);
            uint dibHeaderSize = ReadUInt32(bytes, 14);
            if (dibHeaderSize < MinimumInfoHeaderSize)
            {
                throw new NotSupportedException(
                    $"BMP DIB headers smaller than {MinimumInfoHeaderSize} bytes are not supported.");
            }

            long dibEnd = BitmapFileHeaderSize + (long)dibHeaderSize;
            if (dibEnd > bytes.LongLength)
            {
                throw new FormatException("The BMP DIB header extends beyond the end of the file.");
            }

            int width = ReadInt32(bytes, 18);
            int signedHeight = ReadInt32(bytes, 22);
            ushort planes = ReadUInt16(bytes, 26);
            ushort bitsPerPixel = ReadUInt16(bytes, 28);
            uint compression = ReadUInt32(bytes, 30);

            if (width <= 0 || signedHeight == 0)
            {
                throw new FormatException("BMP width must be positive and height must be non-zero.");
            }

            if (planes != 1)
            {
                throw new FormatException($"BMP plane count must be 1, but the file declares {planes}.");
            }

            if (compression != BiRgb)
            {
                throw new NotSupportedException(
                    $"BMP compression mode {compression} is not supported; only uncompressed BI_RGB files are supported.");
            }

            if (bitsPerPixel != 1 && bitsPerPixel != 4 && bitsPerPixel != 8 &&
                bitsPerPixel != 16 && bitsPerPixel != 24 && bitsPerPixel != 32)
            {
                throw new NotSupportedException(
                    $"{bitsPerPixel}-bit BMP pixels are not supported. Expected 1, 4, 8, 16, 24, or 32 bits per pixel.");
            }

            long absoluteHeight = Math.Abs((long)signedHeight);
            if (absoluteHeight > int.MaxValue)
            {
                throw new FormatException("The BMP height is too large to load.");
            }

            int height = (int)absoluteHeight;
            bool topDown = signedHeight < 0;
            bool indexed = bitsPerPixel <= 8;
            Color32[] palette = null;

            if (indexed)
            {
                uint declaredColorCount = ReadUInt32(bytes, 46);
                int maximumColorCount = 1 << bitsPerPixel;
                int colorCount = declaredColorCount == 0 ? maximumColorCount : checked((int)declaredColorCount);

                if (colorCount <= 0 || colorCount > maximumColorCount)
                {
                    throw new FormatException(
                        $"The BMP declares {colorCount} palette colors, but {bitsPerPixel}-bit pixels allow at most {maximumColorCount}.");
                }

                long paletteOffset = dibEnd;
                long paletteEnd = paletteOffset + colorCount * 4L;
                if (paletteEnd > pixelDataOffset)
                {
                    throw new FormatException("The BMP palette overlaps its pixel data.");
                }

                RequireRange(bytes, paletteOffset, colorCount * 4L, "BMP palette");
                palette = new Color32[colorCount];
                for (int i = 0; i < colorCount; i++)
                {
                    int entryOffset = checked((int)(paletteOffset + i * 4L));
                    palette[i] = new Color32(
                        bytes[entryOffset + 2],
                        bytes[entryOffset + 1],
                        bytes[entryOffset],
                        byte.MaxValue);
                }
            }

            if (pixelDataOffset < dibEnd || pixelDataOffset > bytes.LongLength)
            {
                throw new FormatException("The BMP pixel-data offset is outside the file.");
            }

            long rowStride = (((long)width * bitsPerPixel + 31L) / 32L) * 4L;
            long pixelDataLength = rowStride * height;
            RequireRange(bytes, pixelDataOffset, pixelDataLength, "BMP pixel data");

            long pixelCount = (long)width * height;
            if (pixelCount > int.MaxValue)
            {
                throw new FormatException("The BMP contains too many pixels to load.");
            }

            var pixels = new Color32[(int)pixelCount];
            byte[] paletteIndices = indexed ? new byte[(int)pixelCount] : null;

            for (int fileRow = 0; fileRow < height; fileRow++)
            {
                int z = topDown ? height - 1 - fileRow : fileRow;
                int destinationOffset = z * width;
                int sourceOffset = checked((int)(pixelDataOffset + fileRow * rowStride));

                for (int x = 0; x < width; x++)
                {
                    int destinationIndex = destinationOffset + x;
                    if (indexed)
                    {
                        byte paletteIndex = ReadPaletteIndex(bytes, sourceOffset, x, bitsPerPixel);
                        if (paletteIndex >= palette.Length)
                        {
                            throw new FormatException(
                                $"BMP pixel ({x}, {fileRow}) references missing palette index {paletteIndex}.");
                        }

                        paletteIndices[destinationIndex] = paletteIndex;
                        pixels[destinationIndex] = palette[paletteIndex];
                    }
                    else
                    {
                        pixels[destinationIndex] = ReadDirectColor(bytes, sourceOffset, x, bitsPerPixel);
                    }
                }
            }

            return new BmpImageData(width, height, pixels, palette, paletteIndices);
        }

        private static byte ReadPaletteIndex(byte[] bytes, int rowOffset, int x, int bitsPerPixel)
        {
            switch (bitsPerPixel)
            {
                case 1:
                    return (byte)((bytes[rowOffset + (x >> 3)] >> (7 - (x & 7))) & 0x01);
                case 4:
                    byte packed = bytes[rowOffset + (x >> 1)];
                    return (byte)((x & 1) == 0 ? packed >> 4 : packed & 0x0F);
                case 8:
                    return bytes[rowOffset + x];
                default:
                    throw new ArgumentOutOfRangeException(nameof(bitsPerPixel));
            }
        }

        private static Color32 ReadDirectColor(byte[] bytes, int rowOffset, int x, int bitsPerPixel)
        {
            if (bitsPerPixel == 16)
            {
                int offset = rowOffset + x * 2;
                ushort packed = ReadUInt16(bytes, offset);
                return new Color32(
                    ExpandFiveBits((packed >> 10) & 0x1F),
                    ExpandFiveBits((packed >> 5) & 0x1F),
                    ExpandFiveBits(packed & 0x1F),
                    byte.MaxValue);
            }

            int bytesPerPixel = bitsPerPixel / 8;
            int pixelOffset = rowOffset + x * bytesPerPixel;
            return new Color32(
                bytes[pixelOffset + 2],
                bytes[pixelOffset + 1],
                bytes[pixelOffset],
                byte.MaxValue);
        }

        private static byte ExpandFiveBits(int value)
        {
            return (byte)((value << 3) | (value >> 2));
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] |
                          (bytes[offset + 1] << 8) |
                          (bytes[offset + 2] << 16) |
                          (bytes[offset + 3] << 24));
        }

        private static int ReadInt32(byte[] bytes, int offset)
        {
            return unchecked((int)ReadUInt32(bytes, offset));
        }

        private static void RequireRange(byte[] bytes, long offset, long length, string description)
        {
            if (offset < 0 || length < 0 || offset > bytes.LongLength || length > bytes.LongLength - offset)
            {
                throw new FormatException($"The {description} extends beyond the end of the file.");
            }
        }
    }
}
