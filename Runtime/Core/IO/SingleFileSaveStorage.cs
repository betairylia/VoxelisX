using System;
using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;

namespace Voxelis.IO
{
    /// <summary>
    /// Single-file <c>.vxw</c> implementation of <see cref="IWorldSaveWriter"/> / <see cref="IWorldSaveReader"/>.
    /// Writes are atomic: a <c>.tmp</c> sibling is written and then renamed in place on <see cref="IWorldSaveWriter.Finish"/>.
    /// </summary>
    public sealed class SingleFileSaveStorage : IWorldSaveWriter, IWorldSaveReader
    {
        private readonly string _path;
        private readonly string _tmpPath;
        private readonly bool _isWriting;
        private FileStream _stream;
        private BinaryWriter _writer;
        private BinaryReader _reader;

        // Write state
        private readonly List<EntityRecord> _entityRecords = new();
        private readonly List<(long IndexOffset, int SectorCount)> _entityIndexLocations = new();
        private readonly List<SectorIndexEntry> _currentSectorEntries = new();
        private bool _entityOpen;
        private Guid _pendingGuid;
        private EntityTransformRecord _pendingTransform;
        private ushort _pendingFlags;

        // Read state
        private SaveHeader _header;
        private List<EntityRecord> _readEntities;
        private List<SectorIndexEntry>[] _readSectorIndices;
        private Dictionary<int3, SectorIndexEntry>[] _readSectorMaps;

        public static SingleFileSaveStorage OpenWrite(string path)
        {
            var s = new SingleFileSaveStorage(path, isWrite: true);
            s.ReserveHeader();
            return s;
        }

        public static SingleFileSaveStorage OpenRead(string path)
        {
            var s = new SingleFileSaveStorage(path, isWrite: false);
            s.LoadIndex();
            return s;
        }

        private SingleFileSaveStorage(string path, bool isWrite)
        {
            _path = path;
            _isWriting = isWrite;

            if (isWrite)
            {
                _tmpPath = path + ".tmp";
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _stream = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                _writer = new BinaryWriter(_stream);
            }
            else
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                _reader = new BinaryReader(_stream);
            }
        }

        private void ReserveHeader()
        {
            byte[] zeros = new byte[WorldSaveFormat.HeaderBytes];
            _writer.Write(zeros);
        }

        // ---------- Write ----------

        public void BeginEntity(Guid guid, in EntityTransformRecord transform, ushort entityRequireUpdateFlags)
        {
            EnsureWriting();
            if (_entityOpen) throw new InvalidOperationException("Previous entity not ended.");
            _entityOpen = true;
            _currentSectorEntries.Clear();
            _pendingGuid = guid;
            _pendingTransform = transform;
            _pendingFlags = entityRequireUpdateFlags;
        }

        public void WriteSector(int3 coord, uint[] preview, byte[] compressedPayload)
        {
            EnsureWriting();
            if (!_entityOpen) throw new InvalidOperationException("BeginEntity not called.");
            if (preview == null || preview.Length != Sector.BRICKS_IN_SECTOR)
                throw new ArgumentException($"preview must have {Sector.BRICKS_IN_SECTOR} entries.", nameof(preview));
            if (compressedPayload == null) throw new ArgumentNullException(nameof(compressedPayload));

            long regionOffset = _stream.Position;

            byte[] previewBytes = new byte[WorldSaveFormat.PreviewBytes];
            Buffer.BlockCopy(preview, 0, previewBytes, 0, previewBytes.Length);
            _writer.Write(previewBytes);

            _writer.Write((uint)compressedPayload.Length);
            _writer.Write(compressedPayload);

            long regionEnd = _stream.Position;
            _currentSectorEntries.Add(new SectorIndexEntry(
                coord,
                (ulong)regionOffset,
                (uint)(regionEnd - regionOffset)));
        }

        public void EndEntity()
        {
            EnsureWriting();
            if (!_entityOpen) throw new InvalidOperationException("BeginEntity not called.");

            long indexOffset = _stream.Position;
            int sectorCount = _currentSectorEntries.Count;
            for (int i = 0; i < sectorCount; i++)
            {
                var e = _currentSectorEntries[i];
                _writer.Write(e.Coord.x);
                _writer.Write(e.Coord.y);
                _writer.Write(e.Coord.z);
                _writer.Write(e.RegionOffset);
                _writer.Write(e.RegionSize);
            }

            _entityRecords.Add(new EntityRecord(_pendingGuid, _pendingTransform, _pendingFlags));
            _entityIndexLocations.Add((indexOffset, sectorCount));
            _entityOpen = false;
        }

        public void Finish()
        {
            EnsureWriting();
            if (_entityOpen) throw new InvalidOperationException("Entity not ended.");

            long entityTableOffset = _stream.Position;
            _writer.Write((uint)_entityRecords.Count);
            for (int i = 0; i < _entityRecords.Count; i++)
            {
                var rec = _entityRecords[i];
                _writer.Write(rec.Guid.ToByteArray());
                _writer.Write(rec.Transform.Position.x);
                _writer.Write(rec.Transform.Position.y);
                _writer.Write(rec.Transform.Position.z);
                _writer.Write(rec.Transform.Rotation.value.x);
                _writer.Write(rec.Transform.Rotation.value.y);
                _writer.Write(rec.Transform.Rotation.value.z);
                _writer.Write(rec.Transform.Rotation.value.w);
                _writer.Write(rec.EntityRequireUpdateFlags);
                _writer.Write((ulong)_entityIndexLocations[i].IndexOffset);
                _writer.Write((uint)_entityIndexLocations[i].SectorCount);
            }

            // Backpatch header at offset 0
            _stream.Position = 0;
            _writer.Write(WorldSaveFormat.Magic);
            _writer.Write(WorldSaveFormat.CurrentVersion);
            _writer.Write((ushort)SaveFlags.Deflate);
            _writer.Write((ulong)entityTableOffset);
            // Remaining reserved bytes stay zero (allocated by ReserveHeader).

            _writer.Flush();
            _writer.Dispose();
            _stream.Dispose();
            _writer = null;
            _stream = null;

            if (File.Exists(_path)) File.Delete(_path);
            File.Move(_tmpPath, _path);
        }

