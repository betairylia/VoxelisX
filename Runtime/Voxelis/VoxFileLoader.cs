using Unity.Collections;
using UnityEditor;
using UnityEngine;
using VoxReader.Interfaces;

namespace Voxelis
{
    [RequireComponent(typeof(VoxelEntity))]
    public class VoxFileLoader : MonoBehaviour
    {
        [SerializeField] private string voxFilePath;
        private VoxelEntity entity;
        
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
        
        void Start()
        {
            Initialize();
            entity.UpdateBody();
        }
    }
}