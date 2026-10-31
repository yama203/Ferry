using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Ferry.Views;

/// <summary>左右のペインの境界線（マウスを乗せると左右の矢印のカーソルになる）</summary>
public sealed partial class PaneSplitter : Grid
{
    public PaneSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
