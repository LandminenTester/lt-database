using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bundle.Tests;

public class CryptoTests
{
    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Sha256_known_vectors()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Convert.ToHexString(Sha256.Hash([])).ToLowerInvariant());
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Convert.ToHexString(Sha256.Hash(B("abc"))).ToLowerInvariant());
        Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
            Convert.ToHexString(Sha256.Hash(B("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"))).ToLowerInvariant());
    }

    [Fact]
    public void Sha256_matches_the_platform_for_every_length_around_the_block_boundaries()
    {
        var rng = new Random(42);
        for (var length = 0; length <= 400; length++)
        {
            var data = new byte[length];
            rng.NextBytes(data);
            Assert.Equal(SHA256.HashData(data), Sha256.Hash(data));
        }
        var big = new byte[100_000];
        rng.NextBytes(big);
        Assert.Equal(SHA256.HashData(big), Sha256.Hash(big));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(131)]
    [InlineData(500)]
    public void Hmac_matches_the_platform_for_all_key_sizes(int keyLength)
    {
        var rng = new Random(keyLength);
        var key = new byte[keyLength];
        rng.NextBytes(key);
        foreach (var messageLength in new[] { 0, 1, 55, 56, 63, 64, 65, 200, 1000 })
        {
            var message = new byte[messageLength];
            rng.NextBytes(message);
            Assert.Equal(HMACSHA256.HashData(key, message), Sha256.Hmac(key, message));
        }
    }

    [Fact]
    public void Hmac_rfc4231_cases()
    {
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
            Convert.ToHexString(Sha256.Hmac(B("Jefe"), B("what do ya want for nothing?"))).ToLowerInvariant());
        // test case 6: key longer than the block size
        Assert.Equal("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",
            Convert.ToHexString(Sha256.Hmac(Enumerable.Repeat((byte)0xaa, 131).ToArray(), B("Test Using Larger Than Block-Size Key - Hash Key First"))).ToLowerInvariant());
    }
}

public class WireTests
{
    static readonly byte[] Secret = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    const string Nonce = "AAAAAAAAAAAAAAAAAAAAAA";

    // The same vectors are asserted in lt-database-api (tests/Ldb.Tests/CoreTests.cs), computed independently with Python.
    [Fact]
    public void Request_signature_matches_the_api_vector() =>
        Assert.Equal("42zlXLbDTxoL-oJ5eJ3IuG9oGFoByqkaYeBRHtC8x7c",
            Wire.SignRequest(Secret, "/v1/data/find", "main", 1760000000, Nonce, "{\"namespace\":\"demo\",\"table\":\"players\"}"));

    [Fact]
    public void Response_signature_matches_the_api_vector() =>
        Assert.Equal("Uk6xxTTmVDyXYAuBqMp5aF4vxbPJQH7jjNGM2gs5MH8", Wire.SignResponse(Secret, Nonce, 200, "{\"count\":3}"));

