using DriveGram.Tray.Setup;

namespace DriveGram.Tests;

/// <summary>
/// The phone field of "add an account". People write their number in every shape there is, and Telegram
/// must get the one international form — a wrong number means a code that never arrives, or is sent to
/// somebody else.
/// </summary>
public sealed class PhoneNumberTests
{
    static Country Of(string iso) => Countries.Find(iso) ?? throw new Xunit.Sdk.XunitException($"no {iso}");
    static string Local(string typed, string iso) => PhoneBox.LocalDigits(typed, Of(iso));

    [Theory]
    // written as it is dialled at home, with the leading zero and whatever separators
    [InlineData("050-123-4567", "501234567")]
    [InlineData("050 123 4567", "501234567")]
    [InlineData("0501234567", "501234567")]
    // already without it
    [InlineData("50-123-4567", "501234567")]
    // the whole international number typed into the local box (used to come out as +972972501234567)
    [InlineData("+972 50 123 4567", "501234567")]
    [InlineData("972501234567", "501234567")]
    // the old way of writing "+", with and without the home zero after it
    [InlineData("00972501234567", "501234567")]
    [InlineData("00972-050-1234567", "501234567")]
    // nothing typed
    [InlineData("", "")]
    [InlineData("   ", "")]
    // letters and signs are not digits
    [InlineData("tel: 050-123-4567", "501234567")]
    public void ANumberIsReadAsItWasMeant(string typed, string expected) =>
        Assert.Equal(expected, Local(typed, "IL"));

    [Fact]
    public void ANumberThatBeginsWithItsOwnCountrysCodeIsLeftWhole()
    {
        // every Kazakh number begins with 7, and the country's code IS +7: taking it off would send the
        // code to a different number altogether
        Assert.Equal("7012345678", Local("701 234 5678", "KZ"));
        Assert.Equal("7012345678", Local("+7 701 234 5678", "KZ"));
        // Italian mobiles begin with 39, like the country's code
        Assert.Equal("3912345678", Local("391 234 5678", "IT"));
        // and a country whose numbers are shorter than its own code is long (the Faroes: +298, 6 digits)
        Assert.Equal("298123", Local("298123", "FO"));
    }

    [Fact]
    public void ItalyKeepsTheZeroItIsDialledWith()
    {
        // Italy is dialled from abroad WITH the zero of its city codes: +39 06 1234 5678
        Assert.Equal("0612345678", Local("06 1234 5678", "IT"));
        Assert.Equal("0612345678", Local("+39 06 1234 5678", "IT"));
    }

    [Fact]
    public void RussiaAndKazakhstanMayWriteTheirEightInstead()
    {
        // at home a long-distance call there starts with 8 where the rest of the world writes +7
        Assert.Equal("9161234567", Local("8 916 123 4567", "RU"));
        Assert.Equal("7012345678", Local("8 701 234 5678", "KZ"));
    }

    [Fact]
    public void OnlyTheFirstZeroIsTheWayItIsDialledAtHome()
    {
        // the zeros inside the number are part of it; only the one in front is the home prefix
        Assert.Equal("500123456", Local("050-0123456", "IL"));
        Assert.Equal("5012345670", Local("0501234567-0", "IL"));
    }

    [Fact]
    public void ANumberCannotGrowBeyondWhatAPhoneNumberIs()
    {
        // E.164: 15 digits in all, the calling code included
        Assert.Equal(15 - 3, Local(new string('5', 40), "IL").Length);
        Assert.Equal(15 - 1, Local(new string('5', 40), "US").Length);
    }

    [Theory]
    [InlineData("+972501234567", "IL")]
    [InlineData("00972501234567", "IL")]
    [InlineData("972501234567", "IL")]
    [InlineData("+447911123456", "GB")] // not Guernsey, which shares +44 and comes first in the alphabet
    [InlineData("+12125551234", "US")] // not one of the other twenty countries on +1
    [InlineData("+12645551234", "AI")] // but a longer code wins: +1264 is Anguilla
    [InlineData("+79161234567", "RU")] // +7 is Russia, not Kazakhstan
    [InlineData("+390612345678", "IT")]
    public void TheCountryOfATypedNumberIsTheOneMeant(string number, string iso)
    {
        var country = Countries.OfNumber(number);
        Assert.NotNull(country);
        Assert.Equal(iso, country!.Iso);
    }

    [Fact]
    public void NothingTypedBelongsToNoCountry()
    {
        Assert.Null(Countries.OfNumber(""));
        Assert.Null(Countries.OfNumber("+"));
    }

    [Fact]
    public void EveryCountryHasAFlagInThePicture()
    {
        // the flags are one picture with a cell per ISO code: a country with no cell would show a blank
        foreach (var c in Countries.All)
            Assert.True(Flags.Has(c.Iso), $"no flag for {c.Iso} ({c.Name})");
    }

    [Fact]
    public void NoTwoCountriesAreTheSameCountry()
    {
        var twice = Countries.All.GroupBy(c => c.Iso).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(twice);
    }

    [Fact]
    public void ACallingCodeIsDigitsOnly()
    {
        foreach (var c in Countries.All)
        {
            Assert.All(c.Dial, d => Assert.True(char.IsAsciiDigit(d), $"{c.Iso}: \"{c.Dial}\""));
            Assert.InRange(c.Dial.Length, 1, 4);
        }
    }
}
