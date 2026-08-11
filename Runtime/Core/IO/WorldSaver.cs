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

        public static void Save(
            string path,
            IReadOnlyList<(Guid128 Guid, VoxelEntity Entity, bool HasBody, float3 LinearVelocity, float3 AngularVelocity)> entities)
        {
            using var writer = SingleFileSaveStorage.OpenWrite(path);
            for (int i = 0; i < entities.Count; i++)
            {
                var (guid, entity, hasBody, linearVelocity, angularVelocity) = entities[i];
                SaveEntity(writer, guid, entity, hasBody, linearVelocity, angularVelocity);
            }
            writer.Commit();
        }

        /// <summary>
        /// Writes one entity. Staticness is read straight off the entity's own data
        /// (<see cref="VoxelEntityData.isStatic"/>) rather than passed in, so a caller cannot persist a
        /// value that disagrees with the running entity; only body presence and velocity come from
        /// the caller, since those live on <c>VoxelBody</c>.
        /// </summary>
        public static unsafe void SaveEntity(
            IWorldSaveWriter writer, Guid128 guid, VoxelEntity entity, bool hasBody = false,
            float3 linearVelocity = default, float3 angularVelocity = default)
        {
            var data = entity.GetDataCopy();
            var transformRec = new EntityTransformRecord(data.transform.pos, data.transform.rot);
            bool isProtected = entity != null && entity.IsProtected;

            EntityFlags flags = EntityFlags.None;
            if (hasBody) flags |= EntityFlags.HasBody;
            if (data.isStatic) flags |= EntityFlags.Static;

            var entityRecord = new EntityRecord(
                guid, transformRec, data.entityRequireUpdateFlags, flags, linearVelocity, angularVelocity, isProtected);
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
