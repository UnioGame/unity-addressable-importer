using System;
using UnityEditor.AddressableAssets.Settings;

[Serializable]
public abstract class AddressableImporterCustomRule : IAddressableImporterCustomRule
{
    public string ruleName = string.Empty;

    public bool enabled = true;
    
    public virtual string Name => string.IsNullOrEmpty(ruleName) ? GetType().Name : ruleName;

    public virtual bool Enabled => enabled;
    
    public abstract bool Import(AddressableAssetRuleData importData,
        AddressableAssetSettings settings,
        AddressableImportSettings importSettings);
    
    public virtual bool IsMatch(string searchString)
    {
        if (string.IsNullOrEmpty(searchString)) return true;
        return !string.IsNullOrEmpty(Name) && 
               Name.IndexOf(searchString, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}