using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Voxelis.IO
{
    /// <summary>
    /// Streaming-friendly write abstraction over a world-save backend. The single-file backend
    /// implements this with sequential writes; future per-region or per-entity backends
    /// implement the same surface — call sites do not need to change.
    /// </summary>
    public interface IWorldSaveWriter : IDisposable
    {
        /// <summary>Write one entity and all of its sectors. Sector payloads are consumed immediately and may be generated lazily.</summary>
        void WriteEntity(in EntityRecord entity, IEnumerable<SectorWriteRecord> sectors);

        /// <summary>Finalizes the file: writes the entity table, backpatches the header, atomically replaces the destination.</summary>
        void Commit();
    }

    /// <summary>
    /// Read abstraction over a world-save backend. Reads the manifest eagerly so that
    /// per-sector preview/payload reads are cheap O(1) seeks.
    /// </summary>
    public interface IWorldSaveReader : IDisposable
    {
        SaveHeader Header { get; }
        int EntityCount { get; }
        EntityRecord ReadEntityRecord(int entityIndex);
        IReadOnlyList<SectorIndexEntry> ReadSectorIndex(int entityIndex);

        /// <summary>Reads the cheap, uncompressed preview blob without touching the compressed payload.</summary>
        uint[] ReadPreview(int entityIndex, int3 coord);

        /// <summary>Reads the compressed sector payload — pass to <see cref="SectorSerializer.Unpack"/> to materialize a <see cref="Sector"/>.</summary>
        byte[] ReadPayload(int entityIndex, int3 coord);
    }

    public readonly struct SectorWriteRecord
    {
        public readonly int3 Coord;
        public readonly uint[] Preview;
        public readonly byte[] CompressedPayload;

        public SectorWriteRecord(int3 coord, uint[] preview, byte[] compressedPayload)
        {
            Coord = coord;
            Preview = preview;
            CompressedPayload = compressedPayload;
        }
    }
}
