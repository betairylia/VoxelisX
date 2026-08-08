using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Voxelis.Simulation;
using Voxelis.Utils;

namespace VoxelisX.Tests
{
    public class BrickOverlapGraphTests
    {
        static readonly Guid128 LowGuid = new Guid128(10, 0, 0, 0);
        static readonly Guid128 MidGuid = new Guid128(20, 0, 0, 0);
        static readonly Guid128 HighGuid = new Guid128(30, 0, 0, 0);

        static BrickOverlapKey Key(Guid128 entityId, int x, int y = 0, int z = 0)
        {
            return new BrickOverlapKey
            {
                EntityId = entityId,
                BrickCoord = new int3(x, y, z)
            };
        }

        static VoxelBrickOverlapCandidate Candidate(
            int bodyA, int3 brickA, int bodyB, int3 brickB)
        {
            return new VoxelBrickOverlapCandidate
            {
                BodyIndexA = bodyA,
                BrickCoordsInA = brickA,
                BodyIndexB = bodyB,
                BrickCoordsInB = brickB
            };
        }

        static NativeStream CreateStream(
            params VoxelBrickOverlapCandidate[][] workItems)
        {
            int suppliedWorkItems = workItems?.Length ?? 0;
            int streamWorkItems = math.max(1, suppliedWorkItems);
            var stream = new NativeStream(streamWorkItems, Allocator.TempJob);
            NativeStream.Writer writer = stream.AsWriter();

            for (int i = 0; i < streamWorkItems; i++)
            {
                writer.BeginForEachIndex(i);
                if (i < suppliedWorkItems)
                {
                    VoxelBrickOverlapCandidate[] items = workItems[i];
                    for (int item = 0; item < items.Length; item++)
                    {
                        writer.Write(items[item]);
                    }
                }
                writer.EndForEachIndex();
            }

            return stream;
        }

        static void AssertPair(BrickOverlapGraph graph, int index,
            BrickOverlapKey expectedA, BrickOverlapKey expectedB)
        {
            BrickOverlapPair pair = graph.GetPair(index);
            Assert.That(pair.A, Is.EqualTo(expectedA));
            Assert.That(pair.B, Is.EqualTo(expectedB));
            Assert.That(pair.A.CompareTo(pair.B), Is.LessThan(0),
                "Every published pair must use canonical endpoint order.");
        }

