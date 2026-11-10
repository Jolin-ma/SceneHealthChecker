using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// Editor-only tool window that scans the open scene(s) for common mobile/XR performance problems.
// Checks: missing scripts, meshes over a triangle budget, oversized textures,
// realtime shadow-casting point/spot lights.
public class SceneHealthChecker : EditorWindow
{
    // One problem found by a scan. Every check produces these, so the list UI
    // doesn't need to know which check found what.
    private class Issue
    {
        public GameObject Target;
        public string Category;
        public string Details;
        public Action Fix;      // null = no automatic fix exists for this issue
    }

    private readonly List<Issue> _issues = new List<Issue>();

    // Budgets, editable in the window. Defaults are a starting point for mobile / standalone XR.
    private int _triangleBudget = 10000;
    private int _maxTextureSize = 1024;

    private ListView _resultsList;
    private Label _summaryLabel;
    private Button _fixSelectedButton;
    private Button _fixAllButton;

    [MenuItem("Tools/Scene Health Checker")]
    public static void ShowWindow()
    {
        var window = GetWindow<SceneHealthChecker>();
        window.titleContent = new GUIContent("Scene Health");
        window.minSize = new Vector2(320, 240);
    }

    public void CreateGUI()
    {
        var triField = new IntegerField("Triangle budget per object") { value = _triangleBudget };
        triField.RegisterValueChangedCallback(evt => _triangleBudget = Mathf.Max(1, evt.newValue));
        rootVisualElement.Add(triField);

        var texField = new IntegerField("Max texture size (px)") { value = _maxTextureSize };
        texField.RegisterValueChangedCallback(evt => _maxTextureSize = Mathf.Max(1, evt.newValue));
        rootVisualElement.Add(texField);

        var scanButton = new Button(Scan) { text = "Scan Scene" };
        rootVisualElement.Add(scanButton);

        _summaryLabel = new Label("Press Scan Scene to check the open scene(s).");
        _summaryLabel.style.marginTop = 4;
        _summaryLabel.style.marginBottom = 4;
        rootVisualElement.Add(_summaryLabel);

        _resultsList = new ListView
        {
            itemsSource = _issues,
            makeItem = () => new Label(),
            bindItem = (element, index) =>
            {
                Issue issue = _issues[index];
                string name = issue.Target == null ? "(deleted)" : GetPath(issue.Target.transform);
                string fixTag = issue.Fix != null ? " [fixable]" : "";
                ((Label)element).text = $"[{issue.Category}] {name}  {issue.Details}{fixTag}";
            },
            selectionType = SelectionType.Single,
            fixedItemHeight = 18
        };
        _resultsList.style.flexGrow = 1;
        _resultsList.selectionChanged += OnRowSelected;
        rootVisualElement.Add(_resultsList);

        // Fix buttons sit under the list. They start disabled until there's something to fix.
        var fixRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
        _fixSelectedButton = new Button(FixSelected) { text = "Fix Selected" };
        _fixAllButton = new Button(FixAll) { text = "Fix All Fixable" };
        _fixSelectedButton.style.flexGrow = 1;
        _fixAllButton.style.flexGrow = 1;
        fixRow.Add(_fixSelectedButton);
        fixRow.Add(_fixAllButton);
        rootVisualElement.Add(fixRow);
        UpdateFixButtons();
    }

    // ---------- Scan ----------

