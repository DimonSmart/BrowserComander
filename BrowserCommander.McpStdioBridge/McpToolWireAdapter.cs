using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace BrowserCommander.McpStdioBridge;

/// <summary>
/// Adapts the natural tool contracts to the MCP revision of the *downstream* request.
/// The catalog remains immutable; the upstream MCP session has its own negotiated version.
/// </summary>
public static class McpToolWireAdapter
{
    private const string NaturalOutputSchemaVersion = "2026-07-28";

    public static bool RequiresLegacyOutput(string? protocolVersion) =>
        string.IsNullOrWhiteSpace(protocolVersion) ||
        string.CompareOrdinal(protocolVersion, NaturalOutputSchemaVersion) < 0;

    public static Tool AdaptTool(Tool tool, string? downstreamVersion)
    {
        if (!RequiresLegacyOutput(downstreamVersion) || tool.OutputSchema is not { } schema ||
            !NeedsEnvelope(schema))
        {
            return tool;
        }

        JsonNode natural = JsonNode.Parse(schema.GetRawText())
            ?? throw new InvalidOperationException("An output schema cannot be JSON null.");

        // Moving a subschema changes the meaning of JSON Pointer references relative
        // to the document root. A subschema with its own $id defines a separate
        // resource and its own references must not be rewritten.
        RewriteLocalPointers(natural, hasOwnResource: false);

        var wrapped = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["result"] = natural },
            ["required"] = new JsonArray { "result" }
        };

        return new Tool
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            OutputSchema = JsonSerializer.SerializeToElement(wrapped),
            Annotations = tool.Annotations,
            Icons = tool.Icons,
            Meta = tool.Meta
        };
    }

    public static CallToolResult AdaptResult(
        Tool? naturalTool,
        CallToolResult source,
        string? upstreamVersion,
        string? downstreamVersion)
    {
        if (source.StructuredContent is not { } value)
            return source;

        bool schemaNeedsEnvelope = naturalTool?.OutputSchema is { } schema && NeedsEnvelope(schema);
        JsonElement naturalValue = value;

        // Version 2 of the SDK wraps non-object output when calling a legacy
        // upstream. Undo that single upstream adaptation before applying the
        // independent downstream contract.
        if (schemaNeedsEnvelope && RequiresLegacyOutput(upstreamVersion) &&
            value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("result", out JsonElement inner) &&
            value.EnumerateObject().Count() == 1)
        {
            naturalValue = inner.Clone();
        }

        bool wrapForDownstream = RequiresLegacyOutput(downstreamVersion) &&
            (schemaNeedsEnvelope || naturalValue.ValueKind != JsonValueKind.Object);

        JsonElement downstreamValue = wrapForDownstream
            ? JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["result"] = naturalValue
            })
            : naturalValue;

        // Do not modify or reserialize content (in particular screenshot images).
        // Keep the result's metadata and error status, even for errors with a
        // structured payload.
        return new CallToolResult
        {
            Content = source.Content,
            StructuredContent = downstreamValue,
            IsError = source.IsError,
            Meta = source.Meta,
            ResultType = source.ResultType
        };
    }

    private static bool NeedsEnvelope(JsonElement schema) =>
        schema.ValueKind != JsonValueKind.Object ||
        !schema.TryGetProperty("type", out JsonElement type) ||
        type.ValueKind != JsonValueKind.String ||
        type.GetString() != "object";

    private static void RewriteLocalPointers(JsonNode? node, bool hasOwnResource)
    {
        if (node is JsonArray array)
        {
            foreach (var element in array)
                RewriteLocalPointers(element, hasOwnResource);
            return;
        }

        if (node is not JsonObject obj)
            return;

        // $id scopes fragment references to the nested schema resource.
        bool scoped = hasOwnResource || obj.ContainsKey("$id");
        if (!scoped)
        {
            foreach (string keyword in new[] { "$ref", "$dynamicRef", "$recursiveRef" })
            {
                if (obj[keyword] is not JsonValue reference ||
                    !reference.TryGetValue<string>(out var pointer))
                    continue;

                if (pointer == "#")
                    obj[keyword] = "#/properties/result";
                else if (pointer.StartsWith("#/", StringComparison.Ordinal))
                    obj[keyword] = "#/properties/result" + pointer[1..];
            }
        }

        foreach (var property in obj)
            RewriteLocalPointers(property.Value, scoped);
    }
}
