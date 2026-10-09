using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestFramework.Abstractions.Execution;

namespace TestFramework.Core.Results;

/// <summary>
/// The framework's own file format for a run result: JSON, versioned, readable back.
///
/// Before this every host wrote its own. A result holds an <see cref="Exception"/> and
/// <c>object</c>-typed outputs and variables, neither of which <see cref="JsonSerializer"/> handles
/// on its own, so each host grew converters - and two hosts' files then disagree about field names,
/// about how an exception or a NaN looks, and about whether a file can be read back at all. One
/// format means one report generator, one archive and one importer for every bench.
///
/// The document is an envelope - <c>format</c>, <c>schemaVersion</c>, <c>result</c> - so a reader
/// can tell this file from any other JSON and refuse a schema it does not understand instead of
/// misreading it. Inside, properties are camelCase and enums are their names.
///
/// What survives a round trip, and what cannot:
/// - every field of the result, its items, steps, attempts and measurements;
/// - an exception, as its type name, message, stack trace and inner exceptions - reading gives a
///   <see cref="RecordedException"/>, since the original type may not even be loadable;
/// - output and variable values as JSON can carry them: text, booleans, numbers (integers read
///   back as <see cref="long"/>, doubles - a whole one included, written 5.0 - as <see cref="double"/>),
///   lists and dictionaries.
///   Non-finite numbers are written as "NaN" / "Infinity" text. Any other object is written as its
///   invariant <see cref="object.ToString"/>, because a result must always be writable - losing a
///   value's type is better than losing the whole record of what the DUT was asked to do.
/// </summary>
public static class TestResultJson
{
    public const string Format = "testframework.run-result";

    public const int SchemaVersion = 1;

    private const int MaxValueDepth = 64;

    private static readonly JsonSerializerOptions Options = CreateOptions(indented: true);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(indented: false);

    public static string Serialize(TestSequenceRunResult result, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(new ResultDocument { Result = result }, indented ? Options : CompactOptions);
    }

    public static Task SerializeAsync(Stream stream, TestSequenceRunResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.SerializeAsync(stream, new ResultDocument { Result = result }, Options, cancellationToken);
    }

