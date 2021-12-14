using UnityEditor.AddressableAssets.Settings;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

public interface IAddressableImporterCustomRule : ISearchFilterable
{
    string Name { get; }
    
    public bool Enabled{ get; }

    void Import(AddressableAssetRuleData[] importData,
        AddressableAssetSettings settings,
        AddressableImportSettings importSettings);
}