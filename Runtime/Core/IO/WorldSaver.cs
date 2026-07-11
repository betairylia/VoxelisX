using System.Collections.Generic;
using Unity.Mathematics;
using Voxelis.Utils;

namespace Voxelis.IO
{
    /// <summary>
    /// Top-level save orchestrator. Walks a list of <see cref="VoxelEntity"/> instances,
    /// serializes each entity's sectors (preview + Deflate-compressed payload), and writes
    /// the resulting <c>.vxw</c> file.
    /// </summary>
    public static class WorldSaver
    {
        public static void Save(string path, IReadOnlyList<(Guid128 Guid, VoxelEntity Entity)> entities)
        {
            using var writer = SingleFileSaveStorage.OpenWrite(path);
            for (int i = 0; i < entities.Count; i++)
            {
                var (guid, entity) = entities[i];
                SaveEntity(writer, guid, entity);
            }
            writer.Commit();
        }

        public static void Save(string path, IReadOnlyList<(Guid128 Guid, VoxelEntity Entity, VoxelBodyState Body)> entities)
        {
            using var writer = SingleFileSaveStorage.OpenWrite(path);
            for (int i = 0; i < entities.Count; i++)
            {
                var (guid, entity, body) = entities[i];
                SaveEntity(writer, guid, entity, body);
            }
            writer.Commit();
        }

        public static unsafe void SaveEntity(
            IWorldSaveWriter writer, Guid128 guid, VoxelEntity entity, VoxelBodyState body = VoxelBodyState.Off)
        {
            var data = entity.GetDataCopy();
            var transformRec = new EntityTransformRecord(data.transform.pos, data.transform.rot);
            var entityRecord = new EntityRecord(guid, transformRec, data.entityRequireUpdateFlags, body);
            writer.WriteEntity(in entityRecord, EnumerateSectors(data));
        }

        private static IEnumerable<SectorWriteRecord> EnumerateSectors(VoxelEntityData data)
        {
            foreach (var kvp in data.sectors)
            {
                yield return BuildSectorWriteRecord(kvp.Key, kvp.Value);
            }
        }

        private static unsafe SectorWriteRecord BuildSectorWriteRecord(int3 coord, SectorHandle sector)
        {
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            PreviewBuilder.Build(sector.Ptr, preview);
            ref Sector source = ref sector.Get();
            byte[] payload = SectorSerializer.Pack(in source);
            return new SectorWriteRecord(coord, preview, payload);
        }
    }
}
