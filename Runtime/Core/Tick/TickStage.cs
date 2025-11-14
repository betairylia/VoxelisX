using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Voxelis.Tick
{
    /// <summary>
    /// Represents a single stage in the tick pipeline that executes registered hooks.
    /// Supports both parallel and sequential hook execution based on hook preferences.
    /// </summary>
    /// <typeparam name="TInputs">The input interface type for this stage.</typeparam>
    /// <remarks>
    /// Stages must be configured before the first Schedule() call. After the first execution,
    /// the stage becomes locked and no new hooks can be registered.
    ///
    /// Example usage:
    /// <code>
    /// // Define stage inputs
    /// public interface IPhysicsStageInputs
    /// {
    ///     NativeList&lt;VoxelEntityData&gt; Entities { get; }
    ///     float DeltaTime { get; }
    /// }
    ///
    /// // Create stage
    /// var physicsStage = new TickStage&lt;IPhysicsStageInputs&gt;();
    ///
    /// // Register hooks (before first tick)
    /// physicsStage.RegisterHook(new MyPhysicsHook());
    /// physicsStage.RegisterHook(new MyCollisionHook());
    ///
    /// // In Tick() method
    /// var inputs = new PhysicsStageInputs { Entities = tickBuf, DeltaTime = Time.fixedDeltaTime };
    /// JobHandle handle = physicsStage.Schedule(inputs, previousStageHandle);
    /// </code>
    /// </remarks>
    public class TickStage<TInputs> : IDisposable
    {
        private readonly List<ITickHook<TInputs>> hooks = new List<ITickHook<TInputs>>();
        private bool isLocked = false;
        private bool isDisposed = false;

        // Future: Add profiler markers for debugging
        // private ProfilerMarker profilerMarker;

        /// <summary>
        /// Gets the number of registered hooks in this stage.
        /// </summary>
        public int HookCount => hooks.Count;

        /// <summary>
        /// Registers a hook to execute in this stage.
        /// </summary>
        /// <param name="hook">The hook to register.</param>
        /// <exception cref="InvalidOperationException">Thrown if the stage is already locked (after first Schedule call).</exception>
        /// <exception cref="ArgumentNullException">Thrown if hook is null.</exception>
        public void RegisterHook(ITickHook<TInputs> hook)
        {
            if (isDisposed)
                throw new ObjectDisposedException(nameof(TickStage<TInputs>));

            if (isLocked)
                throw new InvalidOperationException(
                    $"Cannot register hook after the first Schedule() call. " +
                    $"All hooks must be registered during initialization.");

            if (hook == null)
                throw new ArgumentNullException(nameof(hook));

            hooks.Add(hook);
        }

        /// <summary>
        /// Schedules all registered hooks in this stage.
        /// </summary>
        /// <param name="inputs">The stage inputs to pass to all hooks.</param>
        /// <param name="dependency">The dependency from the previous stage or operation.</param>
        /// <returns>
        /// A combined JobHandle representing all hooks in this stage.
        /// This handle should be used as a dependency for subsequent stages.
        /// </returns>
        /// <remarks>
        /// The first call to Schedule() locks the stage, preventing further hook registration.
        ///
        /// Execution model:
        /// - Hooks returning false (parallel) run independently, only depending on the input dependency.
        /// - Hooks returning true (sequential) form a chain, each depending on the previous chained hook.
        /// - All handles (parallel and terminal chained) are combined at the end.
        ///
        /// If no hooks are registered, returns the input dependency unchanged (silent skip).
        /// </remarks>
        public JobHandle Schedule(TInputs inputs, JobHandle dependency)
        {
            if (isDisposed)
                throw new ObjectDisposedException(nameof(TickStage<TInputs>));

            // Lock on first execution
            if (!isLocked)
            {
                isLocked = true;
                // Future: Initialize profiler markers here
            }

            // If no hooks, skip silently
            if (hooks.Count == 0)
                return dependency;

            // Track handles for combining
            NativeList<JobHandle> parallelHandles = new(Allocator.Temp);
            JobHandle chainedHandle = dependency;

            // Execute all hooks
            foreach (var hook in hooks)
            {
                try
                {
                    bool useChaining = hook.Execute(inputs, dependency, chainedHandle, out JobHandle handle);

                    if (useChaining)
                    {
                        // Sequential: update the chain
                        chainedHandle = handle;
                    }
                    else
                    {
                        // Parallel: track independently
                        if (handle.IsCompleted == false)
                        {
                            parallelHandles.Add(handle);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[TickStage] Hook execution failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    // Continue executing other hooks
                }
            }

            // Combine all handles
            // - Terminal chained handle (represents the end of the sequential chain)
            // - All parallel handles (run independently)
            if (parallelHandles.Length == 0)
            {
                // Only chained work
                return chainedHandle;
            }
            else if (chainedHandle.Equals(dependency))
            {
                // Only parallel work (no chaining occurred)
                return parallelHandles.Length == 1
                    ? parallelHandles[0]
                    : JobHandle.CombineDependencies(parallelHandles.AsArray());
            }
            else
            {
                // Both chained and parallel work
                parallelHandles.Add(chainedHandle);
                return JobHandle.CombineDependencies(parallelHandles.AsArray());
            }
        }

        /// <summary>
        /// Releases resources used by this stage.
        /// </summary>
        public void Dispose()
        {
            if (isDisposed)
                return;

            hooks.Clear();
            isDisposed = true;
        }
    }
}
