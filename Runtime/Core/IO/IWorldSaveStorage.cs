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
        /// <summary>Open a new entity in the save. <see cref="WriteSector"/> calls between Begin/End are attributed to this entity.</summary>
        void BeginEntity(Guid guid, in EntityTransformRecord transform, ushort entityRequireUpdateFlags);

        /// <summary>Write one sector's region (uncompressed preview + compressed payload) for the currently-open entity.</summary>
        void WriteSector(int3 coord, uint[] preview, byte[] compressedPayload);

        /// <summary>Close the current entity. Writes the per-entity sector index and records the entity for the entity table.</summary>
        void EndEntity();

        /// <summary>Finalizes the file: writes the entity table, backpatches the header, atomically replaces the destination.</summary>
        void Finish();
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
}
