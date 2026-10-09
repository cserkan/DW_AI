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
            o.Converters.Add(new ShortDoubleConverter());
            return o;
        }

        /// <summary>
        /// .NET Framework'te double en kısa biçimde yazılmıyor (0.94 → 0.93999999999999995). Aynı değere geri okunan en kısa
        /// yazımı kullanır; böylece dosyalar her bilgisayarda aynı çıkar.
        /// </summary>
        private sealed class ShortDoubleConverter : JsonConverter<double>
        {
            public override double Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options) =>
                reader.GetDouble();

            public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    writer.WriteNumberValue(value); // ayarlara göre hata verir, varsayılan davranış
                    return;
                }
                var c = System.Globalization.CultureInfo.InvariantCulture;
                var text = value.ToString("G15", c);
                if (double.Parse(text, c) != value) text = value.ToString("G17", c);
                writer.WriteRawValue(text.Replace("E+", "E"), skipInputValidation: false);
            }
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
