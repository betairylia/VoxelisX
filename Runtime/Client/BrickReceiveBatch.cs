using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;

namespace Caelix.Client
{
    /// <summary>
    /// A bounded run of BrickData messages. Each worker exclusively owns one sector and applies
    /// its messages in wire order. Pinned channel-owned arrays remain alive until jobs complete.
    /// Only the main thread enqueues, flushes and disposes this object.
    /// </summary>
    internal sealed unsafe class BrickReceiveBatch : IDisposable
    {
        private const int MaxPinnedBytes = 64 * 1024 * 1024;
        private const int MaxMessages = 1024;
        private static readonly ProfilerMarker ApplyMarker = new("Client.ApplyBrickBatch");

        private struct SectorWork
        {
            public SectorHandle Sector;
            public int First, Last;
        }

        private struct Message
        {
            [NativeDisableUnsafePtrRestriction] public byte* Payload;
            public int Length, BrickCount, Next;
        }

        private readonly Dictionary<IntPtr, int> sectorIndices = new();
        private readonly List<GCHandle> pins = new();
        private NativeList<SectorWork> sectors = new(64, Allocator.Persistent);
        private NativeList<Message> messages = new(64, Allocator.Persistent);
        private NativeList<ReplicatedBrickBatchError> errors = new(64, Allocator.Persistent);
        private long pinnedBytes;

        public void Add(SectorHandle sector, byte[] message, int payloadOffset, int brickCount)
        {
            if (pins.Count > 0 && (pinnedBytes + message.Length > MaxPinnedBytes || pins.Count >= MaxMessages))
                Flush();

            var pin = GCHandle.Alloc(message, GCHandleType.Pinned);
            pins.Add(pin);
            pinnedBytes += message.Length;
            int index = messages.Length;
            messages.Add(new Message
            {
                Payload = (byte*)pin.AddrOfPinnedObject() + payloadOffset,
                Length = message.Length - payloadOffset,
                BrickCount = brickCount,
                Next = -1,
            });
            errors.Add(ReplicatedBrickBatchError.None);

            var key = (IntPtr)sector.Ptr;
            if (sectorIndices.TryGetValue(key, out int sectorIndex))
            {
                SectorWork work = sectors[sectorIndex];
                Message previous = messages[work.Last];
                previous.Next = index;
                messages[work.Last] = previous;
                work.Last = index;
                sectors[sectorIndex] = work;
            }
            else
            {
                sectorIndices.Add(key, sectors.Length);
                sectors.Add(new SectorWork { Sector = sector, First = index, Last = index });
            }
        }

        public void Flush()
        {
            if (pins.Count == 0) return;
            using var marker = ApplyMarker.Auto();
            try
            {
                var job = new ApplyJob
                {
                    Sectors = sectors.AsArray(),
                    Messages = messages.AsArray(),
                    Errors = errors.AsArray(),
                };
                // A single sector has no independent work: Run still uses Burst and avoids a
                // schedule/wait round trip. Multiple sectors use one worker iteration per sector.
                if (sectors.Length == 1) job.Run(1);
                else job.Schedule(sectors.Length, 1).Complete();

                for (int i = 0; i < errors.Length; i++)
                {
                    if (errors[i] != ReplicatedBrickBatchError.None)
                        Debug.LogException(new InvalidDataException($"Invalid replicated brick batch: {errors[i]}."));
                }
            }
            finally
            {
                // Schedule().Complete() above is the lifetime fence for both sectors and input.
                foreach (GCHandle pin in pins) pin.Free();
                pins.Clear();
                sectorIndices.Clear();
                sectors.Clear();
                messages.Clear();
                errors.Clear();
                pinnedBytes = 0;
            }
        }

        [BurstCompile]
        private struct ApplyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SectorWork> Sectors;
            [ReadOnly] public NativeArray<Message> Messages;
            // Every message is in exactly one sector's chain, so result writes are disjoint.
            [NativeDisableParallelForRestriction] public NativeArray<ReplicatedBrickBatchError> Errors;

            public void Execute(int index)
            {
                SectorWork work = Sectors[index];
                ref Sector sector = ref work.Sector.Get();
                for (int i = work.First; i >= 0; i = Messages[i].Next)
                {
                    Message message = Messages[i];
                    Errors[i] = sector.ApplyReplicatedBrickBatch(message.Payload, message.Length, message.BrickCount);
                }

                // Existing-brick deltas do not change this list. Rebuild once after allocations,
                // rather than scanning all brick coordinates for every packet in a tick backlog.
                if (sector.NonEmptyBricks.Length != sector.brickMap.Count)
                    sector.UpdateNonEmptyBricks();
            }
        }

        public void Dispose()
        {
            Flush();
            sectors.Dispose();
            messages.Dispose();
            errors.Dispose();
        }
    }
}
