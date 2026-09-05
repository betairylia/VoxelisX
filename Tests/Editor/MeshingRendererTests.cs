using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.Rendering.Meshing;
using Caelix.Tests.TestSupport;
using Caelix.Client;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;
using UnityEngine;

namespace Caelix.Tests
{
    public class MeshingRendererTests
    {
        [Test]
        public void ReplicatedSectorRemoval_ReleasesMeshBeforeStorageAndRebuildsReplacement()
        {
            using var server = new CaelixServer();
            var world = server.CreateWorld(CaelixWorldConfig.Default());
            LocalChannel.CreatePair(out var serverEnd, out var clientEnd);
            using var client = new CaelixClient(clientEnd, server.Types);
            server.AddConnection(serverEnd);
            var guid = new Guid128(1u, 2u, 3u, 4u);
            world.CreateEntity(guid, RigidTransform.identity, isStatic: true);
            world.SetBlock(guid, new int3(32), new Block(0x8001));
            var material = new Material(Shader.Find("Caelix/VoxelMeshDiffuse"));
            using var renderer = new VoxelMeshRenderer(128, material);
            try
            {
                server.Step();
                client.Receive();
                renderer.Source = client.World;
                client.PrepareRender();
                renderer.Update();
                client.EndFrame();
                Assert.That(renderer.SectorRendererCount, Is.EqualTo(1));
                client.World.TryGetView(guid, out EntityView view);
                Assert.That(VertexCount(view), Is.EqualTo(24));

                world.GetEntity(guid).RemoveSectorAt(int3.zero);
                server.Step();
                client.Receive();
                Assert.That(renderer.SectorRendererCount, Is.Zero, "removal releases resources during Receive");
                renderer.Update(); // Used to dereference the freed SectorHandle here.

                world.SetBlock(guid, new int3(48), new Block(0x8002));
                server.Step();
                client.Receive();
                client.PrepareRender();
                renderer.Update();
                client.EndFrame();
                Assert.That(renderer.SectorRendererCount, Is.EqualTo(1));
                Assert.That(VertexCount(view), Is.EqualTo(24));

                // Removal and recreation can also arrive together in one client frame.
                world.GetEntity(guid).RemoveSectorAt(int3.zero);
                server.Step();
                world.SetBlock(guid, new int3(64), new Block(0x8003));
                server.Step();
                client.Receive();
                client.PrepareRender();
                renderer.Update();
                Assert.That(VertexCount(view), Is.EqualTo(24));
                Assert.That(view.Data.GetBlock(new int3(48)).isEmpty, Is.True);

                // Changing source releases old renderers even when it happens outside a render tick.
                renderer.Source = null;
                Assert.That(renderer.SectorRendererCount, Is.Zero);
                renderer.Source = client.World;
                renderer.Update();
                Assert.That(VertexCount(view), Is.EqualTo(24));
                server.RemoveWorld(world);
                server.Step();
                client.Receive();
                Assert.That(renderer.Source, Is.Null);
                Assert.That(renderer.SectorRendererCount, Is.Zero);

                world = server.CreateWorld(CaelixWorldConfig.Default());
                world.CreateEntity(guid, RigidTransform.identity, isStatic: true);
                world.SetBlock(guid, new int3(80), new Block(0x8004));
                server.Step();
                client.Receive();
                renderer.Source = client.World;
                client.PrepareRender();
                renderer.Update();
                client.World.TryGetView(guid, out view);
                Assert.That(VertexCount(view), Is.EqualTo(24));
            }
            finally
            {
                renderer.Dispose();
                Object.DestroyImmediate(material);
            }
        }

        private static int VertexCount(EntityView view)
        {
            int total = 0;
            foreach (var filter in view.Component.GetComponentsInChildren<MeshFilter>(true))
                total += filter.sharedMesh != null ? filter.sharedMesh.vertexCount : 0;
            return total;
        }

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
