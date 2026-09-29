using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using Unity.Profiling;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using Debug = UnityEngine.Debug;

#if UNITY_2021_2_OR_NEWER
using UnityEditor.SceneManagement;
#else
using UnityEditor.Experimental.SceneManagement;
#endif

/// <summary>
/// Rule based addressable importer (project fork, idlerpg ADDR-16).
///
/// Differences from upstream:
/// - I1: import batches are queued and processed once per editor tick (nothing is dropped by a time throttle);
/// - I2: an entry is written only when its group, address or labels differ from what the matched rule produces; the
///   settings are marked dirty and saved once, only when something changed;
/// - I3: a pass caused only by addressable configuration files (imports OR deletions) is skipped;
/// - I4: rule regexes are compiled and cached, a literal path prefix pre-check rejects most assets without a regex call,
///   one summary log line per batch instead of one line per asset, the progress bar only for large batches;
/// - I5: an asset matched by rules of two settings assets is reported (the last settings asset still wins, as before);
/// - I7: a group template is applied when a group is created, or once per batch and group in AlwaysOverwrite mode.
/// <see cref="Plan"/> / <see cref="Apply"/> expose the same logic read-only (dry run) for tools.
/// </summary>
[InitializeOnLoad]
public class AddressableImporter : AssetPostprocessor
{
    public static long LastImportTime = 0;        // kept for API compatibility, unused
    public static long ImportDelay = 1000;        // kept for API compatibility, unused
    public static ProfilerMarker ProfilerMarker = new ProfilerMarker("AddressableImporter.OnPostprocessAllAssets");

    /// <summary>log one line per changed entry (off: one summary line per batch)</summary>
    public static bool VerboseLog = false;

    /// <summary>tools that compare importer implementations switch the postprocessor off for a while</summary>
    public static bool Suspended = false;

    public struct BatchStats
    {
        public int batches;
        public int assets;
        public int created;
        public int moved;
        public int updated;
        public int unchanged;
        public int removed;
        public int groupsCreated;
        public int conflicts;
        public int saves;
        public double ms;
    }

    public enum ChangeKind { None, Create, Move, Update }

    public struct EntryPlan
    {
        public string assetPath;
        public string guid;
        public string ruleName;
        public string groupName;
        public string address;
        public string[] labels;
        public LabelWriteMode labelMode;
        public ChangeKind kind;
    }

    static BatchStats _stats;
    public static BatchStats Stats => _stats;
    public static void ResetStats() => _stats = default;

    // The selection active object
    static UnityEngine.Object selectionActiveObject = null;

    static readonly HashSet<string> PendingImported = new HashSet<string>();
    static readonly HashSet<string> PendingDeleted = new HashSet<string>();
    static readonly List<KeyValuePair<string, string>> PendingMoved = new List<KeyValuePair<string, string>>();
    static bool _scheduled;

    static AddressableImporter()
    {
        Selection.selectionChanged += OnSelectionChanged;
    }

    static void OnSelectionChanged()
    {
        selectionActiveObject = Selection.activeObject;
    }

    const string ConfigRoot = "Assets/AddressableAssetsData";

    static bool IsConfigOnly(string[] a, string[] b, string[] c)
    {
        var any = false;
        foreach (var list in new[] { a, b, c })
            foreach (var p in list)
            {
                if (!p.StartsWith(ConfigRoot, StringComparison.Ordinal)) return false;
                any = true;
            }
        return any;
    }

    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        if (Suspended) return;
        if (importedAssets.Length + deletedAssets.Length + movedAssets.Length == 0) return;
        // I3: saving a group or the settings re-imports them; that pass never carries content
        if (IsConfigOnly(importedAssets, deletedAssets, movedAssets)) return;

        foreach (var p in importedAssets) if (!IsAssetIgnored(p)) PendingImported.Add(p);
        foreach (var p in deletedAssets) if (!IsAssetIgnored(p)) PendingDeleted.Add(p);
        for (var i = 0; i < movedAssets.Length; i++)
            if (!IsAssetIgnored(movedAssets[i])) PendingMoved.Add(new KeyValuePair<string, string>(movedAssets[i], movedFromAssetPaths[i]));

