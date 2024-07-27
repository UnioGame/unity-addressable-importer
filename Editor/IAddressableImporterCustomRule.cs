using UnityEditor.AddressableAssets.Settings;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

public interface IAddressableImporterCustomRule
#if ODIN_INSPECTOR_3
    : ISearchFilterable
#endif
{
    string Name { get; }
    
    public bool Enabled{ get; }

    bool Import(AddressableAssetRuleData importData,
        AddressableAssetSettings settings,
        AddressableImportSettings importSettings);
}