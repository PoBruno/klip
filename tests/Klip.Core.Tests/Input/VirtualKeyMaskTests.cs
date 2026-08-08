using Klip.Core.Input;

namespace Klip.Core.Tests.Input;

/// <summary>RF-P1.03: bitmap de 256 bits para decidir "engolir esta tecla?" em O(1).</summary>
public class VirtualKeyMaskTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(255)]
    public void Contains_OnEmptyMask_ReturnsFalse(int virtualKey)
    {
        var mask = new VirtualKeyMask();

        Assert.False(mask.Contains(virtualKey));
    }

    [Fact]
    public void Set_MarksExactlyTheGivenKeys()
    {
        var mask = new VirtualKeyMask();

        mask.Set([0x1B, 0x0D, 0xFF]); // VK_ESCAPE, VK_RETURN, VK_OEM_CLEAR

        Assert.True(mask.Contains(0x1B));
        Assert.True(mask.Contains(0x0D));
        Assert.True(mask.Contains(0xFF));
        Assert.False(mask.Contains(0x1C));
    }

    [Fact]
    public void Set_CoversEveryWordOfTheBitmap()
    {
        var mask = new VirtualKeyMask();

        // Uma chave por palavra de 64 bits, incluindo as bordas.
        mask.Set([0, 63, 64, 127, 128, 191, 192, 255]);

        foreach (int vk in new[] { 0, 63, 64, 127, 128, 191, 192, 255 })
            Assert.True(mask.Contains(vk), $"vk {vk} deveria estar no conjunto");

        foreach (int vk in new[] { 1, 62, 65, 126, 129, 190, 193, 254 })
            Assert.False(mask.Contains(vk), $"vk {vk} nao deveria estar no conjunto");
    }

    [Fact]
    public void Set_ReplacesPreviousSetInsteadOfMerging()
    {
        var mask = new VirtualKeyMask();

        mask.Set([0x41, 0x42]);
        mask.Set([0x43]);

        Assert.True(mask.Contains(0x43));
        Assert.False(mask.Contains(0x41));
        Assert.False(mask.Contains(0x42));
    }

    [Fact]
    public void Set_WithEmptySpan_ClearsEverything()
    {
        var mask = new VirtualKeyMask();
        mask.Set([0x10, 0x90]);

        mask.Set(ReadOnlySpan<int>.Empty);

        Assert.False(mask.Contains(0x10));
        Assert.False(mask.Contains(0x90));
    }

    [Fact]
    public void Clear_RemovesAllKeys()
    {
        var mask = new VirtualKeyMask();
        mask.Set([0, 64, 128, 192, 255]);

        mask.Clear();

        foreach (int vk in new[] { 0, 64, 128, 192, 255 })
            Assert.False(mask.Contains(vk));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Contains_OutOfRange_ReturnsFalseWithoutThrowing(int virtualKey)
    {
        var mask = new VirtualKeyMask();
        mask.Set([0x1B]);

        Assert.False(mask.Contains(virtualKey));
    }

    [Fact]
    public void Set_IgnoresOutOfRangeKeysSilently()
    {
        var mask = new VirtualKeyMask();

        // Nao deve lancar: entrada vinda de configuracao pode conter lixo.
        mask.Set([-1, 256, int.MaxValue, int.MinValue, 0x1B]);

        Assert.True(mask.Contains(0x1B));
        Assert.False(mask.Contains(-1));
        Assert.False(mask.Contains(256));
        Assert.False(mask.Contains(int.MaxValue));
    }
}
