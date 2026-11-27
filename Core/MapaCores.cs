using System;
using System.Windows;
using System.Windows.Media;

namespace FrameForge.Core;

internal static class MapaCores
{
    private static readonly string[] Hex = new string[8] { "#E0457B", "#1FB5C8", "#A66BF2", "#F2B33D", "#2FB37A", "#EE7A35", "#4E79C7", "#7D8494" };

    private static Brush[]? _pinceis;
    private static Brush[]? _degrades;
    private static Brush[]? _textos;

    public static Color Cor(MapaDisco.Tipo t)
    {
        return (Color)ColorConverter.ConvertFromString(Hex[(int)t]);
    }

    public static Brush Pincel(MapaDisco.Tipo t)
    {
        return (_pinceis ?? (_pinceis = Montar((Color c) => new SolidColorBrush(c))))[(int)t];
    }

    public static Brush Degrade(MapaDisco.Tipo t)
    {
        return (_degrades ?? (_degrades = Montar((Color c) =>
        {
            return new LinearGradientBrush(Misturar(c, Colors.White, 0.22), Misturar(c, Colors.Black, 0.12), new Point(0.0, 0.0), new Point(0.4, 1.0));
        })))[(int)t];
    }

    public static Brush Texto(MapaDisco.Tipo t)
    {
        return (_textos ?? (_textos = Montar((Color c) => new SolidColorBrush((0.2126 * (double)(int)c.R + 0.7152 * (double)(int)c.G + 0.0722 * (double)(int)c.B > 165.0) ? Color.FromRgb(19, 26, 39) : Colors.White))))[(int)t];
    }

    public static void Limpar()
    {
        _pinceis = null;
        _degrades = null;
        _textos = null;
    }

    private static Brush[] Montar(Func<Color, Brush> f)
    {
        Brush[] array = new Brush[8];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = f(Cor((MapaDisco.Tipo)i));
            ((Freezable)array[i]).Freeze();
        }
        return array;
    }

    private static Color Misturar(Color a, Color b, double t)
    {
        return Color.FromRgb((byte)((double)(int)a.R + (double)(b.R - a.R) * t), (byte)((double)(int)a.G + (double)(b.G - a.G) * t), (byte)((double)(int)a.B + (double)(b.B - a.B) * t));
    }
}