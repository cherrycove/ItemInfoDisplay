using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using HarmonyLib;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;

using TMPro;

using UnityEngine;

using Zorro.Core;

namespace ItemInfoDisplay;

[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    private static GUIManager guiManager;
    private static TextMeshProUGUI itemInfoDisplayTextMesh;
    private static Dictionary<string, string> effectColors = new Dictionary<string, string>();
    private static float lastKnownSinceItemAttach;
    private static bool hasChanged;
    private static ConfigEntry<float> configFontSize;
    private static ConfigEntry<float> configOutlineWidth;
    private static ConfigEntry<float> configLineSpacing;
    private static ConfigEntry<float> configSizeDeltaX;
    private static ConfigEntry<float> configForceUpdateTime;
    private static ConfigEntry<bool> configEnableTestMode;

    // 测试模式变量
    private static List<Item> allItemPrefabs = new List<Item>();
    private static int currentTestItemIndex = 0;
    private static bool testModeInitialized = false;
    private static bool testModeInputDisabled = false;
    private static bool inputSystemChecked = false;
    private static bool inputSystemAvailable = false;
    private static PropertyInfo inputSystemKeyboardCurrentProp;
    private static PropertyInfo inputSystemKeyboardItemProp;
    private static PropertyInfo inputSystemKeyControlPressedProp;
    private static Type inputSystemKeyType;
    private static bool legacyInputAvailable = true;
    private static HashSet<string> missingEffectColors = new HashSet<string>();
    private static int lastLanternItemId = 0;
    private static int lastLanternRemainingSeconds = -1;
    private static Dictionary<string, string> itemNameKeyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static bool itemNameKeyMapInitialized = false;
    private static bool cookingMultiplierResolved = false;
    private static Func<ItemCooking, float>? cookingMultiplierGetter;
    private static string? cookingMultiplierSource;

    private void Awake()
    {
        Log = Logger;
        InitEffectColors(effectColors);
        lastKnownSinceItemAttach = 0f;
        hasChanged = true;
        configFontSize = ((BaseUnityPlugin)this).Config.Bind<float>("ItemInfoDisplay", "Font Size", 20f, "Customize the Font Size for description text.");
        configOutlineWidth = ((BaseUnityPlugin)this).Config.Bind<float>("ItemInfoDisplay", "Outline Width", 0.08f, "Customize the Outline Width for item description text.");
        configLineSpacing = ((BaseUnityPlugin)this).Config.Bind<float>("ItemInfoDisplay", "Line Spacing", -35f, "Customize the Line Spacing for item description text.");
        configSizeDeltaX = ((BaseUnityPlugin)this).Config.Bind<float>("ItemInfoDisplay", "Size Delta X", 550f, "Customize the horizontal length of the container for the mod. Increasing moves text left, decreasing moves text right.");
        configForceUpdateTime = ((BaseUnityPlugin)this).Config.Bind<float>("ItemInfoDisplay", "Force Update Time", 1f, "Customize the time in seconds until the mod forces an update for the item.");
        configEnableTestMode = ((BaseUnityPlugin)this).Config.Bind<bool>("ItemInfoDisplay", "Enable Test Mode", false, "Enable test mode to cycle through all items with F9/F10 keys.");
        Harmony.CreateAndPatchAll(typeof(ItemInfoDisplayUpdatePatch));
        Harmony.CreateAndPatchAll(typeof(ItemInfoDisplayEquipPatch));
        Harmony.CreateAndPatchAll(typeof(ItemInfoDisplayFinishCookingPatch));
        Harmony.CreateAndPatchAll(typeof(ItemInfoDisplayReduceUsesRPCPatch));
        Log.LogInfo($"Plugin {Name} is loaded!");
    }

    private static class ItemInfoDisplayUpdatePatch
    {
        [HarmonyPatch(typeof(CharacterItems), "Update")]
        [HarmonyPostfix]
        private static void ItemInfoDisplayUpdate(CharacterItems __instance)
        {
            try
            {
                if (guiManager == null)
                {
                    AddDisplayObject();
                }
                else
                {
                    // 测试模式：按键循环物品
                    if (configEnableTestMode.Value
                        && Character.localCharacter != null
                        && ReferenceEquals(__instance.character, Character.localCharacter))
                    {
                        HandleTestModeInput();
                    }

                    if (Character.observedCharacter.data.currentItem != null)
                    {
                        UpdateLanternRefreshFlag(Character.observedCharacter.data.currentItem);
                        if (hasChanged)
                        {
                            hasChanged = false;
                            ProcessItemGameObject();
                        }
                        else if (Mathf.Abs(Character.observedCharacter.data.sinceItemAttach - lastKnownSinceItemAttach) >= configForceUpdateTime.Value)
                        {
                            hasChanged = true;
                            lastKnownSinceItemAttach = Character.observedCharacter.data.sinceItemAttach;
                        }

                        if (!itemInfoDisplayTextMesh.gameObject.activeSelf)
                        {
                            itemInfoDisplayTextMesh.gameObject.SetActive(true);
                        }
                    }
                    else
                    {
                        if (itemInfoDisplayTextMesh.gameObject.activeSelf)
                        {
                            itemInfoDisplayTextMesh.gameObject.SetActive(false);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError(e.Message + e.StackTrace);
            }
        }
    }

    private static class ItemInfoDisplayEquipPatch
    {
        [HarmonyPatch(typeof(CharacterItems), "Equip")]
        [HarmonyPostfix]
        private static void ItemInfoDisplayEquip(CharacterItems __instance)
        {
            try
            {
                if (Character.ReferenceEquals(Character.observedCharacter, __instance.character))
                {
                    hasChanged = true;
                }
            }
            catch (Exception e)
            {
                Log.LogError(e.Message + e.StackTrace);
            }
        }
    }

    private static class ItemInfoDisplayFinishCookingPatch
    {
        [HarmonyPatch(typeof(ItemCooking), "FinishCooking")]
        [HarmonyPostfix]
        private static void ItemInfoDisplayFinishCooking(ItemCooking __instance)
        {
            try
            {
                if (Character.ReferenceEquals(Character.observedCharacter, __instance.item.holderCharacter))
                {
                    hasChanged = true;
                }
            }
            catch (Exception e)
            {
                Log.LogError(e.Message + e.StackTrace);
            }
        }
    }

    private static class ItemInfoDisplayReduceUsesRPCPatch
    {
        [HarmonyPatch(typeof(Action_ReduceUses), "ReduceUsesRPC")]
        [HarmonyPostfix]
        private static void ItemInfoDisplayReduceUsesRPC(Action_ReduceUses __instance)
        {
            try
            {
                if (Character.ReferenceEquals(Character.observedCharacter, __instance.character))
                {
                    hasChanged = true;
                }
            }
            catch (Exception e)
            {
                Log.LogError(e.Message + e.StackTrace);
            }
        }
    }

    private static void ProcessItemGameObject()
    {
        Item item = Character.observedCharacter.data.currentItem; // not sure why this broke after THE MESA update, made no changes (just rebuilt)
        ProcessItemGameObject(item);
    }

    private static void ProcessItemGameObject(Item item)
    {
        GameObject itemGameObj = item.gameObject;
        Component[] itemComponents = itemGameObj
            .GetComponents(typeof(Component))
            .Where(c => c != null)
            .GroupBy(c => c.GetType())
            .Select(g => g.First())
            .ToArray();
        ItemCooking itemCooking = null;
        if (itemComponents != null)
        {
            for (int i = 0; i < itemComponents.Length; i++)
            {
                if (itemComponents[i] is ItemCooking cookingComponent)
                {
                    itemCooking = cookingComponent;
                    break;
                }
            }
        }
        bool hasCookingExplosion = false;
        bool hasCookingWreck = false;
        bool hasCookUseExplosion = false;
        List<(string statusType, float amount)> cookingStatusAdjustments = new List<(string statusType, float amount)>();

        if (itemCooking != null && itemCooking.additionalCookingBehaviors != null)
        {
            foreach (var behavior in itemCooking.additionalCookingBehaviors)
            {
                if (behavior == null)
                {
                    continue;
                }

                if (behavior is CookingBehavior_Explode)
                {
                    hasCookingExplosion = true;
                    continue;
                }
                if (behavior is CookingBehavior_Wreck)
                {
                    hasCookingWreck = true;
                    continue;
                }
                if (behavior is CookingBehavior_AdjustStatusInstantly adjustStatus)
                {
                    if (adjustStatus.statusType != default || adjustStatus.amount != 0f)
                    {
                        cookingStatusAdjustments.Add((adjustStatus.statusType.ToString(), adjustStatus.amount));
                    }
                    continue;
                }
                if (behavior is CookingBehavior_EnableScripts enableScripts)
                {
                    if (!hasCookUseExplosion && enableScripts.scriptsToEnable != null)
                    {
                        foreach (MonoBehaviour script in enableScripts.scriptsToEnable)
                        {
                            if (script != null && ContainsExplodeKeyword(script.GetType().Name))
                            {
                                hasCookUseExplosion = true;
                                break;
                            }
                        }
                    }
                    continue;
                }
                if (behavior is CookingBehavior_RunActions runActions)
                {
                    if (!hasCookingExplosion && runActions.actionsToRun != null)
                    {
                        foreach (ItemAction action in runActions.actionsToRun)
                        {
                            if (action == null)
                            {
                                continue;
                            }
                            if (ContainsExplodeKeyword(action.GetType().Name))
                            {
                                hasCookingExplosion = true;
                                break;
                            }
                            if (action is Action_Spawn spawnAction && spawnAction.objectToSpawn != null)
                            {
                                if (ContainsExplodeKeyword(spawnAction.objectToSpawn.name))
                                {
                                    hasCookingExplosion = true;
                                    break;
                                }
                            }
                        }
                    }
                    continue;
                }

                if (!hasCookUseExplosion && HasExplodeOnUseBehavior(behavior))
                {
                    hasCookUseExplosion = true;
                }
            }
        }
        if (itemCooking != null && !hasCookingExplosion && itemCooking.explosionPrefab != null)
        {
            hasCookingExplosion = true;
        }
        float cookingMultiplier = GetCookingEffectMultiplier(itemCooking);
        bool isConsumable = false;
        string prefixStatus = "";
        string suffixWeight = "";
        string suffixUses = "";
        string suffixCooked = "";
        string suffixAfflictions = "";
        itemInfoDisplayTextMesh.text = "";

        if (Ascents.itemWeightModifier > 0)
        {
            suffixWeight += $"{GetText("WEIGHT", effectColors["Weight"], ((item.carryWeight + Ascents.itemWeightModifier) * 2.5f).ToString("F1").Replace(".0", ""))}</color>";
        }
        else
        {
            suffixWeight += $"{GetText("WEIGHT", effectColors["Weight"], (item.carryWeight * 2.5f).ToString("F1").Replace(".0", ""))}</color>";
        }

        if (itemGameObj.name.Equals("Bugle(Clone)"))
        {
            itemInfoDisplayTextMesh.text += $"{GetText("Bugle")}\n";//MAKE SOME NOISE
        }
        else if (itemGameObj.name.Equals("Pirate Compass(Clone)"))
        {
            itemInfoDisplayTextMesh.text += $"{GetText("Pirate Compass", effectColors["Injury"])}";
            //itemInfoDisplayTextMesh.text += effectColors["Injury"] + "POINTS</color> TO THE NEAREST LUGGAGE\n";
        }
        else if (itemGameObj.name.Equals("Compass(Clone)"))
        {
            itemInfoDisplayTextMesh.text += $"{GetText("Compass", effectColors["Injury"])}";
            //itemInfoDisplayTextMesh.text += effectColors["Injury"] + "POINTS</color> NORTH TO THE PEAK\n";
        }
        else if (itemGameObj.name.Equals("Shell Big(Clone)"))
        {
            itemInfoDisplayTextMesh.text += $"{GetText("Shell Big", effectColors["Hunger"])}";
            //itemInfoDisplayTextMesh.text += "TRY " + effectColors["Hunger"] + "THROWING</color> AT A COCONUT\n";
        }

        for (int i = 0; i < itemComponents.Length; i++)
        {
            if (itemComponents[i].GetType() == typeof(ItemUseFeedback))
            {
                ItemUseFeedback itemUseFeedback = (ItemUseFeedback)itemComponents[i];
                if (itemUseFeedback.useAnimation.Equals("Eat") || itemUseFeedback.useAnimation.Equals("Drink") || itemUseFeedback.useAnimation.Equals("Heal"))
                {
                    isConsumable = true;
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_Consume))
            {
                isConsumable = true;
            }
            else if (itemComponents[i].GetType() == typeof(Action_RestoreHunger))
            {
                Action_RestoreHunger effect = (Action_RestoreHunger)itemComponents[i];
                float restorationAmount = ApplyCookingMultiplier(effect.restorationAmount, cookingMultiplier);
                prefixStatus += ProcessEffect((restorationAmount * -1f), "Hunger");
            }
            else if (itemComponents[i].GetType() == typeof(Action_GiveExtraStamina))
            {
                Action_GiveExtraStamina effect = (Action_GiveExtraStamina)itemComponents[i];
                float staminaAmount = ApplyCookingMultiplier(effect.amount, cookingMultiplier);
                prefixStatus += ProcessEffect(staminaAmount, "Extra Stamina");
            }
            else if (itemComponents[i].GetType() == typeof(Action_InflictPoison))
            {
                Action_InflictPoison effect = (Action_InflictPoison)itemComponents[i];
                float poisonPerSecond = ApplyCookingMultiplier(effect.poisonPerSecond, cookingMultiplier);
                prefixStatus += GetText("InflictPoison", effect.delay.ToString(), ProcessEffectOverTime(poisonPerSecond, 1f, effect.inflictionTime, "Poison"));
                //prefixStatus += "AFTER " + effect.delay.ToString() + "s, " + ProcessEffectOverTime(effect.poisonPerSecond, 1f, effect.inflictionTime, "Poison");
            }
            else if (itemComponents[i].GetType() == typeof(Action_AddOrRemoveThorns))
            {
                Action_AddOrRemoveThorns effect = (Action_AddOrRemoveThorns)itemComponents[i];
                float thornsAmount = ApplyCookingMultiplier((effect.thornCount * 0.05f), cookingMultiplier);
                prefixStatus += ProcessEffect(thornsAmount, "Thorns"); // TODO: Search for thorns amount per applied thorn
            }
            else if (itemComponents[i].GetType() == typeof(Action_ModifyStatus))
            {
                Action_ModifyStatus effect = (Action_ModifyStatus)itemComponents[i];
                float changeAmount = ApplyCookingMultiplier(effect.changeAmount, cookingMultiplier);
                prefixStatus += ProcessEffect(changeAmount, effect.statusType.ToString());
            }
            else if (itemComponents[i].GetType() == typeof(Action_RandomMushroomEffect))
            {
                string randomText = BuildRandomMushroomEffectText(itemComponents[i]);
                itemInfoDisplayTextMesh.text = AppendWithSectionSpacing(itemInfoDisplayTextMesh.text, randomText);
            }
            else if (itemComponents[i].GetType() == typeof(Action_ApplyMassAffliction))
            {
                Action_ApplyMassAffliction effect = (Action_ApplyMassAffliction)itemComponents[i];
                suffixAfflictions += $"{GetText("ApplyMassAffliction")}";
                //suffixAfflictions += "<#CCCCCC>NEARBY PLAYERS WILL RECEIVE:</color>\n";
                suffixAfflictions += ProcessAffliction(effect.affliction, cookingMultiplier);
                if (effect.extraAfflictions != null && effect.extraAfflictions.Length > 0)
                {
                    for (int j = 0; j < effect.extraAfflictions.Length; j++)
                    {
                        if (suffixAfflictions.EndsWith('\n'))
                        {
                            suffixAfflictions = suffixAfflictions.Remove(suffixAfflictions.Length - 1);
                        }
                        suffixAfflictions += ",\n" + ProcessAffliction(effect.extraAfflictions[j], cookingMultiplier);
                    }
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_ApplyAffliction))
            {
                Action_ApplyAffliction effect = (Action_ApplyAffliction)itemComponents[i];
                suffixAfflictions += ProcessAffliction(effect.affliction, cookingMultiplier);
            }
            else if (itemComponents[i].GetType() == typeof(Action_ClearAllStatus))
            {
                Action_ClearAllStatus effect = (Action_ClearAllStatus)itemComponents[i];
                string clearAllStatusText = GetText("ClearAllStatus_Base", effectColors["ItemInfoDisplayPositive"]);
                if (effect.excludeCurse)
                {
                    clearAllStatusText += GetText("ClearAllStatus_ExceptCurse", effectColors["Curse"]);
                }
                if (effect.otherExclusions.Count > 0)
                {
                    foreach (CharacterAfflictions.STATUSTYPE exclusion in effect.otherExclusions)
                    {
                        clearAllStatusText += ", " + effectColors[exclusion.ToString()] + GetText($"Effect_{exclusion.ToString().ToUpper()}").ToUpper() + "</color>";
                    }
                }
                clearAllStatusText = clearAllStatusText.Replace(", <#E13542>" + GetText("Effect_CRAB").ToUpper() + "</color>", "") + "\n";
                itemInfoDisplayTextMesh.text += clearAllStatusText;
            }
            else if (itemComponents[i].GetType() == typeof(Action_ConsumeAndSpawn))
            {
                Action_ConsumeAndSpawn effect = (Action_ConsumeAndSpawn)itemComponents[i];
                if (effect.itemToSpawn.ToString().Contains("Peel"))
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("ConsumeAndSpawn_Peel")}";
                    //itemInfoDisplayTextMesh.text += "<#CCCCCC>GAIN A PEEL WHEN EATEN</color>\n";
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_ReduceUses))
            {
                if (item?.data?.data != null && item.data.data.TryGetValue(DataEntryKey.ItemUses, out DataEntryValue usesData) && usesData is OptionableIntItemData uses && uses.HasData)
                {
                    if (uses.Value > 1)
                    {
                        suffixUses += $"{GetText("ReduceUses", uses.Value.ToString())}";
                        //suffixUses += "   " + uses.Value + " USES";
                    }
                }
            }
            else if (itemComponents[i].GetType() == typeof(Lantern))
            {
                Lantern lantern = (Lantern)itemComponents[i];
                if (itemGameObj.name.Equals("Torch(Clone)"))
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("Torch")}";
                    //itemInfoDisplayTextMesh.text += "CAN BE LIT\n";
                }
                else
                {
                    suffixAfflictions += GetText("Lantern");
                    //suffixAfflictions += "<#CCCCCC>WHEN LIT, NEARBY PLAYERS RECEIVE:</color>\n";
                }

                if (!itemGameObj.name.Equals("Torch(Clone)"))
                {
                    int remainingSeconds = GetLanternRemainingSecondsInt(item, lantern);
                    suffixAfflictions += GetText("LanternRemaining", remainingSeconds.ToString());
                    suffixAfflictions = AppendLanternStatusPerSecond(itemGameObj, suffixAfflictions);
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_RaycastDart))
            {
                Action_RaycastDart effect = (Action_RaycastDart)itemComponents[i];
                isConsumable = true;
                suffixAfflictions += $"{GetText("RaycastDart")}";
                //suffixAfflictions += "<#CCCCCC>SHOOT A DART THAT WILL APPLY:</color>\n";
                if (effect.afflictionsOnHit != null)
                {
                    for (int j = 0; j < effect.afflictionsOnHit.Length; j++)
                    {
                        suffixAfflictions += ProcessAffliction(effect.afflictionsOnHit[j], cookingMultiplier);
                        if (suffixAfflictions.EndsWith('\n'))
                        {
                            suffixAfflictions = suffixAfflictions.Remove(suffixAfflictions.Length - 1);
                        }
                        suffixAfflictions += ",\n";
                    }
                }
                if (suffixAfflictions.EndsWith('\n'))
                {
                    suffixAfflictions = suffixAfflictions.Remove(suffixAfflictions.Length - 2);
                }
                suffixAfflictions += "\n";
            }
            else if (itemComponents[i].GetType() == typeof(MagicBugle))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("MagicBugle")}";
                //itemInfoDisplayTextMesh.text += "WHILE PLAYING THE BUGLE,";
            }
            else if (itemComponents[i].GetType() == typeof(ClimbingSpikeComponent))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("ClimbingSpike", effectColors["Extra Stamina"])}";
                //itemInfoDisplayTextMesh.text += "PLACE A PITON YOU CAN GRAB\nTO " + effectColors["Extra Stamina"] + "REGENERATE STAMINA</color>\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_Flare))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Flare")}";
                //itemInfoDisplayTextMesh.text += "CAN BE LIT\n";
            }
            else if (itemComponents[i].GetType() == typeof(Backpack))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Backpack")}";
                //itemInfoDisplayTextMesh.text += "DROP TO PLACE ITEMS INSIDE\n";
            }
            else if (itemComponents[i].GetType() == typeof(BananaPeel))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("BananaPeel", effectColors["Hunger"])}";
                //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "SLIP</color> WHEN STEPPED ON\n";
            }
            else if (itemComponents[i].GetType() == typeof(Constructable))
            {
                Constructable effect = (Constructable)itemComponents[i];
                if (effect.constructedPrefab != null && effect.constructedPrefab.name.Equals("PortableStovetop_Placed"))
                {
                    Campfire campfire = effect.constructedPrefab.GetComponent<Campfire>();
                    if (campfire != null)
                    {
                        itemInfoDisplayTextMesh.text += $"{GetText("Constructable_PortableStovetop_Placed", effectColors["Injury"], campfire.burnsFor.ToString())}";
                    }
                    else
                    {
                        itemInfoDisplayTextMesh.text += $"{GetText("Constructable")}";
                    }
                    //itemInfoDisplayTextMesh.text += "PLACE A " + effectColors["Injury"] + "COOKING</color> STOVE FOR " + effect.constructedPrefab.GetComponent<Campfire>().burnsFor.ToString() + "s\n";
                }
                else
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("Constructable")}";
                    //itemInfoDisplayTextMesh.text += "CAN BE PLACED\n";
                }
            }
            else if (itemComponents[i].GetType() == typeof(RopeSpool))
            {
                RopeSpool effect = (RopeSpool)itemComponents[i];
                if (effect.isAntiRope)
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("RopeSpool_AntiRope")}";
                    //itemInfoDisplayTextMesh.text += "PLACE A ROPE THAT FLOATS UP\n";
                }
                else
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("RopeSpool")}";
                    //itemInfoDisplayTextMesh.text += "PLACE A ROPE\n";
                }
                itemInfoDisplayTextMesh.text += $"{GetText("RopeSpool_TIP", (effect.minSegments / 4f).ToString("F2").Replace(".0", ""), (Rope.MaxSegments / 4f).ToString("F1").Replace(".0", ""))}";
                //itemInfoDisplayTextMesh.text += "FROM " + (effect.minSegments / 4f).ToString("F2").Replace(".0", "") + "m LONG, UP TO " 
                //    + (Rope.MaxSegments / 4f).ToString("F1").Replace(".0", "") + "m LONG\n";
                //using force update here for remaining length since Rope has no character distinction for Detach_Rpc() hook, maybe unless OK with any player triggering this
                if (configForceUpdateTime.Value <= 1f)
                {
                    suffixUses += GetText("RopeSpool_Left", (effect.RopeFuel / 4f).ToString("F2").Replace(".00", ""));
                }
            }
            else if (itemComponents[i].GetType() == typeof(RopeShooter))
            {
                RopeShooter effect = (RopeShooter)itemComponents[i];
                if (effect.ropeAnchorWithRopePref != null && effect.ropeAnchorWithRopePref.name.Equals("RopeAnchorForRopeShooterAnti"))
                {
                    itemInfoDisplayTextMesh.text += GetText("RopeShooter_Anti", (effect.maxLength / 4f).ToString("F1").Replace(".0", ""));
                    //itemInfoDisplayTextMesh.text += "SHOOT A ROPE ANCHOR WHICH PLACES\nA ROPE THAT ";
                    //itemInfoDisplayTextMesh.text += "FLOATS UP ";
                    //itemInfoDisplayTextMesh.text +=  + "";
                }
                else
                {
                    itemInfoDisplayTextMesh.text += GetText("RopeShooter", (effect.maxLength / 4f).ToString("F1").Replace(".0", ""));
                    //itemInfoDisplayTextMesh.text += "DROPS DOWN ";
                }
                //itemInfoDisplayTextMesh.text += (effect.maxLength / 4f).ToString("F1").Replace(".0", "") + "m\n";
            }
            else if (itemComponents[i].GetType() == typeof(Antigrav))
            {
                Antigrav effect = (Antigrav)itemComponents[i];
                if (effect.intensity != 0f)
                {
                    suffixAfflictions += $"{GetText("Antigrav", effectColors["Injury"])}";
                    //suffixAfflictions += effectColors["Injury"] + "WARNING:</color> <#CCCCCC>FLIES AWAY IF DROPPED</color>\n";
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_Balloon))
            {
                suffixAfflictions += $"{GetText("Balloon")}";
                //suffixAfflictions += "CAN ATTACH TO CHARACTER\n";
            }
            else if (itemComponents[i].GetType() == typeof(VineShooter))
            {
                VineShooter effect = (VineShooter)itemComponents[i];
                itemInfoDisplayTextMesh.text += $"{GetText("VineShooter", (effect.maxLength / (5f / 3f)).ToString("F1").Replace(".0", ""))}";
                //itemInfoDisplayTextMesh.text += "SHOOT A CHAIN THAT CONNECTS FROM\nYOUR POSITION TO WHERE YOU SHOOT\nUP TO "
                //    + (effect.maxLength / (5f / 3f)).ToString("F1").Replace(".0", "") + "m AWAY\n";
            }
            else if (itemComponents[i].GetType() == typeof(ShelfShroom))
            {
                ShelfShroom effect = (ShelfShroom)itemComponents[i];
                if (effect.instantiateOnBreak == null)
                {
                    Log.LogWarning("[ItemInfoDisplay] ShelfShroom instantiateOnBreak was null. Skipping detailed info.");
                }
                else if (effect.instantiateOnBreak.name.Equals("HealingPuffShroomSpawn"))
                {
                    itemInfoDisplayTextMesh.text += GetText("HealingPuffShroom", effectColors["Hunger"]);
                    itemInfoDisplayTextMesh.text = AppendAoeInfoFromPrefab(effect.instantiateOnBreak, itemInfoDisplayTextMesh.text, cookingMultiplier);
                }
                else if (effect.instantiateOnBreak.name.Equals("ShelfShroomSpawn"))
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("ShelfShroomSpawn", effectColors["Hunger"])}";
                    //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> TO DEPLOY A PLATFORM\n";
                }
                else if (effect.instantiateOnBreak.name.Equals("BounceShroomSpawn"))
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("BounceShroomSpawn", effectColors["Hunger"])}";
                    //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> TO DEPLOY A BOUNCE PAD\n";
                }
            }
            else if (itemComponents[i].GetType() == typeof(ScoutEffigy))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("ScoutEffigy", effectColors["Extra Stamina"])}";
                //itemInfoDisplayTextMesh.text += effectColors["Extra Stamina"] + "REVIVE</color> A DEAD PLAYER\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_Die))
            {
                itemInfoDisplayTextMesh.text += GetText("Action_Die", effectColors["Curse"]);
            }
            else if (itemComponents[i].GetType() == typeof(Action_SpawnGuidebookPage))
            {
                isConsumable = true;
                itemInfoDisplayTextMesh.text += $"{GetText("SpawnGuidebookPage")}";
                //itemInfoDisplayTextMesh.text += "CAN BE OPENED\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_Guidebook))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Guidebook")}";
                //itemInfoDisplayTextMesh.text += "CAN BE READ\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_CallScoutmaster))
            {
                itemInfoDisplayTextMesh.text = AppendWithSectionSpacing(itemInfoDisplayTextMesh.text, $"{GetText("CallScoutmaster", effectColors["Injury"])}");
                //itemInfoDisplayTextMesh.text += effectColors["Injury"] + "BREAKS RULE 0 WHEN USED</color>\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_MoraleBoost))
            {
                Action_MoraleBoost effect = (Action_MoraleBoost)itemComponents[i];
                float staminaBoost = ApplyCookingMultiplier(effect.baselineStaminaBoost, cookingMultiplier);
                if (effect.boostRadius < 0)
                {
                    itemInfoDisplayTextMesh.text += GetText("MoraleBoost_Self", effectColors["ItemInfoDisplayPositive"], effectColors["Extra Stamina"], (staminaBoost * 100f).ToString("F1").Replace(".0", ""));
                }
                else if (effect.boostRadius > 0)
                {
                    itemInfoDisplayTextMesh.text += GetText("MoraleBoost_Nearby", effectColors["ItemInfoDisplayPositive"], effectColors["Extra Stamina"], (staminaBoost * 100f).ToString("F1").Replace(".0", ""));
                }
            }
            else if (itemComponents[i].GetType() == typeof(Breakable))
            {
                itemInfoDisplayTextMesh.text = AppendWithSectionSpacing(itemInfoDisplayTextMesh.text, $"{GetText("Breakable", effectColors["Hunger"])}");
                //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> TO CRACK OPEN\n";
            }
            else if (itemComponents[i].GetType() == typeof(Bonkable))
            {
                itemInfoDisplayTextMesh.text = AppendWithSectionSpacing(itemInfoDisplayTextMesh.text, $"{GetText("Bonkable", effectColors["Hunger"], effectColors["Injury"])}");
                //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> AT HEAD TO " + effectColors["Injury"] + "BONK</color>\n";
            }
            else if (itemComponents[i].GetType() == typeof(MagicBean))
            {
                MagicBean effect = (MagicBean)itemComponents[i];
                itemInfoDisplayTextMesh.text += $"{GetText("MagicBean", effectColors["Hunger"], (effect.plantPrefab.maxLength / 2f).ToString("F1").Replace(".0", ""))}";
                //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> TO PLANT A VINE THAT GROWS\nPERPENDICULAR TO TERRAIN UP TO\n"
                //    + (effect.plantPrefab.maxLength / 2f).ToString("F1").Replace(".0", "") + "m OR UNTIL IT HITS SOMETHING\n";
            }
            else if (itemComponents[i].GetType() == typeof(BingBong))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("BingBong")}";
                //itemInfoDisplayTextMesh.text += "MASCOT OF BINGBONG AIRWAYS\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_Passport))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Passport")}\n";//OPEN TO CUSTOMIZE CHARACTER
            }
            else if (itemComponents[i].GetType() == typeof(Actions_Binoculars))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Binoculars")}";
                //itemInfoDisplayTextMesh.text += "USE TO LOOK FURTHER\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_WarpToRandomPlayer))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("WarpToRandomPlayer")}";
                //itemInfoDisplayTextMesh.text += "WARP TO RANDOM PLAYER\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_WarpToBiome))
            {
                Action_WarpToBiome effect = (Action_WarpToBiome)itemComponents[i];
                itemInfoDisplayTextMesh.text += $"{GetText("WarpToBiome", effect.segmentToWarpTo.ToString().ToUpper())}";
            }
            else if (itemComponents[i].GetType() == typeof(Parasol))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Parasol")}\n";
                //itemInfoDisplayTextMesh.text += "OPEN TO SLOW YOUR DESCENT\n";
            }
            else if (itemComponents[i].GetType() == typeof(Frisbee))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Frisbee", effectColors["Hunger"])}";
                //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> IT\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_ConstructableScoutCannonScroll))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("ConstructableScoutCannonScroll")}";
                //itemInfoDisplayTextMesh.text += "\n<#CCCCCC>WHEN PLACED, LIGHT FUSE TO:</color>\nLAUNCH SCOUTS IN BARREL\n";
                //+ "\n<#CCCCCC>LIMITS GRAVITATIONAL ACCELERATION\n(PREVENTS OR LOWERS FALL DAMAGE)</color>\n";
            }
            else if (itemComponents[i].GetType() == typeof(Dynamite))
            {
                Dynamite effect = (Dynamite)itemComponents[i];
                itemInfoDisplayTextMesh.text += GetText("Dynamite", effectColors["Injury"], (effect.explosionPrefab.GetComponent<AOE>().statusAmount * 100f).ToString("F1").Replace(".0", ""));
            }
            else if (itemComponents[i].GetType() == typeof(Scorpion))
            {
                Scorpion effect = (Scorpion)itemComponents[i];
                if (configForceUpdateTime.Value <= 1f)
                {
                    Character holderForStatus = item.holderCharacter ?? Character.observedCharacter;
                    float statusSum = (holderForStatus != null && holderForStatus.refs != null && holderForStatus.refs.afflictions != null)
                        ? holderForStatus.refs.afflictions.statusSum
                        : 0f;
                    float effectPoison = Mathf.Max(0.5f, (1f - statusSum + 0.05f)) * 100f;
                    itemInfoDisplayTextMesh.text += GetText("Scorpion_Dynamic",
                        effectColors["Poison"],
                        effectColors["Curse"],
                        effectColors["Heat"],
                        effectPoison.ToString("F1").Replace(".0", ""),
                        effect.totalPoisonTime.ToString("F1").Replace(".0", ""));
                }
                else
                {
                    itemInfoDisplayTextMesh.text += GetText("Scorpion_Static",
                        effectColors["Poison"],
                        effectColors["Curse"],
                        effectColors["Heat"],
                        effect.totalPoisonTime.ToString("F1").Replace(".0", ""));
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_Spawn))
            {
                Action_Spawn effect = (Action_Spawn)itemComponents[i];
                if (effect.objectToSpawn != null && effect.objectToSpawn.name.Equals("VFX_Sunscreen"))
                {
                    Transform aoeTransform = effect.objectToSpawn.transform.Find("AOE");
                    AOE effectAOE = aoeTransform != null ? aoeTransform.GetComponent<AOE>() : null;
                    RemoveAfterSeconds effectTime = aoeTransform != null ? aoeTransform.GetComponent<RemoveAfterSeconds>() : null;
                    if (effectAOE != null && effectTime != null)
                    {
                        itemInfoDisplayTextMesh.text += $"{GetText("VFX_Sunscreen", effectTime.seconds.ToString("F1").Replace(".0", ""), ProcessAffliction(effectAOE.affliction, cookingMultiplier))}";
                    }
                    //itemInfoDisplayTextMesh.text += "<#CCCCCC>SPRAY A " + effectTime.seconds.ToString("F1").Replace(".0", "") + "s MIST THAT APPLIES:</color>\n"
                    //    + ProcessAffliction(effectAOE.affliction);
                }
            }
            else if (itemComponents[i].GetType() == typeof(CactusBall))
            {
                CactusBall effect = (CactusBall)itemComponents[i];
                itemInfoDisplayTextMesh.text += GetText("CactusBall", effectColors["Thorns"], effectColors["Hunger"], (effect.throwChargeRequirement * 100f).ToString("F1").Replace(".0", ""));
                //itemInfoDisplayTextMesh.text += "{0}STICKS</color> TO YOUR BODY\n\nCAN {1}THROW</color> BY USING\nAT LEAST {2}% POWER\n";
                //itemInfoDisplayTextMesh.text += effectColors["Thorns"] + "STICKS</color> TO YOUR BODY\n\nCAN " + effectColors["Hunger"] 
                //    + "THROW</color> BY USING\nAT LEAST " + (effect.throwChargeRequirement * 100f).ToString("F1").Replace(".0", "") + "% POWER\n";
            }
            else if (itemComponents[i].GetType() == typeof(BingBongShieldWhileHolding))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("BingBongShieldWhileHolding", effectColors["Shield"])}";
                //itemInfoDisplayTextMesh.text += "<#CCCCCC>WHILE EQUIPPED, GRANTS:</color>\n" + effectColors["Shield"] + "SHIELD</color> (INVINCIBILITY)\n";
            }
            else if (itemComponents[i].GetType() == typeof(RescueHook))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("RescueHook")}";
            }
            else if (itemComponents[i].GetType() == typeof(Beehive))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Beehive", effectColors["Injury"], effectColors["Poison"])}";
            }
            else if (itemComponents[i].GetType() == typeof(BugPhobia))
            {
                itemInfoDisplayTextMesh.text = AppendWithSectionSpacing(itemInfoDisplayTextMesh.text, GetText("BugPhobia"));
            }
            else if (itemComponents[i].GetType() == typeof(Mandrake))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Mandrake", effectColors["Drowsy"], effectColors["Heat"])}";
            }
            else if (itemComponents[i].GetType() == typeof(Snowball))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Snowball", effectColors["Cold"])}";
            }
            else if (itemComponents[i].GetType() == typeof(StickyItemComponent))
            {
                StickyItemComponent effect = (StickyItemComponent)itemComponents[i];
                string stickyText = GetText("StickyItemComponent", effectColors["Hunger"]);
                if (effect.addWeightToStuckPlayer > 0)
                {
                    stickyText += GetText("StickyItemComponent_Weight", effectColors["Weight"], (effect.addWeightToStuckPlayer * 2.5f).ToString("F1").Replace(".0", ""));
                }
                if (effect.addThornsToStuckPlayer > 0)
                {
                    stickyText += GetText("StickyItemComponent_Thorns", effectColors["Thorns"], effect.addThornsToStuckPlayer.ToString());
                }
                itemInfoDisplayTextMesh.text += stickyText;
            }
            else if (itemComponents[i].GetType() == typeof(ItemCooking))
            {
                itemCooking = (ItemCooking)itemComponents[i];
                bool breaksWhenCooked = itemCooking.wreckWhenCooked || hasCookingWreck;
                bool showCookEffectHints = !hasCookingExplosion;

                if (!itemCooking.canBeCooked)
                {
                    suffixCooked += $"{GetText("COOK_DISABLED", effectColors["Curse"])}";
                }
                else if (breaksWhenCooked && (itemCooking.timesCookedLocal >= 1))
                {
                    suffixCooked += $"{GetText("COOKED_BROKEN", effectColors["Curse"])}";
                    //suffixCooked += "\n" + effectColors["Curse"] + "BROKEN FROM COOKING</color>";
                }
                else if (itemCooking.timesCookedLocal >= ItemCooking.COOKING_MAX)
                {
                    suffixCooked += $"{GetText("COOKED_MAX", effectColors["Curse"], itemCooking.timesCookedLocal.ToString())}";
                    //suffixCooked += "   " + effectColors["Curse"] + itemCooking.timesCookedLocal.ToString() + "x COOKED\nCANNOT BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal == 0)
                {
                    suffixCooked += $"\n{GetText("COOK", effectColors["Extra Stamina"])}</color>";//CAN BE COOKED
                    if (breaksWhenCooked)
                    {
                        suffixCooked += $"{GetText("COOK_BROKEN", effectColors["Curse"])}";
                    }
                    else if (hasCookingExplosion)
                    {
                        suffixCooked += $"{GetText("COOK_EXPLODE", effectColors["Injury"])}";
                    }
                    else if (hasCookUseExplosion)
                    {
                        suffixCooked += $"{GetText("COOK_USE_EXPLODE", effectColors["Injury"])}";
                    }
                    else
                    {
                        suffixCooked += $"{GetNextCookHint(itemCooking.timesCookedLocal + 1)}";
                    }

                    if (cookingStatusAdjustments.Count > 0)
                    {
                        suffixCooked += $"{GetText("COOK_ON_COOK")}";
                        bool wroteEffect = false;
                        foreach (var adjustment in cookingStatusAdjustments)
                        {
                            string effectText = ProcessEffectInline(adjustment.amount, adjustment.statusType);
                            if (string.IsNullOrEmpty(effectText))
                            {
                                continue;
                            }
                            if (wroteEffect)
                            {
                                suffixCooked += " ";
                            }
                            suffixCooked += effectText;
                            wroteEffect = true;
                        }
                    }
                }
                else if (itemCooking.timesCookedLocal <= 2)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Extra Stamina"], itemCooking.timesCookedLocal.ToString())}";
                    if (showCookEffectHints)
                    {
                        suffixCooked += $"{GetText("COOKED_BUFF", effectColors["Extra Stamina"])}";
                    }
                    //suffixCooked += "   " + effectColors["Extra Stamina"] + itemCooking.timesCookedLocal.ToString() + "x COOKED</color>\n" + effectColors["Hunger"] + "CAN BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal == 3)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Injury"], itemCooking.timesCookedLocal.ToString())}";
                    if (showCookEffectHints)
                    {
                        suffixCooked += $"{GetText("COOKED_NERF", effectColors["Injury"])}";
                    }

                    //suffixCooked += "   " + effectColors["Injury"] + itemCooking.timesCookedLocal.ToString() + "x COOKED</color>\n" + effectColors["Poison"] + "CAN BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal >= 4)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Poison"], itemCooking.timesCookedLocal.ToString())}";
                    if (showCookEffectHints)
                    {
                        suffixCooked += $"{GetText("COOKED_POISON", effectColors["Poison"])}";
                    }

                    //suffixCooked += "   " + effectColors["Poison"] + itemCooking.timesCookedLocal.ToString() + "x COOKED\nCAN BE COOKED</color>";
                }

                if (itemCooking.canBeCooked
                    && !breaksWhenCooked
                    && showCookEffectHints
                    && itemCooking.timesCookedLocal > 0
                    && itemCooking.timesCookedLocal < ItemCooking.COOKING_MAX)
                {
                    suffixCooked += $"{GetNextCookHint(itemCooking.timesCookedLocal + 1)}";
                }

                if (itemCooking.canBeCooked
                    && hasCookingExplosion
                    && itemCooking.timesCookedLocal > 0
                    && itemCooking.timesCookedLocal < ItemCooking.COOKING_MAX
                    && !breaksWhenCooked)
                {
                    suffixCooked += $"{GetText("COOK_EXPLODE", effectColors["Injury"])}";
                }

                if (hasCookUseExplosion && itemCooking.timesCookedLocal > 0)
                {
                    suffixCooked += $"{GetText("COOK_USE_EXPLODE", effectColors["Injury"])}";
                }
            }
        }

        if ((prefixStatus.Length > 0) && isConsumable)
        {
            itemInfoDisplayTextMesh.text = prefixStatus + "\n" + itemInfoDisplayTextMesh.text;
        }
        if (suffixAfflictions.Length > 0)
        {
            itemInfoDisplayTextMesh.text += "\n" + suffixAfflictions;
        }
        string suffixBlock = suffixWeight + suffixUses + suffixCooked;
        if (!string.IsNullOrEmpty(suffixBlock))
        {
            if (itemInfoDisplayTextMesh.text.Length > 0)
            {
                if (itemInfoDisplayTextMesh.text.EndsWith("\n\n"))
                {
                    // already has a blank line before suffix
                }
                else if (itemInfoDisplayTextMesh.text.EndsWith("\n"))
                {
                    itemInfoDisplayTextMesh.text += "\n";
                }
                else
                {
                    itemInfoDisplayTextMesh.text += "\n\n";
                }
            }
            itemInfoDisplayTextMesh.text += suffixBlock;
        }
        while (itemInfoDisplayTextMesh.text.Contains("\n\n\n"))
        {
            itemInfoDisplayTextMesh.text = itemInfoDisplayTextMesh.text.Replace("\n\n\n", "\n\n");
        }
    }

    private static string AppendWithSectionSpacing(string target, string addition)
    {
        if (string.IsNullOrEmpty(addition))
        {
            return target;
        }

        if (target.Length > 0 && !target.EndsWith("\n\n"))
        {
            target += target.EndsWith("\n") ? "\n" : "\n\n";
        }

        target += addition;
        return target;
    }

    private static string AppendAoeInfoFromPrefab(GameObject prefab, string target, float cookingMultiplier)
    {
        if (prefab == null)
        {
            return target;
        }

        AOE[] aoes = prefab.GetComponentsInChildren<AOE>(true);
        if (aoes == null || aoes.Length == 0)
        {
            Log.LogWarning("[ItemInfoDisplay] No AOE components found on HealingPuffShroom prefab. Effect info unavailable.");
            return target;
        }

        TimeEvent fallbackTimeEvent = prefab.GetComponentInChildren<TimeEvent>(true);
        RemoveAfterSeconds fallbackRemoveAfter = prefab.GetComponentInChildren<RemoveAfterSeconds>(true);

        Dictionary<string, float> instantTotals = new Dictionary<string, float>();
        Dictionary<(string status, float duration), float> overtimeTotals = new Dictionary<(string status, float duration), float>();

        foreach (AOE aoe in aoes)
        {
            if (aoe == null)
            {
                continue;
            }

            string statusType = aoe.statusType.ToString();
            if (string.IsNullOrEmpty(statusType))
            {
                continue;
            }

            TimeEvent timeEvent = aoe.GetComponent<TimeEvent>() ?? aoe.GetComponentInParent<TimeEvent>() ?? fallbackTimeEvent;
            RemoveAfterSeconds removeAfter = aoe.GetComponent<RemoveAfterSeconds>() ?? aoe.GetComponentInParent<RemoveAfterSeconds>() ?? fallbackRemoveAfter;
            if (timeEvent != null && removeAfter != null && timeEvent.rate > 0f && removeAfter.seconds > 0f)
            {
                float amountPerSecond = ApplyCookingMultiplier(aoe.statusAmount / timeEvent.rate, cookingMultiplier);
                var key = (status: statusType, duration: removeAfter.seconds);
                overtimeTotals[key] = overtimeTotals.TryGetValue(key, out float existing) ? (existing + amountPerSecond) : amountPerSecond;
            }
            else
            {
                float amount = ApplyCookingMultiplier(aoe.statusAmount, cookingMultiplier);
                instantTotals[statusType] = instantTotals.TryGetValue(statusType, out float existing) ? (existing + amount) : amount;
            }
        }

        foreach (var kvp in instantTotals.OrderBy(k => k.Key))
        {
            float amount = Mathf.Round(kvp.Value * 40f) / 40f;
            target += ProcessEffect(amount, kvp.Key);
        }

        foreach (var kvp in overtimeTotals.OrderBy(k => k.Key.status).ThenBy(k => k.Key.duration))
        {
            float amountPerSecond = Mathf.Round(kvp.Value * 40f) / 40f;
            target += ProcessEffectPerSecondOverTime(amountPerSecond, kvp.Key.duration, kvp.Key.status);
        }

        return target;
    }

    private static string AppendLanternStatusPerSecond(GameObject itemGameObj, string target)
    {
        if (itemGameObj == null)
        {
            return target;
        }

        StatusField[] fields = itemGameObj.GetComponentsInChildren<StatusField>(true);
        if (fields == null || fields.Length == 0)
        {
            return target;
        }

        Dictionary<string, float> totals = new Dictionary<string, float>();
        Dictionary<string, float> entryTotals = new Dictionary<string, float>();
        foreach (StatusField field in fields)
        {
            if (field == null)
            {
                continue;
            }

            string statusType = field.statusType.ToString();
            if (!string.IsNullOrEmpty(statusType))
            {
                totals[statusType] = totals.TryGetValue(statusType, out float existing) ? (existing + field.statusAmountPerSecond) : field.statusAmountPerSecond;
                if (field.statusAmountOnEntry != 0f)
                {
                    entryTotals[statusType] = entryTotals.TryGetValue(statusType, out float entryExisting)
                        ? (entryExisting + field.statusAmountOnEntry)
                        : field.statusAmountOnEntry;
                }
            }

            foreach (StatusField.StatusFieldStatus status in field.additionalStatuses)
            {
                string additionalType = status.statusType.ToString();
                if (string.IsNullOrEmpty(additionalType))
                {
                    continue;
                }
                totals[additionalType] = totals.TryGetValue(additionalType, out float existing) ? (existing + status.statusAmountPerSecond) : status.statusAmountPerSecond;
            }
        }

        AddFaerieLanternSporesPerSecond(itemGameObj, totals);

        foreach (var kvp in totals.OrderBy(k => k.Key))
        {
            target += ProcessEffectPerSecond(kvp.Value, kvp.Key);
        }

        foreach (var kvp in entryTotals.OrderBy(k => k.Key))
        {
            target += ProcessEffectOnEntry(kvp.Value, kvp.Key);
        }

        if (TryGetDispelFogFieldInfo(itemGameObj, out float innerRadius, out float outerRadius))
        {
            target += GetText(
                "DispelFogField_Strength",
                effectColors["ItemInfoDisplayPositive"],
                GetEffectColor("Spores"),
                innerRadius.ToString("F1").Replace(".0", ""),
                outerRadius.ToString("F1").Replace(".0", ""));
        }

        return target;
    }

    private static void AddFaerieLanternSporesPerSecond(GameObject itemGameObj, Dictionary<string, float> totals)
    {
        if (totals == null || itemGameObj == null)
        {
            return;
        }

        if (!IsFaerieLantern(itemGameObj))
        {
            return;
        }

        const float faerieSporesPerSecond = -0.025f;
        totals["Spores"] = totals.TryGetValue("Spores", out float existing)
            ? (existing + faerieSporesPerSecond)
            : faerieSporesPerSecond;
    }

    private static bool IsFaerieLantern(GameObject itemGameObj)
    {
        if (itemGameObj == null)
        {
            return false;
        }

        string name = itemGameObj.name ?? string.Empty;
        return name.StartsWith("Lantern_Faerie", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDispelFogFieldInfo(GameObject itemGameObj, out float innerRadius, out float outerRadius)
    {
        innerRadius = 0f;
        outerRadius = 0f;
        if (itemGameObj == null)
        {
            return false;
        }

        Component[] components = itemGameObj.GetComponentsInChildren<Component>(true);
        foreach (Component component in components)
        {
            if (component == null)
            {
                continue;
            }

            if (component.GetType().Name == "DispelFogField")
            {
                Type type = component.GetType();
                FieldInfo innerField = type.GetField("innerRadius", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo outerField = type.GetField("outerRadius", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (innerField != null)
                {
                    innerRadius = (float)innerField.GetValue(component);
                }
                if (outerField != null)
                {
                    outerRadius = (float)outerField.GetValue(component);
                }
                return true;
            }
        }

        return false;
    }

    private static string BuildRandomMushroomEffectText(object effect)
    {
        if (effect is Action_RandomMushroomEffect)
        {
            return BuildRandomMushroomEffectListText();
        }

        if (effect == null)
        {
            return GetText("RandomMushroomEffect");
        }

        HashSet<string> statusTypes = new HashSet<string>();
        CollectStatusTypes(effect, statusTypes);

        if (statusTypes.Count == 0)
        {
            return GetText("RandomMushroomEffect");
        }

        List<string> parts = new List<string>();
        foreach (string status in statusTypes)
        {
            parts.Add(FormatEffectNameWithColor(status));
        }

        string listText = string.Join(", ", parts);
        return GetText("RandomMushroomEffect_List", listText);
    }

    private static string BuildRandomMushroomEffectListText()
    {
        string result = GetText("RandomMushroomEffect_Title");
        result += GetText("RandomMushroomEffect_0", effectColors["Extra Stamina"], "4");
        result += GetText("RandomMushroomEffect_1", effectColors["ItemInfoDisplayPositive"], "50", "150", "5", "1");
        result += GetText("RandomMushroomEffect_2", effectColors["ItemInfoDisplayPositive"], "3", "15");
        result += GetText("RandomMushroomEffect_3", effectColors["ItemInfoDisplayPositive"], "10");
        result += GetText("RandomMushroomEffect_4",
            effectColors["ItemInfoDisplayPositive"],
            effectColors["Hunger"],
            effectColors["Injury"],
            effectColors["Poison"],
            "<#CCCCCC>");
        result += GetText("RandomMushroomEffect_5", effectColors["Spores"]);
        result += GetText("RandomMushroomEffect_6", effectColors["ItemInfoDisplayNegative"], "60");
        result += GetText("RandomMushroomEffect_7", effectColors["ItemInfoDisplayNegative"]);
        result += GetText("RandomMushroomEffect_8", effectColors["ItemInfoDisplayNegative"], effectColors["Spores"]);
        result += GetText("RandomMushroomEffect_9", effectColors["ItemInfoDisplayNegative"], "60");
        return result;
    }

    private static void CollectStatusTypes(object value, HashSet<string> statusTypes)
    {
        if (value == null)
        {
            return;
        }

        if (value is CharacterAfflictions.STATUSTYPE statusType)
        {
            statusTypes.Add(statusType.ToString());
            return;
        }

        if (value is Peak.Afflictions.Affliction affliction)
        {
            statusTypes.Add(affliction.GetAfflictionType().ToString());
            return;
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            foreach (object entry in enumerable)
            {
                CollectStatusTypes(entry, statusTypes);
            }
            return;
        }

        Type type = value.GetType();
        if (!type.IsClass)
        {
            return;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(string))
            {
                continue;
            }
            object fieldValue = field.GetValue(value);
            CollectStatusTypes(fieldValue, statusTypes);
        }

        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || prop.PropertyType == typeof(string))
            {
                continue;
            }
            object propValue = null;
            try
            {
                propValue = prop.GetValue(value, null);
            }
            catch
            {
                continue;
            }
            CollectStatusTypes(propValue, statusTypes);
        }
    }

    private static string FormatEffectNameWithColor(string effect)
    {
        string name;
        try
        {
            name = GetText($"Effect_{effect.ToUpper()}").ToUpper();
        }
        catch
        {
            name = effect.ToUpper();
        }

        return $"{GetEffectColor(effect)}{name}</color>";
    }

    private static string ProcessEffect(float amount, string effect)
    {
        string result = "";
        var color = string.Empty;
        var action = string.Empty;
        if (amount == 0)
        {
            return result;
        }
        else if (amount > 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            action = "GAIN";
        }
        else if (amount < 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            action = "REMOVE";
        }
        result += GetText("ProcessEffect", color, GetText(action), GetEffectColor(effect), (Mathf.Abs(amount) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.ToUpper()}").ToUpper());

        //result += effectColors[effect] + (Mathf.Abs(amount) * 100f).ToString("F1").Replace(".0", "") + " " + effect.ToUpper() + "</color>\n";

        return result;
    }

    private static string ProcessEffectOverTime(float amountPerSecond, float rate, float time, string effect)
    {
        string result = "";
        var color = string.Empty;
        var action = string.Empty;
        if ((amountPerSecond == 0) || (time == 0))
        {
            return result;
        }
        else if (amountPerSecond > 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            action = "GAIN";
        }
        else if (amountPerSecond < 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            action = "REMOVE";
        }
        result += GetText("ProcessEffectOverTime", color, GetText(action), GetEffectColor(effect), ((Mathf.Abs(amountPerSecond) * time * (1 / rate)) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.ToUpper()}").ToUpper(), time.ToString());
        return result;
    }

    private static string ProcessEffectPerSecond(float amountPerSecond, string effect)
    {
        string result = "";
        var color = string.Empty;
        var action = string.Empty;
        if (amountPerSecond == 0)
        {
            return result;
        }
        else if (amountPerSecond > 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            action = "GAIN";
        }
        else if (amountPerSecond < 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            action = "REMOVE";
        }

        string perSecond = (Mathf.Abs(amountPerSecond) * 100f).ToString("F1").Replace(".0", "");
        result += GetText("ProcessEffectPerSecond", color, GetText(action), GetEffectColor(effect), perSecond, GetText($"Effect_{effect.ToUpper()}").ToUpper());
        return result;
    }

    private static string ProcessEffectPerSecondOverTime(float amountPerSecond, float time, string effect)
    {
        string result = "";
        var color = string.Empty;
        var action = string.Empty;
        if ((amountPerSecond == 0) || (time <= 0))
        {
            return result;
        }
        else if (amountPerSecond > 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            action = "GAIN";
        }
        else if (amountPerSecond < 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            action = "REMOVE";
        }

        string perSecond = (Mathf.Abs(amountPerSecond) * 100f).ToString("F1").Replace(".0", "");
        string timeText = time.ToString("F1").Replace(".0", "");
        result += GetText("ProcessEffectPerSecondOverTime", color, GetText(action), GetEffectColor(effect), perSecond, GetText($"Effect_{effect.ToUpper()}").ToUpper(), timeText);
        return result;
    }

    private static string ProcessEffectOnEntry(float amount, string effect)
    {
        string result = "";
        var color = string.Empty;
        var action = string.Empty;
        if (amount == 0)
        {
            return result;
        }
        else if (amount > 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            action = "GAIN";
        }
        else if (amount < 0)
        {
            if (effect.Equals("Extra Stamina"))
            {
                color = effectColors["ItemInfoDisplayNegative"];
            }
            else
            {
                color = effectColors["ItemInfoDisplayPositive"];
            }
            action = "REMOVE";
        }

        result += GetText("ProcessEffectOnEntry", color, GetText(action), GetEffectColor(effect), (Mathf.Abs(amount) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.ToUpper()}").ToUpper());
        return result;
    }

    private static string ProcessEffectInline(float amount, string effect)
    {
        string result = ProcessEffect(amount, effect);
        if (string.IsNullOrEmpty(result))
        {
            return result;
        }

        result = result.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        return result.Trim();
    }

    private static float ApplyCookingMultiplier(float amount, float cookingMultiplier)
    {
        if (amount == 0f || cookingMultiplier == 1f || float.IsNaN(cookingMultiplier) || float.IsInfinity(cookingMultiplier))
        {
            return amount;
        }

        return amount * cookingMultiplier;
    }

    private static float GetCookingEffectMultiplier(ItemCooking? itemCooking)
    {
        if (itemCooking == null || itemCooking.timesCookedLocal <= 0)
        {
            return 1f;
        }

        EnsureCookingMultiplierGetter();
        if (cookingMultiplierGetter == null)
        {
            return 1f;
        }

        try
        {
            float value = cookingMultiplierGetter(itemCooking);
            if (float.IsNaN(value) || float.IsInfinity(value) || value == 0f)
            {
                return 1f;
            }
            return value;
        }
        catch
        {
            return 1f;
        }
    }

    private static void EnsureCookingMultiplierGetter()
    {
        if (cookingMultiplierResolved)
        {
            return;
        }

        cookingMultiplierResolved = true;
        Type type = typeof(ItemCooking);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        string[] methodNames =
        {
            "GetCookedEffectMultiplier",
            "GetCookedMultiplier",
            "GetCookingEffectMultiplier",
            "GetCookingMultiplier",
            "GetCookMultiplier",
            "GetEffectMultiplier",
            "GetCookedStatusMultiplier"
        };

        foreach (string methodName in methodNames)
        {
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (method.ReturnType != typeof(float))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    MethodInfo captured = method;
                    cookingMultiplierGetter = itemCooking => (float)captured.Invoke(itemCooking, null);
                    cookingMultiplierSource = $"{type.Name}.{captured.Name}()";
                    Log.LogInfo($"[ItemInfoDisplay] Cooking multiplier resolved via {cookingMultiplierSource}.");
                    return;
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                {
                    MethodInfo captured = method;
                    cookingMultiplierGetter = itemCooking => (float)captured.Invoke(itemCooking, new object[] { itemCooking.timesCookedLocal });
                    cookingMultiplierSource = $"{type.Name}.{captured.Name}(int)";
                    Log.LogInfo($"[ItemInfoDisplay] Cooking multiplier resolved via {cookingMultiplierSource}.");
                    return;
                }
            }
        }

        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (!IsLikelyCookingMultiplierMember(field.Name))
            {
                continue;
            }

            Func<ItemCooking, float>? getter = TryCreateMultiplierGetter(field, field.FieldType, field.IsStatic);
            if (getter != null)
            {
                cookingMultiplierGetter = getter;
                cookingMultiplierSource = $"{type.Name}.{field.Name}";
                Log.LogInfo($"[ItemInfoDisplay] Cooking multiplier resolved via {cookingMultiplierSource}.");
                return;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }
            if (!IsLikelyCookingMultiplierMember(property.Name))
            {
                continue;
            }

            bool isStatic = property.GetMethod != null && property.GetMethod.IsStatic;
            Func<ItemCooking, float>? getter = TryCreateMultiplierGetter(property, property.PropertyType, isStatic);
            if (getter != null)
            {
                cookingMultiplierGetter = getter;
                cookingMultiplierSource = $"{type.Name}.{property.Name}";
                Log.LogInfo($"[ItemInfoDisplay] Cooking multiplier resolved via {cookingMultiplierSource}.");
                return;
            }
        }
    }

    private static bool IsLikelyCookingMultiplierMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string lowered = name.ToLowerInvariant();
        if (!lowered.Contains("cook"))
        {
            return false;
        }

        return lowered.Contains("mult") || lowered.Contains("scale") || lowered.Contains("effect");
    }

    private static Func<ItemCooking, float>? TryCreateMultiplierGetter(MemberInfo member, Type memberType, bool isStatic)
    {
        if (memberType == typeof(float))
        {
            return itemCooking =>
            {
                object value = GetMemberValue(member, isStatic, itemCooking);
                return value is float result ? result : 1f;
            };
        }

        if (memberType == typeof(float[]))
        {
            return itemCooking =>
            {
                object value = GetMemberValue(member, isStatic, itemCooking);
                if (value is float[] array)
                {
                    return GetMultiplierFromList(array, itemCooking.timesCookedLocal);
                }
                return 1f;
            };
        }

        if (memberType == typeof(List<float>))
        {
            return itemCooking =>
            {
                object value = GetMemberValue(member, isStatic, itemCooking);
                if (value is List<float> list)
                {
                    return GetMultiplierFromList(list, itemCooking.timesCookedLocal);
                }
                return 1f;
            };
        }

        if (memberType == typeof(AnimationCurve))
        {
            return itemCooking =>
            {
                object value = GetMemberValue(member, isStatic, itemCooking);
                if (value is AnimationCurve curve)
                {
                    return curve.Evaluate(itemCooking.timesCookedLocal);
                }
                return 1f;
            };
        }

        return null;
    }

    private static object GetMemberValue(MemberInfo member, bool isStatic, ItemCooking itemCooking)
    {
        try
        {
            if (member is FieldInfo field)
            {
                return field.GetValue(isStatic ? null : itemCooking);
            }
            if (member is PropertyInfo property)
            {
                return property.GetValue(isStatic ? null : itemCooking, null);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static float GetMultiplierFromList(IReadOnlyList<float> values, int timesCooked)
    {
        if (values == null || values.Count == 0)
        {
            return 1f;
        }

        int index = Mathf.Clamp(timesCooked, 0, values.Count - 1);
        return values[index];
    }

    private static string GetNextCookHint(int nextTimesCooked)
    {
        if (nextTimesCooked >= ItemCooking.COOKING_MAX)
        {
            return GetText("COOK_NEXT_NONE", effectColors["Curse"]);
        }
        if (nextTimesCooked <= 2)
        {
            return GetText("COOK_NEXT_BUFF", effectColors["Extra Stamina"]);
        }
        if (nextTimesCooked == 3)
        {
            return GetText("COOK_NEXT_NERF", effectColors["Injury"]);
        }

        return GetText("COOK_NEXT_POISON", effectColors["Poison"]);
    }

    private static bool HasExplodeOnUseBehavior(object behavior)
    {
        if (behavior == null)
        {
            return false;
        }

        string behaviorName = behavior.GetType().Name;
        if (ContainsExplodeKeyword(behaviorName) && !string.Equals(behaviorName, nameof(CookingBehavior_Explode), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (behaviorName.IndexOf("RunActions", StringComparison.OrdinalIgnoreCase) >= 0
            || behaviorName.IndexOf("EnableScripts", StringComparison.OrdinalIgnoreCase) >= 0
            || behaviorName.IndexOf("DisableScripts", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return BehaviorContainsExplodeTarget(behavior);
        }

        return false;
    }

    private static bool BehaviorContainsExplodeTarget(object behavior)
    {
        if (behavior == null)
        {
            return false;
        }

        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = behavior.GetType();
        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (!NameContainsActionOrScript(field.Name))
            {
                continue;
            }
            if (ValueContainsExplodeKeyword(field.GetValue(behavior)))
            {
                return true;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }
            if (!NameContainsActionOrScript(property.Name))
            {
                continue;
            }

            object value = null;
            try
            {
                value = property.GetValue(behavior, null);
            }
            catch
            {
                continue;
            }

            if (ValueContainsExplodeKeyword(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NameContainsActionOrScript(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string lowered = name.ToLowerInvariant();
        return lowered.Contains("action") || lowered.Contains("script");
    }

    private static bool ValueContainsExplodeKeyword(object value)
    {
        if (value == null)
        {
            return false;
        }

        if (value is string text)
        {
            return ContainsExplodeKeyword(text);
        }

        if (value is UnityEngine.Object unityObject)
        {
            return ContainsExplodeKeyword(unityObject.GetType().Name);
        }

        if (value is IEnumerable enumerable)
        {
            foreach (object item in enumerable)
            {
                if (ValueContainsExplodeKeyword(item))
                {
                    return true;
                }
            }
            return false;
        }

        return ContainsExplodeKeyword(value.GetType().Name);
    }

    private static bool ContainsExplodeKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.IndexOf("Explode", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetEffectColor(string effect)
    {
        if (effectColors.TryGetValue(effect, out string color))
        {
            return color;
        }

        if (!missingEffectColors.Contains(effect))
        {
            missingEffectColors.Add(effect);
            Log.LogWarning($"[ItemInfoDisplay] Missing effect color mapping for '{effect}'. Using fallback.");
        }

        return "<#CCCCCC>";
    }

    private static float GetLanternRemainingSeconds(Item item, Lantern lantern)
    {
        try
        {
            if (item?.data?.data != null && item.data.data.ContainsKey(DataEntryKey.Fuel))
            {
                object fuelData = item.data.data[DataEntryKey.Fuel];
                if (fuelData is FloatItemData floatData)
                {
                    return floatData.Value;
                }
            }
        }
        catch
        {
            // ignore and fallback
        }

        return lantern != null ? lantern.startingFuel : 0f;
    }

    private static void UpdateLanternRefreshFlag(Item item)
    {
        Lantern lantern = item != null ? item.GetComponent<Lantern>() : null;
        if (lantern == null)
        {
            lastLanternItemId = 0;
            lastLanternRemainingSeconds = -1;
            return;
        }

        int remainingSeconds = GetLanternRemainingSecondsInt(item, lantern);
        int itemId = item.GetInstanceID();

        if (itemId != lastLanternItemId)
        {
            lastLanternItemId = itemId;
            lastLanternRemainingSeconds = remainingSeconds;
            hasChanged = true;
            return;
        }

        if (remainingSeconds != lastLanternRemainingSeconds)
        {
            lastLanternRemainingSeconds = remainingSeconds;
            hasChanged = true;
        }
    }

    private static int GetLanternRemainingSecondsInt(Item item, Lantern lantern)
    {
        float remainingSeconds = GetLanternRemainingSeconds(item, lantern);
        int remainingInt = Mathf.CeilToInt(remainingSeconds);
        return Mathf.Max(0, remainingInt);
    }

    private static string ProcessAffliction(Peak.Afflictions.Affliction affliction, float cookingMultiplier)
    {
        string result = "";
        var color = string.Empty;
        var action = string.Empty;
        if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.FasterBoi)
        {
            Peak.Afflictions.Affliction_FasterBoi effect = (Peak.Afflictions.Affliction_FasterBoi)affliction;
            result += GetText("Affliction_FasterBoi",
                effectColors["ItemInfoDisplayPositive"],
                (effect.totalTime + effect.climbDelay).ToString("F1").Replace(".0", ""),
                effectColors["Extra Stamina"],
                Mathf.Round(effect.moveSpeedMod * 100f).ToString("F1").Replace(".0", ""),
                effect.totalTime.ToString("F1").Replace(".0", ""),
                Mathf.Round(effect.climbSpeedMod * 100f).ToString("F1").Replace(".0", ""),
                effectColors["ItemInfoDisplayNegative"],
                effectColors["Drowsy"],
                (effect.drowsyOnEnd * 100f).ToString("F1").Replace(".0", ""));
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.ClearAllStatus)
        {
            Peak.Afflictions.Affliction_ClearAllStatus effect = (Peak.Afflictions.Affliction_ClearAllStatus)affliction;
            result += GetText("ClearAllStatus_Base", effectColors["ItemInfoDisplayPositive"]);
            if (effect.excludeCurse)
            {
                result += GetText("ClearAllStatus_ExceptCurse", effectColors["Curse"]);
            }
            result += "\n";
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.AddBonusStamina)
        {
            Peak.Afflictions.Affliction_AddBonusStamina effect = (Peak.Afflictions.Affliction_AddBonusStamina)affliction;
            float staminaAmount = ApplyCookingMultiplier(effect.staminaAmount, cookingMultiplier);
            result += GetText("Affliction_AddBonusStamina", effectColors["ItemInfoDisplayPositive"], effectColors["Extra Stamina"], (staminaAmount * 100f).ToString("F1").Replace(".0", ""));
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.InfiniteStamina)
        {
            Peak.Afflictions.Affliction_InfiniteStamina effect = (Peak.Afflictions.Affliction_InfiniteStamina)affliction;
            if (effect.climbDelay > 0)
            {
                result += GetText("Affliction_InfiniteStamina_Climb",
                    effectColors["ItemInfoDisplayPositive"],
                    (effect.totalTime + effect.climbDelay).ToString("F1").Replace(".0", ""),
                    effectColors["Extra Stamina"],
                    effect.totalTime.ToString("F1").Replace(".0", ""));
            }
            else
            {
                result += GetText("Affliction_InfiniteStamina",
                    effectColors["ItemInfoDisplayPositive"],
                    effect.totalTime.ToString("F1").Replace(".0", ""),
                    effectColors["Extra Stamina"]);
            }
            if (effect.drowsyAffliction != null)
            {
                result += GetText("AFTERWARDS") + ProcessAffliction(effect.drowsyAffliction, cookingMultiplier);
            }
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.AdjustStatus)
        {
            Peak.Afflictions.Affliction_AdjustStatus effect = (Peak.Afflictions.Affliction_AdjustStatus)affliction;
            float statusAmount = ApplyCookingMultiplier(effect.statusAmount, cookingMultiplier);
            if (statusAmount > 0)
            {
                if (effect.Equals("Extra Stamina"))
                {
                    color = effectColors["ItemInfoDisplayPositive"];
                }
                else
                {
                    color = effectColors["ItemInfoDisplayNegative"];
                }
                action = "GAIN";


                //if (effect.Equals("Extra Stamina"))
                //{
                //    result += effectColors["ItemInfoDisplayPositive"];
                //}
                //else
                //{
                //    result += effectColors["ItemInfoDisplayNegative"];
                //}
                //result += "GAIN</color> ";
            }
            else
            {
                if (effect.Equals("Extra Stamina"))
                {
                    color = effectColors["ItemInfoDisplayNegative"];
                }
                else
                {
                    color = effectColors["ItemInfoDisplayPositive"];
                }
                action = "REMOVE";

                //if (effect.Equals("Extra Stamina"))
                //{
                //    result += effectColors["ItemInfoDisplayNegative"];
                //}
                //else
                //{
                //    result += effectColors["ItemInfoDisplayPositive"];
                //}
                //result += "REMOVE</color> ";
            }
            result += GetText("ProcessEffect", color, GetText(action), effectColors[effect.statusType.ToString()], (Mathf.Abs(statusAmount) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.statusType.ToString().ToUpper()}").ToUpper());

            //result += effectColors[effect.statusType.ToString()] + (Mathf.Abs(effect.statusAmount) * 100f).ToString("F1").Replace(".0", "")
            //    + " " + effect.statusType.ToString().ToUpper() + "</color>\n";
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.DrowsyOverTime)
        {
            Peak.Afflictions.Affliction_AdjustDrowsyOverTime effect = (Peak.Afflictions.Affliction_AdjustDrowsyOverTime)affliction;
            float statusPerSecond = ApplyCookingMultiplier(effect.statusPerSecond, cookingMultiplier);
            color = statusPerSecond > 0 ? effectColors["ItemInfoDisplayNegative"] : effectColors["ItemInfoDisplayPositive"];
            action = statusPerSecond > 0 ? "GAIN" : "REMOVE";
            result += GetText("ProcessEffectOverTime", color, GetText(action), effectColors["Drowsy"], (Mathf.Round((Mathf.Abs(statusPerSecond) * effect.totalTime * 100f) * 0.4f) / 0.4f).ToString("F1").Replace(".0", ""), GetText("Effect_DROWSY").ToUpper(), effect.totalTime.ToString("F1").Replace(".0", ""));
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.ColdOverTime)
        {
            Peak.Afflictions.Affliction_AdjustColdOverTime effect = (Peak.Afflictions.Affliction_AdjustColdOverTime)affliction;
            float statusPerSecond = ApplyCookingMultiplier(effect.statusPerSecond, cookingMultiplier);
            color = statusPerSecond > 0 ? effectColors["ItemInfoDisplayNegative"] : effectColors["ItemInfoDisplayPositive"];
            action = statusPerSecond > 0 ? "GAIN" : "REMOVE";
            result += GetText("ProcessEffectOverTime", color, GetText(action), effectColors["Cold"], (Mathf.Abs(statusPerSecond) * effect.totalTime * 100f).ToString("F1").Replace(".0", ""), GetText("Effect_COLD").ToUpper(), effect.totalTime.ToString("F1").Replace(".0", ""));
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.Chaos)
        {
            result += GetText("Affliction_Chaos",
                effectColors["ItemInfoDisplayPositive"],
                effectColors["Hunger"],
                effectColors["Extra Stamina"],
                effectColors["Injury"],
                effectColors["Poison"],
                effectColors["Cold"],
                effectColors["Hot"],
                effectColors["Drowsy"],
                effectColors["Thorns"],
                effectColors["Spores"]);
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.Sunscreen)
        {
            Peak.Afflictions.Affliction_Sunscreen effect = (Peak.Afflictions.Affliction_Sunscreen)affliction;
            result += $"{GetText("ProcessAffliction_Sunscreen", effectColors["Heat"], effect.totalTime.ToString("F1").Replace(".0", ""))}";
            //result += "PREVENT " + effectColors["Heat"] + "HEAT</color> IN MESA'S SUN FOR " + effect.totalTime.ToString("F1").Replace(".0", "") + "s\n";
        }

        return result;
    }

    private static void HandleTestModeInput()
    {
        bool isOffline = Photon.Pun.PhotonNetwork.OfflineMode;
        if (!isOffline)
        {
            if (!Photon.Pun.PhotonNetwork.IsConnected || !Photon.Pun.PhotonNetwork.InRoom) return;
            if (!Photon.Pun.PhotonNetwork.IsMasterClient) return;
        }

        bool pressed = IsTestKeyDown(KeyCode.F1)
            || IsTestKeyDown(KeyCode.F2)
            || IsTestKeyDown(KeyCode.F3)
            || IsTestKeyDown(KeyCode.F4);
        if (!pressed) return;

        Log.LogInfo($"[TestMode] Input detected. Mode={(isOffline ? "Offline" : "Online")} Connected={Photon.Pun.PhotonNetwork.IsConnected} InRoom={Photon.Pun.PhotonNetwork.InRoom} Master={Photon.Pun.PhotonNetwork.IsMasterClient}");

        // 初始化物品列表（延迟到按键触发）
        if (!testModeInitialized)
        {
            Log.LogInfo("[TestMode] Initializing item list...");
            if (!InitializeTestItemList())
            {
                Log.LogInfo("[TestMode] Initialization failed. Database not ready.");
                return;
            }
            testModeInitialized = true;
        }

        // F1: 生成下一个物品
        if (IsTestKeyDown(KeyCode.F1))
        {
            SpawnNextTestItem();
        }

        // F2: 生成上一个物品
        if (IsTestKeyDown(KeyCode.F2))
        {
            SpawnPreviousTestItem();
        }

        // F3: 输出当前物品信息到日志
        if (IsTestKeyDown(KeyCode.F3))
        {
            LogCurrentItemInfo();
        }

        // F4: 输出所有物品信息到日志
        if (IsTestKeyDown(KeyCode.F4))
        {
            LogAllItemInfo();
        }
    }

    private static bool IsTestKeyDown(KeyCode keyCode)
    {
        if (testModeInputDisabled) return false;

        if (TryGetInputSystemKeyDown(keyCode, out bool pressedByInputSystem))
        {
            return pressedByInputSystem;
        }

        if (TryGetLegacyInputKeyDown(keyCode, out bool pressedByLegacy))
        {
            return pressedByLegacy;
        }

        testModeInputDisabled = true;
        Log.LogWarning("[TestMode] Input not available. Disabling test mode input checks.");
        return false;
    }

    private static bool TryGetInputSystemKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;

        if (!inputSystemChecked)
        {
            inputSystemAvailable = InitializeInputSystemReflection();
            inputSystemChecked = true;
        }

        if (!inputSystemAvailable) return false;

        try
        {
            var keyboard = inputSystemKeyboardCurrentProp?.GetValue(null, null);
            if (keyboard == null) return true;

            var keyEnum = Enum.Parse(inputSystemKeyType, keyCode.ToString());
            var keyControl = inputSystemKeyboardItemProp?.GetValue(keyboard, new[] { keyEnum });
            if (keyControl == null) return true;

            pressed = (bool)inputSystemKeyControlPressedProp.GetValue(keyControl, null);
            return true;
        }
        catch (Exception e)
        {
            inputSystemAvailable = false;
            Log.LogWarning($"[TestMode] Input System unavailable ({e.GetType().Name}). Falling back to legacy input.");
            return false;
        }
    }

    private static bool InitializeInputSystemReflection()
    {
        try
        {
            var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            inputSystemKeyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            var keyControlType = Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");
            if (keyboardType == null || inputSystemKeyType == null || keyControlType == null) return false;

            inputSystemKeyboardCurrentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            inputSystemKeyboardItemProp = keyboardType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            inputSystemKeyControlPressedProp = keyControlType.GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);

            return inputSystemKeyboardCurrentProp != null
                && inputSystemKeyboardItemProp != null
                && inputSystemKeyControlPressedProp != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLegacyInputKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;
        if (!legacyInputAvailable) return false;

        try
        {
            pressed = Input.GetKeyDown(keyCode);
            return true;
        }
        catch (Exception e)
        {
            legacyInputAvailable = false;
            Log.LogWarning($"[TestMode] Legacy Input unavailable ({e.GetType().Name}).");
            return false;
        }
    }

    private static bool InitializeTestItemList()
    {
        allItemPrefabs.Clear();

        var db = SingletonAsset<ItemDatabase>.Instance;
        if (db == null || db.Objects == null || db.Objects.Count == 0)
        {
            int count = db?.Objects?.Count ?? -1;
            Log.LogInfo($"[TestMode] ItemDatabase not ready or empty. Count={count}. Try again after fully entering a match or starting offline.");
            return false;
        }

        allItemPrefabs = db.Objects
            .Where(i => i != null)
            .OrderBy(i => i.name)
            .ToList();
        EnsureItemNameKeyMap();
        Log.LogInfo($"[TestMode] Loaded {allItemPrefabs.Count} items from ItemDatabase. Press F1/F2 to cycle, F3 to log current item info, F4 to log all items.");
        return allItemPrefabs.Count > 0;
    }

    private static void SpawnNextTestItem()
    {
        if (allItemPrefabs.Count == 0) return;

        currentTestItemIndex = (currentTestItemIndex + 1) % allItemPrefabs.Count;
        SpawnTestItem(currentTestItemIndex);
    }

    private static void SpawnPreviousTestItem()
    {
        if (allItemPrefabs.Count == 0) return;

        currentTestItemIndex = (currentTestItemIndex - 1 + allItemPrefabs.Count) % allItemPrefabs.Count;
        SpawnTestItem(currentTestItemIndex);
    }

    private static void SpawnTestItem(int index)
    {
        if (index < 0 || index >= allItemPrefabs.Count) return;

        var prefab = allItemPrefabs[index];
        var player = Character.localCharacter;
        if (player == null) return;

        Vector3 spawnPos = player.Center + player.transform.forward * 1.5f;

        try
        {
            // 使用游戏内置的 PhotonNetwork.InstantiateItemRoom 生成物品
            var spawnedObj = Photon.Pun.PhotonNetwork.InstantiateItemRoom(prefab.name, spawnPos, Quaternion.identity);
            Log.LogInfo($"[TestMode] Spawned [{index + 1}/{allItemPrefabs.Count}]: {prefab.name}");

            // 输出组件信息
            var components = spawnedObj.GetComponents<Component>();
            var componentNames = components.Select(c => c == null ? "<Missing Script>" : c.GetType().Name).ToList();
            Log.LogInfo($"[TestMode] Components: {string.Join(", ", componentNames)}");
        }
        catch (Exception e)
        {
            Log.LogError($"[TestMode] Failed to spawn {prefab.name}: {e.Message}");
        }
    }

    private static void LogCurrentItemInfo()
    {
        var currentItem = Character.observedCharacter?.data?.currentItem;
        if (currentItem == null)
        {
            Log.LogInfo("[TestMode] No item equipped");
            return;
        }

        string localizedName = GetLocalizedItemName(currentItem);
        string nameSuffix = string.IsNullOrEmpty(localizedName) ? "" : $" ({localizedName})";
        Log.LogInfo($"[TestMode] Current Item: {currentItem.name}{nameSuffix}");
        var components = currentItem.GetComponents<Component>();
        foreach (var c in components)
        {
            Log.LogInfo($"[TestMode]   - {(c == null ? "<Missing Script>" : c.GetType().Name)}");
        }
        Log.LogInfo($"[TestMode] Display Text:\n{itemInfoDisplayTextMesh.text}");
    }

    private static void LogAllItemInfo()
    {
        if (allItemPrefabs.Count == 0)
        {
            Log.LogInfo("[TestMode] No items loaded");
            return;
        }

        Log.LogInfo($"[TestMode] F4 logging {allItemPrefabs.Count} items...");
        int successCount = 0;
        int failCount = 0;
        string originalText = itemInfoDisplayTextMesh.text;

        foreach (var itemPrefab in allItemPrefabs)
        {
            if (itemPrefab == null)
            {
                failCount++;
                continue;
            }

            try
            {
                LogItemInfoForTest(itemPrefab);
                successCount++;
            }
            catch (Exception e)
            {
                failCount++;
                Log.LogWarning($"[TestMode] Failed to log {itemPrefab.name}: {e.GetType().Name} {e.Message}");
            }
        }

        itemInfoDisplayTextMesh.text = originalText;
        Log.LogInfo($"[TestMode] F4 completed. Logged={successCount}, Failed={failCount}.");
    }

    private static void LogItemInfoForTest(Item item)
    {
        Item itemForLog = item;
        GameObject tempObj = null;
        string previousText = itemInfoDisplayTextMesh.text;

        try
        {
            try
            {
                tempObj = UnityEngine.Object.Instantiate(item.gameObject);
                Item tempItem = tempObj.GetComponent<Item>();
                if (tempItem != null)
                {
                    itemForLog = tempItem;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[TestMode] Clone failed for {item.name}: {e.GetType().Name} {e.Message}");
            }

            string localizedName = GetLocalizedItemName(itemForLog);
            string nameSuffix = string.IsNullOrEmpty(localizedName) ? "" : $" ({localizedName})";
            Log.LogInfo($"[TestMode] Current Item: {itemForLog.name}{nameSuffix}");
            var components = itemForLog.GetComponents<Component>();
            foreach (var c in components)
            {
                Log.LogInfo($"[TestMode]   - {(c == null ? "<Missing Script>" : c.GetType().Name)}");
            }

            ProcessItemGameObject(itemForLog);
            string displayText = itemInfoDisplayTextMesh.text;
            Log.LogInfo($"[TestMode] Display Text:\n{displayText}");
        }
        finally
        {
            itemInfoDisplayTextMesh.text = previousText;
            if (tempObj != null)
            {
                UnityEngine.Object.Destroy(tempObj);
            }
        }
    }

    private static void AddDisplayObject()
    {
        GameObject guiManagerGameObj = GameObject.Find("GAME/GUIManager");
        guiManager = guiManagerGameObj.GetComponent<GUIManager>();
        TMPro.TMP_FontAsset font = guiManager.heroDayText.font;

        GameObject invGameObj = guiManagerGameObj.transform.Find("Canvas_HUD/Prompts/ItemPromptLayout").gameObject;
        GameObject itemInfoDisplayGameObj = new GameObject("ItemInfoDisplay");
        itemInfoDisplayGameObj.transform.SetParent(invGameObj.transform);
        itemInfoDisplayTextMesh = itemInfoDisplayGameObj.AddComponent<TextMeshProUGUI>();
        RectTransform itemInfoDisplayRect = itemInfoDisplayGameObj.GetComponent<RectTransform>();

        itemInfoDisplayRect.sizeDelta = new Vector2(configSizeDeltaX.Value, 0f); // Y is 0, otherwise moves other item prompts
        itemInfoDisplayTextMesh.font = font;
        itemInfoDisplayTextMesh.fontSize = configFontSize.Value;
        itemInfoDisplayTextMesh.alignment = TextAlignmentOptions.BottomLeft;
        itemInfoDisplayTextMesh.lineSpacing = configLineSpacing.Value;
        itemInfoDisplayTextMesh.text = "";
        itemInfoDisplayTextMesh.outlineWidth = configOutlineWidth.Value;

        LoadLocalizedText();
    }

    private static string GetLocalizedItemName(Item item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        EnsureItemNameKeyMap();

        string rawName = item.name ?? string.Empty;
        if (ContainsNonAscii(rawName))
        {
            return rawName.Trim();
        }

        string normalizedName = NormalizeItemNameForMap(rawName);
        if (!string.IsNullOrEmpty(normalizedName) && itemNameKeyMap.TryGetValue(normalizedName, out string mappedKey))
        {
            return ResolveLocalizedText(mappedKey);
        }

        return string.Empty;
    }

    private static void EnsureItemNameKeyMap()
    {
        if (itemNameKeyMapInitialized)
        {
            return;
        }

        LoadItemNameKeyMap();
        itemNameKeyMapInitialized = true;
    }

    private static void LoadItemNameKeyMap()
    {
        itemNameKeyMap.Clear();

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ItemNameKeyMap.json");
            if (stream == null)
            {
                Log.LogWarning("[TestMode] Embedded ItemNameKeyMap.json not found.");
                return;
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
            if (data == null)
            {
                return;
            }

            foreach (var kvp in data)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    itemNameKeyMap[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"[TestMode] Failed to load embedded ItemNameKeyMap.json: {e.GetType().Name} {e.Message}");
        }
    }

    private static string NormalizeItemNameForMap(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string normalized = StripLocPrefix(name.Trim());
        normalized = normalized.Replace("(Clone)", "").Trim();
        return normalized;
    }

    private static bool ContainsNonAscii(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 127)
            {
                return true;
            }
        }

        return false;
    }

    private static string StripLocPrefix(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        const string prefix = "LOC:";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return key.Substring(prefix.Length).Trim();
        }

        return key;
    }

    private static string ResolveLocalizedText(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            string normalizedKey = StripLocPrefix(key.Trim());
            if (string.IsNullOrEmpty(normalizedKey))
            {
                return string.Empty;
            }

            string localized = LocalizedText.GetText(normalizedKey);
            if (string.IsNullOrEmpty(localized))
            {
                return string.Empty;
            }

            string normalizedLocalized = localized.Trim();
            if (normalizedLocalized.StartsWith("LOC:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (string.Equals(normalizedLocalized, normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return normalizedLocalized;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetText(string key, params string[] args)
    {
        return string.Format(LocalizedText.GetText($"Mod_{Name}_{key}".ToUpper()), args);
    }

    private static void LoadLocalizedText()
    {
        var LocalizedTextTable = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(ItemInfoDisplay.Properties.Resources.Localized_Text);
        if (LocalizedTextTable != null)
        {
            foreach (var item in LocalizedTextTable)
            {
                var values = item.Value;
                string firstValue = values[0];
                values = values.Select(x => string.IsNullOrEmpty(x) ? firstValue : x).ToList();
                string localizedKey = $"Mod_{Name}_{item.Key}".ToUpper();
                LocalizedText.MAIN_TABLE[localizedKey] = values;
            }
        }
        else
        {
            Log.LogError($"LoadLocalizedText Fail");
        }
    }

    private static void InitEffectColors(Dictionary<string, string> dict)
    {
        dict.Add("Hunger", "<#FFBD16>");
        dict.Add("Extra Stamina", "<#BFEC1B>");
        dict.Add("Injury", "<#FF5300>");
        dict.Add("Crab", "<#E13542>");
        dict.Add("Poison", "<#A139FF>");
        dict.Add("Cold", "<#00BCFF>");
        dict.Add("Heat", "<#C80918>");
        dict.Add("Hot", "<#C80918>");
        dict.Add("Sleepy", "<#FF5CA4>");
        dict.Add("Drowsy", "<#FF5CA4>");
        dict.Add("Curse", "<#1B0043>");
        dict.Add("Weight", "<#A65A1C>");
        dict.Add("Thorns", "<#768E00>");
        dict.Add("Spores", "<#A45B63>");
        dict.Add("Shield", "<#D48E00>");

        dict.Add("ItemInfoDisplayPositive", "<#DDFFDD>");
        dict.Add("ItemInfoDisplayNegative", "<#FFCCCC>");
    }
}
