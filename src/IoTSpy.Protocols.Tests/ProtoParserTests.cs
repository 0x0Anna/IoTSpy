using IoTSpy.Protocols.Grpc;
using Xunit;

namespace IoTSpy.Protocols.Tests;

public class ProtoParserTests
{
    // ── ParseFlatMap ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseFlatMap_SimpleMessage_ExtractsFields()
    {
        const string proto = """
            message DeviceInfo {
              string device_id = 1;
              string name = 2;
              int32 version = 3;
            }
            """;

        var map = ProtoParser.ParseFlatMap(proto);

        Assert.Equal("device_id", map[1]);
        Assert.Equal("name", map[2]);
        Assert.Equal("version", map[3]);
    }

    [Fact]
    public void ParseFlatMap_MultipleMessages_MergesFields()
    {
        const string proto = """
            message Foo {
              string foo_field = 1;
            }
            message Bar {
              int32 bar_field = 2;
            }
            """;

        var map = ProtoParser.ParseFlatMap(proto);

        Assert.Equal("foo_field", map[1]);
        Assert.Equal("bar_field", map[2]);
    }

    [Fact]
    public void ParseFlatMap_RepeatedField_Extracted()
    {
        const string proto = """
            message Readings {
              repeated float values = 4;
            }
            """;

        var map = ProtoParser.ParseFlatMap(proto);

        Assert.Equal("values", map[4]);
    }

    [Fact]
    public void ParseFlatMap_EmptyProto_ReturnsEmpty()
    {
        var map = ProtoParser.ParseFlatMap("syntax = \"proto3\";");
        Assert.Empty(map);
    }

    [Fact]
    public void ParseFlatMap_ConflictingNumbers_KeepsFirst()
    {
        const string proto = """
            message A { string alpha = 1; }
            message B { int32 beta = 1; }
            """;

        var map = ProtoParser.ParseFlatMap(proto);

        Assert.Equal("alpha", map[1]);
    }

    // ── ParsePerMessage ──────────────────────────────────────────────────────

    [Fact]
    public void ParsePerMessage_TwoMessages_SeparatesMaps()
    {
        const string proto = """
            message Req {
              string query = 1;
            }
            message Resp {
              string result = 1;
              int32 count = 2;
            }
            """;

        var byMsg = ProtoParser.ParsePerMessage(proto);

        Assert.True(byMsg.ContainsKey("Req"));
        Assert.True(byMsg.ContainsKey("Resp"));
        Assert.Equal("query", byMsg["Req"][1]);
        Assert.Equal("result", byMsg["Resp"][1]);
        Assert.Equal("count", byMsg["Resp"][2]);
    }

    // ── ToJson / FromJson round-trip ─────────────────────────────────────────

    [Fact]
    public void ToJson_FromJson_RoundTrip()
    {
        var original = new Dictionary<int, string> { [1] = "id", [2] = "name", [10] = "value" };
        var json = ProtoParser.ToJson(original);
        var restored = ProtoParser.FromJson(json);

        Assert.Equal(original.Count, restored.Count);
        foreach (var (k, v) in original)
            Assert.Equal(v, restored[k]);
    }

    [Fact]
    public void ToJson_EmptyMap_ReturnsEmptyObject()
    {
        Assert.Equal("{}", ProtoParser.ToJson(new Dictionary<int, string>()));
    }

    [Fact]
    public void FromJson_EmptyObject_ReturnsEmpty()
    {
        Assert.Empty(ProtoParser.FromJson("{}"));
    }

    [Fact]
    public void ToJson_FromJson_RoundTrip_FieldNameContainsCommaAndColon()
    {
        // Regression: the old hand-rolled parser split on literal ',' and ':',
        // which broke on field names containing those characters.
        var original = new Dictionary<int, string> { [1] = "weird,field:name" };
        var json = ProtoParser.ToJson(original);
        var restored = ProtoParser.FromJson(json);

        Assert.Equal("weird,field:name", restored[1]);
    }

    [Fact]
    public void FromJson_MalformedInput_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(ProtoParser.FromJson("not valid json"));
    }
}
