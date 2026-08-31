using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.Rendering.Meshing;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    public class MeshingRendererTests
    {
        [TestCase(DirtyFlags.BlockBrickAdded)]
        [TestCase(DirtyFlags.BlockBrickRemoved)]
        [TestCase(DirtyFlags.GeometryWithLocalNeighbor)]
        public void RendererRequireUpdateFlagsInvalidateChunk(DirtyFlags flags)
        {
            Assert.That(SectorMeshRenderer.RequiresRemesh((ushort)flags), Is.True);
        }

        [TestCase(DirtyFlags.None)]
        [TestCase(DirtyFlags.GeneralAutomata)]
        [TestCase(DirtyFlags.Geometry)]
        public void NonRendererRequireUpdateFlagsDoNotInvalidateChunk(DirtyFlags flags)
        {
            Assert.That(SectorMeshRenderer.RequiresRemesh((ushort)flags), Is.False);
        }

        [Test]
        public void GeneratedMeshCarriesExactBlockIdForShaderLookup()
        {
            const ushort blockId = ushort.MaxValue;
            using var scope = new SectorTestScope();
            using var vertices = new NativeList<VoxelVertex>(24, Allocator.TempJob);
            using var indices = new NativeList<int>(36, Allocator.TempJob);

            scope.Set(0, 0, 0, blockId);

            new MeshGenerationJob
            {
                sector = scope.Sector,
                chunkMin = int3.zero,
                chunkSize = new int3(1),
                vertices = vertices,
                indices = indices
            }.Execute();

            Assert.That(vertices.Length, Is.EqualTo(24));
            Assert.That(indices.Length, Is.EqualTo(36));
            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(vertices[i].blockID, Is.EqualTo(blockId));
            }
        }
    }
}