    public static TestSequenceRunResult Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Open(JsonSerializer.Deserialize<ResultDocument>(json, Options));
    }

    public static async Task<TestSequenceRunResult> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Open(await JsonSerializer.DeserializeAsync<ResultDocument>(stream, Options, cancellationToken).ConfigureAwait(false));
    }

    private static TestSequenceRunResult Open(ResultDocument? document)
    {
        if (document is null || !string.Equals(document.Format, Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Not a TestFramework run result: expected format '{Format}'.");
        }

        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Run result schemaVersion {document.SchemaVersion} is not supported; this build reads {SchemaVersion}.");
        }

        return document.Result ?? throw new InvalidDataException("Run result document has no result.");
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,

            // The result's own nesting - items, groups, attempts - sits above any value it holds,
            // and a value may nest MaxValueDepth levels before it is cut off.
            MaxDepth = 256,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,

            // The result's collections are get-only and its dictionaries are case-insensitive by
            // construction; populating the instances the model creates keeps both, where replacing
            // them would fail on the first and silently drop the comparer on the second.
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ExceptionConverter());
        options.Converters.Add(new ValueConverter());
        return options;
    }

    private sealed class ResultDocument
    {
        public string Format { get; set; } = TestResultJson.Format;

        public int SchemaVersion { get; set; } = TestResultJson.SchemaVersion;

        public TestSequenceRunResult? Result { get; set; }
    }

    private sealed class ExceptionConverter : JsonConverter<Exception>
    {
        public override Exception? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ReadException(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            WriteException(writer, value, depth: 0);
        }

        private static void WriteException(Utf8JsonWriter writer, Exception exception, int depth)
        {
            writer.WriteStartObject();
            writer.WriteString("type", exception is RecordedException recorded ? recorded.OriginalType : exception.GetType().FullName);
            writer.WriteString("message", exception.Message);
            if (exception.StackTrace is { } stackTrace)
            {
                writer.WriteString("stackTrace", stackTrace);
            }

            if (exception.InnerException is { } inner && depth < MaxValueDepth)
            {
                writer.WritePropertyName("inner");
                WriteException(writer, inner, depth + 1);
            }

            writer.WriteEndObject();
        }

        private static RecordedException? ReadException(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new RecordedException(
                Text(element, "type") ?? typeof(Exception).FullName!,
                Text(element, "message") ?? string.Empty,
                Text(element, "stackTrace"),
                element.TryGetProperty("inner", out var inner) ? ReadException(inner) : null);
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Writes any value a plugin or a variable can hold, and reads it back as plain data.</summary>
    private sealed class ValueConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ToPlain(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            WriteValue(writer, value, depth: 0);
        }

        private static void WriteValue(Utf8JsonWriter writer, object? value, int depth)
        {
            if (depth > MaxValueDepth)
            {
                // A structure that contains itself would otherwise recurse until the stack ran out.
                writer.WriteStringValue("(nested too deeply to record)");
                return;
            }

            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case string text:
                    writer.WriteStringValue(text);
                    return;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    return;
                case double number:
                    WriteDouble(writer, number);
                    return;
                case float number:
                    WriteDouble(writer, number);
                    return;
                case decimal number:
                    writer.WriteNumberValue(number);
                    return;
                case int or long or short or byte or sbyte or ushort or uint:
                    writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    return;
                case ulong number:
                    writer.WriteNumberValue(number);
                    return;
                case Enum:
                    writer.WriteStringValue(value.ToString());
                    return;
                case DateTimeOffset moment:
                    writer.WriteStringValue(moment);
                    return;
                case DateTime moment:
                    writer.WriteStringValue(moment);
                    return;
                case TimeSpan span:
                    writer.WriteStringValue(span.ToString("c", CultureInfo.InvariantCulture));
                    return;
                case Guid id:
                    writer.WriteStringValue(id);
                    return;
                case Exception exception:
                    writer.WriteStringValue($"{exception.GetType().FullName}: {exception.Message}");
                    return;
                case IDictionary dictionary:
                    writer.WriteStartObject();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                        WriteValue(writer, entry.Value, depth + 1);
                    }

                    writer.WriteEndObject();
                    return;
                case IEnumerable list:
                    writer.WriteStartArray();
                    foreach (var element in list)
                    {
                        WriteValue(writer, element, depth + 1);
                    }

                    writer.WriteEndArray();
                    return;
                default:
                    writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        /// <summary>
        /// A whole double is written with a fraction - 5.0, not 5 - so it reads back as a double. As
        /// 5 it would come back an integer, and a voltage of exactly 5 V would change type between
        /// the run and its report, which is the drift the sequence format already refuses.
        /// </summary>
        private static void WriteDouble(Utf8JsonWriter writer, double number)
        {
            if (!double.IsFinite(number))
            {
                writer.WriteStringValue(number.ToString(CultureInfo.InvariantCulture));
                return;
            }

            var text = number.ToString("R", CultureInfo.InvariantCulture);
            writer.WriteRawValue(text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0");
        }

        private static object? ToPlain(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    // Boxed separately: as one conditional the long would be widened to double.
                    return element.TryGetInt64(out var integral) ? (object)integral : element.GetDouble();
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(ToPlain).ToList();
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        dictionary[property.Name] = ToPlain(property.Value);
                    }

                    return dictionary;
                default:
                    return null;
            }
        }
    }
}

/// <summary>
/// An exception as a result file recorded it: the original type by name, its message, its stack
/// trace. The original type is not recreated - it may live in a plugin this process never loaded -
/// so this stands in for it, and says what it was.
/// </summary>
public sealed class RecordedException : Exception
{
    private readonly string? _stackTrace;

    public RecordedException(string originalType, string message, string? stackTrace, Exception? inner = null)
        : base(message, inner)
    {
        OriginalType = originalType;
        _stackTrace = stackTrace;
    }

    /// <summary>The full name of the exception type that was thrown.</summary>
    public string OriginalType { get; }

    public override string? StackTrace => _stackTrace;

    public override string ToString() => $"{OriginalType}: {Message}";
}
