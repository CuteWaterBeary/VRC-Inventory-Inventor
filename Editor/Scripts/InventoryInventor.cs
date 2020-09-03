﻿using BMBLibraries.Classes;
using BMBLibraries.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.VersionControl;
using UnityEngine;
using UnityEngine.Networking;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

public class InventoryInventor : UnityEngine.Object
{
    // Objects to modify.
    public VRCAvatarDescriptor avatar;
    public VRCExpressionsMenu menu;
    public AnimatorController controller;

    // Input objects.
    public InventoryPreset preset;
    public float refreshRate = 0.05f;
    public bool removeParameters = false;

    // Path related.
    public string relativePath;
    public string outputPath;
    public bool autoOverwrite = false;
    
    // File backup.
    private Backup backupManager;
    private AssetList generated;

    // Other data.
    private int totalToggles;

    // Default constructor.
    public InventoryInventor() { }

    // Called by the Editor window to create an inventory from the stored preset onto the selected Avatar.
    public void CreateInventory()
    {
        // Try catch block because there's surely some Exception I can't account for.
        try
        {
            // Initial Save.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Check that the selected avatar is valid.
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: No Avatar selected.", "Close");
                return;
            }
            else if (avatar.expressionParameters == null)
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Avatar does not have an Expression Parameters object assigned in the descriptor.", "Close");
                Selection.activeObject = avatar;
                return;
            }