        if (_scheduled) return;
        _scheduled = true;
        EditorApplication.delayCall += ProcessPending;
    }

    /// <summary>runs the queued batch now (tools and tests that do not wait for the next editor tick)</summary>
    public static bool FlushPending()
    {
        if (!_scheduled) return false;
        EditorApplication.delayCall -= ProcessPending;
        ProcessPending();
        return true;
    }

    static void ProcessPending()
    {
        _scheduled = false;
        if (PendingImported.Count + PendingDeleted.Count + PendingMoved.Count == 0) return;
        var imported = PendingImported.ToArray();
        var deleted = PendingDeleted.ToArray();
        var moved = PendingMoved.Select(x => x.Key).ToArray();
        var movedFrom = PendingMoved.Select(x => x.Value).ToArray();
        PendingImported.Clear();
        PendingDeleted.Clear();
        PendingMoved.Clear();
        ProcessBatch(imported, deleted, moved, movedFrom);
    }

    static void ProcessBatch(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        var watch = Stopwatch.StartNew();
        var isDirty = false;
        try
        {
            ProfilerMarker.Begin();
            AssetDatabase.StartAssetEditing();
            isDirty = ProcessAddressableAssets(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            ProfilerMarker.End();
        }

        if (isDirty)
        {
            var cached = selectionActiveObject;
            AssetDatabase.SaveAssets();
            _stats.saves++;
            // saving changed groups can null the selection (#71)
            Selection.activeObject = cached;
        }

        watch.Stop();
        _stats.batches++;
        _stats.ms += watch.Elapsed.TotalMilliseconds;
    }

    static bool ProcessAddressableAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, bool applyCustomRules = true)
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            if (!EditorApplication.isUpdating && !EditorApplication.isCompiling)
                Debug.LogWarning("[Addressables] settings file not found.\nPlease go to Menu/Window/Asset Management/Addressables, then click 'Create Addressables Settings' button.");
            return false;
        }

        var importSettingsList = AddressableImportSettingsList.Instance;
        if (importSettingsList == null)
        {
            Debug.LogWarning("[AddressableImporter] import settings file not found.\nPlease go to Assets/AddressableAssetsData folder, right click in the project window and choose 'Create > Addressables > Import Settings'.");
            return false;
        }

        var ruleSettings = importSettingsList.EnabledSettingsList.Where(s => s.rules.Count > 0 || s.customRules.Count > 0).ToList();
        if (ruleSettings.Count == 0)
        {
            return importSettingsList.RemoveMissingImportSettings();
        }

        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
#if UNITY_2020_1_OR_NEWER
        string prefabAssetPath = prefabStage != null ? prefabStage.assetPath : null;
#else
        string prefabAssetPath = prefabStage != null ? prefabStage.prefabAssetPath : null;
