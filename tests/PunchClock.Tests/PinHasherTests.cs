using PunchClock.Core.Security;

namespace PunchClock.Tests;

public sealed class PinHasherTests
{
    private readonly Pbkdf2PinHasher _hasher = TestDatabase.FastHasher;

    [Fact]
    public void Verifies_the_pin_it_hashed()
    {
        var hash = _hasher.Hash("4821");

        Assert.True(_hasher.Verify("4821", hash));
        Assert.False(_hasher.Verify("4822", hash));
    }

    [Fact]
    public void Leading_zeros_are_significant()
    {
        var hash = _hasher.Hash("0123");

        Assert.True(_hasher.Verify("0123", hash));
        Assert.False(_hasher.Verify("123", hash));
    }

    [Fact]
    public void Salts_every_hash()
    {
        Assert.NotEqual(_hasher.Hash("1234"), _hasher.Hash("1234"));
    }

    [Fact]
    public void Default_encoding_names_scheme_and_cost()
    {
        var hash = new Pbkdf2PinHasher().Hash("1234");

        var parts = hash.Split('$');
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal("600000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.DoesNotContain("1234", hash.Replace(parts[2], "").Replace(parts[3], ""));
    }

    [Fact]
    public void Verifies_with_the_cost_stored_in_the_hash()
    {
        var oldHash = new Pbkdf2PinHasher(iterations: 500).Hash("1234");

        Assert.True(_hasher.Verify("1234", oldHash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("pbkdf2-sha256$1000$abc")]
    [InlineData("pbkdf2-sha256$0$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$-5$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$not-base64!$AAAA")]
    [InlineData("pbkdf2-sha256$1000$$")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        Assert.False(_hasher.Verify("1234", encoded));
    }

    [Theory]
    [InlineData("123", true)]
    [InlineData("000000", true)]
    [InlineData("12", false)]
    [InlineData("1234567", false)]
    [InlineData("12a4", false)]
    [InlineData(" 1234", false)]
    [InlineData("１２３４", false)] // full-width digits pass char.IsDigit but are not PIN digits
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_policy(string? pin, bool valid)
    {
        Assert.Equal(valid, PinPolicy.Validate(pin) is null);
    }
}
