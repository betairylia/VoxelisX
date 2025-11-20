using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Voxelis.Rendering.Mesh
{
    /// <summary>
    /// Vertex structure for voxel mesh generation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelVertex
    {
        public float3 position;
        public float3 normal;
        public half4 color;      // RGB555 decoded to Color
        public float2 uv;        // Pack block ID for future texture support

        public VoxelVertex(float3 position, float3 normal, half4 color, float2 uv)
        {
            this.position = position;
            this.normal = normal;
            this.color = color;
            this.uv = uv;
        }
    }

    /// <summary>
    /// Quad structure for face emission phase.
    /// Forms linked list for efficient face storage.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MeshQuad
    {
        public int next;         // Linked list pointer (-1 = end)
        public ushort blockID;   // Block ID (RGB555+emission)

        public MeshQuad(int next, ushort blockID)
        {
            this.next = next;
            this.blockID = blockID;
        }
    }

    /// <summary>
    /// Merged quad after greedy meshing optimization.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MergedQuad
    {
        public int2 min;         // Secondary & tertiary min bounds
        public int2 max;         // Secondary & tertiary max bounds
        public ushort blockID;   // Block ID for rendering

        public MergedQuad(int2 min, int2 max, ushort blockID)
        {
            this.min = min;
            this.max = max;
            this.blockID = blockID;
        }
    }

    /// <summary>
    /// Output data from mesh generation job.
    /// </summary>
    public struct ChunkMeshData
    {
        public NativeList<VoxelVertex> vertices;
        public NativeList<int> indices;
        public Bounds bounds;

        public ChunkMeshData(int vertexCapacity, int indexCapacity, Allocator allocator)
        {
            vertices = new NativeList<VoxelVertex>(vertexCapacity, allocator);
            indices = new NativeList<int>(indexCapacity, allocator);
            bounds = new Bounds();
        }

        public void Dispose()
        {
            if (vertices.IsCreated) vertices.Dispose();
            if (indices.IsCreated) indices.Dispose();
        }

        public bool IsEmpty => vertices.Length == 0;
    }

    /// <summary>
    /// Face direction enumeration.
    /// </summary>
    internal enum FaceDirection
    {
        XPos = 0,
        XNeg = 1,
        YPos = 2,
        YNeg = 3,
        ZPos = 4,
        ZNeg = 5
    }

    /// <summary>
    /// Lookup tables for face processing.
    /// </summary>
    internal static class FaceLookup
    {
        // Primary axis index for each face (0=X, 1=Y, 2=Z)
        public static readonly int[] PrimaryIndex = { 0, 0, 1, 1, 2, 2 };

        // Secondary axis index for each face
        public static readonly int[] SecondaryIndex = { 1, 1, 2, 2, 0, 0 };

        // Tertiary axis index for each face
        public static readonly int[] TertiaryIndex = { 2, 2, 0, 0, 1, 1 };

        // Direction along primary axis (+1 or -1)
        public static readonly int[] Direction = { 1, -1, 1, -1, 1, -1 };

        // Normal vectors for each face
        public static readonly float3[] Normals = new float3[]
        {
            new float3(1, 0, 0),   // X+
            new float3(-1, 0, 0),  // X-
            new float3(0, 1, 0),   // Y+
            new float3(0, -1, 0),  // Y-
            new float3(0, 0, 1),   // Z+
            new float3(0, 0, -1)   // Z-
        };

        // Vertex winding order for CCW triangles (indices into 4-vertex quad)
        public static readonly int[,] WindingOrder = new int[6, 4]
        {
            { 3, 2, 1, 0 },  // X+
            { 0, 1, 2, 3 },  // X-
            { 3, 2, 1, 0 },  // Y+
            { 0, 1, 2, 3 },  // Y-
            { 3, 2, 1, 0 },  // Z+
            { 0, 1, 2, 3 }   // Z-
        };
    }
}
