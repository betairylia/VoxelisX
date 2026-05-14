using System.IO;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis
{
    /// <summary>
    /// Loads voxel data from VL32 files into a <see cref="VoxelEntity"/>.
    /// </summary>
    /// <remarks>
    /// VL32 is a headerless stream of 16-byte records:
    /// big-endian i32 x, y, z followed by u8 alpha, red, green, blue.
    /// Records with alpha equal to zero are treated as empty and skipped.
    /// Colors are quantized from 8-bit channels to the 5-bit channels used by <see cref="Block"/>.
    /// </remarks>
    [RequireComponent(typeof(VoxelEntity))]
    public class VL32FileLoader : MonoBehaviour
    {
        private const int RecordBytes = 16;

        /// <summary>
        /// Path to the .vl32 file to load.
        /// </summary>
        [SerializeField] private string vl32FilePath;

        /// <summary>
        /// Swaps the VL32 Y and Z axes during import to match Unity-style coordinates.
        /// </summary>
        [SerializeField] private bool swapYAndZ = true;

        private VoxelEntity entity;

        /// <summary>
        /// Loads the VL32 file and populates the VoxelEntity with the voxel data.
        /// </summary>
        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(vl32FilePath))
            {
                Debug.LogWarning($"{nameof(VL32FileLoader)} has no VL32 file path.", this);
                return;
            }

            if (!File.Exists(vl32FilePath))
            {
                Debug.LogWarning($"{nameof(VL32FileLoader)} could not find VL32 file: {vl32FilePath}", this);
                return;
            }

            entity = GetComponent<VoxelEntity>();

            using FileStream stream = File.OpenRead(vl32FilePath);
            using BinaryReader reader = new BinaryReader(stream);

            long fullRecordCount = stream.Length / RecordBytes;
            long trailingBytes = stream.Length % RecordBytes;

            for (long i = 0; i < fullRecordCount; i++)
            {
                int x = ReadInt32BigEndian(reader);
                int y = ReadInt32BigEndian(reader);
                int z = ReadInt32BigEndian(reader);

                byte alpha = reader.ReadByte();
                byte red = reader.ReadByte();
                byte green = reader.ReadByte();
                byte blue = reader.ReadByte();

                if (alpha == 0)
                {
                    continue;
                }

                int3 position = swapYAndZ
                    ? new int3(x, z, y)
                    : new int3(x, y, z);

                entity.SetBlock(position, new Block(red >> 3, green >> 3, blue >> 3, false));
            }

            if (trailingBytes != 0)
            {
                Debug.LogWarning(
                    $"{nameof(VL32FileLoader)} ignored {trailingBytes} trailing byte(s) in {vl32FilePath}.",
                    this);
            }
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            uint b0 = reader.ReadByte();
            uint b1 = reader.ReadByte();
            uint b2 = reader.ReadByte();
            uint b3 = reader.ReadByte();

            return unchecked((int)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3));
        }

        private void Start()
        {
            Initialize();
        }
    }
}
