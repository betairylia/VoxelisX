using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Voxelis
{
    /// <summary>
    /// UnsafeHashMap wrapper with an explicit read-only mode for job-facing copies.
    /// Defaults to writable. Use AsReadOnly before passing a copy to Burst jobs that must not mutate it.
    /// </summary>
    public unsafe struct LockableUnsafeHashMap<TKey, TValue> : IDisposable
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        private UnsafeHashMap<TKey, TValue> map;
        private byte readOnly;

        public LockableUnsafeHashMap(int initialCapacity, AllocatorManager.AllocatorHandle allocator)
        {
            map = new UnsafeHashMap<TKey, TValue>(initialCapacity, allocator);
            readOnly = 0;
        }

        public bool IsCreated => map.IsCreated;
        public bool IsLocked => readOnly != 0;
        public int Count => map.Count;

        public TValue this[TKey key]
        {
            get => map[key];
            set
            {
                ThrowIfLocked();
                map[key] = value;
            }
        }

        public void Lock()
        {
            readOnly = 1;
        }

        public void Unlock()
        {
            readOnly = 0;
        }

        public LockableUnsafeHashMap<TKey, TValue> AsReadOnly()
        {
            var copy = this;
            copy.Lock();
            return copy;
        }

        public void Add(TKey key, TValue value)
        {
            ThrowIfLocked();
            map.Add(key, value);
        }

        public bool TryAdd(TKey key, TValue value)
        {
            ThrowIfLocked();
            return map.TryAdd(key, value);
        }

        public bool Remove(TKey key)
        {
            ThrowIfLocked();
            return map.Remove(key);
        }

        public void Clear()
        {
            ThrowIfLocked();
            map.Clear();
        }

        public bool ContainsKey(TKey key)
        {
            return map.ContainsKey(key);
        }

        public bool TryGetValue(TKey key, out TValue item)
        {
            return map.TryGetValue(key, out item);
        }

        public NativeArray<TKey> GetKeyArray(AllocatorManager.AllocatorHandle allocator)
        {
            return map.GetKeyArray(allocator);
        }

        public NativeHashMap<TKey, TValue> ToNativeHashMap(AllocatorManager.AllocatorHandle allocator)
        {
            var nativeMap = new NativeHashMap<TKey, TValue>(Count > 0 ? Count : 1, allocator);
            foreach (var kvp in map)
            {
                nativeMap.Add(kvp.Key, kvp.Value);
            }

            return nativeMap;
        }

        public UnsafeHashMap<TKey, TValue>.Enumerator GetEnumerator()
        {
            return map.GetEnumerator();
        }

        public void Dispose()
        {
            ThrowIfLocked();

            if (map.IsCreated)
            {
                map.Dispose();
            }

            map = default;
            readOnly = 0;
        }

        private void ThrowIfLocked()
        {
            if (readOnly != 0)
            {
                throw new InvalidOperationException("Cannot write to a locked LockableUnsafeHashMap.");
            }
        }
    }
}
