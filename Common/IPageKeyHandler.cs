using Microsoft.UI.Xaml.Input;

namespace DTTS.Common;

/// <summary>
/// ページ外にキーボードフォーカスがあるとき（フォーカスを持つ要素が非表示になった直後など）でも、
/// ウィンドウで受けたキー操作をページへ届けるためのインターフェース。
/// </summary>
public interface IPageKeyHandler
{
    /// <summary>ページのキー処理を実行する。処理したら <c>e.Handled</c> を true にする。</summary>
    void HandlePageKey(KeyRoutedEventArgs e);
}
