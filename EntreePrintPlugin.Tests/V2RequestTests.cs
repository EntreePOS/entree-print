using System.Security.Cryptography;
using System.Text;
using EntreePrintPlugin.Services;
using Microsoft.AspNetCore.Http;

namespace EntreePrintPlugin.Tests;

public sealed class V2RequestTests
{
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void Digest_CoversExactUtf8Bytes_IncludingChineseAndWhitespace()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"html\":\"<p>中文 café</p>\"}");
        var request = V2Request.Parse(bytes, Digest(bytes));
        Assert.Equal("<p>中文 café</p>", request.Body.GetProperty("html").GetString());
        var changed = bytes.Concat(new byte[] { 32 }).ToArray();
        Assert.Equal("CHECKSUM_INVALID", Assert.Throws<CommandException>(() => V2Request.Parse(changed, request.Digest)).Code);
    }

    [Theory]
    [InlineData("", "CHECKSUM_MISSING")]
    [InlineData("broken", "CHECKSUM_FORMAT_INVALID")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "CHECKSUM_INVALID")]
    public void InvalidDigest_IsRejected(string digest, string expected) =>
        Assert.Equal(expected, Assert.Throws<CommandException>(() => V2Request.Parse("{}"u8.ToArray(), digest)).Code);

    [Theory]
    [InlineData("{\"printer\":\"A\",\"printer\":\"B\"}")]
    [InlineData("{\"metadata\":{\"station\":\"A\",\"station\":\"B\"}}")]
    [InlineData("{\"content\":[{\"type\":\"html\",\"type\":\"image\"}]}")]
    [InlineData("{\"incomplete\":")]
    [InlineData("[]")]
    public void ValidDigest_DoesNotPermitMalformedSchemaOrDuplicateKeys(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Equal("REQUEST_INVALID", Assert.Throws<CommandException>(() => V2Request.Parse(bytes, Digest(bytes))).Code);
    }

    [Fact]
    public void InvalidUtf8_IsRejectedRatherThanReplaced()
    {
        byte[] bytes = [123, 34, 120, 34, 58, 34, 255, 34, 125];
        Assert.Equal("REQUEST_INVALID", Assert.Throws<CommandException>(() => V2Request.Parse(bytes, Digest(bytes))).Code);
    }

    [Fact]
    public async Task StreamLimitAndTruncatedUpload_FailBeforeParsing()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(new byte[V2Request.MaxBytes + 1]);
        var large = await Assert.ThrowsAsync<CommandException>(() => V2Request.ReadAsync(context.Request, CancellationToken.None));
        Assert.Equal("REQUEST_TOO_LARGE", large.Code);
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        context.Request.ContentLength = 10;
        var incomplete = await Assert.ThrowsAsync<CommandException>(() => V2Request.ReadAsync(context.Request, CancellationToken.None));
        Assert.Equal("REQUEST_INCOMPLETE", incomplete.Code);
    }

    [Fact]
    public void CanonicalMetadata_IgnoresPropertyOrder_ButNotValues()
    {
        static string Normalize(string json)
        { var bytes = Encoding.UTF8.GetBytes(json); return V2Request.CanonicalJson(V2Request.Parse(bytes, Digest(bytes)).Body); }
        Assert.Equal(Normalize("{\"station\":\"A\",\"orderID\":\"1\"}"), Normalize("{\"orderID\":\"1\",\"station\":\"A\"}"));
        Assert.NotEqual(Normalize("{\"station\":\"A\"}"), Normalize("{\"station\":\"B\"}"));
    }
}
