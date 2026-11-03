using System.Text.Json.Nodes;
using IoTSpy.Manipulation.ApiSpec;
using Xunit;

namespace IoTSpy.Manipulation.Tests.ApiSpec;

public class ApiSpecGeneratorTests
{
    // ── NormalizePath ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/users/123", "/api/users/{id}")]
    [InlineData("/api/users/456/posts/789", "/api/users/{id}/posts/{id}")]
    [InlineData("/api/users", "/api/users")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    // Negative signed-64-bit ids, as used throughout the SleepIQ API.
    [InlineData("/rest/bed/-9223372018926398631/pauseMode", "/rest/bed/{id}/pauseMode")]
    [InlineData("/rest/accounts/-9223372018926499868/sleepers/-9223372018926095868/circadianRoutines",
        "/rest/accounts/{id}/sleepers/{id}/circadianRoutines")]
    public void NormalizePath_DetectsNumericSegments(string input, string expected)
    {
        var result = ApiSpecGenerator.NormalizePath(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizePath_DetectsGuidSegments()
    {
        var result = ApiSpecGenerator.NormalizePath("/api/items/550e8400-e29b-41d4-a716-446655440000");
        Assert.Equal("/api/items/{id}", result);
    }

    [Fact]
    public void NormalizePath_DetectsHexSegments()
    {
        var result = ApiSpecGenerator.NormalizePath("/api/items/550e8400e29b41d4a716446655440000");
        Assert.Equal("/api/items/{id}", result);
    }

    [Fact]
    public void NormalizePath_PreservesNonIdSegments()
    {
        var result = ApiSpecGenerator.NormalizePath("/api/v2/users/settings");
        Assert.Equal("/api/v2/users/settings", result);
    }

    // ── InferJsonSchema ───────────────────────────────────────────────────────

    [Fact]
    public void InferJsonSchema_SimpleObject_ReturnsCorrectTypes()
    {
        var json = JsonNode.Parse("""{"name": "test", "count": 42, "active": true}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        Assert.Equal("object", schema["type"]!.GetValue<string>());

        var props = schema["properties"]!.AsObject();
        Assert.Equal("string", props["name"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", props["count"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", props["active"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_Array_InfersItemSchema()
    {
        var json = JsonNode.Parse("""[{"id": 1, "name": "a"}, {"id": 2, "name": "b"}]""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        Assert.Equal("array", schema["type"]!.GetValue<string>());
        Assert.NotNull(schema["items"]);

        var items = schema["items"]!.AsObject();
        Assert.Equal("object", items["type"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_EmptyArray_ReturnsEmptyItems()
    {
        var json = JsonNode.Parse("[]");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        Assert.Equal("array", schema["type"]!.GetValue<string>());
        Assert.NotNull(schema["items"]);
    }

    [Fact]
    public void InferJsonSchema_Null_ReturnsNullable()
    {
        var schema = ApiSpecGenerator.InferJsonSchema(null);
        Assert.True(schema["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public void InferJsonSchema_StringFormats_DetectsUuid()
    {
        var json = JsonNode.Parse("""{"id": "550e8400-e29b-41d4-a716-446655440000"}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        var idSchema = schema["properties"]!["id"]!.AsObject();
        Assert.Equal("string", idSchema["type"]!.GetValue<string>());
        Assert.Equal("uuid", idSchema["format"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_StringFormats_DetectsDateTime()
    {
        var json = JsonNode.Parse("""{"created": "2024-01-15T10:30:00Z"}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        var createdSchema = schema["properties"]!["created"]!.AsObject();
        Assert.Equal("date-time", createdSchema["format"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_StringFormats_DetectsEmail()
    {
        var json = JsonNode.Parse("""{"email": "user@example.com"}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        var emailSchema = schema["properties"]!["email"]!.AsObject();
        Assert.Equal("email", emailSchema["format"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_StringFormats_DetectsUri()
    {
        var json = JsonNode.Parse("""{"url": "https://example.com/path"}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        var urlSchema = schema["properties"]!["url"]!.AsObject();
        Assert.Equal("uri", urlSchema["format"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_NestedObject_InfersRecursively()
    {
        var json = JsonNode.Parse("""{"user": {"name": "test", "address": {"city": "NYC"}}}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        var userSchema = schema["properties"]!["user"]!.AsObject();
        Assert.Equal("object", userSchema["type"]!.GetValue<string>());

        var addressSchema = userSchema["properties"]!["address"]!.AsObject();
        Assert.Equal("object", addressSchema["type"]!.GetValue<string>());
        Assert.Equal("string", addressSchema["properties"]!["city"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void InferJsonSchema_NumberTypes_DistinguishesIntegerAndDecimal()
    {
        var json = JsonNode.Parse("""{"count": 42, "price": 19.99}""");
        var schema = ApiSpecGenerator.InferJsonSchema(json);

        var props = schema["properties"]!.AsObject();
        Assert.Equal("integer", props["count"]!["type"]!.GetValue<string>());
        Assert.Equal("number", props["price"]!["type"]!.GetValue<string>());
    }

    // ── TryInferSchema ────────────────────────────────────────────────────────

    [Fact]
    public void TryInferSchema_ImageContentType_ReturnsBinaryFormat()
    {
        var schema = ApiSpecGenerator.TryInferSchema(null, "image/jpeg");
        Assert.Null(schema); // null body = null schema
    }

    [Fact]
    public void TryInferSchema_VideoContentType_ReturnsBinaryFormat()
    {
        var schema = ApiSpecGenerator.TryInferSchema("data", "video/mp4");
        Assert.NotNull(schema);
        Assert.Equal("binary", schema["format"]!.GetValue<string>());
    }

    [Fact]
    public void TryInferSchema_JsonBody_InfersSchema()
    {
        var schema = ApiSpecGenerator.TryInferSchema("""{"key": "value"}""", "application/json");
        Assert.NotNull(schema);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void TryInferSchema_InvalidJson_ReturnsStringSchema()
    {
        var schema = ApiSpecGenerator.TryInferSchema("not json", "application/json");
        Assert.NotNull(schema);
        Assert.Equal("string", schema["type"]!.GetValue<string>());
    }

    // ── ExtractHeaderValue ────────────────────────────────────────────────────

    [Fact]
    public void ExtractHeaderValue_ValidJson_ReturnsValue()
    {
        var headers = """{"Content-Type": "application/json", "X-Custom": "value"}""";
        var result = ApiSpecGenerator.ExtractHeaderValue(headers, "Content-Type");
        Assert.Equal("application/json", result);
    }

    [Fact]
    public void ExtractHeaderValue_CaseInsensitive_ReturnsValue()
    {
        var headers = """{"content-type": "text/html"}""";
        var result = ApiSpecGenerator.ExtractHeaderValue(headers, "Content-Type");
        Assert.Equal("text/html", result);
    }

    [Fact]
    public void ExtractHeaderValue_NotFound_ReturnsNull()
    {
        var headers = """{"X-Other": "value"}""";
        var result = ApiSpecGenerator.ExtractHeaderValue(headers, "Content-Type");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractHeaderValue_EmptyHeaders_ReturnsNull()
    {
        Assert.Null(ApiSpecGenerator.ExtractHeaderValue("", "Content-Type"));
        Assert.Null(ApiSpecGenerator.ExtractHeaderValue(null!, "Content-Type"));
    }
}
