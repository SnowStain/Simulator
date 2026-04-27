using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using K4os.Compression.LZ4.Streams;

namespace Simulator.Assets;

public enum MapDocumentFormat
{
    Json,
    Lz4,
}

public sealed record ResolvedMapDocumentSource(string FullPath, MapDocumentFormat Format)
{
    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? string.Empty;
}

public sealed record MapPresetDocument(JsonObject Root, JsonObject Map, ResolvedMapDocumentSource Source);

public sealed class MapDocumentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8EncodingNoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<string> SupportedExtensions { get; } = [".json", ".lz4"];

    public bool TryResolve(string path, out ResolvedMapDocumentSource source)
    {
        source = default!;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        if (TryCreateSource(fullPath, mustExist: true, out source))
        {
            return true;
        }

        string extension = Path.GetExtension(fullPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            foreach (string candidateExtension in SupportedExtensions)
            {
                if (TryCreateSource(fullPath + candidateExtension, mustExist: true, out source))
                {
                    return true;
                }
            }

            return false;
        }

        string alternateExtension = extension.ToLowerInvariant() switch
        {
            ".json" => ".lz4",
            ".lz4" => ".json",
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(alternateExtension)
            && TryCreateSource(Path.ChangeExtension(fullPath, alternateExtension), mustExist: true, out source);
    }

    public ResolvedMapDocumentSource Resolve(string path, string? logicalName = null)
    {
        if (TryResolve(path, out ResolvedMapDocumentSource source))
        {
            return source;
        }

        string description = string.IsNullOrWhiteSpace(logicalName) ? "Map document" : logicalName;
        throw new FileNotFoundException(
            $"{description} was not found at '{path}' (.json/.lz4 are supported).");
    }

    public bool TryResolveRelative(string baseDirectory, string path, out ResolvedMapDocumentSource source)
    {
        string candidate = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
        return TryResolve(candidate, out source);
    }

    public JsonObject Load(ResolvedMapDocumentSource source)
    {
        JsonNode? node = JsonNode.Parse(ReadAllText(source));
        return node as JsonObject
            ?? throw new InvalidDataException($"Map document is not a valid JSON object: {source.FullPath}");
    }

    public JsonObject Load(string path, string? logicalName = null) => Load(Resolve(path, logicalName));

    public JsonObject? TryLoadRelative(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryResolveRelative(baseDirectory, path, out ResolvedMapDocumentSource source))
        {
            return null;
        }

        return Load(source);
    }

    public void Save(ResolvedMapDocumentSource source, JsonObject document)
    {
        string content = document.ToJsonString(JsonOptions) + Environment.NewLine;
        WriteAtomically(
            source.FullPath,
            stream =>
            {
                switch (source.Format)
                {
                    case MapDocumentFormat.Json:
                        using (var writer = new StreamWriter(stream, Utf8EncodingNoBom, bufferSize: 4096, leaveOpen: true))
                        {
                            writer.Write(content);
                            writer.Flush();
                        }
                        break;
                    case MapDocumentFormat.Lz4:
                        using (Stream compressed = LZ4Stream.Encode(stream))
                        using (var writer = new StreamWriter(compressed, Utf8EncodingNoBom, bufferSize: 4096, leaveOpen: false))
                        {
                            writer.Write(content);
                            writer.Flush();
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported map document format: {source.Format}");
                }
            });
    }

    public void Save(string path, JsonObject document)
    {
        ResolvedMapDocumentSource source = ResolveForSave(path);
        Save(source, document);
    }

    public ResolvedMapDocumentSource ResolveForSave(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Map document path cannot be empty.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (TryCreateSource(fullPath, mustExist: false, out ResolvedMapDocumentSource source))
        {
            return source;
        }

        throw new InvalidOperationException(
            $"Unsupported map document extension for '{path}'. Supported extensions: {string.Join(", ", SupportedExtensions)}");
    }

    private static bool TryCreateSource(string path, bool mustExist, out ResolvedMapDocumentSource source)
    {
        source = default!;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        MapDocumentFormat? format = extension switch
        {
            ".json" => MapDocumentFormat.Json,
            ".lz4" => MapDocumentFormat.Lz4,
            _ => null,
        };

        if (!format.HasValue)
        {
            return false;
        }

        if (mustExist && !File.Exists(path))
        {
            return false;
        }

        source = new ResolvedMapDocumentSource(path, format.Value);
        return true;
    }

    private static string ReadAllText(ResolvedMapDocumentSource source)
    {
        return source.Format switch
        {
            MapDocumentFormat.Json => File.ReadAllText(source.FullPath),
            MapDocumentFormat.Lz4 => ReadCompressedText(source.FullPath),
            _ => throw new InvalidOperationException($"Unsupported map document format: {source.Format}"),
        };
    }

    private static string ReadCompressedText(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Stream decompressed = LZ4Stream.Decode(file);
        using var reader = new StreamReader(decompressed, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteAtomically(string path, Action<Stream> writeAction)
    {
        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(parent) ? "." : parent,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            writeAction(stream);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
