using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ProfilePinTests
{
    [Fact]
    public void CompletesAtFourDigitsAndIgnoresFurtherInput()
    {
        var pin = new ProfilePin();
        foreach (var digit in "0123")
        {
            Assert.False(pin.IsComplete);
            Assert.True(pin.Append(digit));
        }

        Assert.True(pin.IsComplete);
        Assert.False(pin.Append('4'));
        Assert.Equal("0123", pin.Digits);
    }

    [Fact]
    public void BackspaceDeletesOneDigitAndIsSafeWhenEmpty()
    {
        var pin = new ProfilePin();
        pin.Delete();
        pin.Append('1');
        pin.Append('2');
        pin.Delete();
        Assert.Equal("1", pin.Digits);
        pin.Delete();
        pin.Delete();
        Assert.Empty(pin.Digits);
    }

    [Fact]
    public void WrongPinCanBeClearedForAnotherTry()
    {
        var pin = new ProfilePin();
        foreach (var digit in "1234")
        {
            pin.Append(digit);
        }

        pin.Clear();
        Assert.False(pin.IsComplete);
        Assert.True(pin.Append('5'));
        Assert.Equal("5", pin.Digits);
    }

    [Theory]
    [InlineData('a')]
    [InlineData(' ')]
    [InlineData('١')]
    public void OnlyAcceptsAsciiDigits(char digit)
    {
        var pin = new ProfilePin();
        Assert.False(pin.Append(digit));
        Assert.Empty(pin.Digits);
    }
}
