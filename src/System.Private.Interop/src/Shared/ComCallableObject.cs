// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// ----------------------------------------------------------------------------------
// Interop library code
//
// Implementation for CCWs
//
// NOTE:
//   These source code are being published to InternalAPIs and consumed by RH builds
//   Use PublishInteropAPI.bat to keep the InternalAPI copies in sync
// ---------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using System.Runtime;
using System.Diagnostics.Contracts;
using Internal.NativeFormat;

namespace System.Runtime.InteropServices
{
    /// <summary>
    /// Cached (CCW, McgTypeInfo) pair
    /// Used for look up CCW based on type/GUID
    /// </summary>
    internal struct CCWCacheEntry
    {
        internal McgTypeInfo TypeInfo;
        internal IntPtr CCW;

        internal CCWCacheEntry(McgTypeInfo typeInfo, IntPtr pCCW)
        {
            TypeInfo = typeInfo;
            CCW = pCCW;
        }
    }

    [Flags]
    internal enum ComCallableObjectFlags : int
    {
        None = 0,

        /// <summary>
        /// The CCW is pegged and will be considered alive if it also has a non-0 Jupiter ref count
        /// </summary>
        IsPegged = 0x1,

        /// <summary>
        /// This CCW is aggregating another RCW - this happens when you have a managed class deriving from
        /// a WinRT/ComImport type
        /// </summary>
        IsAggregatingRCW = 0x2
    }

    /// <summary>
    /// Per-interface CCW that we pass out to native
    /// These are lazily allocated for now (allocated when QI-ed for)
    /// Native code will treat __interface_ccw* as a COM interface pointer
    /// @TODO: We should optimize this by merging multiple interface ccws
    /// into one, like desktop CLR
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe struct __interface_ccw
    {
        //
        // Important note!!!!
        //
        // This structure mirrors the native type __interface_ccw in Interop.Native.lib.
        // Any updates must be made in both places!
        //
        // Also note that SOS takes a dependency on the layout of __interface_ccw and __native_ccw structures.
        // Please update SOS whenever these data structures are updated. See GetCCWTargetObject() in strike.cpp
        //

        void* m_pVtable;                // Points to v-table
        __native_ccw* m_pNativeCCW;             // Points to a native CCW that manages ref count
        // and all the __interface_ccw belong to this native CCW
        __interface_ccw* m_pNext;                  // Points to the next __interface_ccw
        // This is needed for cleaning up
        McgTypeInfo m_McgTypeInfo;             // Refer to McgInterfaceData so that we can share CcwVtable across multiple interfaces
        // TODO: Define unqiue vtable for shared CCW instances, store McgInterfaceData pointer at negative offset

        /// <summary>
        /// Cache for single memory block, perfect for calls like IDependencyProperty.SetValue (System.Object converts to CCW, passed to native code, then released).
        /// Last block not freed at shut-down. Consider more complicated caching when there are evidence for it
        /// </summary>
        static IntPtr s_cached_interface_ccw;

        /// <summary>
        /// Allocates a per-interface CCW based on the type
        /// </summary>
        internal static __interface_ccw* Allocate(ComCallableObject managedCCW, McgTypeInfo typeInfo)
        {
            __interface_ccw* pCcw = (__interface_ccw*)McgComHelpers.CachedAlloc(sizeof(__interface_ccw), ref s_cached_interface_ccw);

            IntPtr vt = typeInfo.CcwVtable;

            if (vt == default(IntPtr))
            {
                // McgTypeInfo.RawValue points to an individual McgInterfaceData static field which should have a symbol in pdb file

#if ENABLE_WINRT
                throw new MissingInteropDataException(McgTypeHelpers.GetDiagnosticMessageForMissingType(managedCCW.TargetObject.GetType().TypeHandle));
#else
                Environment.FailFast("CCW discarded.");
#endif

            }

            pCcw->m_pVtable = vt.ToPointer();
            pCcw->m_pNativeCCW = managedCCW.NativeCCW;
            pCcw->m_McgTypeInfo = typeInfo;

            managedCCW.NativeCCW->Link(pCcw);

            return pCcw;
        }

        internal static void Destroy(__interface_ccw* pInterfaceCCW)
        {
            McgComHelpers.CachedFree(pInterfaceCCW, ref s_cached_interface_ccw);
        }

        internal __interface_ccw* Next
        {
            get
            {
                return m_pNext;
            }

            set
            {
                m_pNext = value;
            }
        }

        internal McgTypeInfo InterfaceInfo
        {
            get
            {
                return m_McgTypeInfo;
            }
        }

        /// <summary>
        /// Returns the CCW
        /// Will failfast if the CCW is "neutered"
        /// </summary>
        internal ComCallableObject ComCallableObject
        {
            get
            {
                return m_pNativeCCW->ComCallableObject;
            }
        }

        /// <summary>
        /// Returns NativeCCW
        /// This works even if the CCW has been "neutered"
        /// </summary>
        internal __native_ccw* NativeCCW
        {
            /// <remarks>
            /// WARNING: This function might be called under a GC callback. Please read the comments in
            /// GCCallbackAttribute to understand all the implications before you make any changes
            /// </remarks>
            [GCCallback]
            get
            {
                return m_pNativeCCW;
            }
        }

        /// <summary>
        /// Fast method for Release callable from internal managed code, without the cost of [NativeCallable]
        /// </summary>
#if !RHTESTCL
        [MethodImplAttribute(MethodImplOptions.AggressiveInlining)]
#endif
        internal static int DirectRelease(IntPtr __IntPtr__pComThis)
        {
            __interface_ccw* pComThis = (__interface_ccw*)__IntPtr__pComThis;

            //
            // Use ->NativeCCW to support calling AddRef/Release for neutered CCWs
            //
            return pComThis->NativeCCW->ReleaseCOMRef();
        }
    }

    /// <summary>
    /// Native CCW
    /// This contains all the basic states that needs to survive after the managed CCW and its target are
    /// garbage collected. The main scenario for this is Jupiter holding a non-0 Jupiter ref count to a
    /// managed object without pegging.
    /// </summary>
    unsafe struct __native_ccw
    {
        //
        // Important note!!!!
        //
        // This structure mirrors the native type __native_ccw in Interop.Native.lib.
        // Any updates must be made in both places!
        //

