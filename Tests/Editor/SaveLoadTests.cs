using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using Voxelis.IO;

namespace VoxelisX.Tests
{
    public unsafe class BrickPreviewTests
    {
        [Test]
        public void Pack_LaysOutBitsAccordingToSpec()
        {
            uint preview = BrickPreview.Pack(0xA5u, 0x42u, (ushort)0xBEEFu);

            Assert.That(BrickPreview.Occupancy(preview), Is.EqualTo(0xA5u));
            Assert.That(BrickPreview.Emission(preview), Is.EqualTo(0x42u));
            Assert.That((uint)BrickPreview.Color565(preview), Is.EqualTo(0xBEEFu));
        }

        [Test]
        public void Pack_DropsBitsOutsideTheirSlots()
        {
            uint preview = BrickPreview.Pack(0x1FFu, 0x300u, 0xFFFF);

            Assert.That(BrickPreview.Occupancy(preview), Is.EqualTo(0xFFu),
                "Occupancy must mask to 8 bits");
            Assert.That(BrickPreview.Emission(preview), Is.EqualTo(0x00u),
                "Emission must mask to 8 bits");
        }

        [Test]
        public void SubBrickIndex_MatchesSpec()
        {
            Assert.That(BrickPreview.SubBrickIndex(0, 0, 0), Is.EqualTo(0));
            Assert.That(BrickPreview.SubBrickIndex(1, 0, 0), Is.EqualTo(1));
            Assert.That(BrickPreview.SubBrickIndex(0, 1, 0), Is.EqualTo(2));
            Assert.That(BrickPreview.SubBrickIndex(0, 0, 1), Is.EqualTo(4));
            Assert.That(BrickPreview.SubBrickIndex(1, 1, 1), Is.EqualTo(7));
        }
    }

    public unsafe class PreviewBuilderTests
    {
        private static Block MakeBlock(int r, int g, int b, bool emission)
            => new Block(r, g, b, emission);

        [Test]
        public void EmptySector_ProducesAllZeroPreview()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);

