using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

// Editor-only tool window that scans the open scene(s) for common mobile/XR performance problems.
// Checks: meshes over a triangle budget and oversized textures.
public class SceneHealthChecker : EditorWindow
{
    // One problem found by a scan. Every check produces these, so the list UI
    // doesn't need to know which check found what.
    private class Issue
    {
        public GameObject Target;
        public string Category;
        public string Details;
    }

    private readonly List<Issue> _issues = new List<Issue>();

    // Budgets, editable in the window. Defaults are a starting point for mobile / standalone XR.
    private int _triangleBudget = 10000;
    private int _maxTextureSize = 1024;

    private ListView _resultsList;
    private Label _summaryLabel;

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
                ((Label)element).text = $"[{issue.Category}] {name}  {issue.Details}";
            },
            selectionType = SelectionType.Single,
            fixedItemHeight = 18
        };
        _resultsList.style.flexGrow = 1;
        _resultsList.selectionChanged += OnRowSelected;
        rootVisualElement.Add(_resultsList);
    }

    // ---------- Scan ----------

    private void Scan()
    {
        _issues.Clear();
        long sceneTriangles = 0;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    sceneTriangles += CheckTriangleBudget(go);
                    CheckTextures(go);
                }
            }
        }

        _summaryLabel.text = (_issues.Count == 0 ? "No issues found." : $"{_issues.Count} issue(s).")
            + $" Triangles: {sceneTriangles:N0}";

        _resultsList.Rebuild();
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

                // tex.width/height are the IMPORTED size (after the import Max Size is applied).
                int size = Mathf.Max(tex.width, tex.height);
                if (size > _maxTextureSize)
                {
                    _issues.Add(new Issue
                    {
                        Target = go,
                        Category = "Big Texture",
                        Details = $"({tex.name} {tex.width}x{tex.height}, max {_maxTextureSize})"
                    });
                }
            }
        }
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
