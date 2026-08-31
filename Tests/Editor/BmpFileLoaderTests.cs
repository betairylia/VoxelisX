using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Caelix;

namespace Caelix.Tests
{
    public class BmpFileLoaderTests
    {
        [Test]
        public void DecodeFourBitBmpPreservesPaletteIndicesAndBottomUpRows()
        {
            BmpImageData image = BmpDecoder.Decode(CreateFourBitBmp());

            Assert.That(image.Width, Is.EqualTo(3));
            Assert.That(image.Height, Is.EqualTo(2));
            Assert.That(image.IsIndexed, Is.True);
            Assert.That(image.PaletteIndices, Is.EqualTo(new byte[] { 1, 2, 3, 3, 2, 1 }));
            Assert.That(image.Pixels[0], Is.EqualTo(new Color32(255, 0, 0, 255)));
            Assert.That(image.Pixels[2], Is.EqualTo(new Color32(0, 0, 255, 255)));
        }

        [Test]
        public void DecodeTopDownTwentyFourBitBmpStillExposesBottomRowAtZZero()
        {
            BmpImageData image = BmpDecoder.Decode(CreateTopDownTwentyFourBitBmp());

            Assert.That(image.IsIndexed, Is.False);
            Assert.That(image.Pixels, Is.EqualTo(new[]
            {
                new Color32(0, 0, 255, 255),
                new Color32(255, 255, 255, 255),
                new Color32(255, 0, 0, 255),
                new Color32(0, 255, 0, 255),
            }));
        }

        [Test]
        public void RefreshIndexedPaletteIncludesOnlyUsedEntriesAndPreservesTargetsByIndex()
        {
            BmpImageData image = BmpDecoder.Decode(CreateFourBitBmp());
            var existing = new List<BmpBlockConversion>
            {
                new BmpBlockConversion(2, new Color32(1, 2, 3, 255), 1234),
            };

            List<BmpBlockConversion> updated =
                BmpFileLoader.CreateUpdatedBlockConversionPalette(image, existing);

            Assert.That(updated.Count, Is.EqualTo(3));
            Assert.That(updated[0].BmpPaletteIndex, Is.EqualTo(1));
            Assert.That(updated[1].BmpPaletteIndex, Is.EqualTo(2));
            Assert.That(updated[1].BmpColor, Is.EqualTo(new Color32(0, 255, 0, 255)));
            Assert.That(updated[1].TargetBlockId, Is.EqualTo(1234));
            Assert.That(updated[2].BmpPaletteIndex, Is.EqualTo(3));
        }

        [Test]
        public void RefreshDirectColorPalettePreservesTargetsByRgbColor()
        {
            BmpImageData image = BmpDecoder.Decode(CreateTopDownTwentyFourBitBmp());
            var existing = new List<BmpBlockConversion>
            {
                new BmpBlockConversion(
                    BmpFileLoader.DirectColorPaletteIndex,
                    new Color32(255, 0, 0, 7),
                    4321),
            };

            List<BmpBlockConversion> updated =
                BmpFileLoader.CreateUpdatedBlockConversionPalette(image, existing);

            Assert.That(updated.Count, Is.EqualTo(4));
            BmpBlockConversion red = FindByColor(updated, new Color32(255, 0, 0, 255));
            Assert.That(red.BmpPaletteIndex, Is.EqualTo(BmpFileLoader.DirectColorPaletteIndex));
            Assert.That(red.TargetBlockId, Is.EqualTo(4321));
        }

        [Test]
        public void DecodeRejectsCompressedBmpWithActionableMessage()
        {
            byte[] bytes = CreateFourBitBmp();
            WriteUInt32(bytes, 30, 2);

            NotSupportedException exception = Assert.Throws<NotSupportedException>(() => BmpDecoder.Decode(bytes));
            Assert.That(exception.Message, Does.Contain("BI_RGB"));
        }

        private static BmpBlockConversion FindByColor(
            IReadOnlyList<BmpBlockConversion> palette,
            Color32 expected)
        {
            for (int i = 0; i < palette.Count; i++)
            {
                Color32 actual = palette[i].BmpColor;
                if (actual.r == expected.r && actual.g == expected.g && actual.b == expected.b)
                {
                    return palette[i];
                }
            }

            Assert.Fail($"Could not find color {expected} in the conversion palette.");
            return default;
        }

        private static byte[] CreateFourBitBmp()
        {
            const int width = 3;
            const int height = 2;
            const int paletteCount = 16;
            const int pixelOffset = 14 + 40 + paletteCount * 4;
            const int rowStride = 4;

            var bytes = new byte[pixelOffset + rowStride * height];
            WriteBitmapInfoHeader(bytes, width, height, 4, pixelOffset, paletteCount, rowStride * height);

            WritePaletteColor(bytes, 1, new Color32(255, 0, 0, 255));
            WritePaletteColor(bytes, 2, new Color32(0, 255, 0, 255));
            WritePaletteColor(bytes, 3, new Color32(0, 0, 255, 255));

            // Bottom row first: [1, 2, 3].
            bytes[pixelOffset] = 0x12;
            bytes[pixelOffset + 1] = 0x30;

            // Top row: [3, 2, 1].
            bytes[pixelOffset + rowStride] = 0x32;
            bytes[pixelOffset + rowStride + 1] = 0x10;
            return bytes;
        }

        private static byte[] CreateTopDownTwentyFourBitBmp()
        {
            const int width = 2;
            const int height = 2;
            const int pixelOffset = 14 + 40;
            const int rowStride = 8;

            var bytes = new byte[pixelOffset + rowStride * height];
            WriteBitmapInfoHeader(bytes, width, -height, 24, pixelOffset, 0, rowStride * height);

            // A top-down BMP stores its top row first: red, green.
            WriteBgr(bytes, pixelOffset, new Color32(255, 0, 0, 255));
            WriteBgr(bytes, pixelOffset + 3, new Color32(0, 255, 0, 255));

            // Bottom row: blue, white.
            WriteBgr(bytes, pixelOffset + rowStride, new Color32(0, 0, 255, 255));
            WriteBgr(bytes, pixelOffset + rowStride + 3, new Color32(255, 255, 255, 255));
            return bytes;
        }

        private static void WriteBitmapInfoHeader(
            byte[] bytes,
            int width,
            int height,
            ushort bitsPerPixel,
            int pixelOffset,
            int paletteCount,
            int pixelDataLength)
        {
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            WriteUInt32(bytes, 2, (uint)bytes.Length);
            WriteUInt32(bytes, 10, (uint)pixelOffset);
            WriteUInt32(bytes, 14, 40);
            WriteInt32(bytes, 18, width);
            WriteInt32(bytes, 22, height);
            WriteUInt16(bytes, 26, 1);
            WriteUInt16(bytes, 28, bitsPerPixel);
            WriteUInt32(bytes, 30, 0);
            WriteUInt32(bytes, 34, (uint)pixelDataLength);
            WriteUInt32(bytes, 46, (uint)paletteCount);
        }

        private static void WritePaletteColor(byte[] bytes, int paletteIndex, Color32 color)
        {
            int offset = 14 + 40 + paletteIndex * 4;
            bytes[offset] = color.b;
            bytes[offset + 1] = color.g;
            bytes[offset + 2] = color.r;
        }

        private static void WriteBgr(byte[] bytes, int offset, Color32 color)
        {
            bytes[offset] = color.b;
            bytes[offset + 1] = color.g;
            bytes[offset + 2] = color.r;
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            WriteUInt32(bytes, offset, unchecked((uint)value));
        }
    }
}
