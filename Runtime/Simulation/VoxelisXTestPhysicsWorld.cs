using System;
using System.Collections.Generic;
using UnityEngine;

namespace Voxelis.Simulation
{
    public class VoxelisXTestPhysicsWorld : VoxelisXPhysicsWorld
    {
        [Header("Test zone")]
        [SerializeField] private List<VoxelBody> dynamicbodies = new();
        [SerializeField] private List<VoxelBody> staticBodies = new();
        
        public override void Init()
        {
            base.Init();
            PrepareTestWorld();
        }
        
        public void PrepareTestWorld()
        {
            physicsWorld.Reset(staticBodies.Count, dynamicbodies.Count, 0);

            int bodyIndex = 0;

            var bodies = physicsWorld.Bodies;
            var motionDatas = physicsWorld.MotionDatas;
            var motionVelocities = physicsWorld.MotionVelocities;

            // Add static bodies (with box colliders)
            for (int i = 0; i < staticBodies.Count; i++)
            {
                VoxelBody vb = staticBodies[i];
                Transform t = vb.transform;

                // Create box collider
                var boxGeometry = new Unity.Physics.BoxGeometry
                {
                    Center = Unity.Mathematics.float3.zero,
                    Orientation = Unity.Mathematics.quaternion.identity,
                    Size = new Unity.Mathematics.float3(1f, 1f, 1f),
                    BevelRadius = 0.05f
                };
                var boxCollider = Unity.Physics.BoxCollider.Create(boxGeometry);

                // Create rigid body
                bodies[bodyIndex] = new Unity.Physics.RigidBody
                {
                    WorldFromBody = new Unity.Mathematics.RigidTransform(
                        new Unity.Mathematics.quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                        new Unity.Mathematics.float3(t.position.x, t.position.y, t.position.z)
                    ),
                    Collider = boxCollider,
                    Entity = Unity.Entities.Entity.Null,
                    Scale = 1.0f,
                    CustomTags = 0
                };

                bodyIndex++;
            }

            // Add dynamic bodies (with sphere colliders)
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                VoxelBody vb = dynamicbodies[i];
                Transform t = vb.transform;

                // Create sphere collider
                var sphereGeometry = new Unity.Physics.SphereGeometry
                {
                    Center = Unity.Mathematics.float3.zero,
                    Radius = 0.5f
                };
                var sphereCollider = Unity.Physics.SphereCollider.Create(sphereGeometry);

                // Create rigid body
                bodies[bodyIndex] = new Unity.Physics.RigidBody
                {
                    WorldFromBody = new Unity.Mathematics.RigidTransform(
                        new Unity.Mathematics.quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                        new Unity.Mathematics.float3(t.position.x, t.position.y, t.position.z)
                    ),
                    Collider = sphereCollider,
                    Entity = Unity.Entities.Entity.Null,
                    Scale = 1.0f,
                    CustomTags = 0
                };

                // Create motion data (center of mass at body origin)
                motionDatas[i] = new Unity.Physics.MotionData
                {
                    WorldFromMotion = new Unity.Mathematics.RigidTransform(
                        new Unity.Mathematics.quaternion(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w),
                        new Unity.Mathematics.float3(t.position.x, t.position.y, t.position.z)
                    ),
                    BodyFromMotion = Unity.Mathematics.RigidTransform.identity,
                    LinearDamping = 0.01f,
                    AngularDamping = 0.05f
                };

                // Create motion velocity
                var massProperties = Unity.Physics.MassProperties.CreateSphere(0.5f);
                motionVelocities[i] = new Unity.Physics.MotionVelocity
                {
                    LinearVelocity = Unity.Mathematics.float3.zero,
                    AngularVelocity = Unity.Mathematics.float3.zero,
                    InverseInertia = Unity.Mathematics.math.rcp(massProperties.MassDistribution.InertiaTensor),
                    InverseMass = 1.0f / 1.0f, // mass = 1kg
                    AngularExpansionFactor = massProperties.AngularExpansionFactor,
                    GravityFactor = 1.0f
                };

                bodyIndex++;
            }
        }

        public void ExportTestWorld()
        {
            for (int i = 0; i < dynamicbodies.Count; i++)
            {
                dynamicbodies[i].transform.position = physicsWorld.Bodies[i + staticBodies.Count].WorldFromBody.pos;
            }
        }

        public override void SimulateStep(float dt)
        {
            base.SimulateStep(dt);
            ExportTestWorld();
        }

        private void Update()
        {
            SimulateStep(Time.deltaTime);
        }
    }
}