        /// <summary>
        /// Combined Ref Count of COM and Jupiter
        /// COM takes lower 32-bit while Jupiter takes higher 32-bit.
        /// It's OK for COM ref count to overflow
        /// Be verycareful when you read this because you can't read this atomicly under x86 directly.
        /// Only use CombinedRefCount property for thread-safety
        /// </summary>
        long m_lRefCount;

        /// <summary>
        /// A ref counted handle that points to CCW
        /// A ref counted handle can be strong or weak depending on the ref count
        /// It has to pointing to CCW because our RefCount handle callback only work for a specific type of
        /// object
        /// </summary>
        GCHandle m_hCCW;

        /// <summary>
        /// The first interface CCW
        /// It is typed as IntPtr to make Interlocked.CompareExchange happy
        /// The real type is __interface_ccw *
        /// </summary>
        IntPtr m_pFirstInterfaceCCW;

        /// <summary>
        /// Flags of CCW
        /// Typed as int to make Interlocked.CompareExchange happy (otherwise you'll get error like
        /// ComCallableObjectFlags must be a reference type to be used in CompareExchange...)
        /// </summary>
        int m_flags;

        /// <summary>
        /// Hashcode of the target object. This is used in CCW map lookup scenarios where we need to save
        /// to save the hashcode in case the target object is dead
        /// </summary>
        int m_hashCode;

        static IntPtr s_cached_native_ccw;

        /// <summary>
        /// Initialize a native CCW
        /// </summary>
        internal static __native_ccw* Allocate(ComCallableObject managedCCW, object targetObject)
        {
            __native_ccw* pNativeCCW = (__native_ccw*)McgComHelpers.CachedAlloc(sizeof(__native_ccw), ref s_cached_native_ccw);

            if (pNativeCCW == null)
                throw new OutOfMemoryException();

            pNativeCCW->Init(managedCCW, targetObject);

            if (InteropEventProvider.IsEnabled())
                InteropEventProvider.Log.TaskCCWCreation((long)pNativeCCW, (long)0, (long)targetObject.GetTypeHandle().GetRawValue().ToInt64());

            return pNativeCCW;
        }

        /// <summary>
        /// Returns handle value for a specific object
        /// </summary>
        IntPtr GetObjectID()
        {
            IntPtr handleVal = default(IntPtr);
            fixed (__native_ccw* pThis = &this)
            {
                handleVal = (IntPtr)pThis;
            }
            return handleVal;
        }

        /// <summary>
        /// Initialize this native CCW
        /// </summary>
        private void Init(ComCallableObject ccw, object targetObject)
        {
            // Start with 1 - we'll release this extra ref count in marshalling code after we QI
            m_lRefCount = 1;

            m_flags = (int)ComCallableObjectFlags.None;

            m_hCCW = GCHandle.FromIntPtr(
                    InteropExtensions.RuntimeHandleAllocRefCounted(ccw)
                );


            Debug.Assert(m_hCCW.IsAllocated); // GCHandle.FromIntPtr throws exception on null handle
            m_pFirstInterfaceCCW = default(IntPtr);

            // Using RuntimeHelpers.GetHashCode ensures that __native_ccw is looked up by identity and not
            // based on semantics defined by a overridden GetHashCode implementation
            m_hashCode = RuntimeHelpers.GetHashCode(targetObject);

            if (ccw.IsAggregatingRCW)
            {
                m_flags |= (int)ComCallableObjectFlags.IsAggregatingRCW;

                //
                // In aggregation, lRefCount is added ref to 1 by default and we'll only release it when the RCW
                // is finalized. This protect us from relying on the exact order of native COM object cleanup
                // and the CCW cleanup as the native COM object might keep a Jupiter reference to the WinRT object
                //
                m_lRefCount++;

                if (InteropEventProvider.IsEnabled())
                    InteropEventProvider.Log.TaskCCWRefCountInc((long)GetObjectID(), m_lRefCount);
            }
        }

        internal void Cleanup()
        {
            fixed (__native_ccw* pThis = &this)
            {
                if (InteropEventProvider.IsEnabled())
                    InteropEventProvider.Log.TaskCCWFinalization((long)GetObjectID(), this.m_lRefCount);

                //
                // Remove from cache
                // This should be done as first step to avoid GetOrCreateCCW walking on a CCW that has been
                // freed
                //
                CCWLookupMap.Unregister(pThis);

                //
                // Walk the linked list of interface CCWs and destroy each one of them
                //
                __interface_ccw* pCurrentInterfaceCCW = (__interface_ccw*)m_pFirstInterfaceCCW.ToPointer();

                while (pCurrentInterfaceCCW != null)
                {
                    __interface_ccw* pNextInterfaceCCW = pCurrentInterfaceCCW->Next;
                    __interface_ccw.Destroy(pCurrentInterfaceCCW);
                    pCurrentInterfaceCCW = pNextInterfaceCCW;
                }

                // This tells whoever that is debugging that the CCW is dead
                m_pFirstInterfaceCCW = new IntPtr(unchecked((int)0xbaadf00d));

                m_hCCW.Free();

                // Delete self
                McgComHelpers.CachedFree(pThis, ref s_cached_native_ccw);
            }
        }

        /// <summary>
        /// Link the interfaceCCW to the interface ccw chain as the new first CCW
        /// </summary>
#if !RHTESTCL
        [MethodImplAttribute(MethodImplOptions.AggressiveInlining)]
#endif
        internal void Link(__interface_ccw* pInterfaceCCW)
        {
            //
            // Loop to handle possible races
            //
            while (true)
            {
                //
                // See if we won the race by replacing the value
                // NOTE: We don't walk this list unless we are cleaning, so no race condition beyond updating
                // m_pFirstInterfaceCCW
                //
                IntPtr pFirstInterfaceCCW_PrevValue = m_pFirstInterfaceCCW;

                if (Interlocked.CompareExchange(
                        ref m_pFirstInterfaceCCW,
                        (IntPtr)pInterfaceCCW,
                        (IntPtr)pFirstInterfaceCCW_PrevValue) == pFirstInterfaceCCW_PrevValue)
                {
                    pInterfaceCCW->Next = (__interface_ccw*)pFirstInterfaceCCW_PrevValue.ToPointer();
                    break;
                }
            }
        }

