namespace DTTS.Common;

/// <summary>
/// 「戻る」操作（Esc・Alt+←・マウスの戻るボタン・キーボードの戻るキー）を受け取るページ。
/// MainWindow がウィンドウ全体で入力を受け、表示中のページがこれを実装していれば呼び出す。
/// 確認ダイアログを出すかどうか、戻り先はページ側が決める。
/// </summary>
public interface IBackRequestHandler
{
    /// <summary>戻る操作が行われた。</summary>
    void OnBackRequested();
}
