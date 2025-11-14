using Unity.Jobs;

namespace Voxelis.Tick
{
    /// <summary>
    /// Interface for hooks that execute within a tick stage.
    /// Hooks can execute in parallel or chain sequentially based on their dependencies.
    /// </summary>
    /// <typeparam name="TInputs">The input interface type for this stage.</typeparam>
    /// <remarks>
    /// Hooks control their execution order by returning true (sequential chaining) or false (parallel execution).
    ///
    /// Example usage:
    /// <code>
    /// public class MyPhysicsHook : ITickHook&lt;IPhysicsStageInputs&gt;
    /// {
    ///     public bool Execute(IPhysicsStageInputs inputs, JobHandle stageStart, JobHandle chained, out JobHandle handle)
    ///     {
    ///         var job = new MyPhysicsJob { entities = inputs.Entities };
    ///         handle = job.Schedule(chained);
    ///         return true; // Chain with previous hooks
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public interface ITickHook<TInputs>
    {
        /// <summary>
        /// Executes the hook logic, optionally scheduling jobs.
        /// </summary>
        /// <param name="inputs">Stage-specific typed inputs (e.g., entity data, delta time).</param>
        /// <param name="stageStart">The dependency handle from the previous stage. Use this for parallel execution.</param>
        /// <param name="chained">The dependency handle from previous hooks in this stage. Use this for sequential execution.</param>
        /// <param name="handle">
        /// Output handle for the scheduled job.
        /// If no job is scheduled (synchronous work), return default(JobHandle).
        /// </param>
        /// <returns>
        /// True: Sequential execution - your job depends on <paramref name="chained"/> and subsequent hooks will depend on your handle.
        /// False: Parallel execution - your job only depends on <paramref name="stageStart"/> and runs independently of other hooks.
        /// </returns>
        /// <remarks>
        /// Execution flow:
        /// - Return false (parallel): Your job runs alongside other parallel hooks, only waiting for the previous stage.
        /// - Return true (sequential): Your job runs after all previous chained hooks, forming a dependency chain.
        ///
        /// The stage will combine all returned handles to ensure proper synchronization.
        /// </remarks>
        bool Execute(TInputs inputs, JobHandle stageStart, JobHandle chained, out JobHandle handle);
    }
}
