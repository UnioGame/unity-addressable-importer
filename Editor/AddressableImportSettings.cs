using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using System.Collections.Generic;
using System.Linq;
using UnityAddressableImporter.Helper;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

[CreateAssetMenu(fileName = "AddressableImportSettings", menuName = "Addressable Assets/Import Settings", order = 50)]
public class AddressableImportSettings : ScriptableObject
{
    private static Color _oddColor = new Color(0.2f, 0.4f, 0.3f);

    public const string kDefaultConfigObjectName = "addressableimportsettings";
    public const string kDefaultPath = "Assets/AddressableAssetsData/AddressableImportSettings.asset";

    public bool enablePostprocess = false;
    public bool enableCustomPostprocess = false;
    
    [Tooltip("Creates a group if the specified group doesn't exist.")]
    public bool allowGroupCreation = false;

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

    public static AddressableImportSettings Instance
    {
        get
        {
            AddressableImportSettings so;
            // Try to locate settings via EditorBuildSettings.
            if (EditorBuildSettings.TryGetConfigObject(kDefaultConfigObjectName, out so))
                return so;
            // Try to locate settings via path.
            so = AssetDatabase.LoadAssetAtPath<AddressableImportSettings>(kDefaultPath);
            if (so != null)
                EditorBuildSettings.AddConfigObject(kDefaultConfigObjectName, so, true);
            return so;
        }
    }
    
    
}