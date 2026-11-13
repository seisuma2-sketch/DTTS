using DTTS.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DTTS.Views;

public sealed partial class TitlePage : Page
{
    public TitleViewModel ViewModel { get; }

    /// <summary>フッターに表示するバージョン（パッケージのマニフェストの値）。</summary>
    public string VersionText { get; } = $"v{GetPackageVersion()} • Offline Ready";

    public TitlePage()
    {
        ViewModel = App.Services.GetRequiredService<TitleViewModel>();
        InitializeComponent();
    }

    private static string GetPackageVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (System.InvalidOperationException)
        {
            // パッケージ化されずに起動した場合（パッケージ ID がない）
            return typeof(TitlePage).Assembly.GetName().Version?.ToString(3) ?? "?";
        }
    }
}
