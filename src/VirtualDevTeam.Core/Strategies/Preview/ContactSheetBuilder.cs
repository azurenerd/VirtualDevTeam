using ImageMagick;
using ImageMagick.Drawing;

namespace VirtualDevTeam.Core.Strategies.Preview;

/// <summary>
/// Composes a list of labeled card images (e.g. screenshots, rendered diagrams,
/// image assets) into a single PNG contact sheet using an approximately-square
/// NxN grid. Shared helper for <c>ImageAssetCandidatePreviewProducer</c> and
/// <see cref="DiagramCandidatePreviewProducer"/>.
/// </summary>
/// <remarks>
/// Implemented on top of <c>Magick.NET</c> (already a Core dep for diagram SVG
/// rasterization). No process spawning, no Playwright — pure in-process composition.
/// Layout: ceil(sqrt(N)) columns × ceil(N/cols) rows. Each cell shows the source
/// image scaled to fit inside <paramref name="cellSize"/> preserving aspect ratio,
/// centered, with a caption strip below.
/// </remarks>
public static class ContactSheetBuilder
{
    /// <summary>
    /// Compose <paramref name="cards"/> into a single PNG. Even with one card,
    /// produces a card-with-caption layout for visual consistency.
    /// </summary>
    public static byte[] Build(
        IReadOnlyList<(byte[] ImageBytes, string Caption)> cards,
        int cellSize = 400,
        int padding = 12,
        int captionHeight = 40)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0)
            throw new ArgumentException("At least one card is required.", nameof(cards));
        if (cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (padding < 0)
            throw new ArgumentOutOfRangeException(nameof(padding));
        if (captionHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(captionHeight));

        var backgroundColor = new MagickColor("#1a1a2e");
        var cellBackground = new MagickColor("#16213e");
        var captionBackground = new MagickColor("#0f3460");
        var captionTextColor = new MagickColor("#e0e0e0");

        int cellW = cellSize;
        int cellH = cellSize + captionHeight;

        int cols = (int)Math.Ceiling(Math.Sqrt(cards.Count));
        int rows = (int)Math.Ceiling((double)cards.Count / cols);

        int sheetW = cols * cellW + (cols + 1) * padding;
        int sheetH = rows * cellH + (rows + 1) * padding;

        using var sheet = new MagickImage(backgroundColor, (uint)sheetW, (uint)sheetH);

        for (int i = 0; i < cards.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            int x = padding + col * (cellW + padding);
            int y = padding + row * (cellH + padding);

            using var cell = new MagickImage(cellBackground, (uint)cellW, (uint)cellH);

            new Drawables()
                .FillColor(captionBackground)
                .Rectangle(0, cellSize, cellW, cellH)
                .Draw(cell);

            try
            {
                using var img = new MagickImage(cards[i].ImageBytes);
                img.Resize(new MagickGeometry((uint)cellSize, (uint)cellSize) { Greater = true });
                int offsetX = (cellSize - (int)img.Width) / 2;
                int offsetY = (cellSize - (int)img.Height) / 2;
                cell.Composite(img, offsetX, offsetY, CompositeOperator.Over);
            }
            catch
            {
                // Bad bytes for this card — leave the cell background visible.
            }

            var caption = TruncateForCaption(cards[i].Caption, maxChars: 48);
            new Drawables()
                .FillColor(captionTextColor)
                .FontPointSize(13)
                .TextAlignment(TextAlignment.Center)
                .Text(cellW / 2.0, cellSize + captionHeight * 0.65, caption)
                .Draw(cell);

            sheet.Composite(cell, x, y, CompositeOperator.Over);
        }

        return sheet.ToByteArray(MagickFormat.Png);
    }

    private static string TruncateForCaption(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars - 1), "…");
    }
}
