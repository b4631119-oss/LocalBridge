using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LocalBridge.Converters;

/// <summary>
/// Конвертеры для переключения вкладок.
/// Возвращает разные Brush в зависимости от того, активна ли вкладка.
/// </summary>
internal static class TabButtonConverter
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#89B4FA"));
    private static readonly SolidColorBrush InactiveBrush = new(Color.Parse("#313244"));
    private static readonly SolidColorBrush ActiveFg = new(Color.Parse("#11111B"));
    private static readonly SolidColorBrush InactiveFg = new(Color.Parse("#CDD6F4"));

    private static readonly FuncValueConverter<int, IBrush> FilesConverter =
        new(v => v == 0 ? ActiveBrush : InactiveBrush);

    private static readonly FuncValueConverter<int, IBrush> FilesFgConverter =
        new(v => v == 0 ? ActiveFg : InactiveFg);

    private static readonly FuncValueConverter<int, IBrush> ClipboardConverter =
        new(v => v == 1 ? ActiveBrush : InactiveBrush);

    private static readonly FuncValueConverter<int, IBrush> ClipboardFgConverter =
        new(v => v == 1 ? ActiveFg : InactiveFg);

    private static readonly FuncValueConverter<int, IBrush> LinkConverter =
        new(v => v == 2 ? ActiveBrush : InactiveBrush);

    private static readonly FuncValueConverter<int, IBrush> LinkFgConverter =
        new(v => v == 2 ? ActiveFg : InactiveFg);

    public static IValueConverter Files => FilesConverter;
    public static IValueConverter FilesFg => FilesFgConverter;
    public static IValueConverter Clipboard => ClipboardConverter;
    public static IValueConverter ClipboardFg => ClipboardFgConverter;
    public static IValueConverter Link => LinkConverter;
    public static IValueConverter LinkFg => LinkFgConverter;
}

/// <summary>
/// Конвертеры int → bool для переключения видимости панелей вкладок.
/// IsZero:  true если SelectedTabIndex == 0
/// IsOne:   true если SelectedTabIndex == 1
/// IsTwo:   true если SelectedTabIndex == 2
/// </summary>
internal static class BoolConverters
{
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(v => v == 0);

    public static readonly IValueConverter IsOne =
        new FuncValueConverter<int, bool>(v => v == 1);

    public static readonly IValueConverter IsTwo =
        new FuncValueConverter<int, bool>(v => v == 2);
}