        static void AssertNeighbors(BrickOverlapGraph graph, BrickOverlapKey source,
            params BrickOverlapKey[] expected)
        {
            Assert.That(graph.TryGetOverlaps(source, out BrickOverlapEnumerator neighbors),
                Is.True, $"Missing adjacency range for {source}.");
            Assert.That(neighbors.Count, Is.EqualTo(expected.Length));

            var actual = new List<BrickOverlapKey>(expected.Length);
            foreach (BrickOverlapKey neighbor in neighbors)
            {
                actual.Add(neighbor);
            }

            Assert.That(actual, Has.Count.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]),
                    $"Neighbor {i} of {source} was not deterministically ordered.");
            }
        }

        static void AssertExpectedThreePairGraph(BrickOverlapGraph graph, int version)
        {
            BrickOverlapKey low2 = Key(LowGuid, -2);
            BrickOverlapKey low = Key(LowGuid, -1);
            BrickOverlapKey mid = Key(MidGuid, 2);
            BrickOverlapKey high = Key(HighGuid, 4);

            Assert.That(graph.IsCreated, Is.True);
            Assert.That(graph.Version, Is.EqualTo(version));
            Assert.That(graph.PairCount, Is.EqualTo(3));
            Assert.That(graph.SourceCount, Is.EqualTo(4));

            // Canonical pair list order is source key, then target key.
            AssertPair(graph, 0, low2, mid);
            AssertPair(graph, 1, low, high);
            AssertPair(graph, 2, mid, high);

            // The unique pair list stores each edge once, while adjacency contains both
            // directed halves. Sources and each source's neighbors remain sorted.
            AssertNeighbors(graph, low2, mid);
            AssertNeighbors(graph, low, high);
            AssertNeighbors(graph, mid, low2, high);
            AssertNeighbors(graph, high, low, mid);

            BrickOverlapKey[] expectedSources = { low2, low, mid, high };
            int[] expectedStarts = { 0, 1, 2, 4 };
            int[] expectedCounts = { 1, 1, 2, 2 };
            Assert.That(graph.SourceRanges.Length, Is.EqualTo(expectedSources.Length));
            for (int i = 0; i < expectedSources.Length; i++)
            {
                BrickOverlapSourceRange range = graph.SourceRanges[i];
                Assert.That(range.Source, Is.EqualTo(expectedSources[i]));
                Assert.That(range.Start, Is.EqualTo(expectedStarts[i]));
                Assert.That(range.Count, Is.EqualTo(expectedCounts[i]));
            }

            Assert.That(graph.TryGetOverlaps(Key(HighGuid, 999), out _), Is.False);
        }

        [Test]
        public void DefaultGraph_IsSafeAndEmpty()
        {
            BrickOverlapGraph graph = default;

            Assert.That(graph.IsCreated, Is.False);
            Assert.That(graph.Version, Is.Zero);
            Assert.That(graph.PairCount, Is.Zero);
            Assert.That(graph.SourceCount, Is.Zero);
            Assert.That(graph.TryGetOverlaps(Key(LowGuid, 0), out BrickOverlapEnumerator neighbors),
                Is.False);
            Assert.That(neighbors.Count, Is.Zero);
        }

        [Test]
        public void SerialAndParallelBuilds_MapAndProduceIdenticalSymmetricGraphs()
        {
            // Physics body order is intentionally the reverse of GUID order for two bodies.
            // Public graph ordering must follow stable GUIDs, never transient body indices.
            using var bodyIndexToGuid = new NativeArray<Guid128>(new[]
            {
                HighGuid, LowGuid, MidGuid
            }, Allocator.TempJob);

            int3 highBrick = new int3(4, 0, 0);
            int3 lowBrick = new int3(-1, 0, 0);
            int3 midBrick = new int3(2, 0, 0);
            int3 lowBrick2 = new int3(-2, 0, 0);

            // Unique producer records split across the dynamic and static-static streams and
            // multiple work items.
            using var serialDynamic = CreateStream(
                new[]
                {
                    Candidate(0, highBrick, 1, lowBrick)
                },
                new[]
                {
                    Candidate(0, highBrick, 2, midBrick)
                });
            using var serialStatic = CreateStream(new[]
            {
                Candidate(2, midBrick, 1, lowBrick2)
            });

            // Same logical candidates in a different order and stream partition. This build is
            // forced down the parallel path to check parity with the serial fallback as well as
            // independence from raw stream order.
            using var parallelDynamic = CreateStream(
                new[]
                {
                    Candidate(2, midBrick, 1, lowBrick2),
                    Candidate(0, highBrick, 2, midBrick)
                },
                Array.Empty<VoxelBrickOverlapCandidate>());
            using var parallelStatic = CreateStream(
                new[]
                {
                    Candidate(0, highBrick, 1, lowBrick)
                });

            using var builder = new BrickOverlapGraphBuilder
            {
                serialBuildThreshold = int.MaxValue
            };

            builder.BuildAndPublish(
                new VoxelBrickOverlapCandidates(serialDynamic, serialStatic),
                bodyIndexToGuid);
            BrickOverlapGraph serialGraph = builder.Graph;
            BrickOverlapGraphStats serialStats = builder.LastBuildStats;

            AssertExpectedThreePairGraph(serialGraph, version: 1);
            Assert.That(serialStats.RawCandidates, Is.EqualTo(3));
            Assert.That(serialStats.PublishedPairs, Is.EqualTo(3));
            Assert.That(serialStats.ActiveSourceBricks, Is.EqualTo(4));
            Assert.That(serialStats.NumBodies, Is.EqualTo(3));
            Assert.That(serialStats.UsedSerialPath, Is.True);
            Assert.That(serialStats.BuildMilliseconds, Is.GreaterThanOrEqualTo(0));

            builder.serialBuildThreshold = 0;
            builder.BuildAndPublish(
                new VoxelBrickOverlapCandidates(parallelDynamic, parallelStatic),
                bodyIndexToGuid);
            BrickOverlapGraph parallelGraph = builder.Graph;
            BrickOverlapGraphStats parallelStats = builder.LastBuildStats;

            AssertExpectedThreePairGraph(parallelGraph, version: 2);
            Assert.That(parallelStats.RawCandidates, Is.EqualTo(3));
            Assert.That(parallelStats.PublishedPairs, Is.EqualTo(3));
            Assert.That(parallelStats.ActiveSourceBricks, Is.EqualTo(4));
            Assert.That(parallelStats.NumBodies, Is.EqualTo(3));
            Assert.That(parallelStats.UsedSerialPath, Is.False);

            // The previous view remains valid across one publish because the builder is
            // double-buffered; consumers are required to re-fetch before the following publish.
            AssertExpectedThreePairGraph(serialGraph, version: 1);
        }

        [Test]
        public void EmptyPublish_ReplacesPriorGraphAndAdvancesVersion()
        {
            using var bodyIndexToGuid = new NativeArray<Guid128>(new[]
            {
                HighGuid, LowGuid
            }, Allocator.TempJob);
            using var dynamicStream = CreateStream(new[]
            {
                Candidate(0, new int3(4, 0, 0), 1, new int3(-1, 0, 0))
            });
            using var builder = new BrickOverlapGraphBuilder();

            Assert.That(builder.Graph.IsCreated, Is.False);
            builder.BuildAndPublish(
                new VoxelBrickOverlapCandidates(dynamicStream, default),
                bodyIndexToGuid);
            Assert.That(builder.Graph.Version, Is.EqualTo(1));
            Assert.That(builder.Graph.PairCount, Is.EqualTo(1));

            builder.BuildAndPublish(default, bodyIndexToGuid);
            BrickOverlapGraph empty = builder.Graph;

            Assert.That(empty.IsCreated, Is.True,
                "An empty result is still a successful publication.");
            Assert.That(empty.Version, Is.EqualTo(2));
            Assert.That(empty.PairCount, Is.Zero);
            Assert.That(empty.SourceCount, Is.Zero);
            Assert.That(empty.TryGetOverlaps(Key(HighGuid, 4), out _), Is.False);
            Assert.That(builder.LastBuildStats.RawCandidates, Is.Zero);
            Assert.That(builder.LastBuildStats.PublishedPairs, Is.Zero);
            Assert.That(builder.LastBuildStats.ActiveSourceBricks, Is.Zero);
        }
    }
}
