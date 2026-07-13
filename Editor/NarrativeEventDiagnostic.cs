using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using AssetDatabase = Amplitude.Framework.Asset.AssetDatabase;

/// <summary>
/// Diagnostic: the vanilla "MercuryDatabases" bundle throws a NullReferenceException from
/// Amplitude.Mercury.Data.Simulation.NarrativeEventDefinition.OnValidate every time it's mounted
/// (also reproduces in a stock project, so it's a pre-existing vanilla-data issue, not ours).
/// This loads the bundle directly (bypassing the Amplitude provider wrapper, which caches objects
/// and won't re-trigger OnValidate) and probes each NarrativeEventDefinition main asset one at a
/// time, watching for the exception, to name the offending asset.
/// </summary>
static class NarrativeEventDiagnostic
{
    const string ProviderName = "mercurydatabases.assetbundle";

    [MenuItem("Tools/Debug/Tech Tree/Find Bad NarrativeEventDefinition", false, 103)]
    static void Run()
    {
        string bundlePath = VanillaDatabaseMount.BundlePath;
        if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
        {
            Debug.LogError("[NarrativeEventDiagnostic] Vanilla bundle not found (set the Humankind folder in Mod Editor).");
            return;
        }

        bool wasMounted = VanillaDatabaseMount.IsMounted || AssetDatabase.IsMounted(ProviderName);
        if (wasMounted)
        {
            try { AssetDatabase.UnmountAssetBundle(ProviderName); } catch { /* phantom handle */ }
            VanillaDatabaseMount.Invalidate();
        }

        var savedSelection = Selection.objects;
        Selection.activeObject = null;
        
        var bundle = UnityEngine.AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            Debug.LogError("[NarrativeEventDiagnostic] Failed to load the bundle directly with UnityEngine.AssetBundle.LoadFromFile.");
        }
        else
        {
            try
            {
                var narrativeEventType = typeof(Amplitude.Mercury.Data.Simulation.NarrativeEventDefinition);
                var names = bundle.GetAllAssetNames();
                int checkedCount = 0, suspectCount = 0;
                foreach (var name in names)
                {
                    bool sawException = false;
                    string lastConditionText = null;
                    Application.LogCallback handler = (condition, stackTrace, type) =>
                    {
                        if (type == LogType.Exception) { sawException = true; lastConditionText = condition; }
                    };

                    Application.logMessageReceived += handler;
                    UnityEngine.Object obj;
                    try { obj = bundle.LoadAsset(name, narrativeEventType); }
                    finally { Application.logMessageReceived -= handler; }

                    if (obj == null) continue; // not a NarrativeEventDefinition
                    checkedCount++;

                    if (sawException)
                    {
                        suspectCount++;
                        Debug.LogWarning($"[NarrativeEventDiagnostic] SUSPECT NarrativeEventDefinition '{obj.name}' (bundle path: {name}) — threw: {lastConditionText}");
                        
                        var type = obj.GetType();
                        Debug.LogWarning($"[NarrativeEventDiagnostic] Inspecting nested fields of '{obj.name}' via reflection:");
                        
                        var triggerField = type.GetField("Trigger");
                        if (triggerField != null)
                        {
                            var trigger = triggerField.GetValue(obj);
                            if (trigger == null)
                                Debug.LogWarning("  Trigger = [NULL]");
                            else
                            {
                                Debug.LogWarning($"  Trigger = {trigger.GetType().Name}");
                                var triggerType = trigger.GetType();
                                foreach (var tf in triggerType.GetFields())
                                {
                                    var val = tf.GetValue(trigger);
                                    if (val == null)
                                        Debug.LogWarning($"    Trigger.{tf.Name} = [NULL]");
                                    else if (val is UnityEngine.Object uobj && uobj == null)
                                        Debug.LogWarning($"    Trigger.{tf.Name} = [UNITY NULL]");
                                }
                            }
                        }
                        
                        var choicesField = type.GetField("Choices");
                        if (choicesField != null)
                        {
                            var choices = choicesField.GetValue(obj) as Array;
                            if (choices == null)
                                Debug.LogWarning("  Choices = [NULL]");
                            else
                            {
                                Debug.LogWarning($"  Choices = {choices.Length} elements");
                                for (int i = 0; i < choices.Length; i++)
                                {
                                    var choice = choices.GetValue(i);
                                    if (choice == null)
                                    {
                                        Debug.LogWarning($"    Choices[{i}] = [NULL]");
                                        continue;
                                    }
                                    var choiceType = choice.GetType();
                                    foreach (var cf in choiceType.GetFields())
                                    {
                                        var val = cf.GetValue(choice);
                                        if (val == null)
                                            Debug.LogWarning($"    Choices[{i}].{cf.Name} = [NULL]");
                                        else if (val is UnityEngine.Object uobj && uobj == null)
                                            Debug.LogWarning($"    Choices[{i}].{cf.Name} = [UNITY NULL]");
                                    }
                                }
                            }
                        }
                    }
                    Selection.activeObject = null;
                }
                Debug.Log($"[NarrativeEventDiagnostic] Scan complete. {suspectCount} suspect asset(s) out of {checkedCount} NarrativeEventDefinition assets checked.");
            }
            finally
            {
                bundle.Unload(true);
            }
        }
        
        Selection.objects = savedSelection;

        if (VanillaDatabaseMount.ForceRemount(out var error))
            Debug.Log("[NarrativeEventDiagnostic] Restored vanilla mount.");
        else
            Debug.LogError($"[NarrativeEventDiagnostic] Failed to restore vanilla mount: {error}");
    }
}