        /// <summary>
        /// Returns the managed CCW
        /// Will failfast if CCW is neutered
        /// </summary>
        internal ComCallableObject ComCallableObject
        {
            get
            {
                ComCallableObject ccw = InteropExtensions.UncheckedCast<ComCallableObject>(m_hCCW.Target);

                if (ccw == null)
                {
                    AccessNeuteredCCW_FailFast();
                }

                return ccw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void AccessNeuteredCCW_FailFast()
        {
            //
            // Failfast to make sure Jupiter doesn't execute some random never-tested failure code
            // path
            //
            Environment.FailFast(
                "Failfast due to accessing a neutered CCW! " +
                "You can only access a neutered CCW from ICCW or IUnknown::AddRef/Release");
        }

        /// <summary>
        /// Returns managed CCW without checking. Could return null if CCW is neutered
        /// (Neutered state: target object + CCW both garbage collected while native CCW is still alive due
        /// to pending  jupiter ref count)
        /// </summary>
        internal ComCallableObject ComCallableObjectUnsafe
        {
            get
            {
                return InteropExtensions.UncheckedCast<ComCallableObject>(m_hCCW.Target);
            }
        }

        internal GCHandle CCWHandle
        {
            get
            {
                return m_hCCW;
            }
        }

        internal bool IsAggregatingRCW
        {
            get
            {
                return (m_flags & (int)ComCallableObjectFlags.IsAggregatingRCW) != 0;
            }
        }


        /// <summary>
        /// Whether the CCW is pending cleanup
        /// NOTE: We do want aggregated CCWs to be returned even if nobody is referencing them in native code
        /// And in that case their ref count would be 1 and the same == 0 check would suffice
        /// </summary>
        internal bool IsCleanupPending
        {
            get
            {
                return (CombinedRefCount == 0);
            }
        }

        /// <summary>
        /// Whether the variable counted handle should be considered a strong one or a weak handle
        /// If it is a strong handle, it'll keep the managed CCW and the target object alive
        /// </summary>
        internal bool IsAlive()
        {
            long lRefCount = CombinedRefCount;

            if (lRefCount == 0)
                return false;

            //
            // In aggregation, lRefCount is added ref to 1 by default and we'll only release it when the RCW
            // is finalized. This protect us from relying on the exact order of native COM object cleanup
            // and the CCW cleanup as the native COM object might keep a Jupiter reference to the WinRT object
            //
            if (lRefCount == 1 && IsAggregatingRCW)
            {
                return false;
            }

            return IsAlive(lRefCount);
        }

        /// <summary>
        /// Calling IsAlive based on the specified new ref count
        /// </summary>
        private bool IsAlive(long lRefCount)
        {
            if (COMRefCountFrom(lRefCount) > 0)
                return true;

            if (IsPegged || RCWWalker.IsGlobalPeggingOn)
            {
                if (JupiterRefCountFrom(lRefCount) > 0)
                    return true;
            }

            return false;
        }

        #region Ref Count Management

        internal const long COM_REFCOUNT_MASK = 0x00000000FFFFFFFFL;                   // COM -> 32 bits
        internal const long JUPITER_REFCOUNT_MASK = unchecked((long)0xFFFFFFFF00000000L);  // Jupiter -> 32 bits
        internal const int JUPITER_REFCOUNT_SHIFT = 32;
        internal const long JUPITER_REFCOUNT_INC = 0x0000000100000000L;
        internal const long ALL_REFCOUNT_MASK = unchecked((long)0xFFFFFFFFFFFFFFFFL);

        /// <summary>
        /// Thread-safe read operation that reads the 64-bit integer in one atomic operation
        /// </summary>
        private long CombinedRefCount
        {
            get
            {
                return Volatile.Read(ref m_lRefCount);
            }
        }

        /// <summary>
        /// Returns Jupiter ref count from the combined 64-bit ref count
        /// This assumes the value in lRefCount is consistent
        /// </summary>
        private int JupiterRefCountFrom(long lRefCount)
        {
            return unchecked((int)((lRefCount & JUPITER_REFCOUNT_MASK) >> JUPITER_REFCOUNT_SHIFT));
        }

        /// <summary>
        /// Returns COM ref count from the combined 64-bit ref count
        /// This assumes the value in lRefCount is consistent
        /// </summary>
        private int COMRefCountFrom(long lRefCount)
        {
            int comRefCnt = (int)((lRefCount & COM_REFCOUNT_MASK));

            //
            // In aggregation, lRefCount is added ref to 1 by default and we'll only release it when the RCW
            // is finalized. This protect us from relying on the exact order of native COM object cleanup
            // and the CCW cleanup as the native COM object might keep a Jupiter reference to the WinRT object
            //
            if (IsAggregatingRCW)
                comRefCnt--;

            return comRefCnt;
        }

        /// <summary>
        /// Returns the Jupiter ref count
        /// Be very careful when you use both ref count to make a decision as you might run into a race
        /// </summary>
        internal int JupiterRefCount
        {
            get
            {
                return JupiterRefCountFrom(CombinedRefCount);
            }
        }

        /// <summary>
        /// Returns the COM ref count
        /// Be very careful when you use both ref count to make a decision as you might run into a race
        /// </summary>
        internal int COMRefCount
        {
            get
            {
                return COMRefCountFrom(CombinedRefCount);
            }
        }

        internal int AddJupiterRef()
        {
            long lNewRefCount = Interlocked.Add(ref m_lRefCount, JUPITER_REFCOUNT_INC);

            if (InteropEventProvider.IsEnabled())
                InteropEventProvider.Log.TaskCCWRefCountInc((long)GetObjectID(), lNewRefCount);

            return JupiterRefCountFrom(lNewRefCount);
        }

        internal int ReleaseJupiterRef()
        {
            long lNewRefCount = Interlocked.Add(ref m_lRefCount, -1 * JUPITER_REFCOUNT_INC);

            //
            // Only cleanup if both ref count reaches 0
            // This is a thread-safe way to observe both ref count in one big 64-bit integer
            //
            if (lNewRefCount == 0)
            {
                Cleanup();
                return 0;
            }

            if (InteropEventProvider.IsEnabled())
                InteropEventProvider.Log.TaskCCWRefCountInc((long)GetObjectID(), lNewRefCount);

            return JupiterRefCountFrom(lNewRefCount);
        }

        internal int AddCOMRef()
        {
            long lNewRefCount = Interlocked.Increment(ref m_lRefCount);

            if (InteropEventProvider.IsEnabled())
                InteropEventProvider.Log.TaskCCWRefCountInc((long)GetObjectID(), lNewRefCount);

            return COMRefCountFrom(lNewRefCount);
        }

        /// <summary>
        /// We consider ref count == 0 to be a state of pending cleanup and GetOrCreateCCW should never
        /// return such CCW from the map
        /// However, there is a possible race condition that the CCW ref count drops to 0 after the
        /// IsPendingCleanup check. So we are doing our ultimate check by doing an addref here.
        /// If the addref-ed refcount is not 1, then we've successfully done a addref and potentially 'rescued' the
        /// CCW
        /// If the ref count is 1, then we've seen a pending cleanup CCW and we should reset the ref
        /// count and bail out
        /// </summary>
        /// <returns>True if AddRef is done successfully. false means the CCW is pending cleanup and
        /// AddRef is failed</returns>
        internal bool TryAddCOMRefIfNotCleanupPending()
        {
            long lNewRefCount = Interlocked.Increment(ref m_lRefCount);

            if (InteropEventProvider.IsEnabled())
                InteropEventProvider.Log.TaskCCWRefCountInc((long)GetObjectID(), lNewRefCount);

            if (lNewRefCount == 1)
            {
                //
                // We've just resurrected this CCW that is currently being cleaned up by a CCW
                // Fortunately, that thread is still waiting on us to release the lock (we are in
                // GetOrCreateCCW) so no harm is done yet. We should bail out and repair ref count
                //
                lNewRefCount = Interlocked.Decrement(ref m_lRefCount);

                if (InteropEventProvider.IsEnabled())
                    InteropEventProvider.Log.TaskCCWRefCountDec((long)GetObjectID(), lNewRefCount);

                return false;
            }

            return true;
        }

        internal int ReleaseCOMRef()
        {
            long lNewRefCount = Interlocked.Decrement(ref m_lRefCount);

            if (InteropEventProvider.IsEnabled())
                InteropEventProvider.Log.TaskCCWRefCountDec((long)GetObjectID(), lNewRefCount);

            //
            // Only cleanup if both ref count reaches 0
            // This is a thread-safe way to observe both ref count in one big 64-bit integer
            //
            if (lNewRefCount == 0)
            {
                // Perf optimization: when ref count reaches zero, handle is going to be freed, no need to change type
                Cleanup();

                return 0;
            }

            return COMRefCountFrom(lNewRefCount);
        }

        #endregion

        #region Flags

        /// <summary>
        /// Whether the NativeCCW is neutered
        /// (Neutered state: target object + CCW both garbage collected while native CCW is still alive due
        /// to pending jupiter ref count)
        /// </summary>
        internal bool IsNeutered
        {
            get
            {
                return ComCallableObjectUnsafe == null;
            }
        }

        /// <summary>
        /// Whether this CCW is pegged
        /// </summary>
        internal bool IsPegged
        {
            get
            {
                return IsFlagSet(ComCallableObjectFlags.IsPegged);
            }

            set
            {
                SetFlag(ComCallableObjectFlags.IsPegged, value);
            }
        }

        /// <summary>
        /// Thread-safe version of setting flags
        /// </summary>
        private void SetFlag(ComCallableObjectFlags newFlag, bool isSet)
        {
            SpinWait spin = new SpinWait();

            while (true)
            {
                int oldFlags = m_flags;
                int updatedFlags = oldFlags;

                if (isSet)
                    updatedFlags |= (int)newFlag;
                else
                    updatedFlags &= (int)(~newFlag);

                if (Interlocked.CompareExchange(ref m_flags, updatedFlags, oldFlags) == oldFlags)
                    return;

                spin.SpinOnce();
            }
        }

        private bool IsFlagSet(ComCallableObjectFlags flag)
        {
            return ((m_flags & (int)flag) != 0);
        }

        internal int GetFlags()
        {
            return m_flags;
        }

        #endregion

        #region CCW lookup related

        internal int GetHashCodeForLookup()
        {
            return m_hashCode;
        }
        #endregion
    }

    /// <summary>
    /// System.IntPtr does not provide IEquatable<System.IntPtr>. So we need our own wrapper
    /// </summary>
    internal struct EquatableIntPtr : IEquatable<EquatableIntPtr>
    {
        unsafe private void* m_value;

        unsafe internal EquatableIntPtr(void* p)
        {
            m_value = p;
        }

        internal unsafe void* ToPointer()
        {
            return m_value;
        }

        public unsafe bool Equals(EquatableIntPtr other)
        {
            return m_value == other.m_value;
        }
    }

    /// <summary>
    /// Maintains target object -> CCW mapping so that we could go from an object to its existing CCW without
    /// creating a new one
    /// The goal of this map is to maintain the mapping without introducing extra GCHandles, which means it
    /// basically utilize the existing RefCounted Handle from CCWs
    /// New implementation using Internal.HashSet<EquatableIntPtr> searching using RuntimeHelpers.GetHashCode(target) as hash code
    /// </summary>
    internal unsafe class CCWLookupMap
    {
        /// <summary>
        /// Maintains target object -> CCW mapping
        /// </summary>
        static System.Collections.Generic.Internal.HashSet<EquatableIntPtr> s_ccwLookupMap;

        /// <summary>
        /// Lock for s_ccwLookupMap, much faster than locking through object
        /// </summary>
        static Lock s_ccwLookupMapLock;

        static internal void InitializeStatics()
        {
            const int CCWLookupMapDefaultSize = 101;

            s_ccwLookupMap = new System.Collections.Generic.Internal.HashSet<EquatableIntPtr>(CCWLookupMapDefaultSize);

            s_ccwLookupMapLock = new Lock();
        }

        /// <summary>
        /// Returns existing CCW or a new CCW for the target object
        /// The returned CCW has been Addrefed to avoid race condition
        /// </summary>
        /// <remarks>If typeInfo is not empty, add it to newly created CCW under lock for perf</remarks>
        internal static ComCallableObject GetOrCreateCCW(object target, McgTypeInfo typeInfo, out IntPtr interfaceCCW)
        {
            interfaceCCW = default(IntPtr);

            try
            {
                s_ccwLookupMapLock.Acquire();

                ComCallableObject ret = null;

                // Hashcode to search for
                int hashCode = RuntimeHelpers.GetHashCode(target);

                // FindFirstKey/FindNextKey will return keys with matching hash code here
                EquatableIntPtr key = default(EquatableIntPtr);

                // Internal.HashSet's special replacement for TryGetValue, allowing us to operate on key only, also inline comparer
                for (int entry = s_ccwLookupMap.FindFirstKey(ref key, hashCode); entry >= 0; entry = s_ccwLookupMap.FindNextKey(ref key, entry))
                {
                    // Compare CCW to check for matching targets
                    __native_ccw* pNativeCCW = (__native_ccw*)key.ToPointer();

                    ComCallableObject ccw = pNativeCCW->ComCallableObjectUnsafe;

                    //
                    // It is possible that we walked up on a CCW that
                    // 1) has been 'neutered' and its variable handle field points to NULL.
                    // At that point, ComCallableObject and the managed target object are already gone
                    // 2) we might not finished removing it from the cache because the Unregister call
                    // is waiting on this lookup operation
                    //
                    // To avoid us from crashing, we should retrieve the ComCallableObject and see if it is
                    // null. If it is not null, proceed to retrieve its target object which could either be:
                    // 1) NULL and thus never matches your search
                    // 2) NON-NULL and might be a match. NOTE: There is no risk of resurrecting this object.
                    // If it is a match, then it must be alive in order to be the target of the lookup
                    //

                    // Using Object.ReferenceEquals ensures that the targetObject is looked up by identity and not
                    // based on semantics defined by a overridden Equals implementation
                    if ((ccw != null) && Object.ReferenceEquals(target, ccw.TargetObject) && !pNativeCCW->IsCleanupPending)
                    {
                        //
                        // If we successfully add ref this, we'll keep this CCW and we'll never race with a cleanup
                        // If we happen to addref this from 0 to 1, then TryAddCOMRefIfNotCleanupPending will reset it to 0
                        // and we'll skip this CCW
                        //
                        if (!pNativeCCW->TryAddCOMRefIfNotCleanupPending())
                            continue;

                        //
                        // Most likely the managed CCW is still there when we are making comparisons, but it might
                        // be gone right after that and before we call __native_ccw->ComCallableObject
                        // So call the unsafe version to avoid failing fast - we'll create a new CCW later
                        //
                        ret = ccw;

                        break;
                    }
                }

                if (ret == null)
                {
                    ret = new ComCallableObject(target, true /* locked */);

                    // For newly created CCW, add typeInfo to it as first entry without extra locking for perf
                    if (!typeInfo.IsNull)
                    {
                        interfaceCCW = ret.AddFirstTypeInfo_NoAddRef(typeInfo);
                    }
                }

                //
                // Make sure the target object will be alive at this point. See above for details
                //
                GC.KeepAlive(target);

                return ret;
            }
            finally
            {
                s_ccwLookupMapLock.Release();
            }
        }

        /// <summary>
        /// Register the CCW for lookup
        /// </summary>
        internal static void Register(__native_ccw* pNativeCCW)
        {
            //
            // Creates a lookup key that contains the native CCW
            // We set target object to null because we don't want the target object to held alive
            // target object will only be set to non-null if we are doing a lookup
            //
            EquatableIntPtr key = new EquatableIntPtr(pNativeCCW);

            try
            {
                s_ccwLookupMapLock.Acquire();

                bool existing = s_ccwLookupMap.Add(key, pNativeCCW->GetHashCodeForLookup());

                Debug.Assert(!existing);
            }
            finally
            {
                s_ccwLookupMapLock.Release();
            }
        }

        /// <summary>
        /// Register the CCW for lookup under lock
        /// </summary>
#if !RHTESTCL
        [MethodImplAttribute(MethodImplOptions.AggressiveInlining)]
#endif
        internal static void RegisterLocked(__native_ccw* pNativeCCW)
        {
            //
            // Creates a lookup key that contains the native CCW
            // We set target object to null because we don't want the target object to held alive
            // target object will only be set to non-null if we are doing a lookup
            //
            EquatableIntPtr key = new EquatableIntPtr(pNativeCCW);

            bool existing = s_ccwLookupMap.Add(key, pNativeCCW->GetHashCodeForLookup());

            Debug.Assert(!existing);
        }

        internal static void Unregister(__native_ccw* pNativeCCW)
        {
            Debug.Assert(s_ccwLookupMapLock != null);

            EquatableIntPtr key = new EquatableIntPtr(pNativeCCW);

            Lock _lock = s_ccwLookupMapLock;

            try
            {
                _lock.Acquire();

                //
                // Because CCWLookupMap will never resurrect a CCW from ref count 0 to 1
                // We should always successfully remove this CCW from the map
                //
                bool removed = s_ccwLookupMap.Remove(key, pNativeCCW->GetHashCodeForLookup());
                Debug.Assert(removed);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// The actual CCW walk happens here.
        /// </summary>
        /// <remarks>
        /// WARNING: This function will be called under a GC callback. Please read the comments in
        /// GCCallbackAttribute to understand all the implications before you make any changes
        /// </remarks>
        [GCCallback]
        internal static void LogCCWs()
        {
            EquatableIntPtr key = default(EquatableIntPtr);
            int index = -1;

            for (bool hasEntry = s_ccwLookupMap.GetNext(ref key, ref index); hasEntry; hasEntry = s_ccwLookupMap.GetNext(ref key, ref index))
            {
                __native_ccw* pNativeCCW = (__native_ccw*)key.ToPointer();
                ComCallableObject ccw = pNativeCCW->ComCallableObjectUnsafe;

                if (ccw != null)
                {
                    //TODO extract CCW IUnk and pass it instead of null
                    GCEventProvider.TaskLogLiveCCW(GCHandle.ToIntPtr(pNativeCCW->CCWHandle), 
                        InteropExtensions.GetObjectID(ccw.TargetObject), ccw.TargetObject.GetTypeHandle().GetRawValue(), (IntPtr)0,
                        pNativeCCW->COMRefCount, pNativeCCW->JupiterRefCount, pNativeCCW->GetFlags());
                }
            }
        }
    }

#if RHTESTCL
    //====================================================================
    // The enum of the return value of ICustomQueryInterface.GetInterface
    // NOTE: Public to MCG only
    //====================================================================
    public enum CustomQueryInterfaceResult
    {
        Handled = 0,
        NotHandled = 1,
        Failed = 2,
    }

    //====================================================================
    // The interface for customizing IQueryInterface
    // NOTE: Public to MCG only
    //====================================================================
    public interface ICustomQueryInterface
    {
        CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv);
    }
#endif

    /// <summary>
    /// Managed CCW implementation
    ///
    /// The hierachy of CCW look like this:
    ///
    /// __interface_ccw ->
    ///                  |
    /// __interface_ccw -> __native_ccw -> (RefCountedHandle) -> managed CCW -> object
    ///                  |
    /// __interface_ccw ->
    ///
    /// 1. __interface_ccw is the per-interface CCW that acts likea  COM interface pointer
    /// 2. CCW manages the lifetime of all __interface_ccw(s) and points to the target object
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    unsafe internal class ComCallableObject
    {
        #region Private variables

        __native_ccw* m_pNativeCCW;         // Native CCW
        LightweightList<CCWCacheEntry>.WithInlineStorage m_CCWs; // List of cached CCWs

        /// <summary>
        /// Managed object aggregating a native COM/WinRT object
        /// CCW is the outer, and the RCW is the inner
        /// </summary>
        __ComObject m_innerRCW;

        /// <summary>
        /// Points to the target object
        /// </summary>
        object m_target;

        /// <summary>
        /// Points to the per-type CCWTemplateData which contains per-type information such as what is the
        /// runtime class name for this CCW, etc
        /// </summary>
        CCWTemplateInfo m_ccwTemplateInfo;

        #endregion


        #region RefCounted handle support

        static internal void InitRefCountedHandleCallback()
        {
            //
            // Register the callback to ref-counted handles
            // Inside this callback we'll determine whether the ref count handle to the target object
            // should be weak or strong
            //
            InteropExtensions.RuntimeRegisterRefCountedHandleCallback(
                AddrOfIntrinsics.AddrOf<AddrOfIntrinsics.AddrOfIsAlive>(ComCallableObject.IsAlive),
                typeof(ComCallableObject).TypeHandle
                );
        }

        /// <summary>
        /// Whether this CCW is alive
        /// The ref-counted handle to the target object will be set to strong handle or weak handle depending on
        /// the answer here
        /// <summary>
        [GCCallback]
        internal static bool IsAlive(ComCallableObject ccw)
        {
            return ccw.NativeCCW->IsAlive();
        }

        #endregion

        /// <summary>
        /// Per-Type CCW information. See CCWTemplateData for more details
        /// </summary>
        internal CCWTemplateInfo Template
        {
            get
            {
                return m_ccwTemplateInfo;
            }
        }

        /// <summary>
        /// Check whether the COM pointer is a CCW
        /// If it is, return the corresponding CCW
        /// </summary>
        /// <param name="pComItf">Incoming COM pointer to check</param>
        /// <param name="ccw">The returned CCW</param>
        /// <returns>True if the com pointer is a CCW. False otherwise. </returns>
        internal static unsafe bool TryGetCCW(IntPtr pComItf, out ComCallableObject ccw)
        {
            __com_IUnknown* pUnk = (__com_IUnknown*)(void*)pComItf;

            //
            // Check if the v-table slot matches - if it does, it is our __interface_ccw structure
            //
            if (pUnk->pVtable->pfnQueryInterface == ((__vtable_IUnknown*)__vtable_IUnknown.GetCcwvtable_IUnknown())->pfnQueryInterface)
            {
                ccw = ComCallableObject.FromThisPointer(new IntPtr(pUnk));
                return true;
            }
            else
            {
                ccw = null;
                return false;
            }
        }

        /// <summary>
        /// Get CCW from this pointer. You should only use inside a CCW v-table method where this pointer
        /// is guaranteed to be correct
        /// </summary>
        /// <param name="pUnk">The IUnknown pointer</param>
        /// <returns>The corresponding CCW. Will failfast if CCW is neutered</returns>
        /// <remarks>
        /// WARNING1: This function might be called under a GC callback. Please read the comments in
        /// GCCallbackAttribute to understand all the implications before you make any changes
        /// WARNING2: This will *CRASH* if the CCW is neutered.
        /// </remarks>
        [GCCallback]
        internal static ComCallableObject FromThisPointer(IntPtr pUnk)
        {
            return ((__interface_ccw*)pUnk)->ComCallableObject;
        }

        [MethodImplAttribute(MethodImplOptions.NoInlining)]
        internal static object GetTarget(IntPtr pUnk)
        {
            ComCallableObject obj = ((__interface_ccw*)pUnk)->ComCallableObject;

            return obj.TargetObject;
        }

        /// <summary>
        /// Replacement for GetTarget for shared CCW implementation
        ///   1) Get target object
        ///   3) Return marshal index
        ///   4) Return thunk function, stored in CcwVtable field
        /// </summary>
        [MethodImplAttribute(MethodImplOptions.NoInlining)]
        internal static object GetThunk(IntPtr pUnk, out int marshalIndex, out McgModule module,out IntPtr thunk)
        {
            McgTypeInfo typeInfo = ((__interface_ccw*)pUnk)->InterfaceInfo;

            marshalIndex = typeInfo.MarshalIndex;

            module = typeInfo.ContainingModule;

            ComCallableObject obj = ((__interface_ccw*)pUnk)->ComCallableObject;

            object target = obj.TargetObject;

            // Casting will be performed when calling methods on interface from thunk function
            Debug.Assert((typeInfo.Flags & McgInterfaceFlags.useSharedCCW) != 0);

            thunk = typeInfo.InterfaceData.CcwVtable;

            return target;
        }

        internal ComCallableObject(object target)
        {
            Init(target, null, false);
        }

        internal ComCallableObject(object target, bool locked)
        {
            Debug.Assert(locked);

            Init(target, null, locked);
        }

        internal void Init(object target, __ComObject innerRCW, bool locked)
        {
            if (innerRCW != null)
            {
                Debug.Assert(!innerRCW.ExtendsComObject);

                m_innerRCW = innerRCW;
                m_innerRCW.ExtendsComObject = true;

                //
                // This forms a cycle between the CCW and the actual managed object (RCW)
                // which keeps their lifetime tied together and is exactly what we want
                //
                m_innerRCW.Outer = this;
            }

            m_CCWs = new LightweightList<CCWCacheEntry>.WithInlineStorage();
            m_pNativeCCW = __native_ccw.Allocate(this, target);
            m_target = target;

            if (locked)
                CCWLookupMap.RegisterLocked(m_pNativeCCW);
            else
                CCWLookupMap.Register(m_pNativeCCW);

            m_ccwTemplateInfo = McgModuleManager.GetCCWTemplateInfo(m_target.GetTypeHandle());
        }

        /// <summary>
        /// Returns the native CCW that manages all the interface CCWs
        /// </summary>
        internal __native_ccw* NativeCCW
        {
            get
            {
                return m_pNativeCCW;
            }
        }

        /// <summary>
        /// Increase Jupiter ref count which will make ref count handle a strong handle if pegged & Jupiter
        /// ref count > 0
        /// </summary>
        internal int AddJupiterRef()
        {
            return m_pNativeCCW->AddJupiterRef();
        }

        /// <summary>
        /// Decrease Jupiter ref count
        /// </summary>
        internal int ReleaseJupiterRef()
        {
            return m_pNativeCCW->ReleaseJupiterRef();
        }

        /// <summary>
        /// Peg this CCW - it'll make ref counted handle a strong handle if jupiter ref count > 0
        /// NOTE: It is not a count, but rather a boolean state
        /// </summary>
        internal void Peg()
        {
            m_pNativeCCW->IsPegged = true;
        }

        /// <summary>
        /// Unpeg this CCW
        /// NOTE: It is not a count, but rather a boolean state
        /// </summary>
        internal void Unpeg()
        {
            m_pNativeCCW->IsPegged = false;
        }

        /// <summary>
        /// Constructor that creates an CCW that aggregates a RCW
        /// CCW is the outer, and RCW is the inner
        /// This usually happens when you have a managed object deriving from a native object
        /// </summary>
        internal ComCallableObject(object target, __ComObject innerRCW)
        {
            Init(target, innerRCW, false);
        }

        /// <summary>
        /// Returns target managed object that this CCW is holding onto
        /// </summary
        /// <remarks>
        /// WARNING: This function might be called under a GC callback. Please read the comments in
        /// GCCallbackAttribute to understand all the implications before you make any changes
        /// </remarks>
        [GCCallback]
        internal object TargetObject
        {
            get
            {
                return m_target;
            }
        }

        internal IntPtr GetComInterfaceForIID(ref Guid iid)
        {
            return GetComInterfaceForIID(ref iid, McgTypeInfo.Null);
        }

        internal IntPtr GetComInterfaceForIID(ref Guid iid, McgTypeInfo typeInfo)
        {
            IntPtr pRet = GetComInterfaceForIIDInternal(ref iid, typeInfo);

            // The interface for a specific interface ID is not available. In this case, the returned interface is null.
            // E_NOINTERFACE is fired in the event.
            if ((pRet == default(IntPtr)) && (InteropEventProvider.IsEnabled()))
                InteropEventProvider.Log.TaskCCWQueryInterfaceFailure((long)this.NativeCCW, iid);

            return pRet;
        }

        private static bool SupportsWellKnownInterface(McgTypeInfo typeInfo, object targetObject)
        {
            bool isInternalModule = typeInfo.IsInternalModule;

            if (isInternalModule)
            {
                switch ((InternalModule.Indexes)typeInfo.MarshalIndex)
                {
                    // interfaces supported by every CCW
                    case InternalModule.Indexes.IUnknown:
#if ENABLE_WINRT
                    case InternalModule.Indexes.IInspectable:
                    case InternalModule.Indexes.IWeakReferenceSource:
                    case InternalModule.Indexes.ICustomPropertyProvider:
                    case InternalModule.Indexes.IStringable:
                        // Implemented by all managed objects
                        return true;

                    case InternalModule.Indexes.IWeakReference:
                        return targetObject is WeakReferenceSource;

                    case InternalModule.Indexes.ICCW:
#if X86 && RHTESTCL
                        //
                        // Make sure we don't tap into ICCW in rhtestcl x86. See ICCW implementation in RCWWalker for
                        // more details
                        //
                        return false;
#else
                        return true;
#endif // X86 && RHTESTCL

                    case InternalModule.Indexes.IJupiterObject:
                        // No CCW implements IJupiterObject
                        return false;

                    case InternalModule.Indexes.IRestrictedErrorInfo:
                        // No CCW implements IRestrictedErrorInfo
                        return false;

                    case InternalModule.Indexes.IActivationFactoryInternal:
                        return targetObject is IActivationFactoryInternal;

                    case InternalModule.Indexes.IManagedActivationFactory:
                        return targetObject is IManagedActivationFactory;
#endif

                    case InternalModule.Indexes.IMarshal:
                        // Every CCW implements IMarshal
                        return true;

                    default:
                        Debug.Assert(false, "Unrecgonized InternalModule.Index");
                        break;
                }
            }

            return false;
        }

        private IntPtr GetComInterfaceForIIDInternal(ref Guid iid, McgTypeInfo typeInfo)
        {
#pragma warning disable 618
            object targetObject = TargetObject;

            if (InteropExtensions.GuidEquals(ref iid, ref Interop.COM.IID_IUnknown))
            {
                typeInfo = McgModuleManager.IUnknown;
                return GetComInterfaceForTypeInfo_NoCheck(typeInfo);
            }

            //
            // Simplified implementation of ICustomQueryInterface
            // It is needed internally to customize CCW behavior
            //
            ICustomQueryInterface customQueryInterfaceImpl = targetObject as ICustomQueryInterface;

            if (customQueryInterfaceImpl != null)
            {
                IntPtr pRet;
                CustomQueryInterfaceResult result = customQueryInterfaceImpl.GetInterface(ref iid, out pRet);
                if (result == CustomQueryInterfaceResult.Failed)
                    return default(IntPtr);
                else if (result == CustomQueryInterfaceResult.Handled)
                    return pRet;
                else
                    Debug.Assert(result == CustomQueryInterfaceResult.NotHandled);
            }
#pragma warning restore 618
            //
            // Check IAgileObject - all CCWs are agile by default
            //
            if (InteropExtensions.GuidEquals(ref iid, ref Interop.COM.IID_IAgileObject))
            {
                // Return the IUnknown pointer for IAgileObject - IAgileObject has no other methods anyway
                return GetComInterfaceForTypeInfo_NoCheck(McgModuleManager.IUnknown);
            }

            CCWTemplateInfo ccwTemplateData = this.Template;

            //
            // Determine whether this interface is supported by this CCW
            // Variance is supported by the template itself which includes all possible variant interfaces
            //
            if (!ccwTemplateData.IsNull)
            {
                InterfaceCheckResult checkResult =
                    ccwTemplateData.ContainingModule.SupportsInterface(ccwTemplateData.Index, ref iid, ref typeInfo);

                if (checkResult == InterfaceCheckResult.Supported)
                {
                    return GetComInterfaceForTypeInfo_NoCheck(typeInfo);
                }
                else if (checkResult == InterfaceCheckResult.Rejected)
                {
                    //
                    // This interface is defined in a WinRT class and as a result we've rejected this
                    // interface. This happens because client QI for a interface from the WinRT base
                    // class, and we can simply let the base handle that request.
                    //
                }
            }

            //
            // Look it up in internal module (where all the well known interfaces are)
            // NOTE: This needs to happen after we check the templates because we prefer user implementation
            // over to our own implementation
            //
            if (typeInfo.IsNull)
                typeInfo = McgModuleManager.s_internalModule.GetTypeInfo(ref iid);

            if (!typeInfo.IsNull && SupportsWellKnownInterface(typeInfo, targetObject))
                return GetComInterfaceForTypeInfo_NoCheck(typeInfo);

            //
            // If there isn't a McgTypeInfo for the iid and we are aggregating then QI on the inner
            // since it may support the iid even if we haven't seen it before
            //
            //
            // In phone, InfinitePivot is trying to QI on the outer for Windows.UI.Xaml.Phone.Pivot
            // during their factory creation, and we are not yet ready for that because the inner RCW
            // is not setup yet. We should reject that QI request.
            //
            if (m_innerRCW != null && m_innerRCW.BaseIUnknown_UnsafeNoAddRef != default(IntPtr))
            {
                return McgMarshal.ComQueryInterfaceNoThrow(m_innerRCW.BaseIUnknown_UnsafeNoAddRef, ref iid);
            }

            //
            // Do a global McgTypeInfo lookup if everything else failed and we don't have a CCWTemplate
            // We should never do a global McgTypeInfo lookup if we have a CCWTemplate - that would
            // defeat the whole purpose of CCWTemplate
            // NOTE: This is not correct in the case of aggregation, because the 'is' cast might
            // should be made on the derived type itself, not the base, but there is no way to do that yet.
            // However, because we would only miss CCWTemplate for MCG generated wrappers and there won't
            // be any aggregation for them.
            //
            if (ccwTemplateData.IsNull)
            {
                //
                // Fallback to old slow code path for types we don't have template for
                // This does a global lookup across all modules so eventually we should try to get rid of
                // this completely.
                // I'm keeping this code path for now because there is no guarantee we'll have CCWTemplate
                // for everything we need yet (such as IKeyValuePair adapters in MCG)
                //
                if (typeInfo.IsNull)
                {
                    //
                    // Enumerate all McgTypeInfo with specified iid and check whether Object support any of these McgTypeInfo
                    // The reason for doing this is that the ccwTemplateData is null and thus we dont know which type does this Object support
                    // in future, we should try to fix ccwTemplateData is null case.
                    foreach (McgTypeInfo info in McgModuleManager.GetTypeInfosFromGuid(ref iid))
                    {
                        //
                        // This happens when there is a user managed object instance that doesn't implement any interop
                        // interface, while somebody QIs for a known interface that was imported but was reduced
                        // because we decided we don't need generate reverse interop methods for this interface (when
                        // no managed object that was constructed implementing this interface).
                        // In the future, once we generate CCW templates for all MCG generated wrappers, we don't have
                        // to handle this scenario anymore, and we can safely get rid of this slow path and the ugly
                        // 'is this interface reduced' check.
                        //
                        if (!info.IsNull && info.ItfType.Equals(McgModule.s_DependencyReductionTypeRemovedTypeHandle))
                            continue;

                        if (!info.IsNull && info.IsSupportedBy(targetObject))
                            return GetComInterfaceForTypeInfo_NoCheck(info);
                    }
                }
                else
                {
                    //
                    // This happens when there is a user managed object instance that doesn't implement any interop
                    // interface, while somebody QIs for a known interface that was imported but was reduced
                    // because we decided we don't need generate reverse interop methods for this interface (when
                    // no managed object that was constructed implementing this interface).
                    // In the future, once we generate CCW templates for all MCG generated wrappers, we don't have
                    // to handle this scenario anymore, and we can safely get rid of this slow path and the ugly
                    // 'is this interface reduced' check.
                    //
                    if (!typeInfo.IsNull && typeInfo.ItfType.Equals(McgModule.s_DependencyReductionTypeRemovedTypeHandle))
                        return default(IntPtr);

                    if (!typeInfo.IsNull && typeInfo.IsSupportedBy(targetObject))
                        return GetComInterfaceForTypeInfo_NoCheck(typeInfo);
                }
            }

            //
            // We are not aggregating and don't have a McgTypeInfo for the iid so we must return null
            //
            return default(IntPtr);
        }

        internal IntPtr GetComInterfaceForTypeInfo_NoCheck_NoAddRef(McgTypeInfo typeInfo)
        {
            //
            // Look up our existing list of CCWs and see if we can find a match
            //
            foreach (CCWCacheEntry entry in m_CCWs)
            {
                if (typeInfo == entry.TypeInfo)
                {
                    return entry.CCW;
                }
            }

            //
            // Target object supports this interface
            // Let's create a new CCW for it
            //
            IntPtr pNewCCW = new IntPtr(__interface_ccw.Allocate(this, typeInfo));

            m_CCWs.Add(new CCWCacheEntry(typeInfo, pNewCCW));

            return pNewCCW;
        }

        /// <summary>
        /// Create first CCW to support an interface
        /// </summary>
        internal IntPtr AddFirstTypeInfo_NoAddRef(McgTypeInfo typeInfo)
        {
            // Target object supports this interface
            // Let's create a new CCW for it
            //
            IntPtr pNewCCW = new IntPtr(__interface_ccw.Allocate(this, typeInfo));

            m_CCWs.AddFirst(new CCWCacheEntry(typeInfo, pNewCCW));

            return pNewCCW;
        }

        internal IntPtr GetComInterfaceForTypeInfo_NoCheck(McgTypeInfo typeInfo)
        {
            IntPtr result = GetComInterfaceForTypeInfo_NoCheck_NoAddRef(typeInfo);

            if (result != default(IntPtr))
            {
                this.AddRef();
            }

            return result;
        }

        internal int AddRef()
        {
            return m_pNativeCCW->AddCOMRef();
        }

        internal int Release()
        {
            return m_pNativeCCW->ReleaseCOMRef();
        }

        #region Aggregation Support

        internal bool IsAggregatingRCW
        {
            get
            {
                return m_innerRCW != null;
            }
        }
        #endregion
    }
}
