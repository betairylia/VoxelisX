using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Jobs;
using Voxelis.Tick;

namespace VoxelisX.Tests
{
    public class TickStageTests
    {
        private sealed class Inputs
        {
        }

        private sealed class RecordingHook : ITickHook<Inputs>
        {
            private readonly List<int> calls;
            private readonly int value;
            private readonly bool sequential;

            public RecordingHook(List<int> calls, int value, bool sequential = true)
            {
                this.calls = calls;
                this.value = value;
                this.sequential = sequential;
            }

            public bool Execute(Inputs inputs, JobHandle stageStart, JobHandle chained, out JobHandle handle)
            {
                calls.Add(value);
                handle = chained;
                return sequential;
            }
        }

        [Test]
        public void ManagedHooksRunInRegistrationOrderForSequentialHooks()
        {
            var stage = new TickStage<Inputs>();
            var calls = new List<int>();
            stage.RegisterHook(new RecordingHook(calls, 1));
            stage.RegisterHook(new RecordingHook(calls, 2));
            stage.RegisterHook(new RecordingHook(calls, 3));

            stage.Schedule(new Inputs(), default).Complete();

            Assert.That(calls, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void RegisteringAfterFirstScheduleThrows()
        {
            var stage = new TickStage<Inputs>();

            stage.Schedule(new Inputs(), default).Complete();

            Assert.Throws<InvalidOperationException>(() => stage.RegisterHook(new RecordingHook(new List<int>(), 1)));
        }

        [Test]
        public void UsingDisposedStageThrows()
        {
            var stage = new TickStage<Inputs>();
            stage.Dispose();

            Assert.Throws<ObjectDisposedException>(() => stage.RegisterHook(new RecordingHook(new List<int>(), 1)));
            Assert.Throws<ObjectDisposedException>(() => stage.Schedule(new Inputs(), default));
        }

        [Test]
        public void NoHooksReturnsIncomingDependency()
        {
            var stage = new TickStage<Inputs>();
            JobHandle dependency = default;

            JobHandle result = stage.Schedule(new Inputs(), dependency);

            Assert.That(result.Equals(dependency), Is.True);
        }

        [Test]
        public void ScheduleWithHooksDisposesTemporaryHandleStorage()
        {
            var stage = new TickStage<Inputs>();
            var calls = new List<int>();
            stage.RegisterHook(new RecordingHook(calls, 1, sequential: false));

            Assert.DoesNotThrow(() => stage.Schedule(new Inputs(), default).Complete());
            Assert.That(calls, Is.EqualTo(new[] { 1 }));
        }
    }
}
