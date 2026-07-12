namespace Klip.Core.Recording;

/// <summary>
/// Regiao de gravacao em pixels FISICOS, em coordenadas do desktop virtual
/// (origem = canto superior esquerdo do monitor primario; monitores a esquerda
/// tem coordenadas negativas). Limitada a um unico monitor no MVP (RF-F2.09).
/// </summary>
/// <param name="Left">Borda esquerda, px fisicos, coords de desktop virtual.</param>
/// <param name="Top">Borda superior, px fisicos, coords de desktop virtual.</param>
/// <param name="Width">Largura em px fisicos (o motor arredonda para PAR, RF-F2.03).</param>
/// <param name="Height">Altura em px fisicos (o motor arredonda para PAR, RF-F2.03).</param>
public readonly record struct RecordingRegion(int Left, int Top, int Width, int Height);
