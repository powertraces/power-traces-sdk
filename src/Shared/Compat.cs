// Shims for runtime APIs newer than the oldest target framework a package builds for.
//
// Linked into every project under src/ (see src/Directory.Build.props) and compiled as internal
// types, so each assembly carries its own copy and nothing leaks into the public API. Each block
// only exists on targets where the real API is missing, so on modern targets the code above calls
// the real framework methods with no change in behaviour or performance.
//
// The C# 14 `extension` blocks add *static* members to framework types (Random.Shared,
// Convert.ToHexStringLower, ...), so the call sites are written exactly as they would be on
// .NET 9 and need no #if of their own.

#if !NET9_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

#if !NET6_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
#endif

namespace System.Threading
{
#if !NET9_0_OR_GREATER
    /// <summary>Stand-in for .NET 9's <c>System.Threading.Lock</c>. The C# compiler turns
    /// <c>lock (x)</c> on a value of this type into <c>using (x.EnterScope())</c>, which here is a
    /// plain <see cref="Monitor"/> enter/exit: the same semantics as locking on an object.</summary>
    internal sealed class Lock
    {
        // Monitor works on any object; a private one keeps outside code from locking on it too.
        private readonly object _monitor = new();

        public Scope EnterScope()
        {
            Monitor.Enter(_monitor);
            return new Scope(_monitor);
        }

        public ref struct Scope
        {
            private object? _monitor;

            internal Scope(object monitor) => _monitor = monitor;

            public void Dispose()
            {
                var monitor = _monitor;
                if (monitor is not null)
                {
                    _monitor = null;
                    Monitor.Exit(monitor);
                }
            }
        }
    }
#endif
}

namespace PowerTraces.Internal.Compat
{
    internal static class FrameworkShims
    {
#if !NET9_0_OR_GREATER
        extension(Convert)
        {
            /// <summary>.NET 9's <c>Convert.ToHexStringLower</c>.</summary>
            public static string ToHexStringLower(byte[] bytes)
            {
                var chars = new char[bytes.Length * 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    var b = bytes[i];
                    chars[i * 2] = HexLower(b >> 4);
                    chars[(i * 2) + 1] = HexLower(b & 0xF);
                }

                return new string(chars);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static char HexLower(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
#endif

#if !NET7_0_OR_GREATER
        extension(char)
        {
            /// <summary>.NET 7's <c>char.IsAsciiHexDigit</c>.</summary>
            public static bool IsAsciiHexDigit(char c) =>
                c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
        }
#endif

#if !NET5_0_OR_GREATER
        extension(Convert)
        {
            /// <summary>.NET 5's <c>Convert.FromHexString</c>.</summary>
            public static byte[] FromHexString(string s)
            {
                if (s is null)
                {
                    throw new ArgumentNullException(nameof(s));
                }

                if (s.Length % 2 != 0)
                {
                    throw new FormatException("The input is not a valid hex string as its length is not a multiple of 2.");
                }

                var bytes = new byte[s.Length / 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)((HexValue(s[i * 2]) << 4) | HexValue(s[(i * 2) + 1]));
                }

                return bytes;
            }
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new FormatException("The input is not a valid hex string as it contains a non-hex character."),
        };
#endif

#if !NET6_0_OR_GREATER
        extension(Random)
        {
            /// <summary>.NET 6's <c>Random.Shared</c>: one thread-safe instance per thread.</summary>
            public static Random Shared => SharedRandom.Instance;
        }

        extension(ArgumentNullException)
        {
            /// <summary>.NET 6's <c>ArgumentNullException.ThrowIfNull</c>.</summary>
            public static void ThrowIfNull(
                [NotNull] object? argument,
                [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                if (argument is null)
                {
                    throw new ArgumentNullException(paramName);
                }
            }
        }

        private static class SharedRandom
        {
            [ThreadStatic]
            private static Random? _instance;

            public static Random Instance => _instance ??= new Random(Guid.NewGuid().GetHashCode());
        }
#endif
    }

#if !NET6_0_OR_GREATER
    // Each hash type gets its own class: both HashData methods compile to the same static signature.
    internal static class Md5Shims
    {
        extension(MD5)
        {
            /// <summary>.NET 5's <c>MD5.HashData</c>.</summary>
            public static byte[] HashData(byte[] source)
            {
                using var md5 = MD5.Create();
                return md5.ComputeHash(source);
            }
        }
    }

    internal static class Sha256Shims
    {
        extension(SHA256)
        {
            /// <summary>.NET 5's <c>SHA256.HashData</c>.</summary>
            public static byte[] HashData(byte[] source)
            {
                using var sha = SHA256.Create();
                return sha.ComputeHash(source);
            }
        }
    }
#endif
}

namespace System.Linq
{
#if !NET6_0_OR_GREATER
    internal static class EnumerableShims
    {
        /// <summary>.NET 6's <c>Enumerable.Chunk</c>.</summary>
        public static IEnumerable<TSource[]> Chunk<TSource>(this IEnumerable<TSource> source, int size)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (size < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            return Iterate(source, size);

            static IEnumerable<TSource[]> Iterate(IEnumerable<TSource> source, int size)
            {
                var buffer = new List<TSource>(size);
                foreach (var item in source)
                {
                    buffer.Add(item);
                    if (buffer.Count == size)
                    {
                        yield return buffer.ToArray();
                        buffer.Clear();
                    }
                }

                if (buffer.Count > 0)
                {
                    yield return buffer.ToArray();
                }
            }
        }
    }
#endif

#if !NET7_0_OR_GREATER
    internal static class ConditionalWeakTableShims
    {
        /// <summary>.NET 7's <c>ConditionalWeakTable.TryAdd</c>. <c>Add</c> is atomic and throws
        /// when the key is already there, so this is too.</summary>
        public static bool TryAdd<TKey, TValue>(this System.Runtime.CompilerServices.ConditionalWeakTable<TKey, TValue> table, TKey key, TValue value)
            where TKey : class
            where TValue : class
        {
            try
            {
                table.Add(key, value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    internal static class OrderShims
    {
        /// <summary>.NET 7's <c>Enumerable.Order</c>.</summary>
        public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source) => source.OrderBy(static x => x);
    }
#endif
}
