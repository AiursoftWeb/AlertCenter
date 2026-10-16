using System.Text;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class WebhookConfigurationProtectorTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    [DataRow("not-base64")]
    public void MissingOrMalformedKeysDisableProtection(string? key)
    {
        var protector = Protector(key);

        Assert.IsFalse(protector.IsAvailable);
        Assert.IsFalse(protector.TryProtect("test headers", out var encrypted));
        Assert.IsNull(encrypted);
        Assert.IsFalse(protector.TryUnprotect("v1:AA==", out var decrypted));
        Assert.IsNull(decrypted);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(31)]
    [DataRow(33)]
    public void RejectsDecodedKeysWithIncorrectLength(int length)
    {
        var protector = Protector(Convert.ToBase64String(new byte[length]));

        Assert.IsFalse(protector.IsAvailable);
        Assert.IsFalse(protector.TryProtect("test headers", out var encrypted));
        Assert.IsNull(encrypted);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow(null, true)]
    [DataRow("", true)]
    public void EmptyConfigurationNeedsNoKeyAndRemainsNull(string? value, bool hasKey)
    {
        var protector = Protector(hasKey ? Convert.ToBase64String(Key) : null);

        Assert.IsTrue(protector.TryProtect(value, out var encrypted));
        Assert.IsNull(encrypted);
        Assert.IsTrue(protector.TryUnprotect(value, out var decrypted));
        Assert.IsNull(decrypted);
    }

    [TestMethod]
    [DataRow("plain text")]
    [DataRow(" \t\r\n")]
    [DataRow("值班通知 🔔 café\n第二行")]
    [DataRow("{\"X-Source\":\"alert-center\",\"X-Quote\":\"\\\"\\\\\"}")]
    public void RoundTripPreservesExactUtf8AcrossInstances(string value)
    {
        var key = Convert.ToBase64String(Key);
        var writer = Protector(key);
        var reader = Protector(key);

        Assert.IsTrue(writer.IsAvailable);
        Assert.IsTrue(writer.TryProtect(value, out var encrypted));
        Assert.IsNotNull(encrypted);
        StringAssert.StartsWith(encrypted, "v1:");
        Assert.AreEqual(12 + 16 + Encoding.UTF8.GetByteCount(value), Convert.FromBase64String(encrypted[3..]).Length);
        Assert.IsTrue(reader.TryUnprotect(encrypted, out var decrypted));
        Assert.AreEqual(value, decrypted);
    }

    [TestMethod]
    public void RepeatedProtectionUsesFreshNoncesAndBothValuesRemainReadable()
    {
        var protector = Protector(Convert.ToBase64String(Key));
        const string value = "{\"X-Source\":\"alert-center\"}";

        Assert.IsTrue(protector.TryProtect(value, out var first));
        Assert.IsTrue(protector.TryProtect(value, out var second));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        var firstBytes = Convert.FromBase64String(first[3..]);
        var secondBytes = Convert.FromBase64String(second[3..]);
        Assert.IsFalse(firstBytes.AsSpan(0, 12).SequenceEqual(secondBytes.AsSpan(0, 12)));
        Assert.IsTrue(protector.TryUnprotect(first, out var firstPlaintext));
        Assert.IsTrue(protector.TryUnprotect(second, out var secondPlaintext));
        Assert.AreEqual(value, firstPlaintext);
        Assert.AreEqual(value, secondPlaintext);
    }

    [TestMethod]
    [DataRow("plain text")]
    [DataRow("v2:AA==")]
    [DataRow("V1:AA==")]
    [DataRow("v1:")]
    [DataRow("v1:not-base64")]
    [DataRow("v1:AA==")]
    public void InvalidEnvelopesReturnFailureWithoutPlaintext(string encrypted)
    {
        var protector = Protector(Convert.ToBase64String(Key));

        Assert.IsFalse(protector.TryUnprotect(encrypted, out var decrypted));
        Assert.IsNull(decrypted);
    }

    [TestMethod]
    [DataRow(27)]
    [DataRow(28)]
    [DataRow(29)]
    public void TruncatedOrUnauthenticatedBinaryEnvelopesAreRejected(int length)
    {
        var protector = Protector(Convert.ToBase64String(Key));
        var encrypted = "v1:" + Convert.ToBase64String(new byte[length]);

        Assert.IsFalse(protector.TryUnprotect(encrypted, out var decrypted));
        Assert.IsNull(decrypted);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(27)]
    [DataRow(28)]
    public void ChangingNonceTagOrCiphertextNeverReleasesPlaintext(int offset)
    {
        var protector = Protector(Convert.ToBase64String(Key));
        Assert.IsTrue(protector.TryProtect("test headers", out var encrypted));
        Assert.IsNotNull(encrypted);
        var bytes = Convert.FromBase64String(encrypted[3..]);
        bytes[offset] ^= 1;

        Assert.IsFalse(protector.TryUnprotect("v1:" + Convert.ToBase64String(bytes), out var decrypted));
        Assert.IsNull(decrypted);
    }

    [TestMethod]
    public void AnotherKeyCannotDecryptButTheOriginalKeyStillCan()
    {
        var protector = Protector(Convert.ToBase64String(Key));
        Assert.IsTrue(protector.TryProtect("test headers", out var encrypted));
        var otherKey = Key.ToArray();
        otherKey[0] ^= 1;
        var otherProtector = Protector(Convert.ToBase64String(otherKey));

        Assert.IsTrue(otherProtector.IsAvailable);
        Assert.IsFalse(otherProtector.TryUnprotect(encrypted, out var rejected));
        Assert.IsNull(rejected);
        Assert.IsTrue(protector.TryUnprotect(encrypted, out var decrypted));
        Assert.AreEqual("test headers", decrypted);
    }

    private static WebhookConfigurationProtector Protector(string? key) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Webhook:HeaderProtectionKey"] = key }).Build());
}
