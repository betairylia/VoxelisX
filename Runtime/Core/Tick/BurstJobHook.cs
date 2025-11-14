using Unity.Jobs;

namespace Voxelis.Tick
{
    /// <summary>
    /// Abstract helper class for creating hooks that schedule Burst-compatible IJob jobs.
    /// Simplifies the implementation of job-based hooks by handling scheduling boilerplate.
    /// </summary>
    /// <typeparam name="TInputs">The input interface type for this stage.</typeparam>
    /// <typeparam name="TJob">The IJob struct type to execute.</typeparam>
    /// <remarks>
    /// This helper automatically schedules your job and handles the Execute() contract.
    /// You only need to implement PrepareJob() to configure the job from stage inputs.
    ///
    /// Example usage:
    /// <code>
    /// // Define your job
    /// public struct MyPhysicsJob : IJob
    /// {
    ///     public NativeList&lt;VoxelEntityData&gt; entities;
    ///     public float deltaTime;
    ///
    ///     public void Execute()
    ///     {
    ///         // Your physics logic here
    ///     }
    /// }
    ///
    /// // Create a hook using the helper
    /// public class MyPhysicsHook : BurstJobHook&lt;IPhysicsStageInputs, MyPhysicsJob&gt;
    /// {
    ///     protected override bool UseChaining => true; // Run sequentially
    ///
    ///     protected override MyPhysicsJob PrepareJob(IPhysicsStageInputs inputs)
    ///     {
    ///         return new MyPhysicsJob
    ///         {
    ///             entities = inputs.Entities,
    ///             deltaTime = inputs.DeltaTime
    ///         };
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public abstract class BurstJobHook<TInputs, TJob> : ITickHook<TInputs>
        where TJob : struct, IJob
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
        /// Executes the hook by preparing and scheduling the job.
        /// </summary>
        /// <param name="inputs">Stage inputs.</param>
        /// <param name="stageStart">Dependency from the previous stage.</param>
        /// <param name="chained">Dependency from previous chained hooks in this stage.</param>
        /// <param name="handle">Output handle for the scheduled job.</param>
        /// <returns>The value of UseChaining (true for sequential, false for parallel).</returns>
        public bool Execute(TInputs inputs, JobHandle stageStart, JobHandle chained, out JobHandle handle)
        {
            TJob job = PrepareJob(inputs);
            JobHandle dependency = UseChaining ? chained : stageStart;
            handle = job.Schedule(dependency);
            return UseChaining;
        }
    }
}
