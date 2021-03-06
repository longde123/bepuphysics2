﻿using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BepuPhysics.Collidables
{
    public struct Mesh : IMeshShape
    {
        public Tree Tree;
        public Buffer<Triangle> Triangles;
        internal Vector3 scale;
        internal Vector3 inverseScale;
        public Vector3 Scale
        {
            get
            {
                return scale;
            }
            set
            {
                Debug.Assert(value.X != 0 && value.Y != 0 && value.Z != 0, "All components of scale must be nonzero.");
                scale = value;
                inverseScale = Vector3.One / value;
            }
        }

        public Mesh(Buffer<Triangle> triangles, Vector3 scale, BufferPool pool) : this()
        {
            Triangles = triangles;
            Tree = new Tree(pool, triangles.Length);
            pool.Take<BoundingBox>(triangles.Length, out var boundingBoxes);
            for (int i = 0; i < triangles.Length; ++i)
            {
                ref var t = ref triangles[i];
                ref var bounds = ref boundingBoxes[i];
                bounds.Min = Vector3.Min(t.A, Vector3.Min(t.B, t.C));
                bounds.Max = Vector3.Max(t.A, Vector3.Max(t.B, t.C));
            }
            Tree.SweepBuild(pool, boundingBoxes.Slice(0, triangles.Length));
            Scale = scale;
        }

        public void ComputeBounds(in BepuUtilities.Quaternion orientation, out Vector3 min, out Vector3 max)
        {
            Matrix3x3.CreateFromQuaternion(orientation, out var r);
            min = new Vector3(float.MaxValue);
            max = new Vector3(-float.MaxValue);
            for (int i = 0; i < Triangles.Length; ++i)
            {
                //This isn't an ideal bounding box calculation for a mesh. 
                //-You might be able to get a win out of widely vectorizing.
                //-Indexed smooth meshes would tend to have a third as many max/min operations.
                //-Even better would be a set of extreme points that are known to fully enclose the mesh, eliminating the need to test the vast majority.
                //But optimizing this only makes sense if dynamic meshes are common, and they really, really, really should not be.
                ref var triangle = ref Triangles[i];
                Matrix3x3.Transform(scale * triangle.A, r, out var a);
                Matrix3x3.Transform(scale * triangle.B, r, out var b);
                Matrix3x3.Transform(scale * triangle.C, r, out var c);
                var min0 = Vector3.Min(a, b);
                var min1 = Vector3.Min(c, min);
                var max0 = Vector3.Max(a, b);
                var max1 = Vector3.Max(c, max);
                min = Vector3.Min(min0, min1);
                max = Vector3.Max(max0, max1);
            }
        }

        public ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        {
            return new MeshShapeBatch<Mesh>(pool, initialCapacity);
        }

        public bool RayTest(in RigidPose pose, in Vector3 origin, in Vector3 direction, out float t, out Vector3 normal)
        {
            t = 0;
            normal = default;
            return false;
        }

        public void RayTest<TRayHitHandler>(RigidPose pose, ref RaySource rays, ref TRayHitHandler hitHandler) where TRayHitHandler : struct, IShapeRayHitHandler
        {
        }
        struct Enumerator : IBreakableForEach<int>
        {
            public BufferPool<int> Pool;
            public QuickList<int, Buffer<int>> Children;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool LoopBody(int i)
            {
                Children.Add(i, Pool);
                return true;
            }
        }

        public unsafe void FindLocalOverlaps(in Vector3 min, in Vector3 max, BufferPool pool, ref QuickList<int, Buffer<int>> childIndices)
        {
            Debug.Assert(childIndices.Span.Memory != null, "The given list reference is expected to already be constructed and ready for use.");
            var scaledMin = min * inverseScale;
            var scaledMax = max * inverseScale;
            Enumerator enumerator;
            enumerator.Pool = pool.SpecializeFor<int>();
            enumerator.Children = childIndices;
            Tree.GetOverlaps(scaledMin, scaledMax, ref enumerator);
            childIndices = enumerator.Children;
        }

        public unsafe void FindLocalOverlaps(ref Buffer<IntPtr> meshes, ref Vector3Wide min, ref Vector3Wide max, int count, BufferPool pool, ref Buffer<QuickList<int, Buffer<int>>> childIndices)
        {
            for (int i = 0; i < count; ++i)
            {
                Vector3Wide.ReadSlot(ref min, i, out var narrowMin);
                Vector3Wide.ReadSlot(ref max, i, out var narrowMax);
                Unsafe.AsRef<Mesh>(meshes[i].ToPointer()).FindLocalOverlaps(narrowMin, narrowMax, pool, ref childIndices[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void GetTriangles(ref QuickList<int, Buffer<int>> childIndices, ref Buffer<Triangle> triangles)
        {
            for (int i = 0; i < childIndices.Count; ++i)
            {
                ref var source = ref Triangles[childIndices[i]];
                ref var target = ref triangles[i];
                target.A = scale * source.A;
                target.B = scale * source.B;
                target.C = scale * source.C;
            }
        }
        /// <summary>
        /// Type id of mesh shapes.
        /// </summary>
        public const int Id = 6;
        public int TypeId => Id;
    }
}
