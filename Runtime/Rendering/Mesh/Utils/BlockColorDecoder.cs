using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Rendering.Mesh
{
    /// <summary>
    /// Utility for decoding RGB555 block IDs to colors.
    /// </summary>
    public static class BlockColorDecoder
    {
        /// <summary>
        /// Decodes a block ID (RGB555 + emission) to a Color32.
        /// Block ID format:
        /// - Bits 11-15: Red (5 bits, 0-31)
        /// - Bits 6-10:  Green (5 bits, 0-31)
        /// - Bits 1-5:   Blue (5 bits, 0-31)
        /// - Bit 0:      Emission flag
        /// </summary>
        /// <param name="blockID">The 16-bit block ID</param>
        /// <returns>Decoded color with emission in alpha channel</returns>
        public static Color32 DecodeColor32(ushort blockID)
        {
            byte r = (byte)(((blockID >> 11) & 0x1F) * 255 / 31);
            byte g = (byte)(((blockID >> 6) & 0x1F) * 255 / 31);
            byte b = (byte)(((blockID >> 1) & 0x1F) * 255 / 31);
            byte emission = (byte)((blockID & 0x1) * 255);

            return new Color32(r, g, b, emission);
        }

        /// <summary>
        /// Decodes a block ID to a Color (normalized float values).
        /// </summary>
        /// <param name="blockID">The 16-bit block ID</param>
        /// <returns>Decoded color with emission in alpha channel</returns>
        public static Color DecodeColor(ushort blockID)
        {
            float r = ((blockID >> 11) & 0x1F) / 31.0f;
            float g = ((blockID >> 6) & 0x1F) / 31.0f;
            float b = ((blockID >> 1) & 0x1F) / 31.0f;
            float emission = (blockID & 0x1);

            return new Color(r, g, b, emission);
        }

        /// <summary>
        /// Decodes a block ID to a half4 for vertex data.
        /// </summary>
        /// <param name="blockID">The 16-bit block ID</param>
        /// <returns>Decoded color as half-precision float4</returns>
        public static half4 DecodeColorHalf(ushort blockID)
        {
            half r = (half)(((blockID >> 11) & 0x1F) / 31.0f);
            half g = (half)(((blockID >> 6) & 0x1F) / 31.0f);
            half b = (half)(((blockID >> 1) & 0x1F) / 31.0f);
            half emission = (half)(blockID & 0x1);

            return new half4(r, g, b, emission);
        }

        /// <summary>
        /// Checks if a block ID has emission enabled.
        /// </summary>
        /// <param name="blockID">The 16-bit block ID</param>
        /// <returns>True if emission bit is set</returns>
        public static bool HasEmission(ushort blockID)
        {
            return (blockID & 0x1) != 0;
        }

        /// <summary>
        /// Extracts RGB components as normalized float3.
        /// </summary>
        /// <param name="blockID">The 16-bit block ID</param>
        /// <returns>RGB as float3 (0-1 range)</returns>
        public static float3 DecodeRGB(ushort blockID)
        {
            float r = ((blockID >> 11) & 0x1F) / 31.0f;
            float g = ((blockID >> 6) & 0x1F) / 31.0f;
            float b = ((blockID >> 1) & 0x1F) / 31.0f;

            return new float3(r, g, b);
        }
    }
}