#endif
        var batch = new BatchContext(settings);
        var dirty = false;
        var showBar = importSettingsList.ShowImportProgressBar && importedAssets.Length > 400;
        try
        {
            for (var i = 0; i < importedAssets.Length; i++)
            {
                var importedAsset = importedAssets[i];
                if (IsAssetIgnored(importedAsset)) continue;
                if (prefabStage != null && prefabAssetPath == importedAsset) continue; // ignore the prefab being edited

                if (showBar && i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar(
                        "Processing addressable import settings", $"[{i}/{importedAssets.Length}] {importedAsset}",
                        (float)i / importedAssets.Length))
                    break;

                _stats.assets++;
                dirty |= ApplyToAsset(batch, ruleSettings, importedAsset, null);
            }
        }
        finally
        {
            if (showBar) EditorUtility.ClearProgressBar();
        }

        for (var i = 0; i < movedAssets.Length; i++)
        {
            var movedAsset = movedAssets[i];
            if (IsAssetIgnored(movedAsset)) continue;
            if (prefabStage != null && prefabAssetPath == movedAsset) continue;
            _stats.assets++;
            dirty |= ApplyToAsset(batch, ruleSettings, movedAsset, movedFromAssetPaths[i]);
        }

        // Deleted assets: Addressables removes the entries of deleted assets itself. The importer must not: a deleted path
        // resolves to no GUID, and a path deleted and imported again inside one batch resolves to the NEW asset (removing its
        // fresh entry). Upstream's removal branch was dead for that reason; it is dropped.

        dirty |= importSettingsList.RemoveMissingImportSettings();

        if (dirty && batch.Changed) settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        else if (dirty && batch.removed > 0) settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);

        if (batch.Changed || batch.removed > 0)
        {
            Debug.Log($"[AddressableImporter] batch: {importedAssets.Length + movedAssets.Length + deletedAssets.Length} assets, created {batch.created}, moved {batch.moved}, updated {batch.updated}, removed {batch.removed}" +
                      (batch.conflicts > 0 ? $", {batch.conflicts} matched by more than one settings asset" : string.Empty));
        }

        return dirty;
    }

    sealed class BatchContext
    {
        public readonly AddressableAssetSettings settings;
        public readonly HashSet<AddressableAssetGroup> templated = new HashSet<AddressableAssetGroup>();
        public int created, moved, updated, removed, conflicts;
        public BatchContext(AddressableAssetSettings s) { settings = s; }
        public bool Changed => created + moved + updated > 0;
    }

    static bool ApplyToAsset(BatchContext batch, List<AddressableImportSettings> ruleSettings, string assetPath, string movedFromAssetPath)
    {
        var dirty = false;
        var matches = 0;
        foreach (var importSettings in ruleSettings)
        {
            if (TryGetMatchedRule(assetPath, importSettings, out var matchedRule))
            {
                matches++;
                dirty |= ApplyRule(batch, importSettings, matchedRule, assetPath);
            }
            else if (!string.IsNullOrEmpty(movedFromAssetPath) && TryGetMatchedRule(movedFromAssetPath, importSettings, out _))
            {
                // moved out of a managed path: the importer removes only entries it manages
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (batch.settings.RemoveAssetEntry(guid, false))
                {
                    dirty = true;
                    batch.removed++;
                    _stats.removed++;
                }
            }

            if (importSettings.customRules.Count > 0)
            {
                var ruleData = new AddressableAssetRuleData { assetPath = assetPath, movedFromAssetPath = movedFromAssetPath };
                foreach (var customRule in importSettings.customRules)
                {
                    if (!customRule.Enabled) continue;
                    if (customRule.Import(ruleData, batch.settings, importSettings))
                    {
                        dirty = true;
                        batch.updated++;
                        _stats.updated++;
                    }
                }
            }
        }

        if (matches > 1)
        {
            batch.conflicts++;
            _stats.conflicts++;
        }
        return dirty;
    }

    static bool ApplyRule(BatchContext batch, AddressableImportSettings importSettings, AddressableImportRule rule, string assetPath)
    {
        var plan = BuildPlan(batch.settings, importSettings, rule, assetPath, out var group, out var newGroup);
        if (group == null) return false;

        if (rule.groupTemplate != null && (newGroup || (rule.groupTemplateApplicationMode == GroupTemplateApplicationMode.AlwaysOverwriteGroupSettings && batch.templated.Add(group))))
        {
            var templateSchema = rule.groupTemplate.SchemaObjects;
            foreach (var schema in templateSchema.Where(schema => !group.HasSchema(schema.GetType())))
                group.AddSchema(schema.GetType());
            rule.groupTemplate.ApplyToAddressableAssetGroup(group);
            if (newGroup) _stats.groupsCreated++;
        }

        if (plan.kind == ChangeKind.None)
        {
            _stats.unchanged++;
            return false;
        }

        var entry = batch.settings.FindAssetEntry(plan.guid);
        if (entry == null || entry.parentGroup != group)
            entry = batch.settings.CreateOrMoveEntry(plan.guid, group, false, false);
        if (entry == null) return false;

        if (entry.address != plan.address) entry.SetAddress(plan.address, false);
        WriteLabels(entry, plan, rule);

        switch (plan.kind)
        {
            case ChangeKind.Create: batch.created++; _stats.created++; break;
            case ChangeKind.Move: batch.moved++; _stats.moved++; break;
            default: batch.updated++; _stats.updated++; break;
        }

        if (VerboseLog) Debug.Log($"[AddressableImporter] {plan.kind} {assetPath} -> {plan.groupName} / {plan.address}");
        return true;
    }

    static void WriteLabels(AddressableAssetEntry entry, EntryPlan plan, AddressableImportRule rule)
    {
        var wanted = new HashSet<string>(plan.labels ?? Array.Empty<string>());
        if (plan.labelMode == LabelWriteMode.Replace)
            foreach (var label in entry.labels.ToArray())
                if (!wanted.Contains(label)) entry.SetLabel(label, false, false, false);
        foreach (var label in wanted)
            if (!entry.labels.Contains(label)) entry.SetLabel(label, true, true, false);
    }

    /// <summary>what the matched rule produces for the asset, compared with the current entry (nothing is written)</summary>
    static EntryPlan BuildPlan(AddressableAssetSettings settings, AddressableImportSettings importSettings, AddressableImportRule rule,
        string assetPath, out AddressableAssetGroup group, out bool newGroup)
    {
        newGroup = false;
        var plan = new EntryPlan
        {
            assetPath = assetPath,
            guid = AssetDatabase.AssetPathToGUID(assetPath),
            ruleName = rule.Name,
            groupName = rule.ParseGroupReplacement(assetPath),
            labelMode = rule.LabelMode,
            kind = ChangeKind.None,
        };

        if (!TryGetGroup(settings, plan.groupName, out group))
        {
            if (importSettings.allowGroupCreation)
            {
                group = CreateAssetGroup<BundledAssetGroupSchema>(settings, plan.groupName);
                newGroup = true;
            }
            else
            {
                Debug.LogErrorFormat("[AddressableImporter] Failed to find group {0} when importing {1}. Please check if the group exists, then reimport the asset.", rule.groupName, assetPath);
                return plan;
            }
        }
        plan.groupName = group.Name;

        var labels = new List<string>();
        labels.AddRange(rule.labelsRefsEnum);
        if (rule.dynamicLabels != null)
            foreach (var dynamicLabel in rule.dynamicLabels)
            {
                var label = rule.ParseReplacement(assetPath, dynamicLabel);
                if (!string.IsNullOrEmpty(label)) labels.Add(label);
            }
        plan.labels = labels.Distinct().ToArray();

        var entry = settings.FindAssetEntry(plan.guid);
        var currentAddress = entry?.address;
        plan.address = currentAddress;
        if (entry == null || string.IsNullOrEmpty(currentAddress) || currentAddress.StartsWith("Assets/") ||
            rule.simplified || !string.IsNullOrWhiteSpace(rule.addressReplacement))
            plan.address = rule.ParseAddressReplacement(assetPath);

        if (entry == null) { plan.kind = ChangeKind.Create; return plan; }
        if (entry.parentGroup != group) { plan.kind = ChangeKind.Move; return plan; }
        if (entry.address != plan.address) { plan.kind = ChangeKind.Update; return plan; }

        var current = entry.labels;
        var missing = plan.labels.Any(l => !current.Contains(l));
        var extra = plan.labelMode == LabelWriteMode.Replace && current.Any(l => !plan.labels.Contains(l));
        if (missing || extra) plan.kind = ChangeKind.Update;
        return plan;
    }

    /// <summary>Read-only plan for the given asset paths against every enabled settings asset (or only <paramref name="only"/>).</summary>
    public static List<EntryPlan> Plan(IEnumerable<string> assetPaths, AddressableImportSettings only = null)
    {
        var result = new List<EntryPlan>();
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var list = AddressableImportSettingsList.Instance;
        if (settings == null || list == null) return result;
        var candidates = only != null ? new List<AddressableImportSettings> { only } : list.EnabledSettingsList;
        foreach (var path in assetPaths)
        {
            if (IsAssetIgnored(path)) continue;
            foreach (var importSettings in candidates)
            {
                if (!TryGetMatchedRule(path, importSettings, out var rule)) continue;
                var plan = BuildPlanReadOnly(settings, importSettings, rule, path);
                result.Add(plan);
            }
        }
        return result;
    }

    static EntryPlan BuildPlanReadOnly(AddressableAssetSettings settings, AddressableImportSettings importSettings, AddressableImportRule rule, string assetPath)
    {
        var groupName = rule.ParseGroupReplacement(assetPath);
        var hasGroup = TryGetGroup(settings, groupName, out var group);
        var guid = AssetDatabase.AssetPathToGUID(assetPath);
        var plan = new EntryPlan { assetPath = assetPath, guid = guid, ruleName = rule.Name, groupName = hasGroup ? group.Name : groupName, labelMode = rule.LabelMode, kind = ChangeKind.None };
        var labels = new List<string>(rule.labelsRefsEnum);
        if (rule.dynamicLabels != null)
            foreach (var dynamicLabel in rule.dynamicLabels)
            {
                var label = rule.ParseReplacement(assetPath, dynamicLabel);
                if (!string.IsNullOrEmpty(label)) labels.Add(label);
            }
        plan.labels = labels.Distinct().ToArray();
        var entry = settings.FindAssetEntry(guid);
        plan.address = entry?.address;
        if (entry == null || string.IsNullOrEmpty(plan.address) || plan.address.StartsWith("Assets/") || rule.simplified || !string.IsNullOrWhiteSpace(rule.addressReplacement))
            plan.address = rule.ParseAddressReplacement(assetPath);
        if (entry == null) plan.kind = ChangeKind.Create;
        else if (!hasGroup || entry.parentGroup != group) plan.kind = ChangeKind.Move;
        else if (entry.address != plan.address) plan.kind = ChangeKind.Update;
        else
        {
            var current = entry.labels;
            if (plan.labels.Any(l => !current.Contains(l)) || (plan.labelMode == LabelWriteMode.Replace && current.Any(l => !plan.labels.Contains(l))))
                plan.kind = ChangeKind.Update;
        }
        return plan;
    }

    /// <summary>Applies the rules to the given paths now (one batch, one save). Returns the number of entries written.</summary>
    public static int Apply(IEnumerable<string> assetPaths, AddressableImportSettings only = null)
    {
        var before = _stats.created + _stats.moved + _stats.updated;
        var paths = assetPaths.Where(p => !IsAssetIgnored(p)).ToArray();
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var list = AddressableImportSettingsList.Instance;
        if (settings == null || list == null || paths.Length == 0) return 0;
        var candidates = only != null ? new List<AddressableImportSettings> { only } : list.EnabledSettingsList.Where(s => s.rules.Count > 0 || s.customRules.Count > 0).ToList();
        var watch = Stopwatch.StartNew();
        var batch = new BatchContext(settings);
        var dirty = false;
        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var path in paths)
            {
                _stats.assets++;
                dirty |= ApplyToAsset(batch, candidates, path, null);
            }
            if (dirty) settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
        if (dirty)
        {
            AssetDatabase.SaveAssets();
            _stats.saves++;
        }
        watch.Stop();
        _stats.batches++;
        _stats.ms += watch.Elapsed.TotalMilliseconds;
        if (batch.Changed)
            Debug.Log($"[AddressableImporter] apply: {paths.Length} assets, created {batch.created}, moved {batch.moved}, updated {batch.updated}");
        return _stats.created + _stats.moved + _stats.updated - before;
    }

    /// <summary>
    /// Explicit entry for custom rules (generated manifests): the asset goes to <paramref name="groupName"/> with the given
    /// address and at least the given labels. Writes only what differs; returns true when something was written (the caller
    /// batches the save). A missing group is reported and skipped (rules never create groups here).
    /// </summary>
    public static bool EnsureEntry(AddressableAssetSettings settings, string guid, string groupName, string address, IReadOnlyList<string> labels)
    {
        if (string.IsNullOrEmpty(guid)) return false;
        if (!TryGetGroup(settings, groupName, out var group))
        {
            Debug.LogError($"[AddressableImporter] group {groupName} not found for {guid}");
            return false;
        }

        var changed = false;
        var entry = settings.FindAssetEntry(guid);
        if (entry == null || entry.parentGroup != group)
        {
            entry = settings.CreateOrMoveEntry(guid, group, false, false);
            changed = true;
        }
        if (entry == null) return false;

        if (!string.IsNullOrEmpty(address) && entry.address != address)
        {
            entry.SetAddress(address, false);
            changed = true;
        }

        if (labels != null)
            foreach (var label in labels)
            {
                if (string.IsNullOrEmpty(label) || entry.labels.Contains(label)) continue;
                entry.SetLabel(label, true, true, false);
                changed = true;
            }

        if (changed) settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, false, true);
        return changed;
    }

    static bool IsAssetIgnored(string assetPath)
    {
        return assetPath.EndsWith(".meta") || assetPath.EndsWith(".DS_Store") || assetPath.EndsWith("~");
    }

    static AddressableAssetGroup CreateAssetGroup<SchemaType>(AddressableAssetSettings settings, string groupName)
    {
        return settings.CreateGroup(groupName, false, false, false, new List<AddressableAssetGroupSchema> { settings.DefaultGroup.Schemas[0] }, typeof(SchemaType));
    }

    static bool TryGetMatchedRule(
        string assetPath,
        AddressableImportSettings importSettings,
        out AddressableImportRule rule)
    {
        foreach (var r in importSettings.rules)
        {
            if (r.enabled == false) continue;
            if (!r.Match(assetPath)) continue;
            rule = r;
            return true;
        }

        rule = null;
        return false;
    }

    /// <summary>
    /// Attempts to get the group using the provided <paramref name="groupName"/>.
    /// </summary>
    static bool TryGetGroup(AddressableAssetSettings settings, string groupName, out AddressableAssetGroup group)
    {
        group = null;

        if (string.IsNullOrWhiteSpace(groupName))
        {
            group = settings.DefaultGroup;
            return true;
        }

        var trimmed = groupName.Trim();
        foreach (var settingsGroup in settings.groups)
        {
            if (settingsGroup == null || string.IsNullOrEmpty(settingsGroup.Name))
            {
                Debug.LogError($"{nameof(AddressableImporter)} {nameof(TryGetGroup)} ERROR at GroupName {groupName} Addressable Group {settingsGroup}");
                continue;
            }

            if (!string.Equals(settingsGroup.Name, trimmed))
                continue;

            group = settingsGroup;
            break;
        }

        return group != null;
    }

    /// <summary>
    /// Allows assets within the selected folder to be checked agains the Addressable Importer rules.
    /// </summary>
    public class FolderImporter
    {
        public static void ReimportFolders(IEnumerable<String> assetPaths, bool showConfirmDialog = true)
        {
            var pathsToImport = new HashSet<string>();
            foreach (var assetPath in assetPaths)
            {
                if (!Directory.Exists(assetPath)) continue;

                pathsToImport.Add(assetPath.Replace('\\', '/'));

                var dirsToAdd = Directory.GetDirectories(assetPath, "*", SearchOption.AllDirectories);
                foreach (var dir in dirsToAdd)
                {
                    if (!dir.StartsWith(".") && !dir.EndsWith("~"))
                        pathsToImport.Add(dir.Replace('\\', '/'));
                }

                var filesToAdd = Directory.GetFiles(assetPath, "*", SearchOption.AllDirectories);
                foreach (var file in filesToAdd)
                {
                    if (!file.EndsWith(".meta") && !file.EndsWith(".DS_Store") && !IsAssetIgnored(file))
                        pathsToImport.Add(file.Replace('\\', '/'));
                }
            }
            if (pathsToImport.Count > 0)
            {
                Debug.Log($"AddressableImporter: Found {pathsToImport.Count} asset paths...");

                if (showConfirmDialog &&
                    !EditorUtility.DisplayDialog("Process files?",
                                                 $"About to process {pathsToImport.Count} files and folders, is that OK?",
                                                 "Yes", "No"))
                    return;

                ProcessBatch(pathsToImport.ToArray(), new string[0], new string[0], new string[0]);
            }
        }

        [MenuItem("Assets/AddressableImporter: Check Folder(s)")]
        private static void CheckFoldersFromSelection()
        {
            ReimportSelectedFolderAssets(false);
        }

        [MenuItem("Assets/AddressableImporter: Check Folder(s) With Rules")]
        private static void CheckFoldersWithCustomRuleFromSelection()
        {
            ReimportSelectedFolderAssets(true);
        }

        private static void ReimportSelectedFolderAssets(bool useCustomRule)
        {
            List<string> assetPaths = new List<string>();
            foreach (UnityEngine.Object obj in Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets))
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (Directory.Exists(assetPath))
                    assetPaths.Add(assetPath);
            }
            ReimportFolders(assetPaths, useCustomRule);
        }

        [MenuItem("Assets/AddressableImporter: Check Folder(s)", true)]
        private static bool ValidateCheckFoldersFromSelection() => ValidateSelectedFolder();

        [MenuItem("Assets/AddressableImporter: Check Folder(s) With Rules", true)]
        private static bool ValidateCheckFoldersWithCustomRuleFromSelection() => ValidateSelectedFolder();

        private static bool ValidateSelectedFolder()
        {
            foreach (UnityEngine.Object obj in Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets))
            {
                if (Directory.Exists(AssetDatabase.GetAssetPath(obj)))
                    return true;
            }
            return false;
        }
    }
}
