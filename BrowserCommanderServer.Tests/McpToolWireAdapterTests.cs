using System.Text.Json;
using System.Text.Json.Nodes;
using BrowserCommander.McpStdioBridge;
using ModelContextProtocol.Protocol;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class McpToolWireAdapterTests
{
    private const string Old = "2025-11-25";
    private const string New = "2026-07-28";

    [Theory]
    [InlineData("{\"type\":\"array\"}", "[]")]
    [InlineData("{\"type\":\"array\"}", "[1,2]")]
    [InlineData("{\"type\":\"string\"}", "\"hello\"")]
    [InlineData("{\"type\":\"number\"}", "1.25")]
    [InlineData("{\"type\":\"boolean\"}", "true")]
    [InlineData("{\"type\":[\"object\",\"null\"]}", "{\"ok\":true}")]
    [InlineData("{\"type\":[\"object\",\"null\"]}", "null")]
    [InlineData("{\"type\":[\"array\",\"null\"]}", "null")]
    public void Legacy_WrapsNonObjectSchemasAndValues(string schemaJson, string valueJson)
    {
        var tool = ToolWithSchema(schemaJson);
        var original = Result(valueJson);
        var adaptedTool = McpToolWireAdapter.AdaptTool(tool, Old);
        var adapted = McpToolWireAdapter.AdaptResult(tool, original, New, Old);

        Assert.True(adaptedTool.OutputSchema.HasValue);
        var schema = adaptedTool.OutputSchema.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("result", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal(schemaJson, schema.GetProperty("properties").GetProperty("result").GetRawText());
        Assert.Equal(valueJson, adapted.StructuredContent?.GetProperty("result").GetRawText());
        Assert.Equal(valueJson, original.StructuredContent?.GetRawText());
    }

    [Theory]
    [InlineData("{\"type\":\"object\"}", "{\"a\":1}")]
    [InlineData("{\"type\":\"array\"}", "[]")]
    [InlineData("{\"type\":\"string\"}", "\"hello\"")]
    [InlineData("{\"type\":[\"object\",\"null\"]}", "null")]
    public void Modern_PreservesNaturalContracts(string schema, string value)
    {
        var tool = ToolWithSchema(schema);
        var result = Result(value);
        Assert.Same(tool, McpToolWireAdapter.AdaptTool(tool, New));
        Assert.Same(result, McpToolWireAdapter.AdaptResult(tool, result, New, New));
    }

    [Fact]
    public void Legacy_PlainObjectNeedsNoEnvelope()
    {
        var tool = ToolWithSchema("{\"type\":\"object\"}");
        Assert.Same(tool, McpToolWireAdapter.AdaptTool(tool, Old));
        Assert.Equal("{\"a\":1}", McpToolWireAdapter.AdaptResult(
            tool, Result("{\"a\":1}"), New, Old).StructuredContent?.GetRawText());
    }

    [Theory]
    [InlineData(Old, Old)]
    [InlineData(Old, New)]
    [InlineData(New, Old)]
    [InlineData(New, New)]
    public void VersionMatrix_NeverDoubleWraps(string upstream, string downstream)
    {
        var tool = ToolWithSchema("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}");
        var actualUpstream = upstream == Old ? "{\"result\":[\"a\"]}" : "[\"a\"]";
        var response = Result(actualUpstream);
        var converted = McpToolWireAdapter.AdaptResult(tool, response, upstream, downstream);
        Assert.Equal(downstream == Old ? "{\"result\":[\"a\"]}" : "[\"a\"]",
            converted.StructuredContent?.GetRawText());
        Assert.Equal(actualUpstream, response.StructuredContent?.GetRawText());
    }

    [Fact]
    public void AbsentSchema_NeverInventsOutputSchema_ButLegacyContentIsObject()
    {
        var tool = new Tool { Name = "no_schema" };
        Assert.Null(McpToolWireAdapter.AdaptTool(tool, Old).OutputSchema);
        Assert.Equal("{\"result\":true}", McpToolWireAdapter.AdaptResult(
            tool, Result("true"), New, Old).StructuredContent?.GetRawText());
    }

    [Fact]
    public void JsonPointersAndDefinitions_ArePreservedWhenRebased()
    {
        const string source = """
            {"type":"array","items":{"$ref":"#/$defs/Node"},"$defs":{"Node":{"type":"object","properties":{"child":{"$ref":"#/$defs/Node"},"self":{"$ref":"#"}}}}}
            """;
        var tool = ToolWithSchema(source);
        var adapted = McpToolWireAdapter.AdaptTool(tool, Old);
        Assert.True(adapted.OutputSchema.HasValue);
        var root = adapted.OutputSchema.Value.GetProperty("properties").GetProperty("result");
        Assert.Equal("#/properties/result/$defs/Node",
            root.GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal("#/properties/result/$defs/Node",
            root.GetProperty("$defs").GetProperty("Node").GetProperty("properties")
                .GetProperty("child").GetProperty("$ref").GetString());
        Assert.Equal("#/properties/result",
            root.GetProperty("$defs").GetProperty("Node").GetProperty("properties")
                .GetProperty("self").GetProperty("$ref").GetString());
        Assert.Equal(source, tool.OutputSchema?.GetRawText());
    }

    [Fact]
    public void NestedResourceReferences_DoNotChange()
    {
        var tool = ToolWithSchema("""
            {"type":"array","items":{"$id":"https://example.com/child","$ref":"#/$defs/x","$defs":{"x":{"type":"string"}}}}
            """);
        var inner = McpToolWireAdapter.AdaptTool(tool, Old).OutputSchema!.Value
            .GetProperty("properties").GetProperty("result").GetProperty("items");
        Assert.Equal("#/$defs/x", inner.GetProperty("$ref").GetString());
    }

    [Fact]
    public void ScreenshotsErrorsAndMetadata_ArePreserved()
    {
        var tool = ToolWithSchema("{\"type\":\"array\"}");
        tool.Title = "Title";
        tool.Annotations = new ToolAnnotations { ReadOnlyHint = true };
        tool.Meta = new JsonObject { ["key"] = "value" };
        var image = ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png");
        var original = new CallToolResult
        {
            Content = [image],
            StructuredContent = JsonDocument.Parse("[]").RootElement.Clone(),
            IsError = true,
            Meta = new JsonObject { ["trace"] = "1" }
        };
        var adapted = McpToolWireAdapter.AdaptResult(tool, original, New, Old);
        var publication = McpToolWireAdapter.AdaptTool(tool, Old);
        Assert.Same(tool.Annotations, publication.Annotations);
        Assert.Same(tool.Meta, publication.Meta);
        Assert.Equal("Title", publication.Title);
        Assert.True(adapted.IsError is true);
        Assert.Same(original.Meta, adapted.Meta);
        Assert.Same(original.Content, adapted.Content);
        Assert.Same(image, Assert.Single(adapted.Content));
        Assert.Equal("[]", original.StructuredContent?.GetRawText());

        var rawImage = new CallToolResult { Content = [image] };
        Assert.Same(rawImage, McpToolWireAdapter.AdaptResult(tool, rawImage, New, Old));
    }

    [Fact]
    public void ParallelRequests_DoNotMutateCatalog()
    {
        var tool = ToolWithSchema("{\"type\":\"array\"}");
        var schemas = Enumerable.Range(0, 100).AsParallel()
            .Select(i => McpToolWireAdapter.AdaptTool(tool, i % 2 == 0 ? Old : New).OutputSchema!.Value)
            .ToArray();
        Assert.Equal(50, schemas.Count(s => s.GetProperty("type").GetString() == "object"));
        Assert.Equal("{\"type\":\"array\"}", tool.OutputSchema?.GetRawText());
    }

    private static Tool ToolWithSchema(string schema) => new()
    {
        Name = "test",
        OutputSchema = JsonDocument.Parse(schema).RootElement.Clone()
    };

    private static CallToolResult Result(string payload) => new()
    {
        Content = [new TextContentBlock { Text = "unchanged" }],
        StructuredContent = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
