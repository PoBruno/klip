using Klip.Core.Clipboard;

namespace Klip.Core.Tests;

public class SensitiveDataDetectorTests
{
    [Fact]
    public void Find_Email_IsDetected()
    {
        var hits = SensitiveDataDetector.Find("contact me at joao.silva@example.com please");
        Assert.Contains("joao.silva@example.com", hits);
    }

    [Fact]
    public void Find_ValidCreditCard_IsDetected()
    {
        // 4111 1111 1111 1111 is a well-known Luhn-valid test number
        var hits = SensitiveDataDetector.Find("card: 4111 1111 1111 1111");
        Assert.Contains(hits, h => h.Replace(" ", "") == "4111111111111111");
    }

    [Fact]
    public void Find_ShortDigitRun_NotFlaggedAsLongNumber()
    {
        // too short to be a card or phone
        var hits = SensitiveDataDetector.Find("code 1234 done");
        Assert.DoesNotContain(hits, h => h.Replace(" ", "").Length >= 8);
    }

    [Fact]
    public void Find_Cpf_IsDetected()
    {
        var hits = SensitiveDataDetector.Find("CPF 123.456.789-09 do cliente");
        Assert.Contains("123.456.789-09", hits);
    }

    [Fact]
    public void Find_IpAddress_IsDetected()
    {
        var hits = SensitiveDataDetector.Find("server at 192.168.0.1 is down");
        Assert.Contains("192.168.0.1", hits);
    }

    [Fact]
    public void Find_PlainText_HasNoFalsePositives()
    {
        var hits = SensitiveDataDetector.Find("the quick brown fox jumps over the lazy dog");
        Assert.Empty(hits);
    }

    [Fact]
    public void IsSensitiveWord_Email_True()
    {
        Assert.True(SensitiveDataDetector.IsSensitiveWord("me@site.org"));
    }

    [Fact]
    public void IsSensitiveWord_DigitGroup_True()
    {
        // a chunk of a phone/card number the OCR split off
        Assert.True(SensitiveDataDetector.IsSensitiveWord("5678"));
    }

    [Fact]
    public void IsSensitiveWord_NormalWord_False()
    {
        Assert.False(SensitiveDataDetector.IsSensitiveWord("hello"));
    }
}
