using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Voxelis;
using Voxelis.IO;
using Voxelis.Utils;

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
                    Assert.That(loaded.GetBlock(16, 0, 0), Is.EqualTo(Block.Empty));
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
        public void Pack_ExcludesSlotAuxBufferAndRoundTripsVoxelData()
        {
            // A slot's aux buffer holds per-brick data derived from the voxels; it is recomputed on
            // load, so it must not reach the payload. The voxel data of the same slot must still
            // round-trip.
            const int extraPerBrickBytes = 8 * sizeof(ulong); // 512-voxel occupancy bitmap
            const SectorSlotId auxSlot = SectorSlotId.Reserved1;
            const ushort reservedValue = 0xABCD;

            var handle = SectorHandle.AllocEmpty();
            try
            {
                Block b1 = MakeBlock(10, 20, 5, false);
                Block b2 = MakeBlock(31, 31, 31, true);
                handle.SetBlock(0, 0, 0, b1);
                handle.SetBlock(64, 64, 64, b2);

                ref Sector source = ref handle.Get();

                // Give the slot per-voxel data (creating it) plus a derived per-brick aux buffer.
                // Bricks (0,0,0) and (8,8,8) exist thanks to the SetBlock calls above.
                source.SetVoxelSlot(auxSlot, 0, 0, 0, reservedValue);
                source.EnsureAuxAllocated(auxSlot, extraPerBrickBytes);
                source.SetBrickAux<ulong>(auxSlot, 0, 0, 0, 0, 0xDEADBEEFDEADBEEFul);
                source.SetBrickAux<ulong>(auxSlot, 8, 8, 8, 7 * sizeof(ulong), 0x0123456789ABCDEFul);

                Assert.That(source.slots[(int)auxSlot].IsCreated, Is.True);
                Assert.That(source.slots[(int)auxSlot].HasAux, Is.True);

                byte[] packed = SectorSerializer.Pack(in source);
                var loaded = SectorSerializer.Unpack(packed, Allocator.Persistent);
                try
                {
                    Assert.That(loaded.GetBlock(0, 0, 0), Is.EqualTo(b1));
                    Assert.That(loaded.GetBlock(64, 64, 64), Is.EqualTo(b2));
                    Assert.That(loaded.GetVoxelSlot<ushort>(auxSlot, 0, 0, 0), Is.EqualTo(reservedValue));
                    Assert.That(loaded.slots[(int)auxSlot].HasAux, Is.False,
                        "aux is derived data, rebuilt after load rather than persisted");
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
                int rawBytes = source.brickMap.Capacity * Sector.BLOCKS_IN_BRICK * sizeof(ushort)
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
        private static uint _guidSeed = 1;

        [SetUp]
        public void SetUp()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), $"voxelisx_test_{NewGuid128()}.vxw");
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
            Guid128 entityGuid = NewGuid128();
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
                var entityRecord = new EntityRecord(entityGuid, transform, entityFlags);
                writer.WriteEntity(in entityRecord, new[]
                {
                    new SectorWriteRecord(coordA, previewA, payloadA),
                    new SectorWriteRecord(coordB, previewB, payloadB),
                });
                writer.Commit();
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
            Assert.That(rec.Body, Is.EqualTo(VoxelBodyState.Off),
                "Records written without a body state must read back as Off");

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
        public void OpenRead_RejectsSaveWithoutDeflateFlag()
        {
            WriteMinimalSave();
            OverwriteSaveFlags(SaveFlags.None);

            Assert.Throws<InvalidDataException>(() =>
            {
                using var _ = SingleFileSaveStorage.OpenRead(_tempPath);
            });
        }

        [Test]
        public void OpenRead_RejectsUnknownSaveFlags()
        {
            WriteMinimalSave();
            OverwriteSaveFlags(SaveFlags.Deflate | (SaveFlags)0x8000);

            Assert.Throws<InvalidDataException>(() =>
            {
                using var _ = SingleFileSaveStorage.OpenRead(_tempPath);
            });
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
                var entityRecord = new EntityRecord(NewGuid128(), default, 0);
                writer.WriteEntity(in entityRecord, new[]
                {
                    new SectorWriteRecord(coord, preview, payload),
                });
                writer.Commit();
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
                    var entityRecord = new EntityRecord(NewGuid128(), default, 0);
                    writer.WriteEntity(in entityRecord, new[]
                    {
                        new SectorWriteRecord(coord, preview, payload),
                    });
                    writer.Commit();
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

        [Test]
        public void WriteRead_RoundTripsVoxelBodyState()
        {
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            byte[] payload = new byte[] { 1, 2, 3 };

            Guid128 guidOff = NewGuid128();
            Guid128 guidStatic = NewGuid128();
            Guid128 guidDynamic = NewGuid128();

            using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
            {
                var recOff = new EntityRecord(guidOff, default, 0, VoxelBodyState.Off);
                var recStatic = new EntityRecord(guidStatic, default, 0, VoxelBodyState.Static);
                var recDynamic = new EntityRecord(guidDynamic, default, 0, VoxelBodyState.Dynamic);

                writer.WriteEntity(in recOff, new[]
                {
                    new SectorWriteRecord(new int3(0, 0, 0), preview, payload),
                });
                writer.WriteEntity(in recStatic, new[]
                {
                    new SectorWriteRecord(new int3(0, 0, 0), preview, payload),
                });
                writer.WriteEntity(in recDynamic, Array.Empty<SectorWriteRecord>());
                writer.Commit();
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            Assert.That(reader.EntityCount, Is.EqualTo(3));
            Assert.That(reader.ReadEntityRecord(0).Body, Is.EqualTo(VoxelBodyState.Off));
            Assert.That(reader.ReadEntityRecord(1).Body, Is.EqualTo(VoxelBodyState.Static));
            Assert.That(reader.ReadEntityRecord(2).Body, Is.EqualTo(VoxelBodyState.Dynamic));
            Assert.That(reader.ReadEntityRecord(1).Guid, Is.EqualTo(guidStatic));
        }

        [Test]
        public void WriteRead_RoundTripsBodyVelocity()
        {
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            byte[] payload = new byte[] { 1, 2, 3 };

            Guid128 guid = NewGuid128();
            float3 linearVelocity = new float3(1.5f, -2.25f, 3.75f);
            float3 angularVelocity = new float3(-0.5f, 0.25f, -1.0f);

            using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
            {
                var rec = new EntityRecord(guid, default, 0, VoxelBodyState.Dynamic, linearVelocity, angularVelocity);
                writer.WriteEntity(in rec, new[]
                {
                    new SectorWriteRecord(new int3(0, 0, 0), preview, payload),
                });
                writer.Commit();
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            Assert.That(reader.Header.Version, Is.EqualTo(WorldSaveFormat.CurrentVersion));

            var read = reader.ReadEntityRecord(0);
            Assert.That(read.Body, Is.EqualTo(VoxelBodyState.Dynamic));
            Assert.That(read.LinearVelocity.x, Is.EqualTo(linearVelocity.x).Within(1e-6f));
            Assert.That(read.LinearVelocity.y, Is.EqualTo(linearVelocity.y).Within(1e-6f));
            Assert.That(read.LinearVelocity.z, Is.EqualTo(linearVelocity.z).Within(1e-6f));
            Assert.That(read.AngularVelocity.x, Is.EqualTo(angularVelocity.x).Within(1e-6f));
            Assert.That(read.AngularVelocity.y, Is.EqualTo(angularVelocity.y).Within(1e-6f));
            Assert.That(read.AngularVelocity.z, Is.EqualTo(angularVelocity.z).Within(1e-6f));
        }

        [Test]
        public void WriteRead_RoundTripsProtectedFlag()
        {
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            byte[] payload = new byte[] { 1, 2, 3 };

            Guid128 guidProtected = NewGuid128();
            Guid128 guidNormal = NewGuid128();

            using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
            {
                var recProtected = new EntityRecord(
                    guidProtected, default, 0, VoxelBodyState.Static, float3.zero, float3.zero, true);
                var recNormal = new EntityRecord(
                    guidNormal, default, 0, VoxelBodyState.Static, float3.zero, float3.zero, false);

                writer.WriteEntity(in recProtected, new[]
                {
                    new SectorWriteRecord(new int3(0, 0, 0), preview, payload),
                });
                writer.WriteEntity(in recNormal, new[]
                {
                    new SectorWriteRecord(new int3(0, 0, 0), preview, payload),
                });
                writer.Commit();
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            Assert.That(reader.Header.Version, Is.EqualTo(WorldSaveFormat.CurrentVersion));
            Assert.That(reader.EntityCount, Is.EqualTo(2));
            Assert.That(reader.ReadEntityRecord(0).Protected, Is.True);
            Assert.That(reader.ReadEntityRecord(0).Guid, Is.EqualTo(guidProtected));
            Assert.That(reader.ReadEntityRecord(1).Protected, Is.False);
            // Existing fields must still round-trip alongside the new v5 byte.
            Assert.That(reader.ReadEntityRecord(0).Body, Is.EqualTo(VoxelBodyState.Static));
        }

        [Test]
        public void OpenRead_Version4Save_ReadsProtectedAsFalse()
        {
            // Hand-write a v4 file: its entity records have no protected byte.
            Guid128 guid = NewGuid128();
            using (var fs = new FileStream(_tempPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(new byte[WorldSaveFormat.HeaderBytes]);
                long tableOffset = fs.Position;

                bw.Write(1u); // entityCount
                uint4 g = guid.Value;
                bw.Write(g.x);
                bw.Write(g.y);
                bw.Write(g.z);
                bw.Write(g.w);
                for (int i = 0; i < 7; i++) bw.Write(0f); // pos.xyz + rot.xyzw
                bw.Write((ushort)0x0042); // entityRequireUpdateFlags
                bw.Write((byte)VoxelBodyState.Dynamic); // v3 body state
                for (int i = 0; i < 6; i++) bw.Write(0f); // v4 velocity (linear.xyz + angular.xyz) — no v5 protected byte
                bw.Write((ulong)tableOffset); // sectorIndexOffset (no sectors, never dereferenced)
                bw.Write(0u); // sectorCount

                fs.Position = 0;
                bw.Write(WorldSaveFormat.FileMagic);
                bw.Write((ushort)4); // version 4
                bw.Write((ushort)SaveFlags.Deflate);
                bw.Write((ulong)tableOffset);
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            Assert.That(reader.Header.Version, Is.EqualTo(4));
            Assert.That(reader.EntityCount, Is.EqualTo(1));

            var rec = reader.ReadEntityRecord(0);
            Assert.That(rec.Guid, Is.EqualTo(guid));
            Assert.That(rec.Body, Is.EqualTo(VoxelBodyState.Dynamic));
            Assert.That(rec.Protected, Is.False, "Pre-v5 saves must read as not-protected");
        }

        [Test]
        public void OpenRead_Version2Save_ReadsBodyStateAsOff()
        {
            // Hand-write a v2 file: its entity records have no body-state byte.
            Guid128 guid = NewGuid128();
            using (var fs = new FileStream(_tempPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(new byte[WorldSaveFormat.HeaderBytes]);
                long tableOffset = fs.Position;

                bw.Write(1u); // entityCount
                uint4 g = guid.Value;
                bw.Write(g.x);
                bw.Write(g.y);
                bw.Write(g.z);
                bw.Write(g.w);
                for (int i = 0; i < 7; i++) bw.Write(0f); // pos.xyz + rot.xyzw
                bw.Write((ushort)0x0042); // entityRequireUpdateFlags — v2 record ends here + index location
                bw.Write((ulong)tableOffset); // sectorIndexOffset (no sectors, never dereferenced)
                bw.Write(0u); // sectorCount

                fs.Position = 0;
                bw.Write(WorldSaveFormat.FileMagic);
                bw.Write((ushort)2); // version 2
                bw.Write((ushort)SaveFlags.Deflate);
                bw.Write((ulong)tableOffset);
            }

            using var reader = SingleFileSaveStorage.OpenRead(_tempPath);
            Assert.That(reader.Header.Version, Is.EqualTo(2));
            Assert.That(reader.EntityCount, Is.EqualTo(1));

            var rec = reader.ReadEntityRecord(0);
            Assert.That(rec.Guid, Is.EqualTo(guid));
            Assert.That(rec.EntityRequireUpdateFlags, Is.EqualTo((ushort)0x0042));
            Assert.That(rec.Body, Is.EqualTo(VoxelBodyState.Off));
        }

        private void WriteMinimalSave()
        {
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            byte[] payload = new byte[] { 1, 2, 3 };
            using var writer = SingleFileSaveStorage.OpenWrite(_tempPath);
            var entityRecord = new EntityRecord(NewGuid128(), default, 0);
            writer.WriteEntity(in entityRecord, new[]
            {
                new SectorWriteRecord(new int3(0, 0, 0), preview, payload),
            });
            writer.Commit();
        }

        private void OverwriteSaveFlags(SaveFlags flags)
        {
            using var fs = new FileStream(_tempPath, FileMode.Open, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);
            fs.Position = sizeof(uint) + sizeof(ushort);
            bw.Write((ushort)flags);
        }

        private static Guid128 NewGuid128()
        {
            var random = Unity.Mathematics.Random.CreateFromIndex(_guidSeed++);
            return Guid128.NewGuid(ref random);
        }
    }
}
