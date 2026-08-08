using System.Runtime.CompilerServices;

namespace Klip.Core.Input;

/// <summary>
/// RF-P1.03: bitmap de 256 bits (4 x ulong) para decidir em O(1), dentro do
/// callback do hook, se uma virtual key deve ser engolida. Sem lock, sem
/// alocacao e sem branch dependente do tamanho do conjunto.
/// </summary>
public sealed class VirtualKeyMask
{
    // Palavra N cobre as virtual keys [N*64, N*64+63].
    private ulong _w0;
    private ulong _w1;
    private ulong _w2;
    private ulong _w3;

    /// <summary>Substitui o conjunto inteiro de forma atomica do ponto de vista do leitor.</summary>
    public void Set(ReadOnlySpan<int> virtualKeys)
    {
        // Monta fora dos campos: o leitor nunca observa um estado intermediario
        // de construcao, apenas a publicacao palavra a palavra logo abaixo.
        ulong w0 = 0, w1 = 0, w2 = 0, w3 = 0;

        for (int i = 0; i < virtualKeys.Length; i++)
        {
            int vk = virtualKeys[i];
            if ((uint)vk > 255u)
                continue; // fora de 0..255: ignorado em silencio, nao e erro do chamador

            ulong bit = 1UL << (vk & 63);
            switch (vk >> 6)
            {
                case 0: w0 |= bit; break;
                case 1: w1 |= bit; break;
                case 2: w2 |= bit; break;
                default: w3 |= bit; break;
            }
        }

        Publish(w0, w1, w2, w3);
    }

    /// <summary>Esvazia o conjunto: nenhuma tecla passa a ser engolida.</summary>
    public void Clear() => Publish(0, 0, 0, 0);

    /// <summary>Leitura O(1), lock-free, segura para chamar de dentro de um callback de hook.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(int virtualKey)
    {
        // Comparacao unsigned cobre negativos e >= 256 num unico branch.
        if ((uint)virtualKey > 255u)
            return false;

        ulong bit = 1UL << (virtualKey & 63);
        return (virtualKey >> 6) switch
        {
            0 => (Volatile.Read(ref _w0) & bit) != 0,
            1 => (Volatile.Read(ref _w1) & bit) != 0,
            2 => (Volatile.Read(ref _w2) & bit) != 0,
            _ => (Volatile.Read(ref _w3) & bit) != 0,
        };
    }

    private void Publish(ulong w0, ulong w1, ulong w2, ulong w3)
    {
        Volatile.Write(ref _w0, w0);
        Volatile.Write(ref _w1, w1);
        Volatile.Write(ref _w2, w2);
        Volatile.Write(ref _w3, w3);
    }
}
