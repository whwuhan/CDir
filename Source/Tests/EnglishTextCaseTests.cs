using System.Globalization;
using System.Text.RegularExpressions;

internal sealed partial class RegressionRunner
{
    // These resources are prose or status fragments; all other English resources are functional captions.
    private static readonly HashSet<string> SentenceCaseKeys =
    [
        "RestoringFiles", "RestoreComplete", "DeletingFiles", "DeleteComplete", "HistoryStorageProtected", "HistoryHint", "HistoryEmpty", "HistoryChanged", "NothingToUndo", "NothingToRedo", "Undoing", "Redoing", "UndoComplete", "RedoComplete",
        "DriveUsageFormat", "DriveUsageTooltip", "DriveUsageUnavailable", "DriveUsageUnavailableTooltip",
        "HideSidebarTooltip", "ShowSidebarTooltip", "OpenRecycleBin", "ShellMenuError", "NewWindowError",
        "ExitOnCloseTooltip", "TrayUnavailable", "CopyOnDropTooltip", "FileDropCopyTo", "FileDropMoveTo",
        "RestoreDefaultsTooltip", "DragCategory", "ExpandCategoryFoldersTooltip", "CollapseCategoryFoldersTooltip", "NoCategories", "DeleteCategoryQuestion", "DeleteCategoryHint",
        "EmptyCategory", "CategoryNameUnavailable", "AccessDenied", "AddressAncestors", "AddressEdit",
        "AddressTooltip", "BackTooltip", "BrowseHint", "BusyCloseNotice", "ClipboardEmpty",
        "ClipboardError", "CopiedItemsNotice", "CopyComplete", "CopyCurrentPathTooltip", "CopyingFiles",
        "CopyTooltip", "CreatingFolder", "CurrentPathCopied", "CutItemsNotice", "CutTooltip",
        "DialogNameHint", "DirectoryMissing", "EmptyFolder", "EmptyFolderHint", "ExpandFolderError",
        "FileMissing", "FilterCurrentFolder", "FilteredItemsCount", "FolderCreated",
        "FolderUnavailable", "ForwardTooltip", "HelpBody", "HiddenItemsTooltip", "ItemsCount",
        "LocationUnavailable", "LocationUnavailableHint", "MoveComplete", "MovingFiles", "NewFolderTooltip",
        "NoResults", "NoResultsHint", "OpenFileError", "Opening", "OpenLocationError",
        "OperationCancelled", "OperationIncomplete", "PasteError", "PasteTooltip", "PathTooLong",
        "SearchColumnHint", "ColumnsHint", "SearchHint", "SearchFilesTip", "SearchDirectoriesTip", "SearchResultsCount", "SearchProgressCount",
        "SearchRegexTip", "SearchWildcardTip", "SearchPatternError", "SearchRegexInvalid", "SearchRegexTimeout",
        "SearchRunning", "SearchScopeHint", "SearchError", "ReadDrivesError", "RecycleComplete",
        "RecycleItemsSubject", "RecycleQuestion", "RecycleTooltip", "Recycling", "RefreshTooltip",
        "RenameComplete", "RenameTooltip", "Renaming", "SelectedCount", "SelectedPathsCopied",
        "ServiceCopyFailed", "ServiceCopyRootDenied", "ServiceDeleteRootDenied", "ServiceDuplicateSelection", "ServiceFolderMissing",
        "ServiceInvalidName", "ServiceLinkUnsupported", "ServicePathRequired", "ServiceRecursiveCopyDenied", "ServiceRecycleUnsupported",
        "ServiceRenameRootDenied", "ServiceReservedName", "ServiceSameLocation", "ServiceSourceChanged", "ServiceTargetConflict",
        "ServiceTargetMissing", "SettingsSaveError", "TreeAccessError", "TreeExpandHint", "UpTooltip",
        "ZeroItems", "VcsUnavailable", "VcsContains",
        "TerminalToggleTooltip", "TerminalRestartTooltip", "TerminalVisible", "TerminalHidden", "TerminalExited", "TerminalStartError",
        "TerminalColorHint", "TerminalColorInvalid", "TabAliasHint",
        "TerminalFontStatus", "TerminalBackgroundStatus",
        "ShortcutHint", "ShortcutRecordHint", "ShortcutFixedHint", "ShortcutInvalid", "ShortcutConflict", "ShortcutSaveError", "NoShortcutResults", "HelpOverview", "AddressEditCustom",
    ];

    private void RunEnglishTextCaseChecks()
    {
        var English = ReadTranslation("English.json");
        this.Test("English functional captions use title case and retain sidebar names", () =>
        {
            Check(English["DirectoryTree"] == "My Computer" && English["Favorites"] == "Quick Access", "Sidebar headings must retain their names.");
            foreach (var Entry in English.Where(Entry => !SentenceCaseKeys.Contains(Entry.Key)))
                foreach (Match Word in Regex.Matches(Entry.Value, "(?<![A-Za-z/])[A-Za-z]+"))
                    Check(char.IsUpper(Word.Value[0]), Entry.Key + " has a functional caption without a capitalized word: " + Word.Value);
        });
        this.Test("English messages use sentence case and preserve names brands and shortcuts", () =>
        {
            foreach (var Key in SentenceCaseKeys)
            {
                Check(English.ContainsKey(Key), "Missing sentence-case resource: " + Key);
                var Text = English[Key];
                if (Key == "HelpBody") Text = Regex.Replace(Text, @"(?m)^(?:Ctrl|Alt|F\d)[^:\r\n]*:\s*", "");
                Text = Regex.Replace(Text, @"File Explorer|Recycle Bin|Windows Shell|%[A-Z_]+%|\{\d+(?:[^}]*)\}|\b(?:Windows|Ctrl|Shift|Alt|Enter|Esc|Tab|Delete|UNC|CDir|Git|SVN|HEAD|AM|PM)\b",
                    Match => new string(' ', Match.Length));
                foreach (Match Word in Regex.Matches(Text, @"\b[A-Z][a-z]+\b"))
                {
                    var Prefix = Text[..Word.Index];
                    var Boundary = Prefix.LastIndexOfAny(['.', '?', '!', '\n', '·']);
                    Check(!Prefix[(Boundary + 1)..].Any(char.IsLetterOrDigit),
                        Key + " capitalizes an ordinary word inside a sentence: " + Word.Value);
                }
            }
            Check(English["DeleteCategoryQuestion"] == "Delete the category “{0}”?", "Confirmation questions must use sentence case.");
            Check(English["DeleteCategoryHint"].Contains("The original folders will be kept."), "Each sentence starts with a capital letter.");
            Check(English["SelectedCount"] == "{0} selected" && English["NoResults"] == "No matching items", "Status and empty-state hints must use sentence case.");
            Check(English["HelpBody"].Contains("Ctrl + L / Alt + D") && English["HelpBody"].Contains("(UNC)") && English["HelpBody"].Contains("%USERPROFILE%"),
                "Keyboard names acronyms and environment variables retain their spelling.");
            const string CategoryName = "my_PROJECT - 工作";
            Check(string.Format(CultureInfo.InvariantCulture, English["DeleteCategoryQuestion"], CategoryName).Contains(CategoryName),
                "Sentence casing must not change user-provided names.");
        });
    }
}

