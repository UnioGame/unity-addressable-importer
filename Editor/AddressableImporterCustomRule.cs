using System;
using UnityEditor.AddressableAssets.Settings;

[Serializable]
public abstract class AddressableImporterCustomRule : IAddressableImporterCustomRule
{
    public string ruleName = string.Empty;
    
    public virtual string Name => string.IsNullOrEmpty(ruleName) ? GetType().Name : ruleName;
    
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