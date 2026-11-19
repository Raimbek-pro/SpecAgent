using SpecAgent.Ingestion.Pii;

namespace SpecAgent.Tests;

public class PiiMaskerTests
{
    private readonly InMemoryPiiVault _vault = new();
    private PiiMasker Masker => new(_vault);

    [Theory]
    [InlineData("900101300037", true)]
    [InlineData("900101300038", false)]
    [InlineData("123456789012", false)]
    public void Iin_checksum(string iin, bool valid) => Assert.Equal(valid, PiiMasker.IsValidIin(iin));

    [Fact]
    public void Masks_bank_pii_and_round_trips()
    {
        const string text = "ИИН 900101300037, карта 4111 1111 1111 1111, счёт KZ86125KZT5004100100, тел. +7 701 123 45 67, api_key=sk_live_abcdef123";
        var masked = Masker.Mask("s1", text);

        Assert.DoesNotContain("900101300037", masked);
        Assert.DoesNotContain("4111", masked);
        Assert.DoesNotContain("KZ86125KZT5004100100", masked);
        Assert.DoesNotContain("701 123", masked);
        Assert.DoesNotContain("sk_live", masked);
        Assert.Contains("[IIN_1]", masked);
        Assert.Contains("[PAN_1]", masked);

        Assert.Equal(text, Masker.Demask("s1", masked));
    }

    [Fact]
    public void Same_value_same_token_and_sessions_are_isolated()
    {
        var a = Masker.Mask("s1", "900101300037 и снова 900101300037");
        Assert.Equal("[IIN_1] и снова [IIN_1]", a);
        Assert.Equal("[IIN_1]", Masker.Demask("other-session", "[IIN_1]"));
    }

    [Fact]
    public void Does_not_mask_random_numbers_that_fail_checksums()
    {
        const string text = "Лимит 100000000000 тенге, заказ 1234567890123";
        Assert.Equal(text, Masker.Mask("s1", text));
    }
}
