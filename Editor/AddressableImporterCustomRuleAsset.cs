using System;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif


public abstract class AddressableImporterCustomRuleAsset : ScriptableObject,
    ISearchFilterable
{

    public virtual string Name => string.IsNullOrEmpty(name) ? GetType().Name : name;
    
    public abstract void Import(AddressableAssetRuleData[] importData,
        AddressableAssetSettings settings,
        AddressableImportSettings importSettings);

    public virtual bool IsMatch(string searchString)
    {
        if (string.IsNullOrEmpty(searchString)) return true;
        return !string.IsNullOrEmpty(Name) && 
               Name.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}