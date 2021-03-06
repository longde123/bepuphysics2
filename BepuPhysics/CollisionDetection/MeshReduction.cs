﻿using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.CollisionDetection
{
    public struct MeshReduction : ICollisionTestContinuation
    {
        /// <summary>
        /// Flag used to mark a contact as being generated by the face of a triangle in its feature id.
        /// </summary>
        public const int FaceCollisionFlag = 32768;
        public Buffer<Triangle> Triangles;
        //MeshReduction relies on all of a mesh's triangles being in slot B, as they appear in the mesh collision tasks.
        //However, the original user may have provided this pair in unknown order and triggered a flip. We'll compensate for that when examining contact positions.
        public bool RequiresFlip;
        //The triangles array is in the mesh's local space. In order to test any contacts against them, we need to be able to transform contacts.
        public BepuUtilities.Quaternion MeshOrientation;
        //This uses all of the nonconvex reduction's logic, so we just nest it.
        public NonconvexReduction Inner;

        public void Create(int childManifoldCount, BufferPool pool)
        {
            Inner.Create(childManifoldCount, pool);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void OnChildCompleted<TCallbacks>(ref PairContinuation report, ConvexContactManifold* manifold, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks
        {
            Inner.OnChildCompleted(ref report, manifold, ref batcher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChildCompletedEmpty<TCallbacks>(ref PairContinuation report, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Inner.OnChildCompletedEmpty(ref report, ref batcher);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ComputeMeshSpaceContacts(in ConvexContactManifold manifold, in Matrix3x3 inverseMeshOrientation, bool requiresFlip, Vector3* meshSpaceContacts, out Vector3 meshSpaceNormal)
        {
            //First, if the manifold considers the mesh and its triangles to be shape B, then we need to flip it.
            if (requiresFlip)
            {
                //If the manifold considers the mesh and its triangles to be shape B, it needs to be flipped before being transformed.
                for (int i = 0; i < manifold.Count; ++i)
                {
                    Matrix3x3.Transform(manifold.Contact0.Offset - manifold.OffsetB, inverseMeshOrientation, out meshSpaceContacts[i]);
                }
                Matrix3x3.Transform(-manifold.Normal, inverseMeshOrientation, out meshSpaceNormal);
            }
            else
            {
                //No flip required.
                for (int i = 0; i < manifold.Count; ++i)
                {
                    Matrix3x3.Transform(manifold.Contact0.Offset, inverseMeshOrientation, out meshSpaceContacts[i]);
                }
                Matrix3x3.Transform(manifold.Normal, inverseMeshOrientation, out meshSpaceNormal);
            }
        }

        struct TestTriangle
        {
            //The test triangle contains AOS-ified layouts for quicker per contact testing.
            public Vector4 AnchorX;
            public Vector4 AnchorY;
            public Vector4 AnchorZ;
            public Vector4 NX;
            public Vector4 NY;
            public Vector4 NZ;
            public float DistanceThreshold;
            public int ChildIndex;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TestTriangle(in Triangle triangle, int sourceChildIndex)
            {
                var ab = triangle.B - triangle.A;
                var bc = triangle.C - triangle.B;
                var ca = triangle.A - triangle.C;
                //TODO: This threshold might result in bumps when dealing with small triangles. May want to include a different source of scale information, like from the original convex test.
                DistanceThreshold = 1e-4f * (float)Math.Sqrt(MathHelper.Max(ab.LengthSquared(), bc.LengthSquared()));
                Vector3x.Cross(ab, bc, out var n);
                //Edge normals point outward.
                Vector3x.Cross(ab, n, out var edgeNormalAB);
                Vector3x.Cross(bc, n, out var edgeNormalBC);
                Vector3x.Cross(ca, n, out var edgeNormalCA);

                NX = new Vector4(n.X, edgeNormalAB.X, edgeNormalBC.X, edgeNormalCA.X);
                NY = new Vector4(n.Y, edgeNormalAB.Y, edgeNormalBC.Y, edgeNormalCA.Y);
                NZ = new Vector4(n.Z, edgeNormalAB.Z, edgeNormalBC.Z, edgeNormalCA.Z);
                var normalLengthSquared = NX * NX + NY * NY + NZ * NZ;
                var inverseLength = Vector4.One / Vector4.SquareRoot(normalLengthSquared);
                NX *= inverseLength;
                NY *= inverseLength;
                NZ *= inverseLength;
                AnchorX = new Vector4(triangle.A.X, triangle.A.X, triangle.B.X, triangle.C.X);
                AnchorY = new Vector4(triangle.A.Y, triangle.A.Y, triangle.B.Y, triangle.C.Y);
                AnchorZ = new Vector4(triangle.A.Z, triangle.A.Z, triangle.B.Z, triangle.C.Z);

                ChildIndex = sourceChildIndex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool ShouldCorrectNormal(in TestTriangle triangle, Vector3* meshSpaceContacts, int contactCount, in Vector3 meshSpaceNormal)
        {
            //While we don't have a decent way to do truly scaling SIMD operations within the context of a single manifold vs triangle test, we can at least use 4-wide operations
            //to accelerate each individual contact test. 
            for (int i = 0; i < contactCount; ++i)
            {
                // distanceFromPlane = (Position - a) * N / ||N||
                // distanceFromPlane^2 = ((Position - a) * N)^2 / (N * N)
                // distanceAlongEdgeNormal^2 = ((Position - edgeStart) * edgeN)^2 / ||edgeN||^2

                //There are four lanes, one for each plane of consideration:
                //X: Plane normal
                //Y: AB edge normal
                //Z: BC edge normal
                //W: CA edge normal
                //They're all the same operation, so we can do them 4-wide. That's better than doing a bunch of individual horizontal dot products.
                ref var contact = ref meshSpaceContacts[i];
                var px = new Vector4(contact.X);
                var py = new Vector4(contact.Y);
                var pz = new Vector4(contact.Z);
                var offsetX = px - triangle.AnchorX;
                var offsetY = py - triangle.AnchorY;
                var offsetZ = pz - triangle.AnchorZ;
                var distanceAlongNormal = offsetX * triangle.NX + offsetY * triangle.NY + offsetZ * triangle.NZ;
                //Note that very very thin triangles can result in questionable acceptance due to not checking for true distance- 
                //a position might be way outside a vertex, but still within edge plane thresholds. We're assuming that the impact of this problem will be minimal.
                if (distanceAlongNormal.X <= triangle.DistanceThreshold &&
                    distanceAlongNormal.Y <= triangle.DistanceThreshold &&
                    distanceAlongNormal.Z <= triangle.DistanceThreshold &&
                    distanceAlongNormal.W <= triangle.DistanceThreshold)
                {
                    //The contact is near the triangle. Is the normal infringing on the triangle's face region?
                    //This occurs when:
                    //1) The contact is near an edge, and the normal points inward along the edge normal.
                    //2) The contact is on the inside of the triangle.
                    var negativeThreshold = -triangle.DistanceThreshold;
                    var onAB = distanceAlongNormal.Y >= negativeThreshold;
                    var onBC = distanceAlongNormal.Z >= negativeThreshold;
                    var onCA = distanceAlongNormal.W >= negativeThreshold;
                    if (!onAB && !onBC && !onCA)
                    {
                        //The contact is within the triangle. 
                        //If this contact resulted in a correction, we can skip the remaining contacts in this manifold.
                        return true;
                    }
                    else
                    {
                        //The contact is on the border of the triangle. Is the normal pointing inward on any edge that the contact is on?
                        //Remember, the contact has been pushed into mesh space. The position is on the surface of the triangle, and the normal points from mesh to convex.
                        //The edge plane normals point outward from the triangle, so if the contact normal is detected as facing the same direction as the edge plane normal,
                        //then it is infringing.
                        var normalDot = triangle.NX * meshSpaceNormal.X + triangle.NY * meshSpaceNormal.Y + triangle.NZ * meshSpaceNormal.Z;
                        if ((onAB && normalDot.Y > 5e-5f) || (onBC && normalDot.Z > 5e-5f) || (onCA && normalDot.W > 5e-5f))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe bool TryFlush<TCallbacks>(int pairId, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Debug.Assert(Inner.ChildCount > 0);
            if (Inner.CompletedChildCount == Inner.ChildCount)
            {
                //Before handing responsibility off to the nonconvex reduction, make sure that no contacts create nasty 'bumps' at the border of triangles.
                //Bumps can occur when an isolated triangle test detects a contact pointing outward, like when a box hits the side. This is fine when the triangle truly is isolated,
                //but if there's a neighboring triangle that's snugly connected, the user probably wants the two triangles to behave as a single coherent surface. So, contacts
                //with normals which wouldn't exist in the ideal 'continuous' form of the surface need to be corrected.

                //A contact is a candidate for correction if it meets three conditions:
                //1) The contact was not generated by a face collision, and
                //2) The contact position is touching another triangle, and
                //3) The contact normal is infringing on the neighbor's face voronoi region.

                //Contacts generated by face collisions are always immediately accepted without modification. 
                //The only time they can cause infringement is when the surface is concave, and in that case, the face normal is correct and will not cause any inappropriate bumps.

                //A contact that isn't touching a triangle can't infringe upon it.
                //Note that triangle-involved manifolds always generate contacts such that the position is on the triangle to make this test meaningful.
                //(That's why the MeshReduction has to be aware of whether the manifolds have been flipped- so that we know we're working with consistent slots.)

                //Contacts generated by face collisions are marked with a special feature id flag. If it is present, we can skip the contact. The collision tester also provided unique feature ids
                //beyond that flag, so we can strip the flag now. (We effectively just hijacked the feature id to store some temporary metadata.)

                //TODO: Note that we perform contact correction prior to reduction. Reduction depends on normals to compute its 'constrainedness' heuristic.
                //You could sacrifice a little bit of reduction quality for faster contact correction (since reduction outputs a low fixed number of contacts), but
                //we should only pursue that if contact correction is a meaningful cost.

                Matrix3x3.CreateFromQuaternion(MeshOrientation, out var meshOrientation);
                Matrix3x3.Transpose(meshOrientation, out var meshInverseOrientation);

                //Allocate enough space for all potential triangles, even though we're only going to be enumerating over the subset which actually have contacts.
                int activeChildCount = 0;
                var activeTriangles = stackalloc TestTriangle[Inner.ChildCount];
                for (int i = 0; i < Inner.ChildCount; ++i)
                {
                    if (Inner.Children[i].Manifold.Count > 0)
                    {
                        activeTriangles[activeChildCount] = new TestTriangle(Triangles[i], i);
                        ++activeChildCount;
                    }
                }
                var meshSpaceContacts = stackalloc Vector3[4];
                for (int i = 0; i < activeChildCount; ++i)
                {
                    ref var sourceTriangle = ref activeTriangles[i];
                    ref var sourceChild = ref Inner.Children[sourceTriangle.ChildIndex];
                    //Can't correct contacts that were created by face collisions.
                    if ((sourceChild.Manifold.Contact0.FeatureId & FaceCollisionFlag) == 0)
                    {
                        ComputeMeshSpaceContacts(sourceChild.Manifold, meshInverseOrientation, RequiresFlip, meshSpaceContacts, out var meshSpaceNormal);
                        for (int j = 0; j < activeChildCount; ++j)
                        {
                            //No point in trying to correct a normal against its own triangle.
                            if (i != j)
                            {
                                ref var targetTriangle = ref activeTriangles[j];
                                if (ShouldCorrectNormal(targetTriangle, meshSpaceContacts, sourceChild.Manifold.Count, meshSpaceNormal))
                                {
                                    //This is a bit of a hack. We arbitrarily say that any corrected contact is not allowed to contribute to position correction at all.
                                    //Further, despite changing the normal, we keep the depth of speculative contacts the same, even though the projected depth is less.
                                    //We don't want to make false collisions *more* prominent.
                                    for (int k = 0; k < sourceChild.Manifold.Count; ++k)
                                    {
                                        ref var depth = ref Unsafe.Add(ref sourceChild.Manifold.Contact0, k).Depth;
                                        if (depth > 0)
                                            depth = 0;
                                    }
                                    //Bring the corrected normal back into world space.
                                    var triangleNormal = new Vector3(targetTriangle.NX.X, targetTriangle.NY.X, targetTriangle.NZ.X);
                                    Matrix3x3.Transform(RequiresFlip ? -triangleNormal : triangleNormal, meshOrientation, out sourceChild.Manifold.Normal);
                                    //Since corrections result in the normal being set to the triangle normal, multiple corrections in sequence would just overwrite each other.
                                    //There is no sequence which is more correct than another, so once we find one correction, we can just quit.
                                    break;
                                }
                            }
                        }
                        //Clear the face flags. This isn't *required* since they're coherent enough anyway and the accumulated impulse redistributor is a decent fallback,
                        //but it costs basically nothing to do this.
                        for (int k = 0; k < sourceChild.Manifold.Count; ++k)
                        {
                            Unsafe.Add(ref sourceChild.Manifold.Contact0, k).FeatureId &= ~FaceCollisionFlag;
                        }
                    }
                }

                //Now that boundary smoothing analysis is done, we no longer need the triangle list.
                batcher.Pool.Return(ref Triangles);
                Inner.Flush(pairId, ref batcher);
                return true;
            }
            return false;
        }

    }
}
