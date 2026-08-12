using System.Text.Json;
using System.Text.Json.Serialization;
using TestFramework.Abstractions.Execution;

namespace TestFramework.App.Services;

public sealed class TestResultStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters =
        {
            new JsonStringEnumConverter(),
            new ExceptionJsonConverter(),
            new SafeObjectJsonConverter()
        }
    };

    public TestResultStore(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public async Task<string> SaveAsync(TestSequenceRunResult result, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var sequenceName = SanitizeFileName(result.SequenceName);
        var fileName = $"{result.StartedAt:yyyyMMdd-HHmmssfff}-{sequenceName}-{Guid.NewGuid():N}.json";
        var filePath = Path.Combine(DirectoryPath, fileName);

        var tempPath = filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, result, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, filePath);
            return filePath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((string.IsNullOrWhiteSpace(name) ? "sequence" : name)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "sequence" : sanitized;
    }

    private sealed class ExceptionJsonConverter : JsonConverter<Exception>
    {
        public override Exception Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.GetType().FullName);
            writer.WriteString("message", value.Message);
            writer.WriteString("stackTrace", value.StackTrace);
            if (value.InnerException is not null)
            {
                writer.WritePropertyName("innerException");
                Write(writer, value.InnerException, options);
            }

            writer.WriteEndObject();
        }
    }

    private sealed class SafeObjectJsonConverter : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case JsonElement element:
                    element.WriteTo(writer);
                    return;
                case string text:
                    writer.WriteStringValue(text);
                    return;
                case bool boolean:
                    writer.WriteBooleanValue(boolean);
                    return;
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    JsonSerializer.Serialize(writer, value, value.GetType(), options);
                    return;
                case DateTime dateTime:
                    writer.WriteStringValue(dateTime);
                    return;
                case DateTimeOffset dateTimeOffset:
                    writer.WriteStringValue(dateTimeOffset);
                    return;
                case Guid guid:
                    writer.WriteStringValue(guid);
                    return;
                case Enum enumValue:
                    writer.WriteStringValue(enumValue.ToString());
                    return;
                case System.Collections.IDictionary dictionary:
                    writer.WriteStartObject();
                    foreach (System.Collections.DictionaryEntry entry in dictionary)
                    {
                        writer.WritePropertyName(Convert.ToString(entry.Key) ?? string.Empty);
                        JsonSerializer.Serialize(writer, entry.Value, options);
                    }

                    writer.WriteEndObject();
                    return;
                case System.Collections.IEnumerable enumerable:
                    writer.WriteStartArray();
                    foreach (var item in enumerable)
                    {
                        JsonSerializer.Serialize(writer, item, options);
                    }

                    writer.WriteEndArray();
                    return;
                default:
                    writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
            }
        }
    }
}
