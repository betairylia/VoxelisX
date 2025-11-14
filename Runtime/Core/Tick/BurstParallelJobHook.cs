using Unity.Jobs;

namespace Voxelis.Tick
{
    /// <summary>
    /// Abstract helper class for creating hooks that schedule Burst-compatible IJobParallelFor jobs.
    /// Simplifies the implementation of parallel job-based hooks by handling scheduling boilerplate.
    /// </summary>
    /// <typeparam name="TInputs">The input interface type for this stage.</typeparam>
    /// <typeparam name="TJob">The IJobParallelFor struct type to execute.</typeparam>
    /// <remarks>
    /// This helper automatically schedules your parallel job and handles the Execute() contract.
    /// You need to implement PrepareJob() and GetBatchCount() to configure the job.
    ///
    /// Example usage:
    /// <code>
    /// // Define your parallel job
    /// public struct ProcessEntitiesJob : IJobParallelFor
    /// {
    ///     public NativeList&lt;VoxelEntityData&gt; entities;
    ///
    ///     public void Execute(int index)
    ///     {
    ///         // Process entity at index
    ///         var entity = entities[index];
    ///         // ... modify entity ...
    ///         entities[index] = entity;
    ///     }
    /// }
    ///
    /// // Create a hook using the helper
    /// public class ProcessEntitiesHook : BurstParallelJobHook&lt;IPhysicsStageInputs, ProcessEntitiesJob&gt;
    /// {
    ///     protected override bool UseChaining => false; // Run in parallel with other hooks
    ///
    ///     protected override ProcessEntitiesJob PrepareJob(IPhysicsStageInputs inputs)
    ///     {
    ///         return new ProcessEntitiesJob { entities = inputs.Entities };
    ///     }
    ///
    ///     protected override int GetBatchCount(IPhysicsStageInputs inputs)
    ///     {
    ///         return inputs.Entities.Length;
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public abstract class BurstParallelJobHook<TInputs, TJob> : ITickHook<TInputs>
        where TJob : struct, IJobParallelFor
    {
        /// <summary>
        /// Gets whether this hook should chain with previous hooks (sequential) or run in parallel.
        /// </summary>
        /// <remarks>
        /// Override this property to control execution order:
        /// - true: Sequential execution - wait for previous chained hooks
        /// - false: Parallel execution - only wait for the previous stage
        ///
        /// Default: true (sequential)
        /// </remarks>
        protected virtual bool UseChaining => true;

        /// <summary>
        /// Gets the batch size for parallel job scheduling.
        /// A smaller batch size can improve work distribution but increases overhead.
        /// </summary>
        /// <remarks>
        /// Default: 32 (a reasonable balance for most workloads)
        /// Override to tune performance for your specific use case.
        /// </remarks>
        protected virtual int BatchSize => 32;

        /// <summary>
        /// Prepares the job instance with data from stage inputs.
        /// </summary>
        /// <param name="inputs">The stage inputs containing data for the job.</param>
        /// <returns>A configured job ready to be scheduled.</returns>
        /// <remarks>
        /// This method is called each tick before scheduling the job.
        /// Use it to populate the job's fields from the stage inputs.
        /// </remarks>
        protected abstract TJob PrepareJob(TInputs inputs);

        /// <summary>
        /// Gets the number of iterations for the parallel job.
        /// </summary>
        /// <param name="inputs">The stage inputs.</param>
        /// <returns>The number of times Execute(int index) should be called (typically the collection length).</returns>
        /// <remarks>
        /// This is typically the length of the collection being processed.
        /// For example, if processing entities, return inputs.Entities.Length.
        /// </remarks>
        protected abstract int GetBatchCount(TInputs inputs);

        /// <summary>
        /// Executes the hook by preparing and scheduling the parallel job.
        /// </summary>
        /// <param name="inputs">Stage inputs.</param>
        /// <param name="stageStart">Dependency from the previous stage.</param>
        /// <param name="chained">Dependency from previous chained hooks in this stage.</param>
        /// <param name="handle">Output handle for the scheduled job.</param>
        /// <returns>The value of UseChaining (true for sequential, false for parallel).</returns>
        public bool Execute(TInputs inputs, JobHandle stageStart, JobHandle chained, out JobHandle handle)
        {
            TJob job = PrepareJob(inputs);
            int batchCount = GetBatchCount(inputs);
            JobHandle dependency = UseChaining ? chained : stageStart;
            handle = job.Schedule(batchCount, BatchSize, dependency);
            return UseChaining;
        }
    }
}
