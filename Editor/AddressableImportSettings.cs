using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityAddressableImporter.Helper;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

public class AddressableImportSettings : ScriptableObject
{
    private static Color _oddColor = new Color(0.2f, 0.4f, 0.3f);
    public const string kDefaultConfigObjectName = "addressableimportsettings";
    public const string kDefaultPath = "Assets/AddressableAssetsData/AddressableImportSettings.asset";

    [Tooltip("Toggle rules enabled state")]
    [SerializeField]
    public bool rulesEnabled = true;

    public bool enablePostprocess = false;
    public bool enableCustomPostprocess = false;
    
    [Tooltip("Creates a group if the specified group doesn't exist.")]
    public bool allowGroupCreation = false;

    [Space]
    [Tooltip("Rules for managing imported assets.")]
#if ODIN_INSPECTOR
    [ListDrawerSettings(HideAddButton = false,Expanded = false,DraggableItems = true,
        HideRemoveButton = false, ListElementLabelName = "@Name",
        ElementColor = nameof(GetElementColor))]
    [Searchable(FilterOptions = SearchFilterOptions.ISearchFilterableInterface)]
#endif
    public List<AddressableImportRule> rules = new List<AddressableImportRule>();

    [Space]
    [Tooltip("User defined Rules for managing imported assets.")]
#if ODIN_INSPECTOR
    [TitleGroup("custom rules")]
    [ListDrawerSettings(HideAddButton = false,Expanded = false,DraggableItems = true,
        HideRemoveButton = false,
        ListElementLabelName = nameof(AddressableImporterCustomRuleAsset.Name))]
    [Searchable(FilterOptions = SearchFilterOptions.ISearchFilterableInterface)]
    [InlineEditor]
#endif
    public List<AddressableImporterCustomRuleAsset> customRulesAssets = new List<AddressableImporterCustomRuleAsset>();
    
    [Tooltip("User defined Rules for managing imported assets.")]
#if ODIN_INSPECTOR
    [TitleGroup("custom rules")]
    [ListDrawerSettings(HideAddButton = false,Expanded = false,DraggableItems = true,HideRemoveButton = false,
        ListElementLabelName = nameof(IAddressableImporterCustomRule.Name))]
    [Searchable(FilterOptions = SearchFilterOptions.ISearchFilterableInterface)]
#endif
    [SerializeReference]
    public List<IAddressableImporterCustomRule> customRules = new List<IAddressableImporterCustomRule>();
    
    [ButtonMethod]
    public void Save()
    {
        AssetDatabase.SaveAssets();
    }

    [ButtonMethod]
    public void Documentation()
    {
        Application.OpenURL("https://github.com/favoyang/unity-addressable-importer/blob/master/Documentation~/AddressableImporter.md");
    }

    [ButtonMethod]
    public void CleanEmptyGroup()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            return;
        }
        var dirty = false;
        var emptyGroups = settings.groups.Where(x => x.entries.Count == 0 && !x.IsDefaultGroup()).ToArray();
        for (var i = 0; i < emptyGroups.Length; i++)
        {
            dirty = true;
            settings.RemoveGroup(emptyGroups[i]);
        }
        if (dirty)
        {
            AssetDatabase.SaveAssets();
        }
    }
    
    private Color GetElementColor(int index, Color defaultColor)
    {
        var result = index % 2 == 0 
            ? _oddColor : defaultColor;
        return result;
    }

    /// <summary>
    /// Create AddressableImportSettings and add it to AddressableImportSettingsList
    /// </summary>
    [MenuItem("Assets/Create/Addressables/Import Settings", false, 50)]
    public static void CreateAsset()
    {
        string directoryPath = "Assets/";
        string fileName = "AddressableImportSettings.asset";

        foreach(var obj in Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets))
        {
            var assetPath = AssetDatabase.GetAssetPath(obj);
            var assetDirectoryPath = AssetDatabase.IsValidFolder(assetPath) ? assetPath : Path.GetDirectoryName(assetPath);
            if (AssetDatabase.IsValidFolder(assetDirectoryPath))
            {
                directoryPath = assetDirectoryPath;
            }
        }
        AddressableImportSettings settings = ScriptableObject.CreateInstance<AddressableImportSettings>();
        var filePath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(directoryPath, fileName));
        AssetDatabase.CreateAsset(settings, filePath);
        Debug.LogFormat("Created AddressableImportSettings at path: {0}", filePath);

        if (!AddressableImportSettingsList.Instance.SettingList.Contains(settings))
        {
            AddressableImportSettingsList.Instance.SettingList.Add(settings);
        }

        AssetDatabase.SaveAssets();
        Selection.activeObject = settings;
    }
    
    
}