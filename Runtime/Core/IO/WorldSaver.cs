using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Voxelis.IO
{
    /// <summary>
    /// Top-level save orchestrator. Walks a list of <see cref="VoxelEntity"/> instances,
    /// serializes each entity's sectors (preview + Deflate-compressed payload), and writes
    /// the resulting <c>.vxw</c> file.
    /// </summary>
    public static class WorldSaver
    {
        public static void Save(string path, IReadOnlyList<(Guid Guid, VoxelEntity Entity)> entities)
        {
            using var writer = SingleFileSaveStorage.OpenWrite(path);
            for (int i = 0; i < entities.Count; i++)
            {
                var (guid, entity) = entities[i];
                SaveEntity(writer, guid, entity);
            }
            writer.Finish();
        }

        public static unsafe void SaveEntity(IWorldSaveWriter writer, Guid guid, VoxelEntity entity)
        {
            var data = entity.GetDataCopy();
            var transformRec = new EntityTransformRecord(data.transform.pos, data.transform.rot);
            writer.BeginEntity(guid, in transformRec, data.entityRequireUpdateFlags);

            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            foreach (var kvp in data.sectors)
            {
                int3 coord = kvp.Key;

                PreviewBuilder.Build(kvp.Value.Ptr, preview);
                byte[] payload = SectorSerializer.Pack(in kvp.Value.Get());
                writer.WriteSector(coord, preview, payload);
            }

            writer.EndEntity();
        }
    }
}
