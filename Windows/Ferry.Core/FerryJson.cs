using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ferry.Core;

/// <summary>
/// Mac 版と同じ形式の JSON を読み書きする。
/// キーは camelCase、ID は大文字の UUID、値が無いキーは省略、キーはアルファベット順（Mac 版の sortedKeys）。
/// </summary>
public static class FerryJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // 日本語を \uXXXX にせずそのまま書く（Mac 版と同じ）
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new UppercaseGuidConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>キーをアルファベット順に並べて書き出す（Dropbox で差分が小さくなるように）</summary>
    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        return Sorted(node)?.ToJsonString(Options) ?? "null";
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Sorted(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    /// <summary>書き込み途中のファイルを読まれないよう、一時ファイルに書いてから置き換える</summary>
    public static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, contents + "\n");
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>ID は Mac 版（Swift の uuidString）に合わせて大文字で書く</summary>
    public static string IdString(Guid id) => id.ToString("D").ToUpperInvariant();

    private sealed class UppercaseGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(IdString(value));
    }
}
