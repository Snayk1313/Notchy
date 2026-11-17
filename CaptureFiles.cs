using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Notchy;

/// <summary>Copying and saving a capture (the capture window, the island's notice and the pinned capture use them).</summary>
internal static class CaptureFiles
{
    private static AppSettings S => AppSettings.Current;

    /// <summary>Where the save window opens: the folder used last time (Imagens\Notchy the first time).</summary>
    public static string Folder => string.IsNullOrWhiteSpace(S.CaptureFolder) || !Directory.Exists(S.CaptureFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Notchy")
        : S.CaptureFolder;

    public static bool Copy(BitmapSource picture)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetImage(picture);
                SoundService.Play(Sfx.Click);
                return true;
            }
            catch (Exception ex) when (i < 4)
            {
                // the clipboard is busy for a moment now and then
                App.Log(ex);
                System.Threading.Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        return false;
    }

    /// <summary>Asks where (opening in the last folder used, which it then remembers) and writes it; null if cancelled or it failed.</summary>
    public static string? Save(BitmapSource picture, Window? owner)
    {
        string folder = Folder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { App.Log(ex); }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            InitialDirectory = folder, FileName = $"Notchy {DateTime.Now:yyyy-MM-dd HH.mm.ss}", DefaultExt = ".png", AddExtension = true,
            Filter = Loc.T("cap.png") + "|*.png", Title = Loc.T("cap.save.title")
        };
        if (dlg.ShowDialog(owner) != true) return null;
        try
        {
            Write(picture, dlg.FileName);
            S.CaptureFolder = Path.GetDirectoryName(dlg.FileName) ?? folder;
            AppSettings.Commit();
            SoundService.Play(Sfx.Click);
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    public static void Write(BitmapSource picture, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(picture));
        using var f = File.Create(file);
        png.Save(f);
    }
}
