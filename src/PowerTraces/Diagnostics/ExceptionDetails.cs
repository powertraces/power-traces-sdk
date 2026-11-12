using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PowerTraces.Diagnostics;

/// <summary>An exception as the dashboard shows it: its chain (the exception and what caused it),
/// each with its stack frames, and — for the frames that are the app's own code — a few lines of
/// the source around the line that ran. Stored as JSON in the <see cref="ExceptionDetails.AttributeKey"/>
/// attribute of the span's standard <c>exception</c> event.</summary>
internal sealed record ExceptionInfo(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("frames")] List<FrameInfo> Frames);

internal sealed record FrameInfo(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("column")] int? Column,
    [property: JsonPropertyName("user")] bool IsUserCode)
{
    [JsonPropertyName("snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceSnapshot? Snapshot { get; set; }
}

/// <summary><see cref="Lines"/> starting at line <see cref="StartLine"/> (1-based).</summary>
internal sealed record SourceSnapshot(
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines);

internal static class ExceptionDetails
{
    /// <summary>The <c>exception</c> event attribute holding the JSON list of <see cref="ExceptionInfo"/>.</summary>
    public const string AttributeKey = "exception.details";

    private const int MaxChain = 5;
    private const int MaxFramesPerException = 50;
    private const int MaxLineLength = 240;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------------------------------------------------------------- from an Exception

    /// <summary>The exception and its causes (inner exceptions; each inner exception of an
    /// <see cref="AggregateException"/>), outermost first.</summary>
    public static string FromException(Exception exception, int snapshotLines, int maxSnapshots)
    {
        var chain = new List<ExceptionInfo>();
        var queue = new Queue<Exception>([exception]);
        while (queue.Count > 0 && chain.Count < MaxChain)
        {
            var ex = queue.Dequeue();
            var frames = Frames(ex);
            AttachSnapshots(frames, snapshotLines, maxSnapshots);
            chain.Add(new ExceptionInfo(ex.GetType().FullName ?? ex.GetType().Name, ex.Message, frames));

            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    queue.Enqueue(inner);
                }
            }
            else if (ex.InnerException is { } inner)
            {
                queue.Enqueue(inner);
            }
        }

        return JsonSerializer.Serialize(chain, Json);
    }

    private static List<FrameInfo> Frames(Exception exception)
    {
        var list = new List<FrameInfo>();
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: true).GetFrames())
        {
            if (list.Count >= MaxFramesPerException)
            {
                break;
            }

            var method = frame.GetMethod();
            if (method is null)
            {
                continue;
            }

            var file = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            var column = frame.GetFileColumnNumber();
            list.Add(new FrameInfo(
                MethodName(method),
                string.IsNullOrEmpty(file) ? null : file,
                line > 0 ? line : null,
                column > 0 ? column : null,
                IsUserCode(method.DeclaringType?.Assembly, file)));
        }

        return list;
    }

    /// <summary><c>Namespace.Type.Method</c>, with the compiler's async state machines and lambdas
    /// (<c>&lt;Submit&gt;d__5.MoveNext</c>, <c>&lt;Post&gt;b__0</c>) folded back into the method
    /// they were written in.</summary>
    private static string MethodName(MethodBase method)
    {
        var type = method.DeclaringType;
        var name = Unwrap(method.Name);
        while (type is not null && type.Name.StartsWith('<'))
        {
            if (method.Name == "MoveNext" || method.Name.StartsWith('<'))
            {
                name = Unwrap(type.Name);
            }

            type = type.DeclaringType;
        }

        return type is null ? name : $"{type.FullName}.{name}";
    }

    /// <summary><c>&lt;Submit&gt;d__5</c> → <c>Submit</c>; <c>&lt;&lt;Main&gt;$&gt;d__0</c> → <c>Main</c>
    /// (the brackets nest).</summary>
    private static string Unwrap(string name)
    {
        while (name.StartsWith('<'))
        {
            var depth = 0;
            var close = -1;
            for (var i = 0; i < name.Length && close < 0; i++)
            {
                depth += name[i] == '<' ? 1 : name[i] == '>' ? -1 : 0;
                if (depth == 0)
                {
                    close = i;
                }
            }

            if (close <= 1)
            {
                break;
            }

            name = name[1..close];
        }

        return name;
    }

    private static bool IsUserCode(Assembly? assembly, string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return false;
        }

        var name = assembly?.GetName().Name ?? "";
        return !(name.StartsWith("System.", StringComparison.Ordinal) || name == "System"
            || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name == "mscorlib"
            || name.StartsWith("OpenTelemetry", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- from stack trace text

    // "   at Ns.Type.Method(Args) in /src/App/File.cs:line 57"
    private static readonly Regex FrameLine = new(@"^\s*at (?<method>.+?)(?: in (?<file>.+):line (?<line>\d+))?\s*$", RegexOptions.Compiled);

    /// <summary>For an <c>exception</c> event recorded by something else (instrumentation, or
    /// OpenTelemetry's own <c>RecordException</c>): the frames parsed from its
    /// <c>exception.stacktrace</c> text, with snapshots. Only the outermost exception — inner
    /// exceptions in the text are left to the raw stack trace.</summary>
    public static string? FromStackTraceText(string? type, string? message, string? stackTrace, int snapshotLines, int maxSnapshots)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return null;
        }

        var frames = new List<FrameInfo>();
        foreach (var raw in stackTrace.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (text.Contains("--- End of inner exception stack trace ---", StringComparison.Ordinal))
            {
                // The text lists the inner exception's frames first; what follows are the outer ones.
                frames.Clear();
                continue;
            }

            if (FrameLine.Match(text) is not { Success: true } m || frames.Count >= MaxFramesPerException)
            {
                continue;
            }

            var file = m.Groups["file"].Success ? m.Groups["file"].Value : null;
            var method = m.Groups["method"].Value;
            var paren = method.IndexOf('(');
            var methodName = string.Join('.', (paren > 0 ? method[..paren] : method).Split('.').Select(part => part.StartsWith('<') ? Unwrap(part) : part));
            frames.Add(new FrameInfo(
                methodName,
                file,
                m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture) : null,
                null,
                file is not null && !methodName.StartsWith("System.", StringComparison.Ordinal) && !methodName.StartsWith("Microsoft.", StringComparison.Ordinal)));
        }

        if (frames.Count == 0)
        {
            return null;
        }

        AttachSnapshots(frames, snapshotLines, maxSnapshots);
        return JsonSerializer.Serialize(
            new List<ExceptionInfo> { new(type ?? "Exception", message ?? "", frames) }, Json);
    }

    // ---------------------------------------------------------------- snapshots

    /// <summary>Adds snapshots to the first <paramref name="budget"/> user-code frames of one
    /// exception whose source can be found; returns how many it added.</summary>
    private static int AttachSnapshots(List<FrameInfo> frames, int snapshotLines, int budget)
    {
        var added = 0;
        foreach (var frame in frames)
        {
            if (added >= budget)
            {
                break;
            }

            if (!frame.IsUserCode || frame.File is null || frame.Line is not { } line)
            {
                continue;
            }

            if (SourceLines.Get(frame.File) is { } lines && line <= lines.Length)
            {
                var start = Math.Max(1, line - Math.Max(0, snapshotLines));
                var end = Math.Min(lines.Length, line + Math.Max(0, snapshotLines));
                frame.Snapshot = new SourceSnapshot(
                    start,
                    lines[(start - 1)..end].Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] + "…" : l).ToList());
                added++;
            }
        }

        return added;
    }

    /// <summary>Source text by the path a stack frame names: from the sources embedded in a loaded
    /// assembly's portable PDB (<c>&lt;EmbedAllSources&gt;true&lt;/EmbedAllSources&gt;</c> — works in
    /// production, where the .cs files aren't deployed), else the file itself if it exists (local
    /// runs). Cached, including misses.</summary>
    private static class SourceLines
    {
        private const int MaxCachedFiles = 256;
        private static readonly ConcurrentDictionary<string, string[]?> Cache = new(StringComparer.Ordinal);
        private static Dictionary<string, Func<string?>>? _embedded;
        private static List<MetadataReaderProvider> _providers = [];
        private static int _assemblyCount;
        private static readonly Lock Gate = new();

        public static string[]? Get(string path)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            if (Cache.Count >= MaxCachedFiles)
            {
                Cache.Clear();
            }

            string[]? lines = null;
            try
            {
                var text = Embedded(path) ?? (File.Exists(path) ? File.ReadAllText(path) : null);
                lines = text?.Replace("\r\n", "\n").Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            {
                // No source is fine — the frame is still shown, just without code.
            }

            Cache[path] = lines;
            return lines;
        }

        private static string? Embedded(string path)
        {
            lock (Gate)
            {
                return EmbeddedIndex().TryGetValue(Normalize(path), out var read) ? read() : null;
            }
        }

        /// <summary>Document path → reader of its embedded source, over every loaded assembly with a
        /// portable PDB (embedded or next to it). Rebuilt when more assemblies have loaded. Called
        /// under <see cref="Gate"/>.</summary>
        private static Dictionary<string, Func<string?>> EmbeddedIndex()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            {
                if (_embedded is not null && assemblies.Length == _assemblyCount)
                {
                    return _embedded;
                }

                foreach (var old in _providers)
                {
                    old.Dispose();
                }

                _providers = [];
                var index = new Dictionary<string, Func<string?>>(StringComparer.Ordinal);
                foreach (var assembly in assemblies)
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || IsFramework(assembly))
                    {
                        continue;
                    }

                    try
                    {
                        AddDocuments(assembly.Location, index);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
                    {
                        // No readable PDB for this assembly.
                    }
                }

                _assemblyCount = assemblies.Length;
                return _embedded = index;
            }
        }

        private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

        private static void AddDocuments(string assemblyPath, Dictionary<string, Func<string?>> index)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.TryOpenAssociatedPortablePdb(assemblyPath, p => File.Exists(p) ? File.OpenRead(p) : null, out var provider, out _) || provider is null)
            {
                return;
            }

            // The provider stays open for the lifetime of the index (reading a document on demand
            // needs it), and is disposed when the index is rebuilt.
            _providers.Add(provider);
            var reader = provider.GetMetadataReader();
            foreach (var handle in reader.CustomDebugInformation)
            {
                var info = reader.GetCustomDebugInformation(handle);
                if (info.Parent.Kind != HandleKind.Document || reader.GetGuid(info.Kind) != EmbeddedSourceKind)
                {
                    continue;
                }

                var document = reader.GetDocument((DocumentHandle)info.Parent);
                var name = Normalize(reader.GetString(document.Name));
                var blob = info.Value;
                index.TryAdd(name, () => ReadEmbedded(reader, blob));
            }
        }

        /// <summary>Embedded source blob: an int32 format (0 = stored as is, otherwise the
        /// uncompressed size of the deflated text that follows), then the bytes.</summary>
        private static string? ReadEmbedded(MetadataReader reader, BlobHandle blob)
        {
            var bytes = reader.GetBlobBytes(blob);
            if (bytes.Length < 4)
            {
                return null;
            }

            var format = BitConverter.ToInt32(bytes, 0);
            if (format == 0)
            {
                return Decode(bytes.AsSpan(4));
            }

            using var input = new MemoryStream(bytes, 4, bytes.Length - 4);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(format);
            deflate.CopyTo(output);
            return Decode(output.GetBuffer().AsSpan(0, (int)output.Length));
        }

        private static string Decode(ReadOnlySpan<byte> bytes)
        {
            var bom = bytes.StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            return Encoding.UTF8.GetString(bytes[bom..]);
        }

        private static bool IsFramework(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? "";
            return name == "System" || name == "mscorlib" || name == "netstandard"
                || name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name.StartsWith("OpenTelemetry", StringComparison.Ordinal);
        }

        private static string Normalize(string path) => path.Replace('\\', '/');
    }
}
