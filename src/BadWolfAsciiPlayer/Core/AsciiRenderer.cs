using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BadWolfAsciiPlayer.Core;

public sealed class AsciiRenderer
{
    public const int CellWidth = 6;
    public const int CellHeight = 10;

    private const string Ramp = " .:-=+*#%@";
    private readonly Dictionary<char, byte[]> _glyphMasks = new();
    private WriteableBitmap? _bitmap;
    private int _columns;
    private int _rows;

    public AsciiRenderer()
    {
        foreach (char c in Ramp)
            _glyphMasks[c] = CreateGlyphMask(c);
    }

    public WriteableBitmap EnsureBitmap(int columns, int rows)
    {
        if (_bitmap is null || _columns != columns || _rows != rows)
        {
            _columns = columns;
            _rows = rows;
            _bitmap = new WriteableBitmap(
                columns * CellWidth,
                rows * CellHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null);
        }

        return _bitmap;
    }

    public unsafe void Render(byte[] rgb, int columns, int rows, AsciiMode mode)
    {
        WriteableBitmap bitmap = EnsureBitmap(columns, rows);
        if (rgb.Length < columns * rows * 3)
            return;

        bitmap.Lock();
        try
        {
            byte* basePtr = (byte*)bitmap.BackBuffer.ToPointer();
            int stride = bitmap.BackBufferStride;

            // Clear the previous frame.
            for (int y = 0; y < rows * CellHeight; y++)
                new Span<byte>(basePtr + y * stride, columns * CellWidth * 4).Clear();

            for (int cellY = 0; cellY < rows; cellY++)
            {
                for (int cellX = 0; cellX < columns; cellX++)
                {
                    int source = (cellY * columns + cellX) * 3;
                    byte r = rgb[source];
                    byte g = rgb[source + 1];
                    byte b = rgb[source + 2];
                    int luminance = (r * 54 + g * 183 + b * 19) >> 8;
                    int rampIndex = luminance * (Ramp.Length - 1) / 255;
                    byte[] mask = _glyphMasks[Ramp[rampIndex]];

                    byte fgR = mode == AsciiMode.Mono ? (byte)235 : r;
                    byte fgG = mode == AsciiMode.Mono ? (byte)235 : g;
                    byte fgB = mode == AsciiMode.Mono ? (byte)235 : b;

                    int originX = cellX * CellWidth;
                    int originY = cellY * CellHeight;

                    for (int glyphY = 0; glyphY < CellHeight; glyphY++)
                    {
                        byte* rowPtr = basePtr + (originY + glyphY) * stride + originX * 4;
                        int maskRow = glyphY * CellWidth;
                        for (int glyphX = 0; glyphX < CellWidth; glyphX++)
                        {
                            byte alpha = mask[maskRow + glyphX];
                            int pixel = glyphX * 4;
                            rowPtr[pixel] = (byte)(fgB * alpha / 255);
                            rowPtr[pixel + 1] = (byte)(fgG * alpha / 255);
                            rowPtr[pixel + 2] = (byte)(fgR * alpha / 255);
                            rowPtr[pixel + 3] = 255;
                        }
                    }
                }
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    private static byte[] CreateGlyphMask(char c)
    {
        if (c == ' ')
            return new byte[CellWidth * CellHeight];

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var text = new FormattedText(
                c.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                9,
                Brushes.White,
                1.0);

            double x = Math.Max(0, (CellWidth - text.WidthIncludingTrailingWhitespace) / 2.0);
            double y = Math.Max(-1, (CellHeight - text.Height) / 2.0 - 0.5);
            dc.DrawText(text, new Point(x, y));
        }

        var render = new RenderTargetBitmap(CellWidth, CellHeight, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        byte[] pixels = new byte[CellWidth * CellHeight * 4];
        render.CopyPixels(pixels, CellWidth * 4, 0);

        byte[] mask = new byte[CellWidth * CellHeight];
        for (int i = 0; i < mask.Length; i++)
        {
            // Pbgra alpha is ideal here; fall back to luminance if font rendering reports full alpha.
            byte alpha = pixels[i * 4 + 3];
            byte luminance = Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2]));
            mask[i] = Math.Max(alpha, luminance);
        }

        return mask;
    }
}
