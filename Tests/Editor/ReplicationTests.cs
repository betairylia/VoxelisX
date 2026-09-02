using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Caelix.Client;
using Caelix.Net;
using Caelix.Simulation;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// Server world to client replica through the in-process channel: the replica must equal
    /// the server's Block slot after every tick, and commands must reach the server.
    /// </summary>
    public unsafe class ReplicationTests
    {
        private struct TestEvent
        {
            public int Value;
            public float3 Position;
        }

        private sealed class Rig : System.IDisposable
        {
            public readonly CaelixServer Server;
            public readonly CaelixWorld World;
            public readonly CaelixClient Client;

            public Rig()
            {
                LocalChannel.CreatePair(out LocalChannel serverEnd, out LocalChannel clientEnd);
                Server = new CaelixServer { TickRate = 100f };
                World = Server.CreateWorld(CaelixWorldConfig.Default(0, "test"));
                Client = new CaelixClient(clientEnd, Server.Types);
                Server.AddConnection(serverEnd);
            }

            /// <summary>One server tick, then one client frame.</summary>
            public void Exchange()
            {
                Server.Step();
                Client.Update();
                Client.EndFrame();
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
            }
        }

        private static readonly Guid128 EntityA = new(0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u);

        [Test]
        public void InitialSync_ReplicatesEntitiesSectorsAndBlocks()
        {
            using var rig = new Rig();
            var pose = new RigidTransform(quaternion.identity, new float3(10f, 0f, -5f));
            rig.World.CreateEntity(EntityA, pose, isStatic: true, isProtected: true);
            rig.World.SetBlock(EntityA, new int3(1, 2, 3), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(130, 2, 3), new Block(0x8002)); // second sector
            rig.World.SetBlock(EntityA, new int3(-1, 0, 0), new Block(0x8003));  // negative sector

            rig.Exchange();

            Assert.That(rig.Client.IsConnected, Is.True);
            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            Assert.That(view.IsStatic, Is.True);
            Assert.That(view.IsProtected, Is.True);
            Assert.That(view.HasBody, Is.False);
            Assert.That(math.all(view.Data.transform.pos == pose.pos), Is.True);
            Assert.That(view.Component, Is.Not.Null, "a view object is spawned for an entity the scene did not author");
            Assert.That(view.IsClientSpawned, Is.True);

            Assert.That(view.Data.sectors.Count, Is.EqualTo(rig.World.GetEntity(EntityA).sectors.Count));
            Assert.That(view.Data.GetBlock(new int3(1, 2, 3)), Is.EqualTo(new Block(0x8001)));
            Assert.That(view.Data.GetBlock(new int3(130, 2, 3)), Is.EqualTo(new Block(0x8002)));
            Assert.That(view.Data.GetBlock(new int3(-1, 0, 0)), Is.EqualTo(new Block(0x8003)));
            Assert.That(view.Data.GetBlock(new int3(0, 0, 0)).isEmpty, Is.True);
        }

        [Test]
        public void Delta_OnlyChangedBricksArriveAndReplicaMatches()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            for (int x = 0; x < 16; x++)
            {
                rig.World.SetBlock(EntityA, new int3(x, 0, 0), new Block(0x8001));
            }

            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);

            // A quiet tick sends nothing for known sectors.
            rig.Exchange();

            rig.World.SetBlock(EntityA, new int3(3, 0, 0), Block.Empty);
            rig.World.SetBlock(EntityA, new int3(40, 40, 40), new Block(0x8009));
            rig.Exchange();

            Assert.That(view.Data.GetBlock(new int3(3, 0, 0)).isEmpty, Is.True);
            Assert.That(view.Data.GetBlock(new int3(4, 0, 0)), Is.EqualTo(new Block(0x8001)));
            Assert.That(view.Data.GetBlock(new int3(40, 40, 40)), Is.EqualTo(new Block(0x8009)));
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
        }

        [Test]
        public void ClientFrame_MarksRendererFlagsOnlyForAppliedBricks()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(8, 8, 8), new Block(0x8001));
            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);

            rig.World.SetBlock(EntityA, new int3(9, 8, 8), new Block(0x8002));
            rig.Server.Step();
            rig.Client.Update(); // BeginFrame + apply + propagate; EndFrame not yet called

            SectorHandle sector = view.Data.sectors[int3.zero];
            int brickIdx = Sector.ToBrickIdx(1, 1, 1);
            ushort require = sector.Get().brickRequireUpdateFlags[brickIdx];
            Assert.That(require & (ushort)DirtyFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(0));
            Assert.That(require & (ushort)DirtyFlags.GeneralAutomata, Is.EqualTo(0));
            Assert.That(sector.IsRendererRequireUpdate, Is.True);

            rig.Client.EndFrame();
            Assert.That(sector.Get().sectorDirtyFlags, Is.EqualTo(0));
        }

        [Test]
        public void Commands_SetBlockAndStaticReachTheServer()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.Exchange();

            rig.Client.SetBlock(EntityA, new int3(2, 2, 2), new Block(0x8004));
            rig.Client.SetEntityStatic(EntityA, true);
            rig.Exchange(); // ProcessIncoming is part of Update, not Step: pump explicitly below
            rig.Server.ProcessIncoming();
            rig.Exchange();

            Assert.That(rig.World.GetBlock(EntityA, new int3(2, 2, 2)), Is.EqualTo(new Block(0x8004)));
            Assert.That(rig.World.GetEntity(EntityA).isStatic, Is.True);
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            Assert.That(view.IsStatic, Is.True);
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.Data.GetBlock(new int3(2, 2, 2)), Is.EqualTo(new Block(0x8004)));
        }

        [Test]
        public void ProtectedEntity_RefusesUnfreezeCommand()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true, isProtected: true);
            rig.Exchange();

            rig.Client.SetEntityStatic(EntityA, false);
            rig.Server.ProcessIncoming();
            Assert.That(rig.World.GetEntity(EntityA).isStatic, Is.True);
        }

        [Test]
        public void Despawn_RemovesViewAndDestroysSpawnedObject()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8001));
            rig.Exchange();
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            GameObject go = view.Component.gameObject;

            rig.World.RemoveEntity(EntityA);
            rig.Exchange();

            Assert.That(rig.Client.World.TryGetView(EntityA, out _), Is.False);
            Assert.That(rig.Client.World.Views.Count, Is.EqualTo(0));
            Assert.That(go == null, Is.True, "client-spawned view object is destroyed");
        }

        [Test]
        public void Events_ReachRegisteredClientHandler()
        {
            using var rig = new Rig();
            var received = new List<TestEvent>();
            rig.Client.RegisterEvent<TestEvent>((worldId, evt) => received.Add(evt));

            rig.World.EmitEvent(new TestEvent { Value = 5, Position = new float3(1f, 2f, 3f) });
            rig.Exchange();

            Assert.That(received.Count, Is.EqualTo(1));
            Assert.That(received[0].Value, Is.EqualTo(5));
            Assert.That(math.all(received[0].Position == new float3(1f, 2f, 3f)), Is.True);
        }

        [Test]
        public void Query_ReturnsNonReplicatedSlot()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(5, 5, 5), new Block(0x8001));
            rig.World.SetSlot(EntityA, (SectorSlotId)2, new int3(5, 5, 5), (short)77);
            rig.Exchange();

            VoxelQueryReply reply = null;
            rig.Client.QueryVoxel(EntityA, new int3(5, 5, 5), 0xFFFF, r => reply = r);
            rig.Server.ProcessIncoming();
            rig.Client.Update();
            rig.Client.EndFrame();

            Assert.That(reply, Is.Not.Null);
            Assert.That(reply.Found, Is.True);
            Assert.That(reply.TryGetSlot(SectorSlotId.Block, out Block block), Is.True);
            Assert.That(block, Is.EqualTo(new Block(0x8001)));
            Assert.That(reply.TryGetSlot((SectorSlotId)2, out short meta), Is.True);
            Assert.That(meta, Is.EqualTo(77));

            // The replica never received slot 2.
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            Assert.That(view.Data.sectors[int3.zero].Get().slots[2].IsCreated, Is.False);
        }

        [Test]
        public void SaveLoad_RoundTripsThroughWorldAndReplicates()
        {
            string path = System.IO.Path.Combine(Application.temporaryCachePath, "replication-test.cxw");
            using (var rig = new Rig())
            {
                rig.World.CreateEntity(EntityA, new RigidTransform(quaternion.identity, new float3(3f, 4f, 5f)), isStatic: false, isProtected: true);
                rig.World.AddBody(EntityA);
                rig.World.SetBlock(EntityA, new int3(7, 7, 7), new Block(0x8005));
                rig.World.Save(path);
            }

            using (var rig = new Rig())
            {
                rig.World.Load(path);
                rig.Exchange();
                Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
                Assert.That(view.HasBody, Is.True);
                Assert.That(view.IsProtected, Is.True);
                Assert.That(view.IsStatic, Is.False);
                Assert.That(view.Data.GetBlock(new int3(7, 7, 7)), Is.EqualTo(new Block(0x8005)));
                // The body is dynamic, so the one tick in Exchange applies gravity for 1/100 s.
                Assert.That(math.distance(view.Data.transform.pos, new float3(3f, 4f, 5f)), Is.LessThan(0.01f));
            }

            System.IO.File.Delete(path);
        }

        private static unsafe void AssertBlockSlotsEqual(in VoxelEntityData server, in VoxelEntityData replica)
        {
            Assert.That(replica.sectors.Count, Is.EqualTo(server.sectors.Count));
            foreach (var kvp in server.sectors)
            {
                Assert.That(replica.sectors.TryGetValue(kvp.Key, out SectorHandle replicaHandle), Is.True);
                ref Sector s = ref kvp.Value.Get();
                ref Sector r = ref replicaHandle.Get();
                for (int brick = 0; brick < Sector.BRICKS_IN_SECTOR; brick++)
                {
                    bool serverHas = s.brickMap.indices[brick] != Sector.BRICKID_EMPTY;
                    if (!serverHas) continue;
                    int3 origin = Sector.ToBrickPos((short)brick) * Sector.SIZE_IN_BLOCKS;
                    for (int z = 0; z < Sector.SIZE_IN_BLOCKS; z++)
                    for (int y = 0; y < Sector.SIZE_IN_BLOCKS; y++)
                    for (int x = 0; x < Sector.SIZE_IN_BLOCKS; x++)
                    {
                        Assert.That(
                            r.GetBlock(origin.x + x, origin.y + y, origin.z + z),
                            Is.EqualTo(s.GetBlock(origin.x + x, origin.y + y, origin.z + z)),
                            $"sector {kvp.Key} voxel {origin + new int3(x, y, z)}");
                    }
                }
            }
        }
    }
}
