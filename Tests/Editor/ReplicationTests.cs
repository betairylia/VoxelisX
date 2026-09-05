using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        private struct TestBatchCommand
        {
            public int Count;
        }

        private struct TestQuery
        {
            public int Wanted;
        }

        private struct TestReply
        {
            public int Count;
        }

        private sealed class Rig : System.IDisposable
        {
            public readonly CaelixServer Server;
            public readonly CaelixWorld World;
            public readonly CaelixClient Client;

            private readonly List<CaelixClient> clients = new();

            public Rig()
            {
                Server = new CaelixServer { TickRate = 100f };
                World = Server.CreateWorld(CaelixWorldConfig.Default(0, "test"));
                Client = AddClient();
            }

            /// <summary>Every client connected to this rig's server, in connection order.</summary>
            public IReadOnlyList<CaelixClient> Clients => clients;

            /// <summary>Connects one more client through its own in-process channel pair.</summary>
            public CaelixClient AddClient()
            {
                LocalChannel.CreatePair(out LocalChannel serverEnd, out LocalChannel clientEnd);
                var client = new CaelixClient(clientEnd, Server.Types);
                Server.AddConnection(serverEnd);
                clients.Add(client);
                return client;
            }

            /// <summary>One server tick, then one client frame on the first client.</summary>
            public void Exchange()
            {
                Server.Step();
                Client.Receive();
                Client.PrepareRender();
                Client.EndFrame();
            }

            /// <summary>One server tick, then one client frame on every connected client.</summary>
            public void ExchangeAll()
            {
                Server.Step();
                for (int i = 0; i < clients.Count; i++)
                {
                    clients[i].Receive();
                    clients[i].PrepareRender();
                    clients[i].EndFrame();
                }
            }

            public void Dispose()
            {
                for (int i = 0; i < clients.Count; i++)
                {
                    clients[i].Dispose();
                }

                clients.Clear();
                Server.Dispose();
            }
        }

        private static readonly Guid128 EntityA = new(0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u);
        private static readonly Guid128 EntityB = new(0x55555555u, 0x66666666u, 0x77777777u, 0x88888888u);

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
            rig.Client.Receive(); // apply messages
            rig.Client.PrepareRender(); // propagate render flags; EndFrame not yet called

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
            rig.Exchange(); // Step drains the inbox before ticking

            Assert.That(rig.World.GetBlock(EntityA, new int3(2, 2, 2)), Is.EqualTo(new Block(0x8004)));
            Assert.That(rig.World.GetEntity(EntityA).isStatic, Is.True);
            rig.Client.World.TryGetView(EntityA, out EntityView view);
            Assert.That(view.IsStatic, Is.True);
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.Data.GetBlock(new int3(2, 2, 2)), Is.EqualTo(new Block(0x8004)));
        }

        [Test]
        public void PayloadCommand_ReachesHandlerWithTrailingBytes()
        {
            using var rig = new Rig();
            int sum = 0;
            int seenRemaining = -1;
            rig.Server.RegisterCommand<TestBatchCommand>(
                (ServerConnection connection, CaelixWorld world, in TestBatchCommand cmd, ref NetMessageReader payload) =>
                {
                    seenRemaining = payload.Remaining;
                    ReadOnlySpan<int> values =
                        MemoryMarshal.Cast<byte, int>(payload.ReadSpan(cmd.Count * sizeof(int)));
                    for (int i = 0; i < values.Length; i++)
                    {
                        sum += values[i];
                    }
                });

            var records = new[] { 1, 2, 3, 4 };
            rig.Client.SendCommand(
                new TestBatchCommand { Count = records.Length },
                MemoryMarshal.AsBytes(new ReadOnlySpan<int>(records)));
            rig.Server.ProcessIncoming();

            Assert.That(seenRemaining, Is.EqualTo(16), "the payload is exactly the trailing bytes");
            Assert.That(sum, Is.EqualTo(10));
        }

        [Test]
        public void TypedQuery_ReturnsReplyAndPayload()
        {
            using var rig = new Rig();
            rig.Server.RegisterQuery<TestQuery, TestReply>(
                (ServerConnection connection, CaelixWorld world, in TestQuery request,
                    ref NetMessageReader requestPayload, NetMessageWriter replyPayload) =>
                {
                    for (int i = 0; i < request.Wanted; i++)
                    {
                        replyPayload.Write(i);
                    }

                    return new TestReply { Count = request.Wanted };
                });

            int replyCount = -1;
            int[] replyValues = null;
            rig.Client.SendQuery<TestQuery, TestReply>(
                new TestQuery { Wanted = 5 },
                ReadOnlySpan<byte>.Empty,
                (ushort worldId, in TestReply reply, ref NetMessageReader payload) =>
                {
                    replyCount = reply.Count;
                    replyValues = MemoryMarshal.Cast<byte, int>(payload.ReadSpan(reply.Count * sizeof(int))).ToArray();
                });

            rig.Server.ProcessIncoming();
            rig.Client.Receive();
            rig.Client.PrepareRender();
            rig.Client.EndFrame();

            Assert.That(replyCount, Is.EqualTo(5));
            Assert.That(replyValues, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        }

        [Test]
        public void SpawnEntityCommand_CreatesEntityWithBodyAndAcceptsSameFrameWrites()
        {
            using var rig = new Rig();
            rig.Client.SpawnEntity(EntityB, RigidTransform.identity, isStatic: false, hasBody: true);
            rig.Client.SetBlock(EntityB, new int3(1, 1, 1), new Block(0x8001));
            rig.Exchange();

            Assert.That(rig.World.HasEntity(EntityB), Is.True);
            Assert.That(rig.World.HasBody(EntityB), Is.True);
            Assert.That(rig.World.GetBlock(EntityB, new int3(1, 1, 1)), Is.EqualTo(new Block(0x8001)));

            Assert.That(rig.Client.World.TryGetView(EntityB, out EntityView view), Is.True);
            Assert.That(view.HasBody, Is.True);
            Assert.That(view.IsClientSpawned, Is.True);
            Assert.That(view.Data.GetBlock(new int3(1, 1, 1)), Is.EqualTo(new Block(0x8001)));

            // The guid is taken: a second spawn must not replace the entity that is already there.
            int entityCount = rig.World.EntityCount;
            rig.Client.SpawnEntity(EntityB, RigidTransform.identity, isStatic: true, hasBody: true);
            rig.Exchange();
            Assert.That(rig.World.EntityCount, Is.EqualTo(entityCount));
            Assert.That(rig.World.GetEntity(EntityB).isStatic, Is.False);
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
        public void Drag_PullsBodyTowardTargetAndTimesOut()
        {
            using var rig = new Rig();
            rig.World.Config.physics.gravity = float3.zero;
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.World.SetBlock(EntityA, new int3(0, 0, 0), new Block(0x8001));
            rig.Exchange();

            rig.Client.SetDrag(new DragCommand
            {
                Entity = EntityA,
                AnchorLocal = new float3(0.5f, 0.5f, 0.5f),
                TargetWorld = new float3(5.5f, 0.5f, 0.5f),
                Spring = 50f,
                Damping = 10f,
                MaxAcceleration = 100f,
            });

            rig.Server.Step(30); // 0.3 s at 100 TPS, inside the 0.5 s timeout
            Assert.That(rig.World.DragCount, Is.EqualTo(1));
            float3 pulled = rig.World.GetEntity(EntityA).transform.pos;
            Assert.That(pulled.x, Is.GreaterThan(0.05f), "spring pulls toward +X");
            Assert.That(math.abs(pulled.y) + math.abs(pulled.z), Is.LessThan(0.05f), "no off-axis drift");

            rig.Server.Step(60); // no refresh: past the timeout, the drag is dropped
            Assert.That(rig.World.DragCount, Is.EqualTo(0));

            rig.Client.ReleaseDrag(EntityA);
            rig.Server.Step();
            Assert.That(rig.World.DragCount, Is.EqualTo(0));
        }

        [Test]
        public void Drag_AnchorAtCenterOfMass_PullsTowardTarget()
        {
            using var rig = new Rig();
            rig.World.Config.physics.gravity = float3.zero;
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false);
            rig.World.AddBody(EntityA);
            rig.World.SetBlock(EntityA, new int3(0, 0, 0), new Block(0x8001));
            rig.Exchange();

            rig.Client.SetDrag(new DragCommand
            {
                Entity = EntityA,
                AnchorLocal = float3.zero, // deliberately wrong; the flag must make the server ignore it
                AnchorAtCenterOfMass = 1,
                TargetWorld = new float3(5.5f, 0.5f, 0.5f),
                Spring = 50f,
                Damping = 10f,
                MaxAcceleration = 100f,
            });

            rig.Server.Step(30);
            Assert.That(rig.World.DragCount, Is.EqualTo(1));
            float3 pulled = rig.World.GetEntity(EntityA).transform.pos;
            Assert.That(pulled.x, Is.GreaterThan(0.05f), "spring pulls toward +X");
            Assert.That(math.abs(pulled.y) + math.abs(pulled.z), Is.LessThan(0.05f), "no off-axis drift");
        }

        [Test]
        public void Drag_IgnoresProtectedEntities()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: false, isProtected: true);
            rig.World.AddBody(EntityA);
            rig.Exchange();

            rig.Client.SetDrag(new DragCommand { Entity = EntityA, TargetWorld = new float3(5f, 0f, 0f), Spring = 50f });
            rig.Server.Step();
            Assert.That(rig.World.DragCount, Is.EqualTo(0));
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
            rig.Client.Receive();
            rig.Client.PrepareRender();
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

        [Test]
        public void TwoClients_ShareDeltaAndLateJoinerGetsFullState()
        {
            using var rig = new Rig();
            CaelixClient second = rig.AddClient();

            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(130, 1, 1), new Block(0x8002)); // second sector
            rig.ExchangeAll();

            AssertReplicaMatchesServer(rig, rig.Client);
            AssertReplicaMatchesServer(rig, second);

            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8003));   // edit a known brick
            rig.World.SetBlock(EntityA, new int3(1, 1, 260), new Block(0x8004)); // a sector nobody has
            rig.ExchangeAll();

            AssertReplicaMatchesServer(rig, rig.Client);
            AssertReplicaMatchesServer(rig, second);

            // A late joiner is caught up from the same tick's batches, not from a replay.
            CaelixClient third = rig.AddClient();
            rig.ExchangeAll();
            AssertReplicaMatchesServer(rig, third);

            rig.World.SetBlock(EntityA, new int3(2, 1, 1), new Block(0x8005));
            rig.ExchangeAll();

            AssertReplicaMatchesServer(rig, rig.Client);
            AssertReplicaMatchesServer(rig, second);
            AssertReplicaMatchesServer(rig, third);

            Assert.That(third.World.TryGetView(EntityA, out EntityView thirdView), Is.True);
            Assert.That(thirdView.Data.GetBlock(new int3(2, 1, 1)), Is.EqualTo(new Block(0x8005)));
            Assert.That(thirdView.Data.GetBlock(new int3(1, 1, 1)), Is.EqualTo(new Block(0x8003)));
            Assert.That(thirdView.Data.GetBlock(new int3(1, 1, 260)), Is.EqualTo(new Block(0x8004)));
        }

        [Test]
        public void ChunkedReplication_JoinAndDeltaSurviveSmallChunks()
        {
            using var rig = new Rig();
            // One one-brick sector message is roughly 1.1 KB, so this packs about two per chunk.
            rig.Server.ReplicationChunkBytes = 2500;

            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            for (int x = 0; x < 12; x++)
            {
                rig.World.SetBlock(EntityA, new int3(x * 128, 0, 0), new Block(0x8001));
            }

            rig.Exchange();

            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
            Assert.That(rig.Server.FullBatchForTests.LastChunkCount, Is.GreaterThanOrEqualTo(4),
                "12 sectors at ~2 per chunk take several chunks");

            // Now every sector is known, so the same 12 sectors go out as a chunked delta.
            for (int x = 0; x < 12; x++)
            {
                rig.World.SetBlock(EntityA, new int3(x * 128 + 1, 1, 1), new Block(0x8002));
            }

            rig.Exchange();

            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
            Assert.That(rig.Server.DeltaBatchForTests.LastChunkCount, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void Delta_SkippedWhenEveryConnectionGotTheSectorInFull()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(0, 0, 0), new Block(0x8001));
            rig.World.SetBlock(EntityA, new int3(128, 0, 0), new Block(0x8002));
            rig.World.SetBlock(EntityA, new int3(256, 0, 0), new Block(0x8003));

            rig.Exchange();

            // All three sectors were new to the only connection, so the shared delta drops them
            // instead of packing bytes nobody sends.
            Assert.That(rig.Server.DeltaBatchForTests.SectorCount, Is.EqualTo(0));
            Assert.That(rig.Client.World.TryGetView(EntityA, out EntityView view), Is.True);
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
        }

        [Test]
        public void WorldLifecycle_AddAndRemoveReachTheClient()
        {
            using var rig = new Rig();
            rig.World.CreateEntity(EntityA, RigidTransform.identity, isStatic: true);
            rig.World.SetBlock(EntityA, new int3(1, 1, 1), new Block(0x8001));
            rig.Exchange();

            CaelixWorld second = rig.Server.CreateWorld(CaelixWorldConfig.Default(1, "second"));
            second.CreateEntity(EntityB, RigidTransform.identity, isStatic: true);
            second.SetBlock(EntityB, new int3(2, 2, 2), new Block(0x8007));
            rig.Exchange();

            ClientWorld replica = FindClientWorld(rig.Client, 1);
            Assert.That(replica, Is.Not.Null, "WorldAdd creates the replica of the new world");
            Assert.That(replica.TryGetView(EntityB, out EntityView view), Is.True);
            Assert.That(view.Data.GetBlock(new int3(2, 2, 2)), Is.EqualTo(new Block(0x8007)));

            var removed = new List<ushort>();
            rig.Client.WorldRemoving += w => removed.Add(w.Id);
            rig.Server.RemoveWorld(second);
            rig.Exchange();

            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(removed[0], Is.EqualTo(1));
            Assert.That(FindClientWorld(rig.Client, 1), Is.Null);

            ClientWorld defaultReplica = FindClientWorld(rig.Client, 0);
            Assert.That(defaultReplica, Is.Not.Null, "the default world is untouched");
            Assert.That(defaultReplica.TryGetView(EntityA, out _), Is.True);
        }

        [Test]
        public void FrozenServer_RunsExactlyOneTick()
        {
            using var rig = new Rig();
            rig.Server.Frozen = true;

            Assert.That(rig.Server.Tick(), Is.True, "the first tick runs even while frozen");
            Assert.That(rig.Server.Tick(), Is.False);
            Assert.That(rig.Server.Tick(), Is.False);
            Assert.That(rig.Server.TickIndex, Is.EqualTo(1u));

            rig.Server.Step();
            Assert.That(rig.Server.TickIndex, Is.EqualTo(2u), "Step ignores the freeze");

            rig.Server.Frozen = false;
            Assert.That(rig.Server.Tick(), Is.True);
            Assert.That(rig.Server.TickIndex, Is.EqualTo(3u));
        }

        [Test]
        public void Hello_ArrivesWithNoWorldHeader()
        {
            using var rig = new Rig();
            rig.Exchange();

            Assert.That(rig.Client.IsConnected, Is.True);
            Assert.That(rig.Client.Hello.TickRate, Is.EqualTo(100f));

            ClientWorld replica = FindClientWorld(rig.Client, 0);
            Assert.That(replica, Is.Not.Null, "the world arrives as WorldAdd, not inside Hello");
            Assert.That(replica.ReplicatedSlotMask, Is.EqualTo(Sector.DefaultReplicatedSlotMask));
        }

        private static ClientWorld FindClientWorld(CaelixClient client, ushort worldId)
        {
            IReadOnlyList<ClientWorld> worlds = client.Worlds;
            for (int i = 0; i < worlds.Count; i++)
            {
                if (worlds[i].Id == worldId) return worlds[i];
            }

            return null;
        }

        private static void AssertReplicaMatchesServer(Rig rig, CaelixClient client)
        {
            Assert.That(client.World.TryGetView(EntityA, out EntityView view), Is.True);
            AssertBlockSlotsEqual(rig.World.GetEntity(EntityA), view.Data);
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
