using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SteamMP
{
    /// <summary>
    /// GC (Garbage Collector) Optimizasyon Yardımcıları
    ///
    /// Unity 5 / .NET 3.5'te GC basit bir mark-and-sweep'tir ve fragmentation'a meyillidir.
    /// Bu sınıf allocation'ları minimize etmek için yardımcı metotlar sağlar.
    /// </summary>
    public static class GCOptimizer
    {
        // --- StringBuilder Pool ---
        static readonly Stack<StringBuilder> sbPool = new Stack<StringBuilder>(16);
        static readonly object sbGate = new object();

        /// <summary>
        /// Pool'dan StringBuilder alır. Kullanım bitince ReleaseStringBuilder ile iade edilmeli.
        /// </summary>
        public static StringBuilder GetStringBuilder(int capacity = 256)
        {
            lock (sbGate)
            {
                if (sbPool.Count > 0)
                {
                    var sb = sbPool.Pop();
                    sb.Length = 0;
                    if (sb.Capacity < capacity) sb.Capacity = capacity;
                    return sb;
                }
            }
            return new StringBuilder(capacity);
        }

        /// <summary>
        /// StringBuilder'ı pool'a iade eder.
        /// </summary>
        public static void ReleaseStringBuilder(StringBuilder sb)
        {
            if (sb == null) return;
            lock (sbGate)
            {
                if (sbPool.Count < 32) // Max pool size
                {
                    sb.Length = 0;
                    sbPool.Push(sb);
                }
            }
        }

        /// <summary>
        /// Formatlı string oluşturur (pool kullanarak allocation yapmaz).
        /// Kullanım: using (var sb = GCOptimizer.Format("Player {0} joined", name)) { ... }
        /// </summary>
        public static StringBuilder Format(string format, params object[] args)
        {
            var sb = GetStringBuilder(256);
            sb.AppendFormat(format, args);
            return sb;
        }

        /// <summary>
        /// Concat string'leri allocation'sız birleştirir.
        /// </summary>
        public static StringBuilder Concat(params string[] parts)
        {
            var sb = GetStringBuilder(256);
            foreach (var p in parts) if (p != null) sb.Append(p);
            return sb;
        }

        // --- List/Array Pool ---

        /// <summary>
        /// Generic List pool'u.
        /// </summary>
        public static class ListPool<T>
        {
            static readonly Stack<List<T>> pool = new Stack<List<T>>(16);
            static readonly object gate = new object();

            public static List<T> Get(int capacity = 0)
            {
                lock (gate)
                {
                    if (pool.Count > 0)
                    {
                        var list = pool.Pop();
                        list.Clear();
                        if (list.Capacity < capacity) list.Capacity = capacity;
                        return list;
                    }
                }
                return capacity > 0 ? new List<T>(capacity) : new List<T>();
            }

            public static void Release(List<T> list)
            {
                if (list == null) return;
                lock (gate)
                {
                    if (pool.Count < 32) pool.Push(list);
                }
            }
        }

        /// <summary>
        /// Array pool'u (primitif tipler için).
        /// </summary>
        public static class ArrayPool<T>
        {
            static readonly Stack<T[]> pool = new Stack<T[]>(8);
            static readonly object gate = new object();

            public static T[] Get(int length)
            {
                lock (gate)
                {
                    if (pool.Count > 0)
                    {
                        var arr = pool.Pop();
                        if (arr.Length >= length) return arr;
                    }
                }
                return new T[length];
            }

            public static void Release(T[] arr)
            {
                if (arr == null) return;
                lock (gate)
                {
                    if (pool.Count < 16) pool.Push(arr);
                }
            }
        }

        // --- Dictionary Cache ---

        /// <summary>
        /// Dictionary'ye GetOrAdd pattern'i ile allocation'sız erişim.
        /// </summary>
        public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, Func<TValue> factory)
        {
            TValue value;
            if (!dict.TryGetValue(key, out value))
            {
                value = factory();
                dict[key] = value;
            }
            return value;
        }

        // --- Struct-based Alternatifler (Boxing önleme) ---

        /// <summary>
        /// Boxing yapmadan enum flag kontrolü.
        /// </summary>
        public static bool HasFlagFast<T>(this T value, T flag) where T : struct, IConvertible
        {
            return (value.ToInt32(null) & flag.ToInt32(null)) != 0;
        }

        // --- GC Pressure Monitoring ---

        static long lastGCMemory;
        static int gen0Collections, gen1Collections, gen2Collections;

        /// <summary>
        /// GC istatistiklerini günceller ve loglar.
        /// </summary>
        public static void UpdateGCStats()
        {
            long current = GC.GetTotalMemory(false);
            long diff = current - lastGCMemory;
            lastGCMemory = current;

            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);

            if (g0 != gen0Collections || g1 != gen1Collections || g2 != gen2Collections)
            {
                if (Telemetry.Verbose)
                {
                    SmpLog.Debug("gc", "stats", string.Format(
                        "GC: Gen0={0} Gen1={1} Gen2={2} | Mem={3:F2}MB (Δ{4:+0;-0}F2MB)",
                        g0 - gen0Collections, g1 - gen1Collections, g2 - gen2Collections,
                        current / 1024f / 1024f, diff / 1024f / 1024f));
                }
                gen0Collections = g0;
                gen1Collections = g1;
                gen2Collections = g2;
            }
        }

        /// <summary>
        /// Zorla GC tetikler (sahne geçişlerinde vb.).
        /// </summary>
        public static void ForceGC()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            UpdateGCStats();
        }
    }

    /// <summary>
    /// Allocation'sız string karşılaştırma yardımcıları.
    /// </summary>
    public static class StringUtils
    {
        /// <summary>
        /// String.StartsWith allocation'sız versiyon.
        /// </summary>
        public static bool StartsWithFast(this string str, string prefix)
        {
            if (str == null || prefix == null) return false;
            if (prefix.Length > str.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (str[i] != prefix[i]) return false;
            return true;
        }

        /// <summary>
        /// String.EndsWith allocation'sız versiyon.
        /// </summary>
        public static bool EndsWithFast(this string str, string suffix)
        {
            if (str == null || suffix == null) return false;
            if (suffix.Length > str.Length) return false;
            int offset = str.Length - suffix.Length;
            for (int i = 0; i < suffix.Length; i++)
                if (str[offset + i] != suffix[i]) return false;
            return true;
        }

        /// <summary>
        /// String.Contains allocation'sız versiyon (basit).
        /// </summary>
        public static bool ContainsFast(this string str, string substr)
        {
            if (str == null || substr == null) return false;
            if (substr.Length == 0) return true;
            if (substr.Length > str.Length) return false;

            for (int i = 0; i <= str.Length - substr.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < substr.Length; j++)
                {
                    if (str[i + j] != substr[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }

        /// <summary>
        /// Path birleştirme (Path.Combine allocation yapar, bunu minimize eder).
        /// </summary>
        public static string CombinePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            
            bool aEnds = a.EndsWith("/") || a.EndsWith("\\");
            bool bStarts = b.StartsWith("/") || b.StartsWith("\\");
            
            if (aEnds && bStarts) return a + b.Substring(1);
            if (!aEnds && !bStarts) return a + "/" + b;
            return a + b;
        }
    }

    /// <summary>
    /// Allocation'sız Vector/Quaternion işlemleri.
    /// </summary>
    public static class MathUtils
    {
        // Stack-allocated struct'lar için yardımcı
        public static void Set(ref Vector3 target, float x, float y, float z)
        {
            target.x = x; target.y = y; target.z = z;
        }

        public static void Set(ref Quaternion target, float x, float y, float z, float w)
        {
            target.x = x; target.y = y; target.z = z; target.w = w;
        }

        public static float SqrDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

        public static bool ApproxEqual(Vector3 a, Vector3 b, float epsilon = 0.0001f)
        {
            return SqrDistance(a, b) < epsilon * epsilon;
        }

        public static bool ApproxEqual(Quaternion a, Quaternion b, float epsilon = 0.0001f)
        {
            return Math.Abs(a.x - b.x) < epsilon &&
                   Math.Abs(a.y - b.y) < epsilon &&
                   Math.Abs(a.z - b.z) < epsilon &&
                   Math.Abs(a.w - b.w) < epsilon;
        }
    }
}