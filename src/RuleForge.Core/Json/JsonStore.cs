using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleForge.Core.Json
{
    /// <summary>Snapshot ve kural dosyaları için ortak JSON ayarları.</summary>
    public static class JsonStore
    {
        public static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var o = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = null,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                // Türkçe karakterler ç gibi kaçışlanmasın, dosyalar okunabilir kalsın.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return o;
        }

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T Deserialize<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Boş JSON.");

        public static void Save<T>(T value, string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(value), new UTF8Encoding(false));
        }

        public static T Load<T>(string path) => Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
    }
}
