using PhotoShelver.Core;

namespace PhotoShelver.App;

/// <summary>Interface texts used from XAML (<c>{x:Static local:Ui.Name}</c>); the language is chosen at start-up.</summary>
public static class Ui
{
    // Main window: source
    public static string Source => L.T("Source", "Источник");
    public static string Browse => L.T("Browse…", "Выбрать…");
    public static string CollapseAll => L.T("Collapse all", "Свернуть все");
    public static string CollapseAllTip => L.T("Collapse all days", "Свернуть все дни");
    public static string ExpandAll => L.T("Expand all", "Развернуть все");
    public static string ExpandAllTip => L.T("Expand all days", "Развернуть все дни");
    public static string IncludeSubfolders => L.T("Include subfolders", "Включая подпапки");
    public static string SelectAllNew => L.T("Select all new", "Выбрать все новые");
    public static string SelectNone => L.T("Clear selection", "Снять все");

    // Tiles and day headers
    public static string Open => L.T("Open", "Открыть");
    public static string ShowInFolder => L.T("Show in folder", "Показать в папке");
    public static string ImportThisFile => L.T("Import this file", "Импортировать этот файл");
    public static string DaySelection => L.T("Day selection", "Выбор дня");
    public static string CollapseOrExpand => L.T("Collapse or expand", "Свернуть или развернуть");

    // Main window: target
    public static string Target => L.T("Target", "Цель");
    public static string FolderTemplate => L.T("Folder template", "Шаблон папок");
    public static string TemplateTip => L.T(
        "Pick a preset or type your own. Tokens in braces: {YYYY} — year, {YY} — year (2 digits), {MM} — month, {MMM} — month (Jan…Dec), {DD} — day; / — subfolder. Text outside braces is kept as is",
        "Выберите вариант или введите свой. Токены в фигурных скобках: {YYYY} — год, {YY} — год (2 цифры), {MM} — месяц, {MMM} — месяц (Jan…Dec), {DD} — день; / — вложенная папка. Текст вне скобок остаётся как есть");
    public static string TemplateLegend => L.T("{YYYY} · {YY} · {MM} · {MMM} · {DD} · / — new level", "{YYYY} · {YY} · {MM} · {MMM} · {DD} · / — новый уровень");
    public static string Advanced => L.T("Advanced", "Дополнительно");
    public static string FileNames => L.T("File names", "Имена файлов");
    public static string NameTemplateTip => L.T(
        "Tokens in braces: {NAME} — the original name ({NAME} alone keeps names as they are), {YYYY}, {YY}, {MM}, {MMM}, {DD} — date, {HH}, {mm}, {ss} — time of the shot. Text outside braces is kept as is; the extension is always kept",
        "Токены в фигурных скобках: {NAME} — исходное имя (один {NAME} — имена как есть), {YYYY}, {YY}, {MM}, {MMM}, {DD} — дата, {HH}, {mm}, {ss} — время съёмки. Текст вне скобок остаётся как есть; расширение всегда сохраняется");
    public static string NameTemplateLegend => L.T("{NAME} — original name · {YYYY} · {MM} · {DD} · {HH} · {mm} · {ss}", "{NAME} — исходное имя · {YYYY} · {MM} · {DD} · {HH} · {mm} · {ss}");
    public static string SecondCopy => L.T("Second copy", "Вторая копия");
    public static string BackupPerImport => L.T(
        "A new folder for each import, files as on the card (same subfolders and names)",
        "Новая папка на каждый импорт, файлы как на карте (те же подпапки и имена)");
    public static string BackupMirror => L.T(
        "A copy of the library: the same date folders and file names",
        "Копия библиотеки: те же папки по датам и имена файлов");
    public static string BackupTip => L.T(
        "Every imported file is also copied here and verified. When moving, the source is deleted only after both copies are verified",
        "Каждый импортируемый файл копируется ещё и сюда и проверяется. При перемещении исходник удаляется только после проверки обеих копий");
    public static string ShowFullTree => L.T("Show the whole tree", "Показать всё дерево");
    public static string Show => L.T("Show", "Показать");
    public static string Delete => L.T("Delete", "Удалить");

    // Bottom bar and notices
    public static string SwitchToCopy => L.T("Copy instead of moving", "Копировать вместо перемещения");
    public static string Move => L.T("Move", "Переместить");
    public static string Copy => L.T("Copy", "Копировать");
    public static string Import => L.T("Import", "Импорт");
    public static string Cancel => L.T("Cancel", "Отмена");

    // Import summary
    public static string ImportFinished => L.T("Import finished", "Импорт завершён");
    public static string OpenLog => L.T("Open log", "Открыть журнал");
    public static string OpenTargetFolder => L.T("Open target folder", "Открыть папку цели");
    public static string Close => L.T("Close", "Закрыть");
}
