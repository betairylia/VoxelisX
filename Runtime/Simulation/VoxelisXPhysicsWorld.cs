using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Physics;
using UnityEngine;

namespace Voxelis.Simulation
{
    public class VoxelisXPhysicsWorld : MonoSingleton<VoxelisXPhysicsWorld>
    {
        private List<VoxelBody> bodies = new();

        private PhysicsWorld physicsWorld;
        private Unity.Physics.Simulation simulation;

        public Vector3 gravity;
        
        [Header("Test zone")]
        [SerializeField] private List<VoxelBody> dynamicbodies = new();
        [SerializeField] private List<VoxelBody> staticBodies = new();

        public override void Init()
        {
            base.Init();
            physicsWorld = new PhysicsWorld(0, 0, 0);
            simulation = Unity.Physics.Simulation.Create();
        }

        public void PrepareTestWorld()
        {
            physicsWorld.Reset(staticBodies.Count, dynamicbodies.Count, 0);
            
            // Add static bodies
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                
            }
            
            // Add dynamic bodies
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                
            }
        }

        public void AddBody(VoxelBody b)
        {
            bodies.Add(b);
        }

        public void RemoveBody(VoxelBody b)
        {
            bodies.Remove(b);
        }

        public void SimulateStep(float dt)
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