using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Physics;
using UnityEngine;

namespace Voxelis.Simulation
{
    public class VoxelisXPhysicsWorld : MonoSingleton<VoxelisXPhysicsWorld>
    {
        protected List<VoxelBody> bodies = new();

        protected PhysicsWorld physicsWorld;
        protected Unity.Physics.Simulation simulation;

        public Vector3 gravity;

        public override void Init()
        {
            base.Init();
            physicsWorld = new PhysicsWorld(0, 0, 0);
            simulation = Unity.Physics.Simulation.Create();
        }

        public void AddBody(VoxelBody b)
        {
            bodies.Add(b);
        }

        public void RemoveBody(VoxelBody b)
        {
            bodies.Remove(b);
        }

        public virtual void SimulateStep(float dt)
        {
            // TODO: Update physics World
            // PhysicsWorldBuilder.cs:88

            SimulationStepInput stepInput = new SimulationStepInput()
            {
                World = physicsWorld,
                TimeStep = dt,
                Gravity = gravity
            };

            simulation.ResetSimulationContext(stepInput);
            simulation.ScheduleStepJobs(stepInput, default, true);
        }

        private void OnDestroy()
        {
            simulation.Dispose();
            physicsWorld.Dispose();
        }
    }
}