    private void Scan()
    {
        _issues.Clear();
        long sceneTriangles = 0;
        int realtimeLights = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    CheckMissingScripts(go);
                    sceneTriangles += CheckTriangleBudget(go);
                    CheckTextures(go);
                    realtimeLights += CheckLight(go);
                }
            }
        }

        _summaryLabel.text = (_issues.Count == 0 ? "No issues found." : $"{_issues.Count} issue(s).")
            + $" Triangles: {sceneTriangles:N0}, realtime lights: {realtimeLights}";

        _resultsList.Rebuild();
        UpdateFixButtons();
    }

    private void CheckMissingScripts(GameObject go)
    {
        int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
        if (missing > 0)
            _issues.Add(new Issue
            {
                Target = go,
                Category = "Missing Script",
                Details = $"({missing})",
                Fix = () => RemoveMissingScripts(go)
            });
    }

    // Returns this object's triangle count (so Scan can total the scene) and
    // records an issue if it's over budget.
    private long CheckTriangleBudget(GameObject go)
    {
        Mesh mesh = null;
        if (go.TryGetComponent(out MeshFilter filter)) mesh = filter.sharedMesh;
        else if (go.TryGetComponent(out SkinnedMeshRenderer skinned)) mesh = skinned.sharedMesh;
        if (mesh == null) return 0;

        long triangles = GetTriangleCount(mesh);
        if (triangles > _triangleBudget)
            _issues.Add(new Issue
            {
                Target = go,
                Category = "High Poly",
                Details = $"({triangles:N0} tris, budget {_triangleBudget:N0})"
            });
        return triangles;
    }

    // Reads index counts from the submesh descriptors instead of mesh.triangles,
    // which would allocate a copy of the whole index buffer for every mesh.
    private static long GetTriangleCount(Mesh mesh)
    {
        long count = 0;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (mesh.GetTopology(sub) == MeshTopology.Triangles)
                count += mesh.GetIndexCount(sub) / 3;
        }
        return count;
    }

    // Flags any texture on this object's materials whose largest side exceeds the limit.
    private void CheckTextures(GameObject go)
    {
        if (!go.TryGetComponent(out Renderer renderer)) return;

        // The same texture is often in several slots (or several materials) on one object;
        // report it once.
        var seen = new HashSet<Texture>();
        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null) continue;

            // Ask the material which texture slots its shader has (_BaseMap, _BumpMap, ...)
            // instead of hardcoding names, so this works with any shader.
            foreach (string slot in material.GetTexturePropertyNames())
            {
                Texture tex = material.GetTexture(slot);
                if (tex == null || !seen.Add(tex)) continue;

                // tex.width/height are the IMPORTED size (after the import Max Size is applied),
                // so once a fix lowers Max Size, the next scan sees the smaller number.
                int size = Mathf.Max(tex.width, tex.height);
                if (size > _maxTextureSize)
                {
                    // Only a standalone texture file (png, jpg...) has a TextureImporter we can edit.
                    // A texture embedded in a .glb belongs to the glTFast importer, which has no
                    // max-size setting, so we can't fix it from here.
                    string assetPath = AssetDatabase.GetAssetPath(tex);
                    bool fixable = AssetImporter.GetAtPath(assetPath) is TextureImporter;
                    int limit = _maxTextureSize; // copy: the lambda below must not see later edits
                    _issues.Add(new Issue
                    {
                        Target = go,
                        Category = "Big Texture",
                        Details = $"({tex.name} {tex.width}x{tex.height}, max {_maxTextureSize})"
                            + (fixable ? "" : " embedded in model, re-export smaller"),
                        Fix = fixable ? () => ClampTextureImportSize(assetPath, limit) : (Action)null
                    });
                }
            }
        }
    }

    // Returns 1 if this object has an enabled realtime light (so Scan can count them).
    // Realtime point/spot lights with shadows re-render the scene once per light (more for
    // point lights, which need a cubemap), which is very expensive on mobile GPUs.
    private int CheckLight(GameObject go)
    {
        if (!go.TryGetComponent(out Light light) || !light.enabled) return 0;
        if (light.lightmapBakeType != LightmapBakeType.Realtime) return 0;

        if (light.type != LightType.Directional && light.shadows != LightShadows.None)
        {
            _issues.Add(new Issue
            {
                Target = go,
                Category = "Realtime Shadows",
                Details = $"({light.type} light, {light.shadows})",
                Fix = () => DisableShadows(light)
            });
        }
        return 1;
    }

    // ---------- Fixes ----------

    private static void RemoveMissingScripts(GameObject go)
    {
        // Register BEFORE changing, so Ctrl+Z can restore the object's previous state.
        // Must be the FullObjectHierarchy variant: RegisterCompleteObjectUndo snapshots the
        // GameObject's own fields but does not bring back a removed missing-script component.
        Undo.RegisterFullObjectHierarchyUndo(go, "Remove Missing Scripts");
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
        EditorSceneManager.MarkSceneDirty(go.scene); // so Unity asks to save the scene
    }

    private static void DisableShadows(Light light)
    {
        Undo.RecordObject(light, "Disable Light Shadows");
        light.shadows = LightShadows.None;
        EditorSceneManager.MarkSceneDirty(light.gameObject.scene);
    }

    // Lowers the texture's Max Size import setting. The source file is untouched; Unity just
    // re-imports it smaller. NOTE: importer changes are not part of Ctrl+Z; to undo, set the
    // Max Size back in the texture's Inspector.
    private static void ClampTextureImportSize(string assetPath, int limit)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return;

        // Max Size must be a power of two. Round the limit DOWN so we never end up over it
        // (ClosestPowerOfTwo can round up, e.g. 1000 -> 1024).
        int target = Mathf.ClosestPowerOfTwo(limit);
        if (target > limit) target /= 2;
        target = Mathf.Max(32, target);

        if (importer.maxTextureSize <= target) return; // already small enough: nothing to do
        importer.maxTextureSize = target;
        importer.SaveAndReimport(); // apply it now, so the next scan sees the new size
    }

    private void FixSelected()
    {
        int index = _resultsList.selectedIndex;
        if (index < 0 || index >= _issues.Count || _issues[index].Fix == null) return;

        _issues[index].Fix();
        Scan(); // re-scan so the fixed row disappears
    }

    private void FixAll()
    {
        // Group every fix into ONE undo step, so a single Ctrl+Z reverts the whole batch.
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        foreach (Issue issue in _issues)
            if (issue.Fix != null && issue.Target != null) issue.Fix();
        Undo.CollapseUndoOperations(group);
        Undo.SetCurrentGroupName("Scene Health Fix All");
        Scan();
    }

    private void UpdateFixButtons()
    {
        int index = _resultsList != null ? _resultsList.selectedIndex : -1;
        bool selectedFixable = index >= 0 && index < _issues.Count && _issues[index].Fix != null;
        _fixSelectedButton?.SetEnabled(selectedFixable);
        _fixAllButton?.SetEnabled(_issues.Exists(i => i.Fix != null));
    }

    // ---------- Selection ----------

    private void OnRowSelected(IEnumerable<object> selectedItems)
    {
        foreach (object item in selectedItems)
        {
            if (item is Issue issue && issue.Target != null)
            {
                Selection.activeGameObject = issue.Target;
                EditorGUIUtility.PingObject(issue.Target);
            }
        }
        UpdateFixButtons();
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