                for (int i = 0; i < Sector.BRICKS_IN_SECTOR; i++)
                {
                    Assert.That(preview[i], Is.EqualTo(0u), $"Brick {i} should be zero");
                }
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void SingleBlockAtBrickCornerSetsOnlyOneSubBrickOccupancy()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                // Block at (0,0,0) sits inside sub-brick (0,0,0) = subIdx 0
                handle.SetBlock(0, 0, 0, MakeBlock(15, 20, 25, false));

                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);

                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.That(BrickPreview.Occupancy(preview[brickIdx]), Is.EqualTo(0x01u));
                Assert.That(BrickPreview.Emission(preview[brickIdx]), Is.EqualTo(0u));
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void BlockInOppositeSubBrickCornerSetsBit7()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                // Block at (7,7,7) sits inside sub-brick (1,1,1) = subIdx 7
                handle.SetBlock(7, 7, 7, MakeBlock(15, 20, 25, false));

                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);

                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                Assert.That(BrickPreview.Occupancy(preview[brickIdx]), Is.EqualTo(0x80u));
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void EmissiveBlockSetsEmissionMaskAtCorrespondingSubBrick()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                handle.SetBlock(4, 0, 0, MakeBlock(10, 10, 10, true));

                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);

                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                // (4,0,0) → sub (1,0,0) → subIdx 1
                Assert.That(BrickPreview.Occupancy(preview[brickIdx]), Is.EqualTo(0x02u));
                Assert.That(BrickPreview.Emission(preview[brickIdx]), Is.EqualTo(0x02u));
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void UniformColorBrickHasMatchingAverageColor()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                // Fill one brick uniformly with a single color
                const int r5 = 15, g5 = 20, b5 = 25;
                for (int z = 0; z < 8; z++)
                for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    handle.SetBlock(x, y, z, MakeBlock(r5, g5, b5, false));
                }

                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);

                int brickIdx = Sector.ToBrickIdx(0, 0, 0);
                ushort color = BrickPreview.Color565(preview[brickIdx]);
                int gotR = (color >> 11) & 0x1F;
                int gotB = color & 0x1F;
                // G in 6-bit slot = g5 << 1
                int gotG6 = (color >> 5) & 0x3F;

                Assert.That(gotR, Is.EqualTo(r5));
                Assert.That(gotG6, Is.EqualTo(g5 << 1));
                Assert.That(gotB, Is.EqualTo(b5));
                Assert.That(BrickPreview.Occupancy(preview[brickIdx]), Is.EqualTo(0xFFu),
                    "All 8 sub-bricks should be occupied");
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }
    }

    public unsafe class SectorSerializerTests
    {
        private static Block MakeBlock(int r, int g, int b, bool emission)
            => new Block(r, g, b, emission);

        [Test]
        public void Pack_Unpack_PreservesBlocksAtVariousPositions()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                Block b1 = MakeBlock(10, 20, 5, false);
                Block b2 = MakeBlock(31, 31, 31, true);
                Block b3 = MakeBlock(1, 2, 3, false);

                handle.SetBlock(0, 0, 0, b1);
                handle.SetBlock(64, 64, 64, b2);
                handle.SetBlock(127, 127, 127, b3);
                handle.SetBlock(8, 0, 0, b1);

                ref Sector source = ref handle.Get();
                int sourceCount = source.brickMap.Count;
                int sourceCapacity = source.brickMap.Capacity;

                byte[] packed = SectorSerializer.Pack(in source);
                var loaded = SectorSerializer.Unpack(packed, Allocator.Persistent);
                try
                {
                    Assert.That(loaded.brickMap.Count, Is.EqualTo(sourceCount));
                    Assert.That(loaded.brickMap.Capacity, Is.EqualTo(sourceCapacity));
                    Assert.That(loaded.GetBlock(0, 0, 0), Is.EqualTo(b1));
                    Assert.That(loaded.GetBlock(64, 64, 64), Is.EqualTo(b2));
                    Assert.That(loaded.GetBlock(127, 127, 127), Is.EqualTo(b3));
                    Assert.That(loaded.GetBlock(8, 0, 0), Is.EqualTo(b1));
                    Assert.That(loaded.GetBlock(1, 1, 1), Is.EqualTo(Block.Empty));
                }
                finally { loaded.Dispose(Allocator.Persistent); }
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void Pack_Unpack_PreservesRequireUpdateFlags()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                handle.SetBlock(0, 0, 0, MakeBlock(5, 5, 5, false));
                handle.SetBlock(32, 32, 32, MakeBlock(15, 15, 15, false));

                ref Sector source = ref handle.Get();

                const ushort autoBits = (ushort)DirtyFlags.GeneralAutomata;
                source.sectorRequireUpdateFlags = autoBits;
                source.brickRequireUpdateFlags[Sector.ToBrickIdx(0, 0, 0)] = autoBits;
                source.brickRequireUpdateFlags[Sector.ToBrickIdx(4, 4, 4)] = autoBits;

                byte[] packed = SectorSerializer.Pack(in source);
                var loaded = SectorSerializer.Unpack(packed, Allocator.Persistent);
                try
                {
                    Assert.That(loaded.sectorRequireUpdateFlags, Is.EqualTo(autoBits));
                    Assert.That(loaded.brickRequireUpdateFlags[Sector.ToBrickIdx(0, 0, 0)], Is.EqualTo(autoBits));
                    Assert.That(loaded.brickRequireUpdateFlags[Sector.ToBrickIdx(4, 4, 4)], Is.EqualTo(autoBits));
                    Assert.That(loaded.brickRequireUpdateFlags[Sector.ToBrickIdx(15, 15, 15)], Is.EqualTo((ushort)0));
                }
                finally { loaded.Dispose(Allocator.Persistent); }
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void Pack_Unpack_PreservesFreelistForIdReuseOrder()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                handle.SetBlock(0, 0, 0, MakeBlock(1, 1, 1, false));
                handle.SetBlock(8, 0, 0, MakeBlock(1, 1, 1, false));
                handle.SetBlock(16, 0, 0, MakeBlock(1, 1, 1, false));

                ref Sector source = ref handle.Get();
                int beforeRemove = source.brickMap.Capacity;
                source.brickMap.RemoveBrick(new int3(1, 0, 0));

                byte[] packed = SectorSerializer.Pack(in source);
                var loaded = SectorSerializer.Unpack(packed, Allocator.Persistent);
                try
                {
                    Assert.That(loaded.brickMap.Capacity, Is.EqualTo(beforeRemove));
                    Assert.That(loaded.brickMap.FreeCount, Is.EqualTo(1));

                    loaded.brickMap.AddBrick(new int3(3, 0, 0), out int newId, out bool exceeds);
                    Assert.That(exceeds, Is.False, "Freelist must hand back the recycled ID");
                    Assert.That(newId, Is.EqualTo(1), "Reused ID should be the freed one (1)");
                }
                finally { loaded.Dispose(Allocator.Persistent); }
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void Pack_AchievesReasonableCompressionOnDenseData()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                // Fill one brick uniformly — highly compressible
                for (int z = 0; z < 8; z++)
                for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    handle.SetBlock(x, y, z, MakeBlock(10, 15, 20, false));
                }

                ref Sector source = ref handle.Get();
                byte[] packed = SectorSerializer.Pack(in source);
                int rawBytes = source.brickMap.Capacity * Sector.BLOCKS_IN_BRICK * sizeof(uint)
                               + Sector.BRICKS_IN_SECTOR * sizeof(short)
                               + Sector.BRICKS_IN_SECTOR * sizeof(ushort)
                               + 14;

                Assert.That(packed.Length, Is.LessThan(rawBytes),
                    $"compressed ({packed.Length}) must be smaller than raw ({rawBytes})");
                UnityEngine.Debug.Log($"[SectorSerializer] raw≈{rawBytes} B, compressed={packed.Length} B, ratio={(float)rawBytes / packed.Length:F2}x");
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }
    }

    public unsafe class SingleFileSaveStorageTests
    {
        private string _tempPath;

        [SetUp]
        public void SetUp()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), $"voxelisx_test_{Guid.NewGuid():N}.vxw");
        }

        [TearDown]
        public void TearDown()
        {
            if (_tempPath != null && File.Exists(_tempPath)) File.Delete(_tempPath);
            string tmp = _tempPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        [Test]
        public void WriteRead_RoundTripsHeaderEntityAndSectorIndex()
        {
            Guid entityGuid = Guid.NewGuid();
            var transform = new EntityTransformRecord(
                new float3(1.5f, -2.0f, 3.25f),
                quaternion.Euler(0.1f, 0.2f, 0.3f));
            const ushort entityFlags = 0x1234;

            int3 coordA = new int3(0, 0, 0);
            int3 coordB = new int3(1, 2, 3);

            uint[] previewA = new uint[Sector.BRICKS_IN_SECTOR];
            uint[] previewB = new uint[Sector.BRICKS_IN_SECTOR];
            previewA[0] = 0xDEADBEEFu;
            previewA[Sector.BRICKS_IN_SECTOR - 1] = 0xCAFEBABEu;
            previewB[42] = 0xA5A5A5A5u;

            byte[] payloadA = new byte[] { 1, 2, 3, 4, 5 };
            byte[] payloadB = new byte[] { 9, 8, 7, 6 };

            using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
            {
                writer.BeginEntity(entityGuid, transform, entityFlags);
                writer.WriteSector(coordA, previewA, payloadA);
                writer.WriteSector(coordB, previewB, payloadB);
                writer.EndEntity();
                writer.Finish();
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            Assert.That(reader.Header.Version, Is.EqualTo(WorldSaveFormat.CurrentVersion));
            Assert.That(reader.Header.Flags & SaveFlags.Deflate, Is.EqualTo(SaveFlags.Deflate));
            Assert.That(reader.EntityCount, Is.EqualTo(1));

            var rec = reader.ReadEntityRecord(0);
            Assert.That(rec.Guid, Is.EqualTo(entityGuid));
            Assert.That(rec.Transform.Position.x, Is.EqualTo(transform.Position.x).Within(1e-6f));
            Assert.That(rec.Transform.Position.y, Is.EqualTo(transform.Position.y).Within(1e-6f));
            Assert.That(rec.Transform.Position.z, Is.EqualTo(transform.Position.z).Within(1e-6f));
            Assert.That(rec.Transform.Rotation.value.x, Is.EqualTo(transform.Rotation.value.x).Within(1e-6f));
            Assert.That(rec.Transform.Rotation.value.w, Is.EqualTo(transform.Rotation.value.w).Within(1e-6f));
            Assert.That(rec.EntityRequireUpdateFlags, Is.EqualTo(entityFlags));

            var index = reader.ReadSectorIndex(0);
            Assert.That(index.Count, Is.EqualTo(2));

            uint[] readPreviewA = reader.ReadPreview(0, coordA);
            Assert.That(readPreviewA, Is.EqualTo(previewA));

            byte[] readPayloadA = reader.ReadPayload(0, coordA);
            Assert.That(readPayloadA, Is.EqualTo(payloadA));

            uint[] readPreviewB = reader.ReadPreview(0, coordB);
            Assert.That(readPreviewB, Is.EqualTo(previewB));

            byte[] readPayloadB = reader.ReadPayload(0, coordB);
            Assert.That(readPayloadB, Is.EqualTo(payloadB));
        }

        [Test]
        public void ReadPreview_DoesNotRequirePayloadAccess()
        {
            // Write a sector with a recognizable preview and a deliberately non-trivial payload,
            // then confirm we can pull just the preview without seeking through the payload.
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            for (int i = 0; i < preview.Length; i++) preview[i] = (uint)(i * 7919);

            byte[] payload = new byte[8192];
            new System.Random(42).NextBytes(payload);

            int3 coord = new int3(5, 6, 7);

            using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
            {
                writer.BeginEntity(Guid.NewGuid(), default, 0);
                writer.WriteSector(coord, preview, payload);
                writer.EndEntity();
                writer.Finish();
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            uint[] readBack = reader.ReadPreview(0, coord);

            Assert.That(readBack.Length, Is.EqualTo(Sector.BRICKS_IN_SECTOR));
            Assert.That(readBack, Is.EqualTo(preview));
        }

        [Test]
        public void EndToEnd_SectorSurvivesSerializerPlusStorage()
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                handle.SetBlock(0, 0, 0, new Block(10, 20, 5, false));
                handle.SetBlock(64, 64, 64, new Block(31, 31, 31, true));

                ref Sector source = ref handle.Get();
                source.sectorRequireUpdateFlags = (ushort)DirtyFlags.GeneralAutomata;

                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);
                byte[] payload = SectorSerializer.Pack(in source);

                int3 coord = new int3(0, 0, 0);
                using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
                {
                    writer.BeginEntity(Guid.NewGuid(), default, 0);
                    writer.WriteSector(coord, preview, payload);
                    writer.EndEntity();
                    writer.Finish();
                }

                using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
                byte[] readPayload = reader.ReadPayload(0, coord);
                var loaded = SectorSerializer.Unpack(readPayload, Allocator.Persistent);
                try
                {
                    Assert.That(loaded.GetBlock(0, 0, 0), Is.EqualTo(new Block(10, 20, 5, false)));
                    Assert.That(loaded.GetBlock(64, 64, 64), Is.EqualTo(new Block(31, 31, 31, true)));
                    Assert.That(loaded.sectorRequireUpdateFlags, Is.EqualTo((ushort)DirtyFlags.GeneralAutomata));
                }
                finally { loaded.Dispose(Allocator.Persistent); }
            }
            finally { handle.Dispose(Allocator.Persistent); }
        }
    }
}
