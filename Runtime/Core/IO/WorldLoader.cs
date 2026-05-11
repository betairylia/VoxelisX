using System;
using Unity.Collections;

namespace Voxelis.IO
{
    /// <summary>
    /// Top-level load orchestrator. Reads entity records and per-sector payloads from an
    /// <see cref="IWorldSaveReader"/>, materializes <see cref="Sector"/> instances, and
    /// attaches them to caller-provided <see cref="VoxelEntity"/> targets.
    ///
    /// On load the saved <c>requireUpdate</c> state is restored verbatim, then renderer +
    /// geometry bits (<c>BrickAdded | GeometryWithLocalNeighbor | Geometry</c>) are OR'd
    /// onto every non-empty brick so the first frame uploads meshes — without spuriously
    /// firing automata work that wasn't pending pre-save.
    /// </summary>
    public static class WorldLoader
    {
        public const ushort FirstFrameUploadFlags =
            (ushort)(DirtyFlags.BrickAdded | DirtyFlags.GeometryWithLocalNeighbor | DirtyFlags.Geometry);

        public static void Load(string path, Func<EntityRecord, VoxelEntity> entityFactory)
        {
            using var reader = SingleFileSaveStorage.OpenRead(path);
            for (int i = 0; i < reader.EntityCount; i++)
            {
                var rec = reader.ReadEntityRecord(i);
                var entity = entityFactory(rec);
                if (entity == null) continue;
                LoadEntity(reader, i, in rec, entity);
            }
        }

        public static unsafe void LoadEntity(IWorldSaveReader reader, int entityIndex, in EntityRecord rec, VoxelEntity entity)
        {
            entity.transform.SetPositionAndRotation(rec.Transform.Position, rec.Transform.Rotation);
            entity.SyncCurrentTransformToData(0f);

            var sectorIndex = reader.ReadSectorIndex(entityIndex);
            for (int s = 0; s < sectorIndex.Count; s++)
            {
                var entry = sectorIndex[s];
                byte[] payload = reader.ReadPayload(entityIndex, entry.Coord);
                var sector = SectorSerializer.Unpack(payload, Allocator.Persistent);
                entity.CopyAndAddSectorAt(entry.Coord, sector);
            }

            entity.RefreshAllocatedBrickLists();
            ApplyFirstFrameUploadFlags(entity);
            ApplyEntityRequireUpdateFlags(entity, rec.EntityRequireUpdateFlags);
        }

        // Accumulating the dirty flags so renderers can pick them up after dirty propagation
        private static unsafe void ApplyFirstFrameUploadFlags(VoxelEntity entity)
        {
            foreach (var kvp in entity.Sectors)
            {
                ref Sector sector = ref kvp.Value.Get();
                int nonEmpty = sector.NonEmptyBricks.Length;
                for (int i = 0; i < nonEmpty; i++)
                {
                    short absBrickIdx = sector.NonEmptyBricks[i];
                    sector.brickDirtyFlags[absBrickIdx] |= FirstFrameUploadFlags;
                }
                sector.sectorDirtyFlags |= FirstFrameUploadFlags;
            }
        }

        private static void ApplyEntityRequireUpdateFlags(VoxelEntity entity, ushort entityRequireUpdateFlags)
        {
            var data = entity.GetDataCopy();
            data.entityRequireUpdateFlags |= entityRequireUpdateFlags;
            // Also fold in the first-frame upload bits at entity level for consistency with sector aggregates.
            data.entityDirtyFlags |= FirstFrameUploadFlags;
            entity.CopyDataFrom(data);
        }
    }
}
