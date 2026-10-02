using PunchClock.Core.Security;

namespace PunchClock.Tests;

public class PinHasherTests
{
    // Low work factor keeps the suite fast; the format and logic are identical.
    private readonly Pbkdf2PinHasher _hasher = new(iterations: 1_000);

    [Fact]
    public void Hash_round_trips()
    {
        var hash = _hasher.Hash("4821");

        Assert.True(_hasher.Verify("4821", hash));
        Assert.False(_hasher.Verify("4822", hash));
    }

    [Fact]
    public void Hash_does_not_contain_the_pin_and_is_salted()
    {
        var first = _hasher.Hash("123456");
        var second = _hasher.Hash("123456");

        Assert.DoesNotContain("123456", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.StartsWith("pbkdf2-sha256$1000$", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_zeros_are_significant()
    {
        var hash = _hasher.Hash("0123");

        Assert.True(_hasher.Verify("0123", hash));
        Assert.False(_hasher.Verify("123", hash));
    }

    [Fact]
    public void Verify_uses_iterations_stored_in_the_hash()
    {
        var hash = new Pbkdf2PinHasher(iterations: 2_000).Hash("9999");

        Assert.True(_hasher.Verify("9999", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("md5$1$AA==$AA==")]
    [InlineData("pbkdf2-sha256$x$AA==$AA==")]
    [InlineData("pbkdf2-sha256$1000$***$AA==")]
    public void Verify_rejects_malformed_hashes(string encoded) =>
        Assert.False(_hasher.Verify("1234", encoded));

    [Theory]
    [InlineData("12")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    [InlineData(" 1234")]
    [InlineData("１２３４")] // full-width digits
    public void Hash_rejects_pins_outside_policy(string pin) =>
        Assert.Throws<ArgumentException>(() => _hasher.Hash(pin));

    [Theory]
    [InlineData("123")]
    [InlineData("12345678")]
    public void Policy_accepts_legacy_and_longer_pins(string pin) =>
        Assert.True(PinPolicy.IsValid(pin));
}
