using Unity.Collections;
using UnityEngine;
using VoxReader.Interfaces;

namespace Voxelis
{
    /// <summary>
    /// Loads voxel data from MagicaVoxel .vox files into a VoxelEntity.
    /// </summary>
    /// <remarks>
    /// Uses the VoxReader library to parse .vox files and converts the voxel data
    /// into blocks. Note that the Y and Z axes are swapped during import to match
    /// Unity's coordinate system. Colors are quantized from 8-bit to 5-bit per channel.
    /// </remarks>
    [RequireComponent(typeof(VoxelEntity))]
    public class VoxFileLoader : MonoBehaviour
    {
        /// <summary>
        /// Path to the .vox file to load.
        /// </summary>
        [SerializeField] private string voxFilePath;

        private VoxelEntity entity;

        /// <summary>
        /// Loads the .vox file and populates the VoxelEntity with the voxel data.
        /// </summary>
        /// <remarks>
        /// Reads all models from the .vox file and imports each voxel.
        /// The Y and Z coordinates are swapped to convert from MagicaVoxel's
        /// coordinate system to Unity's. RGB colors are bit-shifted from 8-bit
        /// to 5-bit per channel (>> 3) to match the Block color format.
        /// </remarks>
        public void Initialize()
        {
            entity = GetComponent<VoxelEntity>();

            IVoxFile voxFile = VoxReader.VoxReader.Read(voxFilePath);
            foreach (var model in voxFile.Models)
            {
                foreach (var voxel in model.Voxels)
                {
                    entity.SetBlock(
                        new Vector3Int(
                            voxel.GlobalPosition.X,
                            voxel.GlobalPosition.Z,
                            voxel.GlobalPosition.Y),
                        new Block(voxel.Color.R >> 3, voxel.Color.G >> 3, voxel.Color.B >> 3, false));
                }
            }
        }

        /// <summary>
        /// Initializes the loader on Start and updates the physics body.
        /// </summary>
        void Start()
        {
            Initialize();
        }
    }
}