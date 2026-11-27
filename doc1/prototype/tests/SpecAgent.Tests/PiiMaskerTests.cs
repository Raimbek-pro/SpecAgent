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
    public async Task Masks_bank_pii_and_round_trips()
    {
        const string text = "ИИН 900101300037, карта 4111 1111 1111 1111, счёт KZ86125KZT5004100100, тел. +7 701 123 45 67, api_key=sk_live_abcdef123";
        var masked = await Masker.MaskAsync("s1", text);

        Assert.DoesNotContain("900101300037", masked);
        Assert.DoesNotContain("4111", masked);
        Assert.DoesNotContain("KZ86125KZT5004100100", masked);
        Assert.DoesNotContain("701 123", masked);
        Assert.DoesNotContain("sk_live", masked);
        Assert.Contains("[IIN_1]", masked);
        Assert.Contains("[PAN_1]", masked);

        Assert.Equal(text, await Masker.DemaskAsync("s1", masked));
    }

    [Fact]
    public async Task Same_value_same_token_and_sessions_are_isolated()
    {
        var a = await Masker.MaskAsync("s1", "900101300037 и снова 900101300037");
        Assert.Equal("[IIN_1] и снова [IIN_1]", a);
        Assert.Equal("[IIN_1]", await Masker.DemaskAsync("other-session", "[IIN_1]"));
    }

    [Fact]
    public async Task Does_not_mask_random_numbers_that_fail_checksums()
    {
        const string text = "Лимит 100000000000 тенге, заказ 1234567890123";
        Assert.Equal(text, await Masker.MaskAsync("s1", text));
    }

    [Theory]
    [InlineData("+1 (415) 555-0199")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("+998 90 123 45 67")]
    [InlineData("+7 (701) 123-45-67")]
    [InlineData("8-777-123-45-67")]
    public async Task Masks_international_and_cis_phone_numbers_and_round_trips(string phone)
    {
        var input = $"Контакт клиента: {phone} (основной)";
        var masked = await Masker.MaskAsync("s-intl", input);

        Assert.DoesNotContain(phone, masked);
        Assert.Contains("[PHONE_1]", masked);
        Assert.Equal(input, await Masker.DemaskAsync("s-intl", masked));
    }

    [Fact]
    public async Task Masks_labeled_synthetic_iin_and_respects_enabled_kinds_toggle()
    {
        var masker = Masker;
        const string text = "ИИН тестового клиента: 950115350123, тел. +7 701 555 44 33";

        var maskedAll = await masker.MaskAsync("s-labeled", text);
        Assert.DoesNotContain("950115350123", maskedAll);
        Assert.Contains("[IIN_1]", maskedAll);
        Assert.Contains("[PHONE_1]", maskedAll);
        Assert.Equal(text, await masker.DemaskAsync("s-labeled", maskedAll));

        // Disabling PHONE keeps phone unmasked while still masking labeled IIN.
        masker.EnabledKinds = new HashSet<string>(["IIN"], StringComparer.OrdinalIgnoreCase);
        var maskedIinOnly = await masker.MaskAsync("s-iin-only", text);
        Assert.Contains("[IIN_1]", maskedIinOnly);
        Assert.Contains("+7 701 555 44 33", maskedIinOnly);
    }
}
