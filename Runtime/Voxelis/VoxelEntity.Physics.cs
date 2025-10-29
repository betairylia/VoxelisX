using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Voxelis.Simulation;

namespace Voxelis
{
    public partial class VoxelEntity : MonoBehaviour, IDisposable
    {
        public void InitializeBody()
        {
            if (!physicsEnabled)
            {
                return;
            }

            body = gameObject.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = gameObject.AddComponent<Rigidbody>();
            }
            debugBody = gameObject.AddComponent<UnityPhysicsCollider>();
            
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
        }

        // TODO: Incremental update with parallel axis theorem for inertia
        public void UpdateBody()
        {
            if (body == null) return;
            
            // Center of Mass
            // Temp array for results
            var CoM = new NativeArray<float4>(1, Allocator.TempJob);
            CoM[0] = new float4(0, 0, 0, 0);
            
            foreach (SectorRef sector in Voxels.Values)
            {
                var sectorBPos = sector.sectorBlockPos;
                
                var sectorJob = new VoxelEntityPhysics.AccumulateSectorCenterOfMass
                {
                    settings = PhysicsSettings.Settings,
                    sector = sector.sector,
                    sectorPosition = new int3(sectorBPos.x, sectorBPos.y, sectorBPos.z),
                    
                    accumulatedCenter = CoM
                };
                
                sectorJob.Schedule().Complete();
            }

            body.mass = CoM[0].w;
            if (CoM[0].w > 0)
            {
                body.centerOfMass = CoM[0].xyz / CoM[0].w;
            }
            else
            {
                body.centerOfMass = Vector3.zero;
            }
            
            // Inertia
            var Inertia = new NativeArray<float3>(1, Allocator.TempJob);
            Inertia[0] = new float3(0, 0, 0);
            foreach (SectorRef sector in Voxels.Values)
            {
                var sectorBPos = sector.sectorBlockPos;
                
                var sectorJob = new VoxelEntityPhysics.AccumulateSectorInertia
                {
                    settings = PhysicsSettings.Settings,
                    sector = sector.sector,
                    sectorPosition = new int3(sectorBPos.x, sectorBPos.y, sectorBPos.z),
                    
                    centerOfMass = body.centerOfMass,
                    accumulatedInertia = Inertia,
                };
                
                sectorJob.Schedule().Complete();
            }

            body.inertiaTensor = Inertia[0];
            body.inertiaTensorRotation = Quaternion.identity;
        }

        public static void ResolveContact(IEnumerable<VoxelCollisionSolver.ContactPoint> wsContactsB, VoxelEntity Ae, VoxelEntity Be)
        {
            var A = Ae.body;
            var B = Be.body;
            if (A == null || B == null) return;
            
            // Try another method to solve this
            foreach (var contact in wsContactsB)
            {
                Ae.debugBody.AddAt(new Vector3Int(contact.srcBlock.x, contact.srcBlock.y, contact.srcBlock.z));
                Be.debugBody.AddAt(new Vector3Int(contact.dstBlock.x, contact.dstBlock.y, contact.dstBlock.z));
            }

            return;
            
            var iInvA = new Vector3(1.0f / A.inertiaTensor.x, 1.0f / A.inertiaTensor.y, 1.0f / A.inertiaTensor.z);
            var iInvB = new Vector3(1.0f / B.inertiaTensor.x, 1.0f / B.inertiaTensor.y, 1.0f / B.inertiaTensor.z);
            var imA = 1.0f / A.mass;
            var imB = 1.0f / B.mass;
            
            foreach (var contact in wsContactsB)
            {
                var relativePosA = (Vector3)contact.position - A.position;
                var relativePosB = (Vector3)contact.position - B.position;

                var angVeloA = Vector3.Cross(A.angularVelocity, relativePosA);
                var angVeloB = Vector3.Cross(B.angularVelocity, relativePosB);

                var fullVeloA = A.linearVelocity + angVeloA;
                var fullVeloB = B.linearVelocity + angVeloB;
                var contactVelo = fullVeloB - fullVeloA;

                float impulse = Vector3.Dot(contactVelo, contact.normal);

                Vector3 inertiaA = Vector3.Cross(
                    Vector3.Scale(iInvA, Vector3.Cross(relativePosA, contact.normal)),
                    relativePosA);
                
                Vector3 inertiaB = Vector3.Cross(
                    Vector3.Scale(iInvB, Vector3.Cross(relativePosB, contact.normal)),
                    relativePosB);

                float angular = Vector3.Dot(inertiaA + inertiaB, contact.normal);
                
                // TODO: Phys material
                float e = 0.5f;

                float j = (-(1.0f + e) * impulse) / (imA + imB + angular);

                A.AddForceAtPosition(-contact.normal * j, contact.position, ForceMode.Impulse);
                B.AddForceAtPosition(+contact.normal * j, contact.position, ForceMode.Impulse);

                // break;
            }
        }

        [ContextMenu("Test")]
        private void TestForce()
        {
            body.AddForceAtPosition(Vector3.up, Vector3.right * 2.0f, ForceMode.Impulse);
        }

        private void OnDrawGizmos()
        {
            if (body == null) return;
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.TransformPoint(body.centerOfMass), 0.25f);
        }
    }
}