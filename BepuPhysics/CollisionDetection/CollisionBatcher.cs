﻿using BepuUtilities.Collections;
using BepuUtilities.Memory;
using BepuPhysics.Collidables;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;

namespace BepuPhysics.CollisionDetection
{

    /// <summary>
    /// Describes the flow control to apply to a convex-convex pair report.
    /// </summary>
    public enum CollisionContinuationType : byte
    {
        /// <summary>
        /// Marks a pair as requiring no further processing before being reported to the user supplied continuations.
        /// </summary>
        Direct,
        /// <summary>
        /// Marks a pair as part of a set of a higher (potentially multi-manifold) pair, potentially requiring contact reduction.
        /// </summary>
        NonconvexReduction,
        //TODO: We don't yet support boundary smoothing for meshes or convexes. Most likely, boundary smoothed convexes won't make it into the first release of the engine at all;
        //they're a pretty experimental feature with limited applications.
        ///// <summary>
        ///// Marks a pair as a part of a set of mesh-convex collisions, potentially requiring mesh boundary smoothing.
        ///// </summary>
        //BoundarySmoothedMesh,
        ///// <summary>
        ///// Marks a pair as a part of a set of convex-convex collisions, potentially requiring general convex boundary smoothing.
        ///// </summary>
        //BoundarySmoothedConvexes,           

    }

    public struct PairContinuation
    {
        public int PairId;
        public int ChildA;
        public int ChildB;
        public uint Packed;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PairContinuation(int pairId, int childA, int childB, CollisionContinuationType continuationType, int continuationIndex)
        {
            PairId = pairId;
            ChildA = childA;
            ChildB = childB;
            Debug.Assert(continuationIndex < (1 << 23));
            Packed = (uint)(((int)continuationType << 24) | continuationIndex);
        }
        public PairContinuation(int pairId)
        {
            PairId = pairId;
            ChildA = 0;
            ChildB = 0;
            Packed = 0;
        }

