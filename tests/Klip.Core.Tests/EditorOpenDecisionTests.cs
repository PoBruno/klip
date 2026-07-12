using Klip.Core.Capture;
using Klip.Core.Settings;

namespace Klip.Core.Tests;

/// <summary>Política pura da spec F1: decisão de abrir a captura no editor.</summary>
public class EditorOpenDecisionTests
{
    // RF-F1.01 / RF-F1.05: pedido do modificador OU toggle "sempre editor"
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ShouldOpenEditor_RequestOrAlwaysToggle(bool requested, bool always, bool expected)
    {
        Assert.Equal(expected, EditorOpenDecision.ShouldOpenEditor(requested, always));
    }

    // CA-F1.1: modificador configurado + pressionado + sem debounce pendente abre o editor
    [Theory]
    [InlineData(CaptureEditorModifier.Control)]
    [InlineData(CaptureEditorModifier.Shift)]
    [InlineData(CaptureEditorModifier.Alt)]
    public void IsModifierRequestValid_HeldAfterOverlayOpened(CaptureEditorModifier modifier)
    {
        Assert.True(EditorOpenDecision.IsModifierRequestValid(modifier, modifierDown: true, heldSinceOpen: false));
    }

    // CA-F1.2: sem a tecla pressionada nada muda
    [Fact]
    public void IsModifierRequestValid_KeyNotDown_ReturnsFalse()
    {
        Assert.False(EditorOpenDecision.IsModifierRequestValid(
            CaptureEditorModifier.Control, modifierDown: false, heldSinceOpen: false));
    }

    // RF-F1.02: "Desativado" nunca pede editor, mesmo com a tecla pressionada
    [Fact]
    public void IsModifierRequestValid_Disabled_ReturnsFalse()
    {
        Assert.False(EditorOpenDecision.IsModifierRequestValid(
            CaptureEditorModifier.None, modifierDown: true, heldSinceOpen: false));
    }

    // RF-F1.07 / CA-F1.4: modificador herdado do hotkey de captura é ignorado até soltar
    [Fact]
    public void IsModifierRequestValid_HeldSinceOverlayOpened_ReturnsFalse()
    {
        Assert.False(EditorOpenDecision.IsModifierRequestValid(
            CaptureEditorModifier.Control, modifierDown: true, heldSinceOpen: true));
    }
}
