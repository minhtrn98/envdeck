using static EnvDeck.Core.Tests.TestDeck;

namespace EnvDeck.Core.Tests;

public class BundleCryptoTests
{
    const string Password = "correct horse battery";

    static string Bundle(TestDeck t, string name = "bundle.envdeck") => Path.Combine(t.Root, name);

    static TestDeck DeckWithProfile(string secret = "S3cr3t-value")
    {
        var t = new TestDeck();
        t.Save("p", Var("DB_PASSWORD", secret), Var("JAVA_HOME", @"C:\jdk"));
        return t;
    }

    [Fact]
    public void An_encrypted_bundle_holds_nothing_readable_and_imports_with_the_password()
    {
        using var from = DeckWithProfile();
        using var to = new TestDeck();
        var file = Bundle(from);

        from.Service.Export(file, null, withHistory: true, withBackups: false, Password);

        Assert.True(BundleCrypto.IsEncrypted(file));
        var bytes = File.ReadAllBytes(file);
        Assert.False(Contains(bytes, "S3cr3t-value"u8), "secret visible in the file");
        Assert.False(Contains(bytes, "DB_PASSWORD"u8), "variable name visible in the file");
        Assert.False(Contains(bytes, "PK"u8), "zip structure visible in the file");

        var result = to.Service.Import(file, overwrite: false, Password);

        Assert.Equal(["p"], result.Imported);
        Assert.Equal("S3cr3t-value", to.Service.Profiles.Get("p").Variables.Single(v => v.Name == "DB_PASSWORD").Value);
    }

    [Fact]
    public void Without_the_password_or_with_a_wrong_one_nothing_is_imported()
    {
        using var from = DeckWithProfile();
        using var to = new TestDeck();
        var file = Bundle(from);
        from.Service.Export(file, null, true, false, Password);

        Assert.Throws<PasswordRequiredException>(() => to.Service.Import(file, false));
        Assert.Throws<WrongPasswordException>(() => to.Service.Import(file, false, "not the password"));
        Assert.False(to.Service.Profiles.Exists("p"));
    }

    [Theory]
    [InlineData(9)]   // iteration count
    [InlineData(20)]  // salt
    [InlineData(40)]  // authentication tag
    [InlineData(100)] // ciphertext
    public void A_modified_file_is_rejected(int offset)
    {
        using var from = DeckWithProfile();
        using var to = new TestDeck();
        var file = Bundle(from);
        from.Service.Export(file, null, true, false, Password);
        var bytes = File.ReadAllBytes(file);
        bytes[offset] ^= 0x01;
        File.WriteAllBytes(file, bytes);

        Assert.ThrowsAny<DeckException>(() => to.Service.Import(file, false, Password));
        Assert.False(to.Service.Profiles.Exists("p"));
    }

    [Fact]
    public void Two_exports_of_the_same_data_differ_because_salt_and_nonce_are_random()
    {
        using var from = DeckWithProfile();
        var a = Bundle(from, "a.envdeck");
        var b = Bundle(from, "b.envdeck");
        from.Service.Export(a, null, true, false, Password);
        from.Service.Export(b, null, true, false, Password);

        Assert.NotEqual(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void Short_passwords_are_refused_and_no_file_is_left_behind()
    {
        using var from = DeckWithProfile();
        var file = Bundle(from);

        Assert.Throws<DeckException>(() => from.Service.Export(file, null, true, false, "short"));

        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void A_plain_bundle_still_imports_without_a_password()
    {
        using var from = DeckWithProfile();
        using var to = new TestDeck();
        var file = Bundle(from, "plain.zip");
        from.Service.Export(file, null, true, false);

        Assert.False(BundleCrypto.IsEncrypted(file));
        Assert.Equal(["p"], to.Service.Import(file, overwrite: false).Imported);
    }

    [Fact]
    public void The_same_accented_password_typed_composed_or_decomposed_matches()
    {
        using var from = DeckWithProfile();
        using var to = new TestDeck();
        var file = Bundle(from);
        var composed = "m\u1EADt kh\u1EA9u m\u1EA1nh";                 // ậ ẩ ạ as single code points
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);

        from.Service.Export(file, null, true, false, composed);

        Assert.Equal(["p"], to.Service.Import(file, false, decomposed).Imported);
    }

    static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