        public CollisionContinuationType Type { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return (CollisionContinuationType)(Packed >> 24); } }
        public int Index { [MethodImpl(MethodImplOptions.AggressiveInlining)] get { return (int)(Packed & 0x007FFFFF); } }
    }
    public struct TestPair
    {
        /// <summary>
        /// Stores whether the types involved in pair require that the resulting contact manifold be flipped to be consistent with the user-requested pair order.
        /// </summary>
        public int FlipMask;
        public RigidPose PoseA;
        public RigidPose PoseB;
        public float SpeculativeMargin;
        public PairContinuation Continuation;
    }

    //Writes by the narrowphase write shape data without type knowledge, so they can't easily operate on regular packing rules. Emulate this with a pack of 1.
    //This allows the reader to still have a quick way to interpret data rather than casting individual shapes.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TestPair<TShapeA, TShapeB>
            where TShapeA : struct, IShape where TShapeB : struct, IShape
    {
        public TShapeA A;
        public TShapeB B;
        public TestPair Shared;
    }
    public interface ICollisionTestContinuation
    {
        void Create(int slots, BufferPool pool);

        unsafe void OnChildCompleted<TCallbacks>(ref PairContinuation report, ConvexContactManifold* manifold, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks;
        unsafe void OnChildCompletedEmpty<TCallbacks>(ref PairContinuation report, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks;
    }

    public struct BatcherContinuations<T> where T : ICollisionTestContinuation
    {
        public Buffer<T> Continuations;
        public IdPool<Buffer<int>> IdPool;
        const int InitialCapacity = 64;

        public ref T CreateContinuation(int slotsInContinuation, BufferPool pool, out int index)
        {
            if (!Continuations.Allocated)
            {
                Debug.Assert(!IdPool.AvailableIds.Span.Allocated);
                //Lazy initialization.
                pool.Take(InitialCapacity, out Continuations);
                IdPool<Buffer<int>>.Create(pool.SpecializeFor<int>(), InitialCapacity, out IdPool);
            }
            index = IdPool.Take();
            if (index >= Continuations.Length)
            {
                pool.Resize(ref Continuations, index, index - 1);
            }
            ref var continuation = ref Continuations[index];
            continuation.Create(slotsInContinuation, pool);
            return ref Continuations[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void ContributeChildToContinuation<TCallbacks>(ref PairContinuation continuation, ConvexContactManifold* manifold, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks
        {
            Continuations[continuation.Index].OnChildCompleted(ref continuation, manifold, ref batcher);
        }


        internal void Dispose(BufferPool pool)
        {
            if (Continuations.Allocated)
            {
                pool.ReturnUnsafely(Continuations.Id);
                Debug.Assert(IdPool.AvailableIds.Span.Allocated);
                IdPool.Dispose(pool.SpecializeFor<int>());
            }
#if DEBUG
            //Makes it a little easier to catch bad accesses.
            this = new BatcherContinuations<T>();
#endif
        }
    }

    public struct NonconvexReductionChild
    {
        public ConvexContactManifold Manifold;
        /// <summary>
        /// Offset from the origin of the first shape's parent to the child's location in world space. If there is no parent, this is the zero vector.
        /// </summary>
        public Vector3 OffsetA;
        /// <summary>
        /// Offset from the origin of the second shape's parent to the child's location in world space. If there is no parent, this is the zero vector.
        /// </summary>
        public Vector3 OffsetB;
    }

    public struct NonconvexReduction : ICollisionTestContinuation
    {
        public int ChildCount;
        public int CompletedChildCount;
        public Buffer<NonconvexReductionChild> Children;

        public void Create(int childManifoldCount, BufferPool pool)
        {
            ChildCount = childManifoldCount;
            CompletedChildCount = 0;
            pool.Take(childManifoldCount, out Children);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void FlushIfCompleted<TCallbacks>(int pairId, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            ++CompletedChildCount;
            Debug.Assert(ChildCount > 0);
            if (ChildCount == CompletedChildCount)
            {
                //This continuation is ready for processing. Find which contact manifold to report.
                int populatedChildManifolds = 0;
                //We cache an index in case there is only one populated manifold. Order of discovery doesn't matter- this value only gets used when there's one manifold.
                int samplePopulatedChildIndex = 0;
                for (int i = 0; i < ChildCount; ++i)
                {
                    if (Children[i].Manifold.Count > 0)
                    {
                        ++populatedChildManifolds;
                        samplePopulatedChildIndex = i;
                    }
                }
                var sampleChild = (NonconvexReductionChild*)Children.Memory + samplePopulatedChildIndex;
                if (populatedChildManifolds > 1)
                {
                    //There are multiple contributing child manifolds, so just assume that the resulting manifold is going to be nonconvex.
                    NonconvexContactManifold reducedManifold;
                    //We should assume that the stack memory backing the reduced manifold is uninitialized. We rely on the count, so initialize it manually.
                    reducedManifold.Count = 0;
                    for (int i = 0; i < ChildCount; ++i)
                    {
                        ref var child = ref Children[i];
                        ref var contactBase = ref child.Manifold.Contact0;
                        for (int j = 0; j < child.Manifold.Count; ++j)
                        {
                            ref var contact = ref Unsafe.Add(ref contactBase, j);
                            contact.Offset += child.OffsetA;
                            //Mix the convex-generated feature id with the child index.
                            contact.FeatureId ^= i << 8;
                            NonconvexContactManifold.Add(&reducedManifold, ref child.Manifold.Normal, ref contact);
                            if (reducedManifold.Count == 8)
                                break;
                        }
                        if (reducedManifold.Count == 8)
                            break;
                    }
                    //The manifold offsetB is the offset from shapeA origin to shapeB origin.
                    var reducedManifoldPointer = &reducedManifold;
                    reducedManifold.OffsetB = sampleChild->Manifold.OffsetB - sampleChild->OffsetB + sampleChild->OffsetA;
                    batcher.Callbacks.OnPairCompleted(pairId, reducedManifoldPointer);
                }
                else
                {
                    //Two possibilities here: 
                    //1) populatedChildManifolds == 1, and samplePopulatedChildIndex is the index of that sole populated manifold. We can directly report it.
                    //It's useful to directly report the convex child manifold for performance reasons- convex constraints do not require multiple normals and use a faster friction model.
                    //2) populatedChildManifolds == 0, and samplePopulatedChildIndex is 0. Given that we know this continuation is only used when there is at least one manifold expected
                    //and that we can only hit this codepath if all manifolds are empty, reporting manifold 0 is perfectly fine.
                    //The manifold offsetB is the offset from shapeA origin to shapeB origin.
                    sampleChild->Manifold.OffsetB = sampleChild->Manifold.OffsetB - sampleChild->OffsetB + sampleChild->OffsetA;
                    var contacts = &sampleChild->Manifold.Contact0;
                    for (int i = 0; i < sampleChild->Manifold.Count; ++i)
                    {
                        contacts[i].Offset += sampleChild->OffsetA;
                    }
                    batcher.Callbacks.OnPairCompleted(pairId, &sampleChild->Manifold);
                }
                batcher.Pool.ReturnUnsafely(Children.Id);
#if DEBUG
                //This makes it a little easier to detect invalid accesses that occur after disposal.
                this = new NonconvexReduction();
#endif
            }
        }
        public unsafe void OnChildCompleted<TCallbacks>(ref PairContinuation report, ConvexContactManifold* manifold, ref CollisionBatcher<TCallbacks> batcher)
            where TCallbacks : struct, ICollisionCallbacks
        {
            Children[CompletedChildCount].Manifold = *manifold;
            FlushIfCompleted(report.PairId, ref batcher);

        }

        public void OnChildCompletedEmpty<TCallbacks>(ref PairContinuation report, ref CollisionBatcher<TCallbacks> batcher) where TCallbacks : struct, ICollisionCallbacks
        {
            Children[CompletedChildCount] = default;
            FlushIfCompleted(report.PairId, ref batcher);
        }
    }


    public struct CollisionBatcher<TCallbacks> where TCallbacks : struct, ICollisionCallbacks
    {

        public BufferPool Pool;
        public Shapes Shapes;
        CollisionTaskRegistry typeMatrix;
        public TCallbacks Callbacks;

        int minimumBatchIndex, maximumBatchIndex;
        //The streaming batcher contains batches for pending work submitted by the user.
        //This pending work can be top level pairs like sphere versus sphere, but it may also be subtasks of submitted work.
        //Consider two compound bodies colliding. The pair will decompose into a set of potentially many convex subpairs.
        Buffer<UntypedList> batches;
        //These collision tasks can then call upon some of the batcher's fixed function post processing stages.
        //For example, compound collisions generate multiple convex-convex manifolds which need to be reduced and combined into a single nonconvex manifold for 
        //efficiency in constraint solving.
        public BatcherContinuations<NonconvexReduction> NonconvexReductions;

        public unsafe CollisionBatcher(BufferPool pool, Shapes shapes, CollisionTaskRegistry collisionTypeMatrix, TCallbacks callbacks)
        {
            Pool = pool;
            Shapes = shapes;
            Callbacks = callbacks;
            typeMatrix = collisionTypeMatrix;
            pool.Take(collisionTypeMatrix.tasks.Length, out batches);
            //Clearing is required ensure that we know when a batch needs to be created and when a batch needs to be disposed.
            batches.Clear(0, collisionTypeMatrix.tasks.Length);
            NonconvexReductions = new BatcherContinuations<NonconvexReduction>();
            minimumBatchIndex = collisionTypeMatrix.tasks.Length;
            maximumBatchIndex = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void Add(ref CollisionTaskReference reference,
            int shapeSizeA, int shapeSizeB, void* shapeA, void* shapeB, ref RigidPose poseA, ref RigidPose poseB, float speculativeMargin,
            int flipMask, ref PairContinuation pairContinuationInfo)
        {
            ref var batch = ref batches[reference.TaskIndex];
            var pairData = batch.AllocateUnsafely();
            Unsafe.CopyBlockUnaligned(pairData, shapeA, (uint)shapeSizeA);
            Unsafe.CopyBlockUnaligned(pairData += shapeSizeA, shapeB, (uint)shapeSizeB);
            var poses = (TestPair*)(pairData += shapeSizeB);
            poses->FlipMask = flipMask;
            poses->PoseA = poseA;
            poses->PoseB = poseB;
            poses->SpeculativeMargin = speculativeMargin;
            poses->Continuation = pairContinuationInfo;
            if (batch.Count == reference.BatchSize)
            {
                typeMatrix[reference.TaskIndex].ExecuteBatch(ref batch, ref this);
                batch.Count = 0;
                batch.ByteCount = 0;
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Add(
            int shapeTypeA, int shapeTypeB, int shapeSizeA, int shapeSizeB, void* shapeA, void* shapeB, ref RigidPose poseA, ref RigidPose poseB, float speculativeMargin,
            ref PairContinuation pairContinuationInfo)
        {
            ref var reference = ref typeMatrix.GetTaskReference(shapeTypeA, shapeTypeB);
            if (reference.TaskIndex < 0)
            {
                //There is no task for this shape type pair. Immediately respond with an empty manifold.
                var manifold = new ConvexContactManifold();
                Callbacks.OnPairCompleted(pairContinuationInfo.PairId, &manifold);
                return;
            }
            ref var batch = ref batches[reference.TaskIndex];
            var pairSize = shapeSizeA + shapeSizeB + Unsafe.SizeOf<TestPair>();
            if (!batch.Buffer.Allocated)
            {
                batch = new UntypedList(pairSize, reference.BatchSize, Pool);
                if (minimumBatchIndex > reference.TaskIndex)
                    minimumBatchIndex = reference.TaskIndex;
                if (maximumBatchIndex < reference.TaskIndex)
                    maximumBatchIndex = reference.TaskIndex;
            }
            Debug.Assert(batch.Buffer.Allocated && batch.ElementSizeInBytes > 0 && batch.ElementSizeInBytes < 131072, "How'd the batch get corrupted?");
            if (shapeTypeA != reference.ExpectedFirstTypeId)
            {
                //The inputs need to be reordered to guarantee that the collision tasks are handed data in the proper order.
                Add(ref reference, shapeSizeB, shapeSizeA, shapeB, shapeA, ref poseB, ref poseA, speculativeMargin, -1, ref pairContinuationInfo);
            }
            else
            {
                Add(ref reference, shapeSizeA, shapeSizeB, shapeA, shapeB, ref poseA, ref poseB, speculativeMargin, 0, ref pairContinuationInfo);
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Add(
           int shapeTypeA, int shapeTypeB, int shapeSizeA, int shapeSizeB, void* shapeA, void* shapeB, ref RigidPose poseA, ref RigidPose poseB, float speculativeMargin,
           int pairId)
        {
            var pairContinuationInfo = new PairContinuation(pairId);
            Add(shapeTypeA, shapeTypeB, shapeSizeA, shapeSizeB, shapeA, shapeB, ref poseA, ref poseB, speculativeMargin, ref pairContinuationInfo);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Add(TypedIndex shapeIndexA, TypedIndex shapeIndexB, ref RigidPose poseA, ref RigidPose poseB, float speculativeMargin,
            ref PairContinuation pairContinuationInfo)
        {
            var shapeTypeA = shapeIndexA.Type;
            var shapeTypeB = shapeIndexB.Type;
            Shapes[shapeIndexA.Type].GetShapeData(shapeIndexA.Index, out var shapeA, out var shapeSizeA);
            Shapes[shapeIndexB.Type].GetShapeData(shapeIndexB.Index, out var shapeB, out var shapeSizeB);
            Add(shapeTypeA, shapeTypeB, shapeSizeA, shapeSizeB, shapeA, shapeB, ref poseA, ref poseB, speculativeMargin, ref pairContinuationInfo);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Add(TypedIndex shapeIndexA, TypedIndex shapeIndexB, ref RigidPose poseA, ref RigidPose poseB, float speculativeMargin, int pairId)
        {
            var pairContinuationInfo = new PairContinuation(pairId);
            Add(shapeIndexA, shapeIndexB, ref poseA, ref poseB, speculativeMargin, ref pairContinuationInfo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Add<TShapeA, TShapeB>(TShapeA shapeA, TShapeB shapeB, ref RigidPose poseA, ref RigidPose poseB, float speculativeMargin, int pairId)
            where TShapeA : struct, IShape where TShapeB : struct, IShape
        {
            //Note that the shapes are passed by copy to avoid a GC hole. This isn't optimal, but it does allow a single code path, and the underlying function is the one
            //that's actually used by the narrowphase (and which will likely be used for most performance sensitive cases).
            //TODO: You could recover the performance and safety once generic pointers exist. By having pointers in the parameter list, we can require that the user handle GC safety.
            //(We could also have an explicit 'unsafe' overload, but that API complexity doesn't seem worthwhile. My guess is nontrivial uses will all use the underlying function directly.)
            var continuation = new PairContinuation(pairId);
            Add(shapeA.TypeId, shapeB.TypeId, Unsafe.SizeOf<TShapeA>(), Unsafe.SizeOf<TShapeB>(), Unsafe.AsPointer(ref shapeA), Unsafe.AsPointer(ref shapeB),
                ref poseA, ref poseB, speculativeMargin, ref continuation);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush()
        {
            //The collision task registry guarantees that tasks which create work for other tasks always appear sooner in the task array than their child tasks.
            //Since there are no cycles, only one flush pass is required.
            for (int i = minimumBatchIndex; i <= maximumBatchIndex; ++i)
            {
                ref var batch = ref batches[i];
                if (batch.Count > 0)
                {
                    typeMatrix.tasks[i].ExecuteBatch(ref batch, ref this);
                }
                //Dispose of the batch and any associated buffers; since the flush is one pass, we won't be needing this again.
                if (batch.Buffer.Allocated)
                {
                    Pool.Return(ref batch.Buffer);
                }
            }
            var listPool = Pool.SpecializeFor<UntypedList>();
            listPool.Return(ref batches);
            NonconvexReductions.Dispose(Pool);
        }

        public unsafe void ProcessConvexResult(ConvexContactManifold* manifold, ref PairContinuation continuation)
        {
            if (continuation.Type == CollisionContinuationType.Direct)
            {
                //This result concerns a pair which had no higher level owner. Directly report the manifold result.
                Callbacks.OnPairCompleted(continuation.PairId, manifold);
            }
            else
            {
                //This result is associated with another pair and requires additional processing.
                //Before we move to the next stage, notify the submitter that the subpair has completed.
                Callbacks.OnChildPairCompleted(continuation.PairId, continuation.ChildA, continuation.ChildB, manifold);
                switch (continuation.Type)
                {
                    case CollisionContinuationType.NonconvexReduction:
                        {
                            NonconvexReductions.ContributeChildToContinuation(ref continuation, manifold, ref this);
                        }
                        break;
                }

            }
        }
    }
}
