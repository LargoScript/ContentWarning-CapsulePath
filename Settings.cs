using UnityEngine;
using Zorro.Settings;

namespace CapsulePath
{
    // Picked up automatically by the game's SettingsHandler assembly scan
    // ([ContentWarningSetting]) and shown in the MODS tab of the settings menu.

    [ContentWarningSetting]
    public class CapsulePathRecalculateKeySetting : KeyCodeSetting, IExposedSetting
    {
        protected override KeyCode GetDefaultKey() => KeyCode.K;

        public SettingCategory GetSettingCategory() => SettingCategory.Mods;

        public string GetDisplayName() => "[CapsulePath] Recalculate path key";
    }

    [ContentWarningSetting]
    public class CapsulePathToggleKeySetting : KeyCodeSetting, IExposedSetting
    {
        protected override KeyCode GetDefaultKey() => KeyCode.O;

        public SettingCategory GetSettingCategory() => SettingCategory.Mods;

        public string GetDisplayName() => "[CapsulePath] Toggle path key";
    }

    [ContentWarningSetting]
    public class CapsulePathHideFromCameraSetting : BoolSetting, IExposedSetting
    {
        protected override bool GetDefaultValue() => true;

        public override void ApplyValue() { }

        public SettingCategory GetSettingCategory() => SettingCategory.Mods;

        public string GetDisplayName() => "[CapsulePath] Hide path in camera footage";
    }
}
