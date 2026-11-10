using PhotoShelver.Core;

namespace PhotoShelver.App;

/// <summary>Interface texts used from XAML (<c>{x:Static local:Ui.Name}</c>); the language is chosen at start-up.</summary>
public static class Ui
{
    // Main window: source
    public static string Source => Strings.Source;
    public static string Browse => Strings.Browse;
    public static string CollapseAll => Strings.CollapseAll;
    public static string CollapseAllTip => Strings.CollapseAllDays;
    public static string ExpandAll => Strings.ExpandAll;
    public static string ExpandAllTip => Strings.ExpandAllDays;
    public static string IncludeSubfolders => Strings.IncludeSubfolders;
    public static string SelectAllNew => Strings.SelectAllNew;
    public static string SelectNone => Strings.ClearSelection;

    // Tiles and day headers
    public static string Open => Strings.Open;
    public static string ShowInFolder => Strings.ShowInFolder;
    public static string ImportThisFile => Strings.ImportThisFile;
    public static string DaySelection => Strings.DaySelection;
    public static string CollapseOrExpand => Strings.CollapseOrExpand;

    // Main window: target
    public static string Target => Strings.Target;
    public static string FolderTemplate => Strings.FolderTemplate;
    public static string TemplateTip => Strings.PickAPresetOrTypeYourOwn;
    public static string TemplateLegend => Strings.YYYYYYMMMMMDDNewLevel;
    public static string Advanced => Strings.Advanced;
    public static string FileNames => Strings.FileNames;
    public static string NameTemplateTip => Strings.TokensInBracesNAMETheOriginalName;
    public static string NameTemplateLegend => Strings.NAMEOriginalNameYYYYMMDDHH;
    public static string SecondCopy => Strings.SecondCopy;
    public static string BackupPerImport => Strings.ANewFolderForEachImportFiles;
    public static string BackupMirror => Strings.ACopyOfTheLibraryTheSame;
    public static string BackupTip => Strings.EveryImportedFileIsAlsoCopiedHere;
    public static string ShowFullTree => Strings.ShowTheWholeTree;
    public static string Show => Strings.Show;
    public static string Delete => Strings.Delete;

    // Bottom bar and notices
    public static string SwitchToCopy => Strings.CopyInsteadOfMoving;
    public static string Move => Strings.Move;
    public static string Copy => Strings.Copy;
    public static string Import => Strings.Import;
    public static string Cancel => Strings.Cancel;

    // Import summary
    public static string ImportFinished => Strings.ImportFinished;
    public static string OpenLog => Strings.OpenLog;
    public static string OpenTargetFolder => Strings.OpenTargetFolder;
    public static string Close => Strings.Close;
}