    [Fact]
    public void Built_request_is_a_valid_envelope()
    {
        var json = Wire.BuildRequest(Secret, "/v1/data/count", "main", 1760000000, Nonce, "{\"namespace\":\"demo\",\"note\":\"line\\nbreak \\\"quoted\\\"\"}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("main", root.GetProperty("kid").GetString());
        Assert.Equal(1760000000, root.GetProperty("ts").GetInt64());
        Assert.Equal(Nonce, root.GetProperty("nonce").GetString());
        var payload = root.GetProperty("payload").GetString()!;
        Assert.Equal("{\"namespace\":\"demo\",\"note\":\"line\\nbreak \\\"quoted\\\"\"}", payload);
        Assert.Equal(Wire.SignRequest(Secret, "/v1/data/count", "main", 1760000000, Nonce, payload), root.GetProperty("sig").GetString());
    }

    static string Answer(int status, string payload, byte[]? secret = null, string? nonce = null, int? signedStatus = null) =>
        JsonText.Object(("v", 1), ("ok", status < 400), ("status", signedStatus ?? status), ("payload", payload), ("sig", Wire.SignResponse(secret ?? Secret, nonce ?? Nonce, signedStatus ?? status, payload)));

    [Fact]
    public void Authentic_answer_is_accepted()
    {
        var r = Wire.ReadResponse(Answer(200, "{\"count\":3}"), Secret, Nonce, 200);
        Assert.True(r.Authentic);
        Assert.Equal("{\"count\":3}", r.Payload);
    }

    [Fact]
    public void Forged_replayed_and_inconsistent_answers_are_refused()
    {
        var other = Encoding.UTF8.GetBytes("a-different-secret-of-sufficient-length");
        Assert.Equal("bad_signature", Wire.ReadResponse(Answer(200, "{}", other), Secret, Nonce, 200).Code);
        Assert.Equal("bad_signature", Wire.ReadResponse(Answer(200, "{}", nonce: "BBBBBBBBBBBBBBBBBBBBBB"), Secret, Nonce, 200).Code);
        var tampered = JsonText.Object(("v", 1), ("ok", true), ("status", 200), ("payload", "{\"x\":1}"), ("sig", Wire.SignResponse(Secret, Nonce, 200, "{}")));
        Assert.Equal("bad_signature", Wire.ReadResponse(tampered, Secret, Nonce, 200).Code);
        Assert.Equal("bad_response", Wire.ReadResponse(Answer(200, "{}"), Secret, Nonce, 500).Code);
        Assert.Equal("bad_response", Wire.ReadResponse("not json", Secret, Nonce, 200).Code);
        Assert.Equal("bad_response", Wire.ReadResponse("[]", Secret, Nonce, 200).Code);
        Assert.False(Wire.ReadResponse(Answer(200, "{}", other), Secret, Nonce, 200).Authentic);
    }

    [Fact]
    public void Unsigned_answers_are_never_authentic_but_keep_the_code()
    {
        var r = Wire.ReadResponse("{\"v\":1,\"ok\":false,\"status\":401,\"payload\":\"{}\",\"code\":\"unauthorized\"}", Secret, Nonce, 401);
        Assert.False(r.Authentic);
        Assert.Equal("unauthorized", r.Code);
    }

    [Fact]
    public void Constant_time_compare_handles_all_lengths()
    {
        Assert.True(Wire.ConstantTimeEquals("abc", "abc"));
        Assert.False(Wire.ConstantTimeEquals("abc", "abd"));
        Assert.False(Wire.ConstantTimeEquals("abc", "ab"));
        Assert.False(Wire.ConstantTimeEquals("", "a"));
        Assert.True(Wire.ConstantTimeEquals("", ""));
    }
}

public class JsonTests
{
    [Fact]
    public void Parses_every_json_type()
    {
        var o = MiniJson.AsObject(MiniJson.Parse("""{"a":1,"b":-2.5,"c":"x\ny","d":[true,false,null],"e":{"f":"\u00e4\ud83d\ude00"},"g":1e3}"""))!;
        Assert.Equal(1L, o["a"]);
        Assert.Equal(-2.5, o["b"]);
        Assert.Equal("x\ny", o["c"]);
        Assert.Equal(new object?[] { true, false, null }, ((List<object?>)o["d"]!).ToArray());
        Assert.Equal("ä😀", MiniJson.GetString(MiniJson.AsObject(o["e"]), "f"));
        Assert.Equal(1000L, MiniJson.GetLong(o, "g"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\":}")]
    [InlineData("{\"a\" 1}")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1} x")]
    [InlineData("\"unterminated")]
    [InlineData("tru")]
    [InlineData("\"\\q\"")]
    [InlineData("\"\\u12\"")]
    public void Rejects_malformed_json(string text) => Assert.Throws<FormatException>(() => MiniJson.Parse(text));

    [Fact]
    public void Rejects_absurd_nesting() => Assert.Throws<FormatException>(() => MiniJson.Parse(new string('[', 100) + new string(']', 100)));

    [Fact]
    public void Writer_output_is_valid_json_for_hostile_text()
    {
        const string nasty = "quote\" back\\slash\nnew\ttab \u0001 control \u2028 emoji 😀 </script>";
        var json = JsonText.Object(("k", nasty), ("n", 1), ("d", 1.5), ("b", true), ("z", null));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(nasty, doc.RootElement.GetProperty("k").GetString());
        Assert.Equal(nasty, MiniJson.GetString(MiniJson.AsObject(MiniJson.Parse(json)), "k"));
    }

    [Fact]
    public void Values_are_written_in_the_formats_the_api_expects()
    {
        Assert.Equal("\"2026-10-15T12:00:00.0000000Z\"", JsonText.Value(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("\"2026-10-15\"", JsonText.Value(new DateOnly(2026, 10, 15)));
        Assert.Equal("\"AQID\"", JsonText.Value(new byte[] { 1, 2, 3 }));
        Assert.Equal("12.50", JsonText.Value(12.50m));
        Assert.Equal("1.5", JsonText.Value(1.5));
        Assert.Equal("{\"a\":1}", JsonText.Value(new JsonText.Raw("{\"a\":1}")));
        Assert.Throws<ArgumentException>(() => JsonText.Value(double.NaN));
        Assert.Throws<ArgumentException>(() => JsonText.Value(new object()));
    }
}

public class BuilderTests
{
    static void AssertValid(string json) => JsonDocument.Parse(json).Dispose();

    [Fact]
    public void Filters_are_valid_json_and_escape_values()
    {
        var f = F.And(F.Gte("score", 10), F.Or(F.StartsWith("name", "A\"B"), F.Not(F.Eq("banned", true))), F.In("id", 1, 2, 3), F.IsNull("bio", false));
        AssertValid(f);
        using var doc = JsonDocument.Parse(f);
        var first = doc.RootElement.GetProperty("and")[0];
        Assert.Equal("score", first.GetProperty("field").GetString());
        Assert.Equal("gte", first.GetProperty("op").GetString());
        Assert.Equal(10, first.GetProperty("value").GetInt32());
        Assert.Equal("A\"B", doc.RootElement.GetProperty("and")[1].GetProperty("or")[0].GetProperty("value").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("and")[2].GetProperty("value").GetArrayLength());
    }

    [Fact]
    public void Injection_text_stays_inside_a_string_value()
    {
        var f = F.Eq("name", "x\",\"op\":\"eq\"},{\"field\":\"password");
        using var doc = JsonDocument.Parse(f);
        Assert.Equal("eq", doc.RootElement.GetProperty("op").GetString());
        Assert.Equal("name", doc.RootElement.GetProperty("field").GetString());
        Assert.Equal("x\",\"op\":\"eq\"},{\"field\":\"password", doc.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void Schema_builder_matches_the_api_format()
    {
        var schema = S.Schema(S.Table("players", ["id"],
            [S.Int64("id", autoIncrement: true), S.Text("name", 32, unique: true), S.Int32("score", @default: 0), S.LongText("bio", nullable: true, renamedFrom: "about"),
             S.Decimal("balance", 12, 2), S.DateTime("created", defaultNow: true), S.Json("meta")],
            [S.Index("by_score", false, "score")]));
        using var doc = JsonDocument.Parse(schema);
        var table = doc.RootElement.GetProperty("tables")[0];
        Assert.Equal("players", table.GetProperty("name").GetString());
        Assert.Equal("id", table.GetProperty("primaryKey")[0].GetString());
        var cols = table.GetProperty("columns");
        Assert.True(cols[0].GetProperty("autoIncrement").GetBoolean());
        Assert.Equal(32, cols[1].GetProperty("length").GetInt32());
        Assert.True(cols[1].GetProperty("unique").GetBoolean());
        Assert.Equal(0, cols[2].GetProperty("default").GetInt32());
        Assert.Equal("about", cols[3].GetProperty("renamedFrom").GetString());
        Assert.Equal(12, cols[4].GetProperty("precision").GetInt32());
        Assert.Equal("now", cols[5].GetProperty("default").GetString());
        Assert.Equal("by_score", table.GetProperty("indexes")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Rows_and_sort_are_valid()
    {
        AssertValid(Q.Row(("name", "alice"), ("score", 10), ("bio", null)));
        using var doc = JsonDocument.Parse(Q.Sort(("score", true), ("name", false)));
        Assert.Equal("desc", doc.RootElement[0].GetProperty("dir").GetString());
        Assert.Equal("asc", doc.RootElement[1].GetProperty("dir").GetString());
    }
}

public class WriteBehindHelperTests
{
    [Fact]
    public void Immediate_adds_the_durability_field_and_keeps_the_payload_valid()
    {
        var payload = JsonText.Object(("namespace", "demo"), ("table", "t"), ("rows", new JsonText.Raw("[{\"a\":1}]")));
        using var doc = JsonDocument.Parse(Q.Immediate(payload));
        Assert.Equal("immediate", doc.RootElement.GetProperty("durability").GetString());
        Assert.Equal("demo", doc.RootElement.GetProperty("namespace").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("rows")[0].GetProperty("a").GetInt32());
    }

    [Fact]
    public void Immediate_works_on_an_empty_object_and_refuses_anything_else()
    {
        using var doc = JsonDocument.Parse(Q.Immediate("{}"));
        Assert.Equal("immediate", doc.RootElement.GetProperty("durability").GetString());
        Assert.Throws<ArgumentException>(() => Q.Immediate("[1]"));
        Assert.Throws<ArgumentException>(() => Q.Immediate(""));
    }
}
