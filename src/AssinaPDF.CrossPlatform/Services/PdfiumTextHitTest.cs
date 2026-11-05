using System;
using System.Collections.Generic;
using System.Linq;

namespace AssinaPDF.MacPrototype.Services;

/// <summary>
/// Localiza o caractere PDF mais próximo de um ponto informado em coordenadas PDF.
/// A origem das coordenadas é o canto inferior esquerdo da página.
/// </summary>
public static class PdfiumTextHitTest
{
    public static PdfiumCharacterInfo? FindCharacterAtPoint(
        IReadOnlyList<PdfiumCharacterInfo> characters,
        int pageIndex,
        double x,
        double y,
        double tolerance = 1.5)
    {
        ArgumentNullException.ThrowIfNull(characters);

        if (pageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "As coordenadas devem ser finitas.");
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        return characters
            .Where(character =>
                character.PageIndex == pageIndex &&
                character.Text.Length > 0 &&
                x >= character.Left - tolerance &&
                x <= character.Right + tolerance &&
                y >= character.Bottom - tolerance &&
                y <= character.Top + tolerance)
            .OrderBy(character =>
            {
                double centerX = (character.Left + character.Right) / 2;
                double centerY = (character.Bottom + character.Top) / 2;
                double dx = x - centerX;
                double dy = y - centerY;
                return dx * dx + dy * dy;
            })
            .FirstOrDefault();
    }
}
