using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DTTS.Setup;

internal static class Program
{
    private const string UninstallRegPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DTTS";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            RunUninstallUi();
            return;
        }

        using var form = new Form
        {
            Text = "DTTS セットアップ",
            Width = 380,
            Height = 135,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
        };

        var label = new Label
        {
            Text = "DTTS をセットアップしています...",
            AutoSize = false,
            Left = 20,
            Top = 20,
            Width = 325,
            Height = 24,
        };

        var progress = new ProgressBar
        {
            Left = 20,
            Top = 52,
            Width = 325,
            Height = 20,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
        };

        form.Controls.Add(label);
        form.Controls.Add(progress);

        form.Shown += async (_, _) =>
        {
            try
            {
                string exePath = await Task.Run(Install);
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                    UseShellExecute = true,
                });
                form.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    form,
                    $"セットアップに失敗しました。\n\n{ex.Message}",
                    "DTTS セットアップ",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                form.Close();
            }
        };

        Application.Run(form);
    }

    private static string GetBaseDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DTTS");

    private static void EnsureNotReparsePoint(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"インストール先がシンボリックリンクまたはジャンクションのため中断しました: {path}");
        }
    }

    private static void StopRunningApp(string appDir)
    {
        string expectedExe = Path.GetFullPath(Path.Combine(appDir, "DTTS.exe"));
        foreach (var proc in Process.GetProcessesByName("DTTS"))
        {
            try
            {
                string? procPath = proc.MainModule?.FileName;
                if (procPath is null || !string.Equals(Path.GetFullPath(procPath), expectedExe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!proc.HasExited)
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(3000))
                    {
                        proc.Kill();
                        proc.WaitForExit(2000);
                    }
                }
            }
            catch
            {
                // アクセス拒否等は無視して続行
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static string Install()
    {
        // %LOCALAPPDATA%\DTTS\app に本体を配置する（DB や設定は %LOCALAPPDATA%\DTTS 直下に残るため安全）
        string baseDir = GetBaseDir();
        string appDir = Path.Combine(baseDir, "app");
        string stagingDir = Path.Combine(baseDir, "app_staging_" + Guid.NewGuid().ToString("N")[..8]);
        string backupDir = Path.Combine(baseDir, "app_backup_" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(baseDir);
        EnsureNotReparsePoint(baseDir);
        EnsureNotReparsePoint(appDir);

        // 起動中の DTTS（インストール先配下のプロセスのみ）があれば終了を待ってから上書きする
        StopRunningApp(appDir);

        // 埋め込みリソース payload.zip を一時フォルダへ展開する
        var asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("埋め込みパッケージが見つかりません。");

        Directory.CreateDirectory(stagingDir);
        bool movedOldToBackup = false;
        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(stagingDir, overwriteFiles: true);
            }

            // 更新失敗時に旧版が消失しないよう、一時退避してから差し替え、失敗時はロールバックする
            if (Directory.Exists(appDir))
            {
                Directory.Move(appDir, backupDir);
                movedOldToBackup = true;
            }
            Directory.Move(stagingDir, appDir);

            if (movedOldToBackup && Directory.Exists(backupDir))
            {
                try { Directory.Delete(backupDir, recursive: true); }
                catch (Exception ex) { Console.Error.WriteLine($"警告: 旧バックアップの削除に失敗しました ({ex.Message})"); }
            }
        }
        catch
        {
            if (movedOldToBackup && !Directory.Exists(appDir) && Directory.Exists(backupDir))
            {
                try { Directory.Move(backupDir, appDir); }
                catch (Exception ex) { Console.Error.WriteLine($"警告: バックアップからの復元に失敗しました ({ex.Message})"); }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); }
                catch (Exception ex) { Console.Error.WriteLine($"警告: 一時フォルダの削除に失敗しました ({ex.Message})"); }
            }
        }

        string exePath = Path.Combine(appDir, "DTTS.exe");
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("DTTS.exe が見つかりません。", exePath);
        }

        // アンインストーラーとして自身を配置し、Windows「アプリと機能」に登録する
        string uninstallerPath = Path.Combine(baseDir, "DTTS_Uninstall.exe");
        try
        {
            string? selfPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(selfPath) && File.Exists(selfPath) &&
                !string.Equals(Path.GetFullPath(selfPath), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selfPath, uninstallerPath, overwrite: true);
            }
            RegisterUninstaller(appDir, exePath, uninstallerPath);
        }
        catch (Exception ex)
        {
            // アンインストーラー登録に失敗しても本体導入自体は続行
            Console.Error.WriteLine($"警告: アンインストーラーの登録に失敗しました ({ex.Message})");
        }

        // デスクトップとスタートメニューにショートカットを作成する
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        CreateShortcut(Path.Combine(desktop, "DTTS.lnk"), exePath, appDir);

        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
        if (Directory.Exists(startMenu))
        {
            CreateShortcut(Path.Combine(startMenu, "DTTS.lnk"), exePath, appDir);
        }

        return exePath;
    }

    private static void RegisterUninstaller(string appDir, string exePath, string uninstallerPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegPath);
        if (key is null) return;
        key.SetValue("DisplayName", "DTTS (大富豪)");
        key.SetValue("Publisher", "seisu");
        key.SetValue("InstallLocation", appDir);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (FileVersionInfo.GetVersionInfo(exePath).ProductVersion is { Length: > 0 } ver)
        {
            key.SetValue("DisplayVersion", ver.Split('+')[0]);
        }
    }

    private static void RunUninstallUi()
    {
        var confirm = MessageBox.Show(
            "DTTS をアンインストールしますか？\n（戦績や設定データ %LOCALAPPDATA%\\DTTS は残ります）",
            "DTTS アンインストール",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try
        {
            string baseDir = GetBaseDir();
            string appDir = Path.Combine(baseDir, "app");
            EnsureNotReparsePoint(baseDir);
            EnsureNotReparsePoint(appDir);

            StopRunningApp(appDir);

            if (Directory.Exists(appDir))
            {
                Directory.Delete(appDir, recursive: true);
            }

            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DTTS.lnk");
            if (File.Exists(desktopLink)) File.Delete(desktopLink);

            string startMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "DTTS.lnk");
            if (File.Exists(startMenuLink)) File.Delete(startMenuLink);

            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegPath, throwOnMissingSubKey: false);

            MessageBox.Show(
                "DTTS のアンインストールが完了しました。",
                "DTTS アンインストール",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"アンインストール中にエラーが発生しました。\n\n{ex.Message}",
                "DTTS アンインストール",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetExePath, string workingDir)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null)
        {
            return;
        }

        object? shortcutObj = null;
        try
        {
            shortcutObj = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObj;
            shortcut.TargetPath = targetExePath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.IconLocation = $"{targetExePath},0";
            shortcut.Description = "DTTS (大富豪)";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObj is not null)
            {
                Marshal.FinalReleaseComObject(shortcutObj);
            }
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