            // Make sure a preset was actually provided.
            if (preset == null)
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: No preset provided.", "Close");
                return;
            }

            /*
                Check for space in parameters list & check for incompatible Animations.
            */

            int paramCount = 0;
            int present = 0;

            // For each parameter, check if it's the Inventory parameter and if it is the correct type.
            foreach (VRCExpressionParameters.Parameter param in avatar.expressionParameters.parameters)
            {
                // The Inventory parameter is already present, exit the loop.
                if (present == 1)
                {
                    break;
                }
                switch (param.name) 
                {
                    case "Inventory":
                        if (param.valueType == VRCExpressionParameters.ValueType.Int)
                        {
                            present++;
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Expression Parameter \"" + param.name + "\" is present with the wrong type.", "Close");
                            Selection.activeObject = avatar.expressionParameters;
                            return;
                        }
                        break;
                    default:
                        if (param.name != "")
                            paramCount++;
                        break;
                }
            }

            // If Inventory isn't present and there's no room to add it.
            if (16 - paramCount < 1 - present)
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: No unused Expression Parameters found.", "Close");
                Selection.activeObject = avatar.expressionParameters;
                return;
            }

            // Check that no Animations modify a humanoid rig or Transform.
            totalToggles = 0;
            int totalUsage = 1;
            foreach (Page page in preset.Pages)
            {
                foreach (PageItem item in page.Items)
                {
                    if (item.Type == PageItem.ItemType.Toggle)
                    {
                        if (!CheckCompatibility(item.EnableClip, false, out Type problem, out string propertyName))
                        {
                            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: " + item.EnableClip.name + " cannot be used because it modifies an invalid property type!\n\nInvalid Property Type: " + problem.Name + "\nName: " + propertyName, "Close");
                            Selection.activeObject = item.EnableClip;
                            return;
                        }
                        if (!CheckCompatibility(item.DisableClip, false, out problem, out propertyName))
                        {
                            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: " + item.DisableClip.name + " cannot be used because it modifies an invalid property type!\n\nInvalid Property Type: " + problem.Name + "\nName: " + propertyName, "Close");
                            Selection.activeObject = item.DisableClip;
                            return;
                        }

                        // Simulatenously, increment the known number of toggles and parameter usage.
                        totalToggles++;
                        switch (item.Sync)
                        {
                            case PageItem.SyncMode.Off:
                                totalUsage += 1;
                                if (item.EnableGroup.Length > 0)
                                    totalUsage++;
                                if (item.DisableGroup.Length > 0)
                                    totalUsage++;
                                break;
                            case PageItem.SyncMode.Manual:
                                totalUsage += 3;
                                break;
                            case PageItem.SyncMode.Auto:
                                totalUsage += 3;
                                if (item.EnableGroup.Length > 0)
                                    totalUsage++;
                                if (item.DisableGroup.Length > 0)
                                    totalUsage++;
                                break;
                        }
                    }
                }
            }

            // The preset has too many toggles.
            if (totalUsage > 256)
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Preset uses too much data for syncing!", "Close");
                Selection.activeObject = preset;
                return;
            }

            // Check that the file destination exists.
            VerifyDestination();

            // Start doing modifications.
            EditorUtility.DisplayProgressBar("Inventory Inventor", "Starting", 0);

            // Initialize backup objects.
            backupManager = new Backup();
            generated = new AssetList();

            /*
                Get FX Animator.
             */

            // An avatar is humanoid if the descriptor has the Gesture and Additive layers available.
            bool humanoid = avatar.baseAnimationLayers.Length == 5;

            AnimatorController animator = controller != null ? controller : null;
            
            // Replace the Animator Controller in the descriptor if this Controller was there to begin with.
            bool replaceAnimator = humanoid ? (animator != null && avatar.baseAnimationLayers[4].animatorController != null && animator == (AnimatorController)avatar.baseAnimationLayers[4].animatorController) : (animator != null && avatar.baseAnimationLayers[2].animatorController != null && animator == (AnimatorController)avatar.baseAnimationLayers[2].animatorController);

            // Create new Animator Controller from SDK template if none was provided.
            if (animator == null)
            {
                switch (CopySDKTemplate(avatar.name + "_FX.controller", "vrc_AvatarV3FaceLayer"))
                {
                    case 1:
                        EditorUtility.DisplayDialog("Inventory Inventor", "Cancelled.", "Close");
                        RevertChanges();
                        return;
                    case 3:
                        EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Failed to create one or more files!", "Close");
                        RevertChanges();
                        return;
                }

                animator = (AssetDatabase.FindAssets(avatar.name + "_FX", new string[] { outputPath + Path.DirectorySeparatorChar + "Animators" }).Length != 0) ? (AnimatorController)AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets(avatar.name + "_FX", new string[] { outputPath + Path.DirectorySeparatorChar + "Animators" })[0]), typeof(AnimatorController)) : null;

                if (animator == null)
                {
                    EditorUtility.DisplayDialog("Inventory Inventor", "Failed to copy template Animator from VRCSDK.", "Close");
                    RevertChanges();
                    return;
                }
            }
            else
            {
                backupManager.AddToBackup(new Asset(AssetDatabase.GetAssetPath(animator)));
            }

            // Create a fresh and clean Animator Controller object.
            AnimatorController newAnimator = new AnimatorController
            {
                name = animator.name,
                parameters = animator.parameters,
                hideFlags = animator.hideFlags
            };
            AssetDatabase.CreateAsset(newAnimator, relativePath + Path.DirectorySeparatorChar + "temp.controller");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            generated.Add(new Asset(AssetDatabase.GetAssetPath(newAnimator)));

            // Clone the provided Animator Controller into the new one, without any Inventory layers.
            for (int i = 0; i < animator.layers.Length; i++)
            {
                // A layer is an Inventory layer if the State Machine has a InventoryMachine behaviour attached.
                if (animator.layers[i].stateMachine.behaviours.Length < 1 || animator.layers[i].stateMachine.behaviours[0].GetType() != typeof(InventoryMachine))
                {
                    EditorUtility.DisplayProgressBar("Inventory Inventor", string.Format("Cloning Layers: {0}", animator.layers[i].name), 0.05f * (float.Parse(i.ToString()) / animator.layers.Length));

                    // Clone the layer.
                    newAnimator.AddLayer(animator.layers[i].name);
                    AnimatorControllerLayer[] layers = newAnimator.layers;
                    AnimatorControllerLayer layer = layers[layers.Length - 1];
                    layer = animator.layers[i].DeepClone();
                    layers[layers.Length - 1] = layer;
                    newAnimator.layers = layers;

                    EditorUtility.DisplayProgressBar("Inventory Inventor", string.Format("Cloning Layers: {0}", animator.layers[i].name), 0.05f * ((i + 1f) / animator.layers.Length));
                }
            }                    
            newAnimator.SaveController();

            // Replace the old Animator Controller.
            string path = AssetDatabase.GetAssetPath(animator);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(newAnimator), path);
            AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(newAnimator), path.Substring(path.LastIndexOf(Path.DirectorySeparatorChar) + 1));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Replace the Animator Controller in the descriptor if it was there.
            if (replaceAnimator)
            {
                if (humanoid)
                    avatar.baseAnimationLayers[4].animatorController = newAnimator;
                else
                    avatar.baseAnimationLayers[2].animatorController = newAnimator;
            }
            controller = newAnimator;

            /*
                Create parameters.
            */

            EditorUtility.DisplayProgressBar("Inventory Inventor", "Creating Parameters", 0.05f);

            AnimatorControllerParameter[] srcParam = newAnimator.parameters;

            // Check if the parameters already exist. If one does as the correct type, use it. If one already exists as the wrong type, abort.
            bool[] existing = new bool[totalToggles + 2];
            for (int i = 0; i < srcParam.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Inventory Inventor", "Creating Parameters", 0.05f + (0.025f * (float.Parse(i.ToString()) / srcParam.Length)));
                bool flag = true;
                foreach (bool exists in existing)
                {
                    if (!exists)
                    {
                        flag = false;
                    }
                }
                if (flag)
                {
                    break;
                }
                for (int j = 0; j < totalToggles; j++)
                {
                    if (srcParam[i].name == "Inventory " + (j + 1))
                    {
                        if (srcParam[i].type == AnimatorControllerParameterType.Bool)
                        {
                            existing[j] = true;
                            break;
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Animator Parameter \"" + srcParam[i].name + "\" already exists as the incorrect type.", "Close");
                            RevertChanges();
                            Selection.activeObject = animator;
                            return;
                        }
                    }
                    else if (srcParam[i].name == "Inventory")
                    {
                        if (srcParam[i].type == AnimatorControllerParameterType.Int)
                        {
                            existing[existing.Length - 2] = true;
                            break;
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Animator Parameter \"" + srcParam[i].name + "\" already exists as the incorrect type.", "Close");
                            RevertChanges();
                            Selection.activeObject = animator;
                            return;
                        }
                    }
                    else if (srcParam[i].name == "IsLocal")
                    {
                        if (srcParam[i].type == AnimatorControllerParameterType.Bool)
                        {
                            existing[existing.Length - 1] = true;
                            break;
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Animator Parameter \"" + srcParam[i].name + "\" already exists as the incorrect type.", "Close");
                            RevertChanges();
                            Selection.activeObject = animator;
                            return;
                        }
                    }
                }
            }

            // Add the needed parameters to the Animator Controller.
            for (int i = 0; i < existing.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Inventory Inventor", "Creating Parameters", 0.075f + (0.025f * (float.Parse(i.ToString()) / existing.Length)));
                if (i < existing.Length - 2)
                {
                    if (!existing[i])
                    {
                        newAnimator.AddParameter("Inventory " + (i + 1), AnimatorControllerParameterType.Bool);
                    }
                }
                else if (i == existing.Length - 2)
                {
                    if (!existing[i])
                    {
                        newAnimator.AddParameter("Inventory", AnimatorControllerParameterType.Int);
                    }
                }
                else if (i == existing.Length - 1)
                {
                    if (!existing[i])
                    {
                        newAnimator.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            EditorUtility.DisplayProgressBar("Inventory Inventor", "Creating Parameters", 0.1f);

            /*
                Create layers.
            */

            CreateMasterLayer(newAnimator, totalToggles, out List<PageItem> items, out List<KeyValuePair<List<int>, List<int>>> activeStates);
            CreateItemLayers(newAnimator, ref items, ref activeStates);

            EditorUtility.DisplayProgressBar("Inventory Inventor", "Saving Controller", 0.9f);
            newAnimator.SaveController();
            AssetDatabase.SaveAssets();

            /*
                Add expression parameters to the list.
            */

            EditorUtility.DisplayProgressBar("Inventory Inventor", "Finalizing", 0.9f);
            foreach (VRCExpressionParameters.Parameter param in avatar.expressionParameters.parameters)
            {
                if (present == 1)
                {
                    break;
                }
                else if (param.name == "")
                {
                    param.name = "Inventory";
                    param.valueType = VRCExpressionParameters.ValueType.Int;
                    break;
                }
            }
            EditorUtility.DisplayProgressBar("Inventory Inventor", "Finalizing", 0.95f);

            /*
                Create Expressions menu for toggles.
            */

            switch (CreateMenus(out VRCExpressionsMenu inventory))
            {
                case 1:
                    EditorUtility.DisplayDialog("Inventory Inventor", "Cancelled.", "Close");
                    RevertChanges();
                    return;
                case 3:
                    EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: Failed to create one or more files!", "Close");
                    RevertChanges();
                    return;
            }

            // Assign the Inventory menu to given menu if possible and provided.
            if (menu != null)
            {
                bool exists = false;

                // Check if the control existed prior. If it did, just replace it.
                foreach (VRCExpressionsMenu.Control control in menu.controls)
                {
                    if (control.name == preset.Pages[0].name && control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu == null)
                    {
                        exists = true;
                        control.icon = preset.Pages[0].Icon;
                        control.subMenu = inventory;
                        break;
                    }
                }

                // Otherwise, if there is free space available, add the menu as a control.
                if (!exists && menu.controls.ToArray().Length < 8)
                {
                    menu.controls.Add(new VRCExpressionsMenu.Control() { name = preset.Pages[0].name, icon = preset.Pages[0].Icon, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = inventory });
                }
                else if (!exists)
                {
                    EditorUtility.DisplayDialog("Inventory Inventory", "WARNING: Inventory controls were not added to the provided menu.\n(No space is available.)", "Close");
                }
            }
            
            EditorUtility.DisplayProgressBar("Inventory Inventor", "Finalizing", 1f);

            /*
                Save configuration.
             */

            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Inventory Inventory", "Success!", "Close");

            // Focus the Editor on the modified menu or the Inventory menu.
            Selection.activeObject = menu != null ? menu : inventory;
            return;
        }
        catch (Exception err)
        {
            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: An exception has occured!\nCheck the console for more details.", "Close");
            Debug.LogError(err);
            RevertChanges();
            return;
        }
    }

    // Checks if an AnimationClip contains invalid bindings.
    private bool CheckCompatibility(AnimationClip clip, bool transformsOnly, out Type problem, out string name)
    {
        if (clip != null)
        {
            // Loop through each modified property in the AnimationClip.
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                // If the binding is a Transform or Animator when transformsOnly is false or vice versa, return false with the binding's type and name.
                if ((transformsOnly && binding.type != typeof(Transform) && binding.type != typeof(Animator)) || (!transformsOnly && (binding.type == typeof(Transform) || binding.type == typeof(Animator))))
                {
                    problem = binding.type;
                    name = binding.propertyName;
                    return false;
                }
            }
        }

        problem = null;
        name = "";
        return true;
    }

    // Checks if the destination is valid.
    private void VerifyDestination()
    {
        // If the destination is not valid, use the default path.
        if (!AssetDatabase.IsValidFolder(outputPath))
        {
            if (!AssetDatabase.IsValidFolder(relativePath + Path.DirectorySeparatorChar + "Output"))
            {
                string guid = AssetDatabase.CreateFolder(relativePath, "Output");
                outputPath = AssetDatabase.GUIDToAssetPath(guid);
            }
            else
            {               
                outputPath = relativePath + Path.DirectorySeparatorChar + "Output";
            }
        }
    }

    // Copies an Animator Controller from the VRCSDK to the given location.
    private int CopySDKTemplate(string outFile, string SDKfile)
    {
        if (!AssetDatabase.IsValidFolder(outputPath + Path.DirectorySeparatorChar + "Animators"))
            AssetDatabase.CreateFolder(outputPath, "Animators");
        bool existed = true;
        if (File.Exists(outputPath + Path.DirectorySeparatorChar + "Animators" + Path.DirectorySeparatorChar + outFile))
        {
            if (!autoOverwrite)
            {
                switch (EditorUtility.DisplayDialogComplex("Inventory Inventor", outFile + " already exists!\nOverwrite the file?", "Overwrite", "Cancel", "Skip"))
                {
                    case 1:
                        return 1;
                    case 2:
                        return 2;
                }
            }
            backupManager.AddToBackup(new Asset(outputPath + Path.DirectorySeparatorChar + "Animators" + Path.DirectorySeparatorChar + outFile));
        }
        else
        {
            existed = false;
        }
        if (!AssetDatabase.CopyAsset(AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets(SDKfile, new string[] { "Assets" + Path.DirectorySeparatorChar + "VRCSDK" + Path.DirectorySeparatorChar + "Examples3" + Path.DirectorySeparatorChar + "Animation" + Path.DirectorySeparatorChar + "Controllers" })[0]), outputPath + Path.DirectorySeparatorChar + "Animators" + Path.DirectorySeparatorChar + outFile))
        {
            return 3;
        }
        else
        {
            AssetDatabase.Refresh();
            if (!existed)
                generated.Add(new Asset(outputPath + Path.DirectorySeparatorChar + "Animators" + Path.DirectorySeparatorChar + outFile));
        }
        return 0;
    }

    // Creates all the menus needed for the generated Inventory.
    private int CreateMenus(out VRCExpressionsMenu mainMenu)
    {
        mainMenu = null;

        // Create a list of menu objects and instantiate a new one for each page.
        List<VRCExpressionsMenu> pages = new List<VRCExpressionsMenu>();

        // Instantiate the menus prior to configuring them.
        for (int i = 0; i < preset.Pages.Count; i++)
        {
            pages.Add(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
            pages[i].name = preset.Pages[i].name;
        }

        // Loop through each page, adding controls as the preset specifies.
        int index = 0;
        for (int i = 0; i < preset.Pages.Count; i++)
        {
            for (int j = 0; j < preset.Pages[i].Items.Count; j++)
            {
                switch (preset.Pages[i].Items[j].Type)
                {
                    case PageItem.ItemType.Toggle:
                        pages[i].controls.Add(new VRCExpressionsMenu.Control() { name = preset.Pages[i].Items[j].name, type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter() { name = "Inventory" }, value = index + 1 });
                        index++;
                        break;
                    case PageItem.ItemType.Page:
                        int val = preset.Pages.IndexOf(preset.Pages[i].Items[j].PageReference);
                        pages[i].controls.Add(new VRCExpressionsMenu.Control() { name = preset.Pages[val].name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = pages[val] });
                        break;
                    case PageItem.ItemType.Submenu:
                        pages[i].controls.Add(new VRCExpressionsMenu.Control() { name = preset.Pages[i].Items[j].name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = preset.Pages[i].Items[j].Submenu });
                        break;
                }
            }
        }

        // Create output directory if not present.
        if (!AssetDatabase.IsValidFolder(outputPath + Path.DirectorySeparatorChar + "Menus"))
            AssetDatabase.CreateFolder(outputPath, "Menus");

        // Create / overwrite each menu asset to the directory.
        foreach (VRCExpressionsMenu page in pages)
        {
            bool exists = true;
            if (File.Exists(outputPath + Path.DirectorySeparatorChar + "Menus" + Path.DirectorySeparatorChar + avatar.name + "_" + page.name + ".asset"))
            {
                if (!autoOverwrite)
                {
                    switch (EditorUtility.DisplayDialogComplex("Inventory Inventor", avatar.name + "_" + page.name + ".asset" + " already exists!\nOverwrite the file?", "Overwrite", "Cancel", "Skip"))
                    {
                        case 1:
                            return 1;
                        case 2:
                            return 2;
                    }
                }
                backupManager.AddToBackup(new Asset(outputPath + Path.DirectorySeparatorChar + "Menus" + Path.DirectorySeparatorChar + avatar.name + "_" + page.name + ".asset"));
                AssetDatabase.DeleteAsset(outputPath + Path.DirectorySeparatorChar + "Menus" + Path.DirectorySeparatorChar + avatar.name + "_" + page.name + ".asset");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            else
            {
                exists = false;
            }
            AssetDatabase.CreateAsset(page, outputPath + Path.DirectorySeparatorChar + "Menus" + Path.DirectorySeparatorChar + avatar.name + "_" + page.name + ".asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
                
            // Check that the asset was saved successfully.
            if (AssetDatabase.FindAssets(page.name, new string[] { outputPath + Path.DirectorySeparatorChar + "Menus" }).Length == 0)
            {
                return 3;
            }
            else
            {
                AssetDatabase.Refresh();
                if (!exists)
                    generated.Add(new Asset(outputPath + Path.DirectorySeparatorChar + "Menus" + Path.DirectorySeparatorChar + page.name + ".asset"));
            }
        }

        // Out the top level menu.
        mainMenu = pages[0];
        return 0;
    }

    // Creates layers for each item in the inventory (ordered by page).
    private void CreateItemLayers(AnimatorController source, ref List<PageItem> items, ref List<KeyValuePair<List<int>, List<int>>> activeStates)
    {
        /*
           CODERS NOTE: Instantiation of Unity Objects is unbelievably slow. Back when I was using a float instead of an int, duplicating a template object instead of instantiating a new one decreased the total execution time immensely. So although this method is absolutely required for large data sets to complete in a reasonable amount of time, it likely isn't as important with the data set used now. It still works with smaller sets though so I didn't bother to change it. "If it ain't broke, don't fix it."
         */

        // Create a template machine to duplicate.
        AnimatorStateMachine templateMachine = new AnimatorStateMachine 
        {
            // This behaviour is added so it will be detected as a Inventory layer.
            behaviours = new StateMachineBehaviour[] { ScriptableObject.CreateInstance<InventoryMachine>() }
        };
        ChildAnimatorState[] states = new ChildAnimatorState[templateMachine.states.Length + 2];
        templateMachine.states.CopyTo(states, 2);

        // Create a template state to duplicate.
        ChildAnimatorState templateState = new ChildAnimatorState
        {
            state = new AnimatorState
            {
                name = "",
                motion = null,
                behaviours = new StateMachineBehaviour[] { ScriptableObject.CreateInstance<VRCAvatarParameterDriver>() }
            }
        };
        ((VRCAvatarParameterDriver)templateState.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });

        // Get a starting position for the states.
        Vector3 pos = templateMachine.anyStatePosition;

        // Create an off state.
        ChangeState(templateState, "Off");
        templateState.position = pos - new Vector3(150, -45);
        states[0] = templateState.DeepClone();

        // Create an on state.
        templateState.position = pos + new Vector3(100, 45);
        ChangeState(templateState, "On");
        states[1] = templateState.DeepClone();

        // Add the states to the template machine.
        templateMachine.states = states;

        // Create a template transition.
        AnimatorStateTransition templateTransition = new AnimatorStateTransition
        {
            destinationState = null,
            isExit = false,
            hasExitTime = false,
            duration = 0,
            canTransitionToSelf = false,
            conditions = null
        };

        // Pregenerate and assign layer names.
        List<string> layerNames = new List<string>();

        for (int i = 0; i < items.Count; i++)
        {
            string name = items[i].name;

            // Deal with layers that have the same name.          
            List<string> names = new List<string>();
            for (int j = 0; j < layerNames.Count; j++)
            {
                if (i != j)
                    names.Add(layerNames[j]);
            }
            string pageName = "";
            foreach (Page page in preset.Pages)
            {
                if (page.Items.Contains(items[i]))
                {
                    pageName = page.name;
                    break;
                }
            }
            if (names.Contains(name + " (" + pageName + ")"))
            {
                int occurance = 0;
                while (names.Contains(name + " (" + pageName + ") " + occurance))
                {
                    occurance++;
                }
                name = items[i].name + " (" + pageName + ") " + occurance;
            }
            else if (names.Contains(name))
            {
                int otherIndex = layerNames.IndexOf(name);
                string otherPageName = "";
                foreach (Page page in preset.Pages)
                {
                    if (page.Items.Contains(items[otherIndex]))
                    {
                        otherPageName = page.name;
                        break;
                    }
                }
                layerNames[otherIndex] = items[otherIndex].name + " (" + otherPageName + ")";

                name = items[i].name + " (" + pageName + ")";
            }

            layerNames.Add(name);
        }

        // For each item in the inventory...
        for (int i = 0; i < items.Count; i++)
        {
            EditorUtility.DisplayProgressBar("Inventory Inventor", string.Format(CultureInfo.InvariantCulture, "Creating Item Layers: {0} ({1:#0.##%})", items[i].name, (i + 1f) / items.Count), 0.55f + (0.35f * (float.Parse(i.ToString()) / items.Count)));
            
            // Grab its active states.
            KeyValuePair<List<int>, List<int>> active = activeStates[i];

            // Create a layer.
            source.AddLayer(layerNames[i]);
            AnimatorControllerLayer[] layers = source.layers;
            AnimatorControllerLayer currentLayer = layers[layers.Length - 1];
            currentLayer.defaultWeight = 1;

            // Create an AnyState transition to the on and off state with their assigned conditionals.
            List<AnimatorStateTransition> transitions = new List<AnimatorStateTransition>();

            // Disabled state.
            ChangeState(templateMachine.states[0].state, items[i].DisableClip);
            ((VRCAvatarParameterDriver)templateMachine.states[0].state.behaviours[0]).parameters[0].name = "Inventory " + (i + 1);
            ((VRCAvatarParameterDriver)templateMachine.states[0].state.behaviours[0]).parameters[0].value = 0;

            // Add a transition for each disabled value.
            for (int j = 0; j < active.Key.Count; j++)
            {
                ChangeTransition(templateTransition, active.Key[j], templateMachine.states[0].state);
                transitions.Add((AnimatorStateTransition)templateTransition.DeepClone(templateMachine.states[0]));
                if (items[i].Sync == PageItem.SyncMode.Off)
                {
                    transitions[transitions.Count - 1].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                }
            }

            // Special transition for local toggles.
            if (items[i].Sync == PageItem.SyncMode.Off)
            {
                ChangeTransition(templateTransition, i + 1, false, templateMachine.states[0].state);
                transitions.Add((AnimatorStateTransition)templateTransition.DeepClone(templateMachine.states[0]));
                transitions[transitions.Count - 1].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            }

            // Enabled state.
            ChangeState(templateMachine.states[1].state, items[i].EnableClip);
            ((VRCAvatarParameterDriver)templateMachine.states[1].state.behaviours[0]).parameters[0].name = "Inventory " + (i + 1);
            ((VRCAvatarParameterDriver)templateMachine.states[1].state.behaviours[0]).parameters[0].value = 1;

            // Add a transition for each enabled value.
            for (int j = 0; j < active.Value.Count; j++)
            {
                ChangeTransition(templateTransition, active.Value[j], templateMachine.states[1].state);
                transitions.Add((AnimatorStateTransition)templateTransition.DeepClone(templateMachine.states[1]));
                if (items[i].Sync == PageItem.SyncMode.Off)
                {
                    transitions[transitions.Count - 1].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                }
            }

            // Special transition for local toggles.
            if (items[i].Sync == PageItem.SyncMode.Off)
            {
                ChangeTransition(templateTransition, i + 1, true, templateMachine.states[1].state);
                transitions.Add((AnimatorStateTransition)templateTransition.DeepClone(templateMachine.states[1]));
                transitions[transitions.Count - 1].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            }

            // Configure the machine and clone it
            templateMachine.name = currentLayer.name;
            templateMachine.anyStateTransitions = transitions.ToArray();
            templateMachine.defaultState = items[i].InitialState ? templateMachine.states[1].state : templateMachine.states[0].state;
            currentLayer.stateMachine = templateMachine.DeepClone();
            layers[layers.Length - 1] = currentLayer;
            source.layers = layers;
        }
        return;
    }

    // Creates the master layer that handles menu inputs and the idle sync.
    private void CreateMasterLayer(AnimatorController source, int itemTotal, out List<PageItem> items, out List<KeyValuePair<List<int>, List<int>>> activeStates)
    {
        /*
           CODERS NOTE: Instantiation of Unity Objects is unbelievably slow. Back when I was using a float instead of an int, duplicating a template object instead of instantiating a new one decreased the total execution time immensely. So although this method is absolutely required for large data sets to complete in a reasonable amount of time, it likely isn't as important with the data set used now. It still works with smaller sets though so I didn't bother to change it. "If it ain't broke, don't fix it."
         */

        EditorUtility.DisplayProgressBar("Inventory Inventor", "Creating Master Layer: Preparing", 0.1f);

        // Add Master Layer.
        source.AddLayer("Inventory Master");
        AnimatorControllerLayer masterLayer = source.layers[source.layers.Length - 1];
        
        // This behaviour is added to identify this layer as an Inventory layer.
        masterLayer.stateMachine.behaviours = new StateMachineBehaviour[] { ScriptableObject.CreateInstance<InventoryMachine>() };

        // Get a list of toggles.
        items = new List<PageItem>();
        foreach (Page page in preset.Pages)
        {
            foreach (PageItem item in page.Items)
            {
                if (item.Type == PageItem.ItemType.Toggle)
                {
                    items.Add(item);
                }
            }
        }

        // Create List of state values (KEY = DISABLE | VALUE = ENABLE).
        activeStates = new List<KeyValuePair<List<int>, List<int>>>();

        // Fill the list with initial values.
        int value = itemTotal + 1;
        for (int i = 0; i < itemTotal; i++)
        {
            switch (items[i].Sync)
            {
                // Off uses up to 3 values.
                case PageItem.SyncMode.Off:
                    activeStates.Add(new KeyValuePair<List<int>, List<int>>(new List<int>() { }, new List<int>() { }));
                    if (items[i].EnableGroup.Length > 0)
                    {
                        activeStates[activeStates.Count - 1].Value.Add(value);
                        value++;
                    }
                    if (items[i].DisableGroup.Length > 0)
                    {
                        activeStates[activeStates.Count - 1].Key.Add(value);
                        value++;
                    }
                    break;
                // Manual uses 3 values always.
                case PageItem.SyncMode.Manual:
                    activeStates.Add(new KeyValuePair<List<int>, List<int>>(new List<int>() { value }, new List<int>() { value + 1 }));
                    value += 2;
                    break;
                // Auto uses between 3 and 5 values.
                case PageItem.SyncMode.Auto:
                    activeStates.Add(new KeyValuePair<List<int>, List<int>>(new List<int>() { value }, new List<int>() { value + 1 }));
                    value += 2;
                    if (items[i].EnableGroup.Length > 0)
                    {
                        activeStates[activeStates.Count - 1].Value.Add(value);
                        value++;
                    }
                    if (items[i].DisableGroup.Length > 0)
                    {
                        activeStates[activeStates.Count - 1].Key.Add(value);
                        value++;
                    }
                    break;
            }
        } 

        // Create an array states to be created.
        List<ChildAnimatorState> states = new List<ChildAnimatorState>();
        states.AddRange(masterLayer.stateMachine.states);

        // Store a starting position for the states.
        Vector3 pos = masterLayer.stateMachine.entryPosition;

        // Create the starting state.
        states.Add(new ChildAnimatorState
        {
            position = pos + new Vector3(-25, 50),
            state = new AnimatorState
            {
                name = "Idle",
                behaviours = new StateMachineBehaviour[] { ScriptableObject.CreateInstance<VRCAvatarParameterDriver>() }
            }
        });
        ((VRCAvatarParameterDriver)states[states.Count - 1].state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });

        // Create a template state for cloning.
        ChildAnimatorState templateState = new ChildAnimatorState
        {
            state = new AnimatorState
            {
                name = "",
                behaviours = new StateMachineBehaviour[] { ScriptableObject.CreateInstance<VRCAvatarParameterDriver>() }
            }
        };
        ((VRCAvatarParameterDriver)templateState.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });

        // Move down a row in the grid.
        pos += new Vector3(0, 125);

        // Create a template transition.
        AnimatorStateTransition templateTransition = new AnimatorStateTransition
        {
            destinationState = null,
            isExit = false,
            hasExitTime = true,
            exitTime = refreshRate,
            duration = 0,
            canTransitionToSelf = false,
            conditions = null
        };

        // Loop through the toggles and add them to the synchronization loop if Auto Sync is enabled.
        bool syncExists = false;
        for (int i = 0; i < items.Count; i++)
        {
            EditorUtility.DisplayProgressBar("Inventory Inventor", string.Format(CultureInfo.InvariantCulture, "Creating Master Layer: Creating States ({0:#0.##%})", (i + 1f) / (items.Count * 2)), 0.1f + (0.225f * ((i + 1f) / (items.Count * 2))));

            if (items[i].Sync == PageItem.SyncMode.Auto)
            {
                // Create the enabled sync state.
                ChangeState(templateState.state, "Syncing " + (i + 1), activeStates[i].Value[0]);
                templateState.position = pos - new Vector3(150, 0);
                states.Add(templateState.DeepClone());

                // Create the disabled sync state.
                ChangeState(templateState.state, "Syncing " + (i + 1) + " ", activeStates[i].Key[0]);
                templateState.position = pos + new Vector3(100, 0);
                states.Add(templateState.DeepClone());

                if (i > 0)
                {
                    // Create transitions to enabled state from the previous pair.
                    ChangeTransition(templateTransition, states[states.Count - 2], (i + 1), true);
                    states[states.Count - 3].state.AddTransition((AnimatorStateTransition)AnimatorExtensions.DeepClone(templateTransition, states[states.Count - 2]));
                    ChangeTransition(templateTransition, states[states.Count - 2], (i + 1), true);
                    states[states.Count - 4].state.AddTransition((AnimatorStateTransition)AnimatorExtensions.DeepClone(templateTransition, states[states.Count - 2]));

                    // Create transitions to disabled state from the previous pair.
                    ChangeTransition(templateTransition, states[states.Count - 1], (i + 1), false);
                    states[states.Count - 3].state.AddTransition((AnimatorStateTransition)AnimatorExtensions.DeepClone(templateTransition, states[states.Count - 1]));
                    ChangeTransition(templateTransition, states[states.Count - 1], (i + 1), false);
                    states[states.Count - 4].state.AddTransition((AnimatorStateTransition)AnimatorExtensions.DeepClone(templateTransition, states[states.Count - 1]));
                }
                else
                {
                    // Track that at least one item is synced for later.
                    syncExists = true;
                }

                // Move down a row.
                pos += new Vector3(0, 75);
            }
        }
        // Final transitions for the Auto Sync loop.
        if (states.Count > 2)
        {
            // Template transition not used here since it's faster and more efficient to write it this way.
            states[states.Count - 1].state.AddExitTransition();
            states[states.Count - 1].state.transitions[0].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            states[states.Count - 1].state.transitions[0].hasExitTime = true;
            states[states.Count - 1].state.transitions[0].exitTime = refreshRate;
            states[states.Count - 1].state.transitions[0].duration = 0;
            states[states.Count - 2].state.AddExitTransition();
            states[states.Count - 2].state.transitions[0].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            states[states.Count - 2].state.transitions[0].hasExitTime = true;
            states[states.Count - 2].state.transitions[0].exitTime = refreshRate;
            states[states.Count - 2].state.transitions[0].duration = 0;
        }       
        
        // First transition to trap remote clients (or acts as an idle state when Auto Sync is disabled for all items).
        states[0].state.AddExitTransition();
        states[0].state.transitions[0].AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
        states[0].state.transitions[0].hasExitTime = false;
        states[0].state.transitions[0].duration = 0;
        masterLayer.stateMachine.exitPosition = pos;

        // Create a template toggle state.
        ChildAnimatorState templateToggle = new ChildAnimatorState
        {
            state = new AnimatorState { behaviours = new StateMachineBehaviour[] { ScriptableObject.CreateInstance<VRCAvatarParameterDriver>() } }
        };

        // Create a template toggle transition.
        AnimatorStateTransition toggleTransition = new AnimatorStateTransition
        {
            isExit = true,
            exitTime = 1f,
            hasExitTime = true,
            duration = 0f,
            canTransitionToSelf = false
        };

        // Reset or adjust some existing values.
        templateTransition.hasExitTime = false;
        pos += new Vector3(0, 60);

        // Create an array of AnyState transitions.
        List<AnimatorStateTransition> anyTransitions = new List<AnimatorStateTransition>();

        // For each item in the inventory...
        for (int i = 0; i < items.Count; i++)
        {
            EditorUtility.DisplayProgressBar("Inventory Inventor", string.Format(CultureInfo.InvariantCulture, "Creating Master Layer: Creating States ({0:#0.##%})", (i + items.Count) / (items.Count * 2f)), 0.1f + (0.225f * ((i + items.Count) / (items.Count * 2f))));

            // Create an On state.
            templateToggle.state.name = ("Toggling " + (i + 1) + ": On");
            templateToggle.position = pos - new Vector3(150, 0);

            // Adjust parameter settings.
            switch (items[i].Sync)
            {
                // Off: If a group is present, use that value instead of the boolean.
                case PageItem.SyncMode.Off:
                    if (items[i].EnableGroup.Length > 0)
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = activeStates[i].Value[0] });                     
                    else
                    {
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory " + (i + 1), value = 1 });
                    }
                    break;
                // Manual: Use the first values in active states.
                case PageItem.SyncMode.Manual:
                    ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });
                    ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters[0].value = activeStates[i].Value[0];
                    break;
                // Auto: If a group is present, use a value different than the one used in the syncing loop.
                case PageItem.SyncMode.Auto:
                    ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });
                    if (items[i].EnableGroup.Length > 0)
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters[0].value = activeStates[i].Value[1];
                    else
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters[0].value = activeStates[i].Value[0];
                    break;
            }
            
            // Add group settings.
            for (int j = 0; j < items[i].EnableGroup.Length; j++)
            {
                // If the group item refers to an actual toggle.
                if (items[i].EnableGroup[j].Item != null)
                    switch (items[i].EnableGroup[j].Reaction)
                    {
                        // Add this toggle's value to the list of disabled states for the group item.
                        case GroupItem.GroupType.Disable:
                            switch (items[i].Sync) 
                            {
                                case PageItem.SyncMode.Off:
                                    if (items[i].EnableGroup.Length > 0)
                                        if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Contains(activeStates[i].Value[0]))
                                            activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Add(activeStates[i].Value[0]);                
                                    break;
                                case PageItem.SyncMode.Manual:
                                    if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Contains(activeStates[i].Value[0]))
                                        activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Add(activeStates[i].Value[0]);
                                    break;
                                case PageItem.SyncMode.Auto:
                                    if (items[i].EnableGroup.Length > 0)
                                    {
                                        if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Contains(activeStates[i].Value[1]))
                                            activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Add(activeStates[i].Value[1]);
                                    }                                        
                                    else
                                        if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Contains(activeStates[i].Value[0]))
                                            activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Key.Add(activeStates[i].Value[0]);
                                    break;
                            }
                            break;
                        // Add this toggle's value to the list of enabled states for the group item.
                        case GroupItem.GroupType.Enable:
                            switch (items[i].Sync)
                            {
                                case PageItem.SyncMode.Off:
                                    if (items[i].EnableGroup.Length > 0)
                                        if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Contains(activeStates[i].Value[0]))
                                            activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Add(activeStates[i].Value[0]);
                                    break;
                                case PageItem.SyncMode.Manual:
                                    if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Contains(activeStates[i].Value[0]))
                                        activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Add(activeStates[i].Value[0]);
                                    break;
                                case PageItem.SyncMode.Auto:
                                    if (items[i].EnableGroup.Length > 0)
                                    {
                                        if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Contains(activeStates[i].Value[1]))
                                            activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Add(activeStates[i].Value[1]);
                                    }
                                    else
                                        if (!activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Contains(activeStates[i].Value[0]))
                                            activeStates[items.IndexOf(items[i].EnableGroup[j].Item)].Value.Add(activeStates[i].Value[0]);
                                    break;
                            }
                            break;
                    }
            }

            // Clone the template state.
            states.Add(templateToggle.DeepClone());

            // Remove the parameters.
            while (((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Count > 0)
            {
                ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.RemoveAt(0);
            }

            // Clone an exit transition.
            states[states.Count - 1].state.transitions = new AnimatorStateTransition[] { (AnimatorStateTransition)toggleTransition.DeepClone() };

            // Configure the AnyState transition template.
            templateTransition.destinationState = states[states.Count - 1].state;
            templateTransition.conditions = new AnimatorCondition[0];
            templateTransition.AddCondition(AnimatorConditionMode.Equals, i + 1, "Inventory");
            templateTransition.AddCondition(AnimatorConditionMode.IfNot, 0, "Inventory " + (i + 1));
            templateTransition.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");

            // Clone the transition and move on to the Off state.
            anyTransitions.Add((AnimatorStateTransition)templateTransition.DeepClone(states[states.Count - 1]));

            // Create an Off state.
            templateToggle.state.name = ("Toggling " + (i + 1) + ": Off");
            templateToggle.position = pos + new Vector3(100, 0);

            // Adjust parameter settings.
            switch (items[i].Sync)
            {
                // Off: If a group is present, use that value instead of the boolean.
                case PageItem.SyncMode.Off:
                    if (items[i].DisableGroup.Length > 0)
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = activeStates[i].Key[0] });
                    else
                    {
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory " + (i + 1), value = 0 });
                    }
                    break;
                // Manual: Use the first values in active states.
                case PageItem.SyncMode.Manual:
                    ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });
                    ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters[0].value = activeStates[i].Key[0];
                    break;
                // Auto: If a group is present, use a value different than the one used in the syncing loop.
                case PageItem.SyncMode.Auto:
                    ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Inventory", value = 0 });
                    if (items[i].DisableGroup.Length > 0)
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters[0].value = activeStates[i].Key[1];
                    else
                        ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters[0].value = activeStates[i].Key[0];
                    break;
            }

            // Add group settings.
            for (int j = 0; j < items[i].DisableGroup.Length; j++)
            {
                // If the group item refers to an actual toggle.
                if (items[i].DisableGroup[j].Item != null)
                    switch (items[i].DisableGroup[j].Reaction)
                    {
                        // Add this toggle's value to the list of disabled states for the group item.
                        case GroupItem.GroupType.Disable:
                            switch (items[i].Sync)
                            {
                                case PageItem.SyncMode.Off:
                                    if (items[i].DisableGroup.Length > 0)
                                        if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Contains(activeStates[i].Key[0]))
                                            activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Add(activeStates[i].Key[0]);
                                    break;
                                case PageItem.SyncMode.Manual:
                                    if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Contains(activeStates[i].Key[0]))
                                        activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Add(activeStates[i].Key[0]);
                                    break;
                                case PageItem.SyncMode.Auto:
                                    if (items[i].DisableGroup.Length > 0)
                                    {
                                        if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Contains(activeStates[i].Key[1]))
                                            activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Add(activeStates[i].Key[1]);
                                    }
                                    else
                                        if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Contains(activeStates[i].Key[0]))
                                        activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Key.Add(activeStates[i].Key[0]);
                                    break;
                            }
                            break;
                        // Add this toggle's value to the list of enabled states for the group item.
                        case GroupItem.GroupType.Enable:
                            switch (items[i].Sync)
                            {
                                case PageItem.SyncMode.Off:
                                    if (items[i].DisableGroup.Length > 0)
                                        if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Contains(activeStates[i].Key[0]))
                                            activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Add(activeStates[i].Key[0]);
                                    break;
                                case PageItem.SyncMode.Manual:
                                    if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Contains(activeStates[i].Key[0]))
                                        activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Add(activeStates[i].Key[0]);
                                    break;
                                case PageItem.SyncMode.Auto:
                                    if (items[i].DisableGroup.Length > 0)
                                    {
                                        if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Contains(activeStates[i].Key[1]))
                                            activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Add(activeStates[i].Key[1]);
                                    }
                                    else
                                        if (!activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Contains(activeStates[i].Key[0]))
                                        activeStates[items.IndexOf(items[i].DisableGroup[j].Item)].Value.Add(activeStates[i].Key[0]);
                                    break;
                            }
                            break;
                    }
            }

            // Clone the template state.
            states.Add(templateToggle.DeepClone());

            // Remove the parameters.
            while (((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.Count > 0)
            {
                ((VRCAvatarParameterDriver)templateToggle.state.behaviours[0]).parameters.RemoveAt(0);
            }

            // Clone an exit transition.
            states[states.Count - 1].state.transitions = new AnimatorStateTransition[] { (AnimatorStateTransition)toggleTransition.DeepClone() };

            // Configure the AnyState transition template.
            templateTransition.destinationState = states[states.Count - 1].state;
            templateTransition.conditions = new AnimatorCondition[0];
            templateTransition.AddCondition(AnimatorConditionMode.Equals, i + 1, "Inventory");
            templateTransition.AddCondition(AnimatorConditionMode.If, 0, "Inventory " + (i + 1));
            templateTransition.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");

            // Clone the transition and move on to next item in the inventory.
            anyTransitions.Add((AnimatorStateTransition)templateTransition.DeepClone(states[states.Count - 1]));

            // Move down a row.
            pos += new Vector3(0, 75);
        }

        // Assign the states and transitions to the master layer.
        masterLayer.stateMachine.anyStatePosition = pos;
        masterLayer.stateMachine.states = states.ToArray();
        masterLayer.stateMachine.anyStateTransitions = anyTransitions.ToArray();
        masterLayer.stateMachine.defaultState = states[0].state;

        // Add the entry transitions if at least one object is synced.
        if (syncExists)
        {
            masterLayer.stateMachine.AddEntryTransition(states[1].state);
            masterLayer.stateMachine.AddEntryTransition(states[2].state);
            AnimatorTransition[] entryTransitions = masterLayer.stateMachine.entryTransitions;
            entryTransitions[0].AddCondition(AnimatorConditionMode.If, 0, "Inventory " + states[1].state.name.Substring(8));
            entryTransitions[1].AddCondition(AnimatorConditionMode.IfNot, 0, "Inventory " + states[1].state.name.Substring(8));
            masterLayer.stateMachine.entryTransitions = entryTransitions;
        }

        // Replace the layer in the Animator Controller.
        AnimatorControllerLayer[] layers = source.layers;
        layers[layers.Length - 1] = masterLayer;
        source.layers = layers;
        return;
    }

    // Helper methods for modifying transitions.
    public static void ChangeTransition(AnimatorStateTransition transition, int value, AnimatorState state)
    {
        transition.destinationState = state;
        transition.conditions = new AnimatorCondition[0];
        transition.AddCondition(AnimatorConditionMode.Equals, value, "Inventory");
    }

    public static void ChangeTransition(AnimatorStateTransition transition, int item, bool value, AnimatorState state)
    {
        transition.destinationState = state;
        transition.conditions = new AnimatorCondition[0];
        transition.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, "Inventory " + item);
    }

    public static void ChangeTransition(AnimatorStateTransition transition, ChildAnimatorState childState, int name, bool value)
    {
        transition.destinationState = childState.state;
        transition.conditions = new AnimatorCondition[0];
        switch (value)
        {
            case true:
                transition.AddCondition(AnimatorConditionMode.If, 0, "Inventory " + name);
                break;
            case false:
                transition.AddCondition(AnimatorConditionMode.IfNot, 0, "Inventory " + name);
                break;
        }      
    }

    // Helper methods for modifying states.
    public static void ChangeState(AnimatorState state, string name, int value)
    {
        state.name = name;
        ((VRCAvatarParameterDriver)state.behaviours[0]).parameters[0].value = value;
        return;
    }

    public static void ChangeState(AnimatorState state, string name)
    {
        state.name = name;
        return;
    }

    public static void ChangeState(ChildAnimatorState childState, string name)
    {
        ChangeState(childState.state, name);
        return;
    }

    public static void ChangeState(AnimatorState state, Motion motion)
    {
        state.motion = motion;
        return;
    }

    // Returns a list of layers and parameters that would be removed with the current settings.
    public void PreviewRemoval(out List<AnimatorControllerLayer> layers, out List<AnimatorControllerParameter> parameters)
    {
        layers = new List<AnimatorControllerLayer>();
        parameters = new List<AnimatorControllerParameter>();

        if (controller == null)
            return;

        // Store Inventory Parameters.
        if (removeParameters)
        {
            Regex nameFilter = new Regex(@"^Inventory( [1-9]|$)([0-9]|$)([0-9]|$)$");

            for (int i = 0; i < controller.parameters.Length; i++)
            {
                if (nameFilter.IsMatch(controller.parameters[i].name))
                {
                    parameters.Add(controller.parameters[i]);
                }
            }
        }

        // Store Inventory Layers.
        for (int i = 0; i < controller.layers.Length; i++)
        {
            if (!(controller.layers[i].stateMachine.behaviours.Length < 1 || controller.layers[i].stateMachine.behaviours[0].GetType() != typeof(InventoryMachine)))
            {
                layers.Add(controller.layers[i]);
            }
        }

        return;
    }

    // Removes Inventory layers and parameters.
    public void RemoveInventory()
    {
        // try catch block because Exceptions exist.
        try
        {
            // Initial Save.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Check that the selected avatar is valid.
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: No Avatar selected.", "Close");
                return;
            }

            // An avatar is humanoid if the descriptor has the Gesture and Additive layers available.
            bool humanoid = avatar.baseAnimationLayers.Length == 5;

            EditorUtility.DisplayProgressBar("Inventory Inventor", "Removing...", 0);

            // Replace the Animator Controller in the descriptor if this Controller was there to begin with.
            bool replaceAnimator = humanoid ? (avatar.baseAnimationLayers[4].animatorController != null && controller == (AnimatorController)avatar.baseAnimationLayers[4].animatorController) : (avatar.baseAnimationLayers[2].animatorController != null && controller == (AnimatorController)avatar.baseAnimationLayers[2].animatorController);

            // Initialize backup objects.
            backupManager = new Backup();
            generated = new AssetList();

            // Backup the Animator before modifying it.
            backupManager.AddToBackup(new Asset(AssetDatabase.GetAssetPath(controller)));

            // Create a fresh and clean Animator object.
            AnimatorController animator = new AnimatorController
            {
                name = controller.name,
                parameters = controller.parameters,
                hideFlags = controller.hideFlags
            };
            AssetDatabase.CreateAsset(animator, relativePath + Path.DirectorySeparatorChar + "temp.controller");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            generated.Add(new Asset(AssetDatabase.GetAssetPath(animator)));

            // Remove Inventory parameters if wanted.
            if (removeParameters)
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                Regex nameFilter = new Regex(@"^Inventory( [1-9]|$)([0-9]|$)([0-9]|$)$");

                for (int i = 0; i < parameters.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("Inventory Inventor", "Removing...", float.Parse(i.ToString()) / parameters.Length * 0.1f);
                    if (nameFilter.IsMatch(parameters[i].name))
                    {
                        animator.RemoveParameter(parameters[i]);
                    }
                }
            }        

            // Clone the provided Animator Controller into the new object, without any Inventory layers.
            for (int i = 0; i < controller.layers.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Inventory Inventor", "Removing...", 0.1f + float.Parse(i.ToString()) / controller.layers.Length * 0.85f);

                // A layer is an Inventory layer if the State Machine has a InventoryMachine behaviour attached.
                if (controller.layers[i].stateMachine.behaviours.Length < 1 || controller.layers[i].stateMachine.behaviours[0].GetType() != typeof(InventoryMachine))
                {
                    animator.AddLayer(controller.layers[i].name);
                    AnimatorControllerLayer[] layers = animator.layers;
                    AnimatorControllerLayer layer = layers[layers.Length - 1];
                    layer = controller.layers[i].DeepClone();
                    layers[layers.Length - 1] = layer;
                    animator.layers = layers;
                }
            }
            animator.SaveController();

            // Replace the old Animator Controller.
            string path = AssetDatabase.GetAssetPath(controller);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(animator), path);
            AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(animator), path.Substring(path.LastIndexOf(Path.DirectorySeparatorChar) + 1));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Replace the Animator Controller in the descriptor if it was there.
            if (replaceAnimator)
            {
                if (humanoid)
                    avatar.baseAnimationLayers[4].animatorController = animator;
                else
                    avatar.baseAnimationLayers[2].animatorController = animator;
            }
            controller = animator;

            EditorUtility.DisplayProgressBar("Inventory Inventor", "Removing...", 1f);

            // Final Save.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("Inventory Inventor", "Success!", "Close");
            return;
        }
        catch (Exception err)
        {
            EditorUtility.DisplayDialog("Inventory Inventor", "ERROR: An exception has occured!\nCheck the console for more details.", "Close");
            Debug.LogError(err);
            RevertChanges();
            return;
        }
    }

    // Reverts any changes made during the process in case of an error or exception.
    private void RevertChanges()
    {
        // Save Assets.
        AssetDatabase.SaveAssets();

        // Restore original data to pre-existing files.
        if (backupManager != null && !backupManager.RestoreAssets())
            Debug.LogError("[Inventory Inventor] Failed to revert all changes.");

        // Delete any generated assets that didn't overwrite files.
        for (int i = 0; generated != null && i < generated.ToArray().Length; i++)
            if (File.Exists(generated[i].path) && !AssetDatabase.DeleteAsset(generated[i].path))
                Debug.LogError("[Inventory Inventor] Failed to revert all changes.");

        // Save assets so folders will be seen as empty.
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Delete created folders if now empty.
        if (AssetDatabase.IsValidFolder(outputPath + Path.DirectorySeparatorChar + "Animators") && AssetDatabase.FindAssets("", new string[] { outputPath + Path.DirectorySeparatorChar + "Animators" }).Length == 0)
            if (!AssetDatabase.DeleteAsset(outputPath + Path.DirectorySeparatorChar + "Animators"))
                Debug.LogError("[Inventory Inventor] Failed to revert all changes.");
        if (AssetDatabase.IsValidFolder(outputPath + Path.DirectorySeparatorChar + "Menus") && AssetDatabase.FindAssets("", new string[] { outputPath + Path.DirectorySeparatorChar + "Menus" }).Length == 0)
            if (!AssetDatabase.DeleteAsset(outputPath + Path.DirectorySeparatorChar + "Menus"))
                Debug.LogError("[Inventory Inventor] Failed to revert all changes.");

        // Final asset save.
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // Updates output and relative paths if the directory of this package changes.
    public void UpdatePaths()
    {
        string old = relativePath;

        // Get the relative path.
        string[] guids = AssetDatabase.FindAssets(GetType().ToString());
        foreach (string guid in guids)
        {
            string tempPath = AssetDatabase.GUIDToAssetPath(guid);
            if (tempPath.LastIndexOf(typeof(InventoryInventor).ToString()) == tempPath.Length - typeof(InventoryInventor).ToString().Length - 3)
            {
                relativePath = tempPath.Substring(0, tempPath.LastIndexOf("Editor") - 1);
                break;
            }
        }

        if (relativePath == old)
        {
            // The path was blank.
            if (outputPath == "")
                outputPath = "Assets";
            return;
        }            
        else if (outputPath == null || !AssetDatabase.IsValidFolder(outputPath))
        {
            outputPath = relativePath + Path.DirectorySeparatorChar + "Output";
        }
    }

    // Blank MonoBehaviour for running network coroutines.
    private class NetworkManager : MonoBehaviour { }

    // Returns the contents of the VERSION file if present.
    public static string GetVersion()
    {
        // Get the relative path.
        string[] guids = AssetDatabase.FindAssets(typeof(InventoryInventor).ToString());
        string relativePath = "";
        foreach (string guid in guids)
        {
            string tempPath = AssetDatabase.GUIDToAssetPath(guid);
            if (tempPath.LastIndexOf(typeof(InventoryInventor).ToString()) == tempPath.Length - typeof(InventoryInventor).ToString().Length - 3)
            {
                relativePath = tempPath.Substring(0, tempPath.LastIndexOf("Editor") - 1);
                break;
            }
        }
        if (relativePath == "")
            return "";

        //Read VERSION file
        string installedVersion = (AssetDatabase.FindAssets("VERSION", new string[] { relativePath }).Length > 0) ? File.ReadAllText(AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("VERSION", new string[] { relativePath })[0])) : "";

        return installedVersion;
    }

    // Compares the VERSION file present to the one on GitHub to see if a newer version is available.
    public static void CheckForUpdates()
    {
        // Read VERSION file.
        string installedVersion = GetVersion();

        // Create hidden object to run the coroutine.
        GameObject netMan = new GameObject { hideFlags = HideFlags.HideInHierarchy };

        // Run a coroutine to retrieve the GitHub data.
        netMan.AddComponent<NetworkManager>().StartCoroutine(GetText("https://raw.githubusercontent.com/Joshuarox100/VRC-Inventory-Inventor/master/Editor/VERSION", latestVersion => {
            // Network Error.
            if (latestVersion == "")
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "Failed to fetch the latest version.\n(Check console for details.)", "Close");
            }
            // VERSION file missing.
            else if (installedVersion == "")
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "Failed to identify installed version.\n(VERSION file was not found.)", "Close");
            }
            // Project has been archived.
            else if (latestVersion == "RIP")
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "Project has been put on hold indefinitely.", "Close");
            }
            // An update is available.
            else if (installedVersion != latestVersion)
            {
                if (EditorUtility.DisplayDialog("Inventory Inventor", "A new update is available! (" + latestVersion + ")\nOpen the Releases page?", "Yes", "No"))
                {
                    Application.OpenURL("https://github.com/Joshuarox100/VRC-Inventory-Inventor");
                }
            }
            // Using latest version.
            else
            {
                EditorUtility.DisplayDialog("Inventory Inventor", "You are using the latest version.", "Close");
            }
            DestroyImmediate(netMan);
        }));
    }

    // Retrieves text from a provided URL.
    private static IEnumerator GetText(string url, Action<string> result)
    {
        UnityWebRequest www = UnityWebRequest.Get(url);
        yield return www.SendWebRequest();

        if (www.isNetworkError || www.isHttpError)
        {
            Debug.LogError(www.error);
            result?.Invoke("");
        }
        else
        {
            result?.Invoke(www.downloadHandler.text);
        }
    }
}