        // ---------- Read ----------

        private void LoadIndex()
        {
            EnsureReading();
            _stream.Position = 0;
            uint magic = _reader.ReadUInt32();
            if (magic != WorldSaveFormat.Magic)
                throw new InvalidDataException($"Bad save file magic 0x{magic:X8}.");

            ushort version = _reader.ReadUInt16();
            ushort flagsRaw = _reader.ReadUInt16();
            ulong entityTableOffset = _reader.ReadUInt64();
            _header = new SaveHeader(version, (SaveFlags)flagsRaw);

            if (version > WorldSaveFormat.CurrentVersion)
                throw new InvalidDataException($"Save file version {version} is newer than supported ({WorldSaveFormat.CurrentVersion}).");

            _stream.Position = (long)entityTableOffset;
            int entityCount = (int)_reader.ReadUInt32();
            _readEntities = new List<EntityRecord>(entityCount);
            var indexLocations = new (long, int)[entityCount];

            for (int i = 0; i < entityCount; i++)
            {
                byte[] guidBytes = _reader.ReadBytes(16);
                var guid = new Guid(guidBytes);
                var pos = new float3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
                var rotV = new float4(
                    _reader.ReadSingle(),
                    _reader.ReadSingle(),
                    _reader.ReadSingle(),
                    _reader.ReadSingle());
                var rot = new quaternion(rotV);
                ushort entFlags = _reader.ReadUInt16();
                ulong idxOff = _reader.ReadUInt64();
                uint sectCount = _reader.ReadUInt32();

                _readEntities.Add(new EntityRecord(guid, new EntityTransformRecord(pos, rot), entFlags));
                indexLocations[i] = ((long)idxOff, (int)sectCount);
            }

            _readSectorIndices = new List<SectorIndexEntry>[entityCount];
            _readSectorMaps = new Dictionary<int3, SectorIndexEntry>[entityCount];

            for (int i = 0; i < entityCount; i++)
            {
                var (off, cnt) = indexLocations[i];
                _stream.Position = off;
                var list = new List<SectorIndexEntry>(cnt);
                var map = new Dictionary<int3, SectorIndexEntry>(cnt);
                for (int s = 0; s < cnt; s++)
                {
                    var coord = new int3(_reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32());
                    ulong regOff = _reader.ReadUInt64();
                    uint regSize = _reader.ReadUInt32();
                    var entry = new SectorIndexEntry(coord, regOff, regSize);
                    list.Add(entry);
                    map[coord] = entry;
                }
                _readSectorIndices[i] = list;
                _readSectorMaps[i] = map;
            }
        }

        public SaveHeader Header => _header;
        public int EntityCount => _readEntities?.Count ?? 0;

        public EntityRecord ReadEntityRecord(int entityIndex)
        {
            EnsureReading();
            return _readEntities[entityIndex];
        }

        public IReadOnlyList<SectorIndexEntry> ReadSectorIndex(int entityIndex)
        {
            EnsureReading();
            return _readSectorIndices[entityIndex];
        }

        public uint[] ReadPreview(int entityIndex, int3 coord)
        {
            EnsureReading();
            var entry = _readSectorMaps[entityIndex][coord];
            _stream.Position = (long)entry.RegionOffset;
            byte[] previewBytes = _reader.ReadBytes(WorldSaveFormat.PreviewBytes);
            if (previewBytes.Length != WorldSaveFormat.PreviewBytes)
                throw new EndOfStreamException("Sector region truncated reading preview.");
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            Buffer.BlockCopy(previewBytes, 0, preview, 0, previewBytes.Length);
            return preview;
        }

        public byte[] ReadPayload(int entityIndex, int3 coord)
        {
            EnsureReading();
            var entry = _readSectorMaps[entityIndex][coord];
            _stream.Position = (long)entry.RegionOffset + WorldSaveFormat.PreviewBytes;
            uint payloadSize = _reader.ReadUInt32();
            byte[] payload = _reader.ReadBytes((int)payloadSize);
            if (payload.Length != (int)payloadSize)
                throw new EndOfStreamException("Sector payload truncated.");
            return payload;
        }

        // ---------- Lifecycle ----------

        public void Dispose()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _stream?.Dispose();
            _writer = null;
            _reader = null;
            _stream = null;
        }

        private void EnsureWriting()
        {
            if (!_isWriting) throw new InvalidOperationException("Storage is open for read.");
            if (_stream == null) throw new ObjectDisposedException(nameof(SingleFileSaveStorage));
        }

        private void EnsureReading()
        {
            if (_isWriting) throw new InvalidOperationException("Storage is open for write.");
            if (_stream == null) throw new ObjectDisposedException(nameof(SingleFileSaveStorage));
        }
    }
}
