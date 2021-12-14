using UnityEditor.AddressableAssets.Settings;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

public interface IAddressableImporterCustomRule : ISearchFilterable
{
    string Name { get; }

    void Import(string assetPath,
        string movedFromAssetPath,
        AddressableAssetSettings settings,
        AddressableImportSettings importSettings);
}