using System;
using Unity.Jobs;

namespace Voxelis.Tick
{
    /// <summary>
    /// A concrete hook implementation for executing managed (non-Burst) code within a tick stage.
    /// This hook completes the dependency before executing, allowing safe access to managed data.
    /// </summary>
    /// <typeparam name="TInputs">The input interface type for this stage.</typeparam>
    /// <remarks>
    /// ManagedHook is useful for:
    /// - Prototyping and debugging (easier to debug managed code)
    /// - Integration with non-Burst-compatible APIs (e.g., Unity APIs, third-party libraries)
    /// - Logic that doesn't require high performance (e.g., occasional state updates)
    ///
    /// Warning: Managed hooks complete the dependency JobHandle before executing, which can cause a sync point
    /// and reduce parallelism. Use Burst-compatible jobs whenever possible for performance-critical code.
    ///
    /// Example usage:
    /// <code>
    /// // Simple lambda-based hook
    /// var hook = new ManagedHook&lt;IPhysicsStageInputs&gt;(inputs =>
    /// {
    ///     Debug.Log($"Processing {inputs.Entities.Length} entities");
    ///     // Your managed logic here
    /// });
    /// physicsStage.RegisterHook(hook);
    ///
    /// // Or as a class for more complex logic
    /// public class MyManagedHook : ManagedHook&lt;IPhysicsStageInputs&gt;
    /// {
    ///     public MyManagedHook() : base(null) { }
    ///
    ///     protected override void ExecuteManaged(IPhysicsStageInputs inputs)
    ///     {
    ///         // Complex managed logic here
    ///         foreach (var entity in inputs.Entities)
    ///         {
    ///             // Process entity (non-Burst code)
    ///         }
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public class ManagedHook<TInputs> : ITickHook<TInputs>
    {
        /// <summary>
        /// gets whether this hook should chain with previous hooks (sequential) or run in parallel.
        /// </summary>
        /// <remarks>
        /// Override this property to control execution order:
        /// - true: Sequential execution - wait for previous chained hooks
        /// - false: Parallel execution - only wait for the previous stage
        ///
        /// Default: true (sequential)
        /// </remarks>
        protected virtual bool UseChaining => true;
        
        private readonly Action<TInputs> action;

        /// <summary>
        /// Creates a managed hook with the specified action.
        /// </summary>
        /// <param name="action">
        /// The action to execute with the stage inputs.
        /// Can be null if overriding ExecuteManaged in a derived class.
        /// </param>
        public ManagedHook(Action<TInputs> action)
        {
            this.action = action;
        }

        /// <summary>
        /// Executes the managed code after completing the dependency.
        /// </summary>
        /// <param name="inputs">Stage inputs.</param>
        /// <param name="stageStart">Dependency from the previous stage (will be completed).</param>
        /// <param name="chained">Dependency from previous chained hooks (ignored for managed hooks).</param>
        /// <param name="handle">Output handle (always default, as managed code completes synchronously).</param>
        /// <returns>False (managed hooks don't participate in job chaining).</returns>
        /// <remarks>
        /// This implementation:
        /// 1. Completes the stageStart dependency (causes a sync point)
        /// 2. Executes the managed code synchronously
        /// 3. Returns default(JobHandle) and false (no async work to track)
        ///
        /// The managed code runs on the main thread and blocks until complete.
        /// </remarks>
        public bool Execute(TInputs inputs, JobHandle stageStart, JobHandle chained, out JobHandle handle)
        {
            // Complete the dependency to ensure safe access to data
            JobHandle dependency = UseChaining ? chained : stageStart;
            dependency.Complete();

            // Execute managed code
            ExecuteManaged(inputs);

            // No job handle to return (synchronous work)
            handle = default;

            // Managed hooks don't chain (they complete synchronously)
            return UseChaining;
        }

        /// <summary>
        /// Executes the managed logic. Override this in derived classes for custom behavior.
        /// </summary>
        /// <param name="inputs">The stage inputs.</param>
        /// <remarks>
        /// The default implementation calls the action provided in the constructor.
        /// Override this method if you want to use inheritance instead of lambda-based hooks.
        /// </remarks>
        protected virtual void ExecuteManaged(TInputs inputs)
        {
            action?.Invoke(inputs);
        }
    }
}
