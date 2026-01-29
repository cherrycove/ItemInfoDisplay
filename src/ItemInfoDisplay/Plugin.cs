using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;

using HarmonyLib;

using MonoMod.Utils;

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing;
using System.Linq;

using TMPro;

using UnityEngine;
using UnityEngine.Rendering;

using Zorro.UI;
using Zorro.UI.Effects;

using static MonoMod.Cil.RuntimeILReferenceBag.FastDelegateInvokers;

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
                    if (configEnableTestMode.Value)
                    {
                        HandleTestModeInput();
                    }

                    if (Character.observedCharacter.data.currentItem != null)
                    {
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
        GameObject itemGameObj = item.gameObject;
        Component[] itemComponents = itemGameObj.GetComponents(typeof(Component)).GroupBy(c => c.GetType()).Select(g => g.First()).ToArray();
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
                prefixStatus += ProcessEffect((effect.restorationAmount * -1f), "Hunger");
            }
            else if (itemComponents[i].GetType() == typeof(Action_GiveExtraStamina))
            {
                Action_GiveExtraStamina effect = (Action_GiveExtraStamina)itemComponents[i];
                prefixStatus += ProcessEffect(effect.amount, "Extra Stamina");
            }
            else if (itemComponents[i].GetType() == typeof(Action_InflictPoison))
            {
                Action_InflictPoison effect = (Action_InflictPoison)itemComponents[i];
                prefixStatus += GetText("InflictPoison", effect.delay.ToString(), ProcessEffectOverTime(effect.poisonPerSecond, 1f, effect.inflictionTime, "Poison"));
                //prefixStatus += "AFTER " + effect.delay.ToString() + "s, " + ProcessEffectOverTime(effect.poisonPerSecond, 1f, effect.inflictionTime, "Poison");
            }
            else if (itemComponents[i].GetType() == typeof(Action_AddOrRemoveThorns))
            {
                Action_AddOrRemoveThorns effect = (Action_AddOrRemoveThorns)itemComponents[i];
                prefixStatus += ProcessEffect((effect.thornCount * 0.05f), "Thorns"); // TODO: Search for thorns amount per applied thorn
            }
            else if (itemComponents[i].GetType() == typeof(Action_ModifyStatus))
            {
                Action_ModifyStatus effect = (Action_ModifyStatus)itemComponents[i];
                prefixStatus += ProcessEffect(effect.changeAmount, effect.statusType.ToString());
            }
            else if (itemComponents[i].GetType() == typeof(Action_ApplyMassAffliction))
            {
                Action_ApplyMassAffliction effect = (Action_ApplyMassAffliction)itemComponents[i];
                suffixAfflictions += $"{GetText("ApplyMassAffliction")}";
                //suffixAfflictions += "<#CCCCCC>NEARBY PLAYERS WILL RECEIVE:</color>\n";
                suffixAfflictions += ProcessAffliction(effect.affliction);
                if (effect.extraAfflictions.Length > 0)
                {
                    for (int j = 0; j < effect.extraAfflictions.Length; j++)
                    {
                        if (suffixAfflictions.EndsWith('\n'))
                        {
                            suffixAfflictions = suffixAfflictions.Remove(suffixAfflictions.Length - 1);
                        }
                        suffixAfflictions += ",\n" + ProcessAffliction(effect.extraAfflictions[j]);
                    }
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_ApplyAffliction))
            {
                Action_ApplyAffliction effect = (Action_ApplyAffliction)itemComponents[i];
                suffixAfflictions += ProcessAffliction(effect.affliction);
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
                OptionableIntItemData uses = (OptionableIntItemData)item.data.data[DataEntryKey.ItemUses];
                if (uses.HasData)
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

                if (itemGameObj.name.Equals("Lantern_Faerie(Clone)"))
                {
                    StatusField effect = itemGameObj.transform.Find("FaerieLantern/Light/Heat").GetComponent<StatusField>();
                    suffixAfflictions += ProcessEffectOverTime(effect.statusAmountPerSecond, 1f, lantern.startingFuel, effect.statusType.ToString());
                    foreach (StatusField.StatusFieldStatus status in effect.additionalStatuses)
                    {
                        if (suffixAfflictions.EndsWith('\n'))
                        {
                            suffixAfflictions = suffixAfflictions.Remove(suffixAfflictions.Length - 1);
                        }
                        suffixAfflictions += ",\n" + ProcessEffectOverTime(status.statusAmountPerSecond, 1f, lantern.startingFuel, status.statusType.ToString());
                    }
                }
                else if (itemGameObj.name.Equals("Lantern(Clone)"))
                {
                    StatusField effect = itemGameObj.transform.Find("GasLantern/Light/Heat").GetComponent<StatusField>();
                    suffixAfflictions += ProcessEffectOverTime(effect.statusAmountPerSecond, 1f, lantern.startingFuel, effect.statusType.ToString());
                }
            }
            else if (itemComponents[i].GetType() == typeof(Action_RaycastDart))
            {
                Action_RaycastDart effect = (Action_RaycastDart)itemComponents[i];
                isConsumable = true;
                suffixAfflictions += $"{GetText("RaycastDart")}";
                //suffixAfflictions += "<#CCCCCC>SHOOT A DART THAT WILL APPLY:</color>\n";
                for (int j = 0; j < effect.afflictionsOnHit.Length; j++)
                {
                    suffixAfflictions += ProcessAffliction(effect.afflictionsOnHit[j]);
                    if (suffixAfflictions.EndsWith('\n'))
                    {
                        suffixAfflictions = suffixAfflictions.Remove(suffixAfflictions.Length - 1);
                    }
                    suffixAfflictions += ",\n";
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
                if (effect.constructedPrefab.name.Equals("PortableStovetop_Placed"))
                {
                    itemInfoDisplayTextMesh.text += $"{GetText("Constructable_PortableStovetop_Placed", effectColors["Injury"], effect.constructedPrefab.GetComponent<Campfire>().burnsFor.ToString())}";
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
                if (effect.ropeAnchorWithRopePref.name.Equals("RopeAnchorForRopeShooterAnti"))
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
                if (effect.instantiateOnBreak.name.Equals("HealingPuffShroomSpawn"))
                {
                    GameObject effect1 = effect.instantiateOnBreak.transform.Find("VFX_SporeHealingExplo").gameObject;
                    AOE effect1AOE = effect1.GetComponent<AOE>();
                    GameObject effect2 = effect1.transform.Find("VFX_SporePoisonExplo").gameObject;
                    AOE effect2AOE = effect2.GetComponent<AOE>();
                    AOE[] effect2AOEs = effect2.GetComponents<AOE>();
                    TimeEvent effect2TimeEvent = effect2.GetComponent<TimeEvent>();
                    RemoveAfterSeconds effect2RemoveAfterSeconds = effect2.GetComponent<RemoveAfterSeconds>();
                    itemInfoDisplayTextMesh.text += GetText("HealingPuffShroom", effectColors["Hunger"]);
                    itemInfoDisplayTextMesh.text += ProcessEffect((Mathf.Round(effect1AOE.statusAmount * 0.9f * 40f) / 40f), effect1AOE.statusType.ToString()); // incorrect? calculates strangely so i somewhat manually adjusted the values
                    itemInfoDisplayTextMesh.text += ProcessEffectOverTime((Mathf.Round(effect2AOE.statusAmount * (1f / effect2TimeEvent.rate) * 40f) / 40f), 1f, effect2RemoveAfterSeconds.seconds, effect2AOE.statusType.ToString()); // incorrect?
                    if (effect2AOEs.Length > 1)
                    {
                        itemInfoDisplayTextMesh.text += ProcessEffectOverTime((Mathf.Round(effect2AOEs[1].statusAmount * (1f / effect2TimeEvent.rate) * 40f) / 40f), 1f, (effect2RemoveAfterSeconds.seconds + 1f), effect2AOEs[1].statusType.ToString()); // incorrect?
                    } // didn't handle dynamically because there were 2 poison removal AOEs but 1 doesn't seem to work or they are buggy in some way (probably time event rate)?
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
                itemInfoDisplayTextMesh.text += $"{(GetText("CallScoutmaster", effectColors["Injury"]))}";
                //itemInfoDisplayTextMesh.text += effectColors["Injury"] + "BREAKS RULE 0 WHEN USED</color>\n";
            }
            else if (itemComponents[i].GetType() == typeof(Action_MoraleBoost))
            {
                Action_MoraleBoost effect = (Action_MoraleBoost)itemComponents[i];
                if (effect.boostRadius < 0)
                {
                    itemInfoDisplayTextMesh.text += GetText("MoraleBoost_Self", effectColors["ItemInfoDisplayPositive"], effectColors["Extra Stamina"], (effect.baselineStaminaBoost * 100f).ToString("F1").Replace(".0", ""));
                }
                else if (effect.boostRadius > 0)
                {
                    itemInfoDisplayTextMesh.text += GetText("MoraleBoost_Nearby", effectColors["ItemInfoDisplayPositive"], effectColors["Extra Stamina"], (effect.baselineStaminaBoost * 100f).ToString("F1").Replace(".0", ""));
                }
            }
            else if (itemComponents[i].GetType() == typeof(Breakable))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Breakable", effectColors["Hunger"])}";
                //itemInfoDisplayTextMesh.text += effectColors["Hunger"] + "THROW</color> TO CRACK OPEN\n";
            }
            else if (itemComponents[i].GetType() == typeof(Bonkable))
            {
                itemInfoDisplayTextMesh.text += $"{GetText("Bonkable", effectColors["Hunger"], effectColors["Injury"])}";
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
                    float effectPoison = Mathf.Max(0.5f, (1f - item.holderCharacter.refs.afflictions.statusSum + 0.05f)) * 100f;
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
                if (effect.objectToSpawn.name.Equals("VFX_Sunscreen"))
                {
                    AOE effectAOE = effect.objectToSpawn.transform.Find("AOE").GetComponent<AOE>();
                    RemoveAfterSeconds effectTime = effect.objectToSpawn.transform.Find("AOE").GetComponent<RemoveAfterSeconds>();
                    itemInfoDisplayTextMesh.text += $"{GetText("VFX_Sunscreen", effectTime.seconds.ToString("F1").Replace(".0", ""), ProcessAffliction(effectAOE.affliction))}";
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
                ItemCooking itemCooking = (ItemCooking)itemComponents[i];
                if (itemCooking.wreckWhenCooked && (itemCooking.timesCookedLocal >= 1))
                {

                    suffixCooked += $"{GetText("COOKED_BROKEN", effectColors["Curse"])}";
                    //suffixCooked += "\n" + effectColors["Curse"] + "BROKEN FROM COOKING</color>";
                }
                else if (itemCooking.wreckWhenCooked)
                {
                    suffixCooked += $"{GetText("COOK_BROKEN", effectColors["Curse"])}";
                    //suffixCooked += "\n" + effectColors["Curse"] + "BREAKS IF COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal >= ItemCooking.COOKING_MAX)
                {
                    suffixCooked += $"{GetText("COOKED_MAX", effectColors["Curse"], itemCooking.timesCookedLocal.ToString())}";
                    //suffixCooked += "   " + effectColors["Curse"] + itemCooking.timesCookedLocal.ToString() + "x COOKED\nCANNOT BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal == 0)
                {
                    suffixCooked += $"\n{GetText("COOK", effectColors["Extra Stamina"])}</color>";//CAN BE COOKED
                }
                else if (itemCooking.timesCookedLocal == 1)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Extra Stamina"], itemCooking.timesCookedLocal.ToString(), effectColors["Hunger"])}";
                    //suffixCooked += "   " + effectColors["Extra Stamina"] + itemCooking.timesCookedLocal.ToString() + "x COOKED</color>\n" + effectColors["Hunger"] + "CAN BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal == 2)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Hunger"], itemCooking.timesCookedLocal.ToString(), effectColors["Injury"])}";
                    //suffixCooked += "   " + effectColors["Hunger"] + itemCooking.timesCookedLocal.ToString() + "x COOKED</color>\n" + effectColors["Injury"] + "CAN BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal == 3)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Injury"], itemCooking.timesCookedLocal.ToString(), effectColors["Poison"])}";

                    //suffixCooked += "   " + effectColors["Injury"] + itemCooking.timesCookedLocal.ToString() + "x COOKED</color>\n" + effectColors["Poison"] + "CAN BE COOKED</color>";
                }
                else if (itemCooking.timesCookedLocal >= 4)
                {
                    suffixCooked += $"{GetText("COOKED", effectColors["Poison"], itemCooking.timesCookedLocal.ToString(), "")}";

                    //suffixCooked += "   " + effectColors["Poison"] + itemCooking.timesCookedLocal.ToString() + "x COOKED\nCAN BE COOKED</color>";
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
        itemInfoDisplayTextMesh.text += "\n" + suffixWeight + suffixUses + suffixCooked;
        itemInfoDisplayTextMesh.text = itemInfoDisplayTextMesh.text.Replace("\n\n\n", "\n\n");
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
        result += GetText("ProcessEffect", color, GetText(action), effectColors[effect], (Mathf.Abs(amount) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.ToUpper()}").ToUpper());

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
        result += GetText("ProcessEffectOverTime", color, GetText(action), effectColors[effect], ((Mathf.Abs(amountPerSecond) * time * (1 / rate)) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.ToUpper()}").ToUpper(), time.ToString());
        return result;
    }

    private static string ProcessAffliction(Peak.Afflictions.Affliction affliction)
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
            result += GetText("Affliction_AddBonusStamina", effectColors["ItemInfoDisplayPositive"], effectColors["Extra Stamina"], (effect.staminaAmount * 100f).ToString("F1").Replace(".0", ""));
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
                result += GetText("AFTERWARDS") + ProcessAffliction(effect.drowsyAffliction);
            }
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.AdjustStatus)
        {
            Peak.Afflictions.Affliction_AdjustStatus effect = (Peak.Afflictions.Affliction_AdjustStatus)affliction;
            if (effect.statusAmount > 0)
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
            result += GetText("ProcessEffect", color, GetText(action), effectColors[effect.statusType.ToString()], (Mathf.Abs(effect.statusAmount) * 100f).ToString("F1").Replace(".0", ""), GetText($"Effect_{effect.statusType.ToString().ToUpper()}").ToUpper());

            //result += effectColors[effect.statusType.ToString()] + (Mathf.Abs(effect.statusAmount) * 100f).ToString("F1").Replace(".0", "")
            //    + " " + effect.statusType.ToString().ToUpper() + "</color>\n";
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.DrowsyOverTime)
        {
            Peak.Afflictions.Affliction_AdjustDrowsyOverTime effect = (Peak.Afflictions.Affliction_AdjustDrowsyOverTime)affliction;
            color = effect.statusPerSecond > 0 ? effectColors["ItemInfoDisplayNegative"] : effectColors["ItemInfoDisplayPositive"];
            action = effect.statusPerSecond > 0 ? "GAIN" : "REMOVE";
            result += GetText("ProcessEffectOverTime", color, GetText(action), effectColors["Drowsy"], (Mathf.Round((Mathf.Abs(effect.statusPerSecond) * effect.totalTime * 100f) * 0.4f) / 0.4f).ToString("F1").Replace(".0", ""), GetText("Effect_DROWSY").ToUpper(), effect.totalTime.ToString("F1").Replace(".0", ""));
        }
        else if (affliction.GetAfflictionType() is Peak.Afflictions.Affliction.AfflictionType.ColdOverTime)
        {
            Peak.Afflictions.Affliction_AdjustColdOverTime effect = (Peak.Afflictions.Affliction_AdjustColdOverTime)affliction;
            color = effect.statusPerSecond > 0 ? effectColors["ItemInfoDisplayNegative"] : effectColors["ItemInfoDisplayPositive"];
            action = effect.statusPerSecond > 0 ? "GAIN" : "REMOVE";
            result += GetText("ProcessEffectOverTime", color, GetText(action), effectColors["Cold"], (Mathf.Abs(effect.statusPerSecond) * effect.totalTime * 100f).ToString("F1").Replace(".0", ""), GetText("Effect_COLD").ToUpper(), effect.totalTime.ToString("F1").Replace(".0", ""));
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
                effectColors["Drowsy"]);
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
        if (!Photon.Pun.PhotonNetwork.IsMasterClient) return;

        // 初始化物品列表
        if (!testModeInitialized)
        {
            InitializeTestItemList();
            testModeInitialized = true;
        }

        // F1: 生成下一个物品
        if (Input.GetKeyDown(KeyCode.F1))
        {
            SpawnNextTestItem();
        }

        // F2: 生成上一个物品
        if (Input.GetKeyDown(KeyCode.F2))
        {
            SpawnPreviousTestItem();
        }

        // F3: 输出当前物品信息到日志
        if (Input.GetKeyDown(KeyCode.F3))
        {
            LogCurrentItemInfo();
        }
    }

    private static void InitializeTestItemList()
    {
        allItemPrefabs.Clear();
        var items = Resources.FindObjectsOfTypeAll<Item>();
        foreach (var item in items)
        {
            // 添加所有物品（包括 Prefab 和有特殊组件的）
            if (!item.name.Contains("(Clone)"))
            {
                allItemPrefabs.Add(item);
            }
        }
        allItemPrefabs = allItemPrefabs.OrderBy(i => i.name).ToList();
        Log.LogInfo($"[TestMode] Found {allItemPrefabs.Count} items. Press F1/F2 to cycle, F3 to log current item info.");
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
            var componentNames = components.Select(c => c.GetType().Name).ToList();
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

        Log.LogInfo($"[TestMode] Current Item: {currentItem.name}");
        var components = currentItem.GetComponents<Component>();
        foreach (var c in components)
        {
            Log.LogInfo($"[TestMode]   - {c.GetType().Name}");
        }
        Log.LogInfo($"[TestMode] Display Text:\n{itemInfoDisplayTextMesh.text}");
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
                LocalizedText.MAIN_TABLE.Add($"Mod_{Name}_{item.Key}".ToUpper(), values);
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
        dict.Add("Shield", "<#D48E00>");

        dict.Add("ItemInfoDisplayPositive", "<#DDFFDD>");
        dict.Add("ItemInfoDisplayNegative", "<#FFCCCC>");
    }
}