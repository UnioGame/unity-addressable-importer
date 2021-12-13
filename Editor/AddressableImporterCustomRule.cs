using System;
using UnityEditor.AddressableAssets.Settings;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

[Serializable]
public abstract class AddressableImporterCustomRule
    : ISearchFilterable
{
    public string ruleName = string.Empty;
    
    public virtual string Name => string.IsNullOrEmpty(ruleName) ? GetType().Name : ruleName;
    
    public abstract void Import(string assetPath,
        string movedFromAssetPath,
        AddressableAssetSettings settings,
        AddressableImportSettings importSettings);
    
    public virtual bool IsMatch(string searchString)
    {
        if (string.IsNullOrEmpty(searchString)) return true;
        return !string.IsNullOrEmpty(Name) && 
               Name.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}