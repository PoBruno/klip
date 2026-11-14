namespace Klip.Core.Input;

/// <summary>Evento de input normalizado que atravessa do hook para o worker.</summary>
public struct InputEvent
{
    public uint Message;   // WM_KEYDOWN, WM_LBUTTONDOWN, KlipInputMessages.CtrlV, ...
    public int A;          // vkCode (teclado) ou x (mouse)
    public int B;          // scanCode (teclado) ou y (mouse)
    public uint Time;      // timestamp do hook
}

/// <summary>Mensagens sinteticas do Klip, fora da faixa WM_* do Windows.</summary>
public static class KlipInputMessages
{
    /// <summary>Ctrl+V detectado com a fila de colagem armada.</summary>
    public const uint CtrlV = 0x7F01;

    /// <summary>Esc with the paste queue armed and the history panel closed.</summary>
    public const uint QueueEscape = 0x7F02;
}
