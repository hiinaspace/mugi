using System.IO;
using UnityEditor;
using UnityEngine;

namespace Space.Hiina.Mugi.Editor
{
    /// <summary>
    /// Editor utility for creating MugiGame prefab variants
    /// </summary>
    public static class MugiGameVariantCreator
    {
        private const string MUGI_GAME_PREFAB_PATH =
            "Packages/space.hiina.mugi/Runtime/MugiGame.prefab";
        private const string DEFAULT_VARIANT_NAME = "MugiGame Variant";

        [MenuItem("GameObject/Mugi/Create Mugi Game", false, 0)]
        public static void CreateMugiGameVariant()
        {
            CreateMugiGameVariantInternal();
        }

        [MenuItem("Assets/Create/Mugi/Mugi Game Variant", false, 0)]
        public static void CreateMugiGameVariantFromAssets()
        {
            CreateMugiGameVariantInternal();
        }

        private static void CreateMugiGameVariantInternal()
        {
            // Load the original MugiGame prefab
            GameObject originalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                MUGI_GAME_PREFAB_PATH
            );
            if (originalPrefab == null)
            {
                EditorUtility.DisplayDialog(
                    "Error",
                    $"Could not find MugiGame prefab at {MUGI_GAME_PREFAB_PATH}. "
                        + "Please ensure the MUGI package is properly installed.",
                    "OK"
                );
                return;
            }

            // Get the current selection path for default save location
            string defaultPath = GetSelectedAssetPath();

            // Show save dialog that works with packages
            string savePath = ShowSaveDialog(defaultPath);

            if (string.IsNullOrEmpty(savePath))
            {
                return; // User cancelled
            }

            // Validate the save path
            if (!ValidateSavePath(savePath))
            {
                return;
            }

            try
            {
                // Instantiate the prefab in the scene temporarily
                GameObject instance = PrefabUtility.InstantiatePrefab(originalPrefab) as GameObject;
                if (instance == null)
                {
                    EditorUtility.DisplayDialog(
                        "Error",
                        "Failed to instantiate MugiGame prefab. The prefab may be corrupted.",
                        "OK"
                    );
                    return;
                }

                // Create the prefab variant
                GameObject variantPrefab = PrefabUtility.SaveAsPrefabAsset(instance, savePath);

                // Clean up the temporary instance
                Object.DestroyImmediate(instance);

                if (variantPrefab != null)
                {
                    // Select and ping the newly created prefab
                    Selection.activeObject = variantPrefab;
                    EditorGUIUtility.PingObject(variantPrefab);

                    Debug.Log($"Successfully created MugiGame variant at: {savePath}");
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Error",
                        "Failed to save the prefab variant. Please check the console for details.",
                        "OK"
                    );
                }
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog(
                    "Error",
                    $"An error occurred while creating the prefab variant:\n{e.Message}",
                    "OK"
                );
                Debug.LogError($"MugiGameVariantCreator error: {e}");
            }
        }

        /// <summary>
        /// Gets the path of the currently selected asset in the Project browser
        /// </summary>
        private static string GetSelectedAssetPath()
        {
            string path = "Assets";

            if (Selection.activeObject != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    if (Directory.Exists(assetPath))
                    {
                        path = assetPath;
                    }
                    else
                    {
                        path = Path.GetDirectoryName(assetPath);
                    }
                }
            }

            return path;
        }

        /// <summary>
        /// Shows a save dialog that works with both Assets and Packages folders
        /// </summary>
        private static string ShowSaveDialog(string defaultPath)
        {
            // Convert project-relative path to absolute path for the dialog
            string projectPath = Path.GetDirectoryName(Application.dataPath);
            string absoluteDefaultPath = Path.Combine(projectPath, defaultPath);

            string absoluteSavePath = EditorUtility.SaveFilePanel(
                "Create Mugi Game Variant",
                absoluteDefaultPath,
                DEFAULT_VARIANT_NAME + ".prefab",
                "prefab"
            );

            if (string.IsNullOrEmpty(absoluteSavePath))
            {
                return null; // User cancelled
            }

            // Convert back to project-relative path
            string relativePath = Path.GetRelativePath(projectPath, absoluteSavePath);

            // Normalize path separators for Unity
            return relativePath.Replace('\\', '/');
        }

        /// <summary>
        /// Validates the save path and shows appropriate error messages
        /// </summary>
        private static bool ValidateSavePath(string savePath)
        {
            if (string.IsNullOrEmpty(savePath))
            {
                return false;
            }

            // Check if path is within the project
            if (!IsPathWithinProject(savePath))
            {
                EditorUtility.DisplayDialog(
                    "Error",
                    "The file must be saved within the Unity project.",
                    "OK"
                );
                return false;
            }

            // Check if target directory is writable
            string directory = Path.GetDirectoryName(savePath);
            string projectPath = Path.GetDirectoryName(Application.dataPath);
            string absoluteDirectory = Path.Combine(projectPath, directory);

            if (!IsDirectoryWritable(absoluteDirectory))
            {
                EditorUtility.DisplayDialog(
                    "Error",
                    $"Cannot write to directory: {directory}\n"
                        + "Please choose a different location or check your permissions.",
                    "OK"
                );
                return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if a path is within the Unity project
        /// </summary>
        private static bool IsPathWithinProject(string relativePath)
        {
            // Valid Unity project paths start with Assets/ or Packages/
            return relativePath.StartsWith("Assets/") || relativePath.StartsWith("Packages/");
        }

        /// <summary>
        /// Checks if a directory is writable
        /// </summary>
        private static bool IsDirectoryWritable(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    return false;
                }

                // Try to create a temporary file to test write permissions
                string tempFile = Path.Combine(directoryPath, Path.GetRandomFileName());
                using (FileStream fs = File.Create(tempFile))
                {
                    // File created successfully
                }
                File.Delete(tempFile);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
