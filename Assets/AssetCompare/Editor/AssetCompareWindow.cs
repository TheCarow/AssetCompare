using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

public class AssetCompareWindow : EditorWindow
{
    private UnityEngine.Object sourceAsset;
    private AssetType currentAssetType = AssetType.None;
    
    private Texture2D sourceTexture;
    private Texture2D previewA;
    private Texture2D previewB;
    private float handlePos = 0.5f;
    private bool dragging = false;
    private float zoom = 1.0f;
    private const float MinZoom = 0.1f;
    private const float MaxZoom = 5.0f;
    
    private AudioClip sourceAudioClip;
    private AudioClip previewAudioA;
    private AudioClip previewAudioB;
    private bool isPlaying = false;
    private bool isPlayingA = true;
    private AudioSource audioSourceA;
    private AudioSource audioSourceB;
    private Texture2D waveformA;
    private Texture2D waveformB;
    private float playbackPosition = 0f;

    private const string AssetCompareTitle = "AssetCompare";
    private const string TempFolderName = "Temp";
    private string assetCompareEditorPath;
    private string assetCompareTempPath;
    private string tempFolder;
    private string tempAPath;
    private string tempBPath;
    
    private Editor importerEditorA;
    private Editor importerEditorB;
    
    private ObjectField sourceField;
    private IMGUIContainer previewContainer;
    private VisualElement audioControlsContainer;
    private Button playStopButton;
    private Button swapAudioButton;
    private Label currentAudioLabel;

    private HelpBox statBoxA;
    private HelpBox statBoxB;

    private enum AssetType
    {
        None,
        Texture,
        Audio,
        Unsupported
    }

    [Flags]
    private enum PreviewUpdateMode
    {
        None = 0,
        A = 1 << 0,
        B = 1 << 1
    }

    private PreviewUpdateMode previewUpdateMode = PreviewUpdateMode.A | PreviewUpdateMode.B;

    [MenuItem("Window/Analysis/Asset Compare")]
    public static void ShowWindow()
    {
        var w = GetWindow<AssetCompareWindow>(AssetCompareTitle);
        w.minSize = new Vector2(900, 360);
    }

    private void OnEnable()
    {
        // Delay initialization to avoid crashes during domain reload
        EditorApplication.delayCall += SafeInitialize;
    }

    private void SafeInitialize()
    {
        if (this == null)
        {
            return;
        }
        
        string[] guids = AssetDatabase.FindAssets("t:Script AssetCompareWindow");
        if (guids.Length == 0)
        {
            Debug.LogError("Could not find AssetCompareWindow script in project.");
            return;
        }
        
        string scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
        
        assetCompareEditorPath = Path.GetDirectoryName(scriptPath);

        if (string.IsNullOrEmpty(assetCompareEditorPath))
        {
            Debug.LogError("assetCompareEditorPath not found.");
            return;
        }
        
        assetCompareTempPath = Path.Combine(assetCompareEditorPath, TempFolderName);

        CleanupPreviewFiles();

        if (!AssetDatabase.IsValidFolder(assetCompareTempPath))
        {
            Directory.CreateDirectory(assetCompareTempPath);
            AssetDatabase.Refresh();
        }
        
        if (audioSourceA == null && audioSourceB == null)
        {
            var go = new GameObject("AssetCompareAudioSources");
            go.hideFlags = HideFlags.HideAndDontSave;
            audioSourceA = go.AddComponent<AudioSource>();
            audioSourceB = go.AddComponent<AudioSource>();
        }

        BuildUI();
    }

    private void OnDisable()
    {
        UnregisterTexturePreviewEvents();
        StopAudio();

        if (importerEditorA != null)
        {
            DestroyImmediate(importerEditorA);
            importerEditorA = null;
        } 

        if (importerEditorB != null)
        {
            DestroyImmediate(importerEditorB);
            importerEditorB = null;
        }

        if (audioSourceA != null && audioSourceA.gameObject != null)
        {
            DestroyImmediate(audioSourceA.gameObject);
        }

        audioSourceA = null;
        audioSourceB = null;

        CleanupPreviewFiles(); 
    }

    private void Update()
    {
        if (currentAssetType == AssetType.Audio && isPlaying && audioSourceA != null && audioSourceB != null)
        {
            if (!audioSourceA.isPlaying && !audioSourceB.isPlaying)
            {
                isPlaying = false;
                UpdatePlayStopButton();
            }
            else
            {
                AudioSource activeSource = isPlayingA ? audioSourceA : audioSourceB;
                if (activeSource.clip != null && activeSource.clip.length > 0)
                {
                    playbackPosition = activeSource.time / activeSource.clip.length;
                }
                previewContainer?.MarkDirtyRepaint();
            }
        }
    }

    private void BuildUI()
    {
        rootVisualElement.Clear();

        // Main split
        var mainRow = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                marginTop = 8,
                flexGrow = 1
            }
        };

        // Left column for all settings
        var leftColumn = new VisualElement
        {
            style =
            {
                width = 620,
                flexShrink = 0,
                flexDirection = FlexDirection.Column
            }
        };

        // Source Asset Selector
        sourceField = new ObjectField()
        {
            objectType = typeof(UnityEngine.Object),
            label = "Source Asset",
        };
        sourceField.RegisterValueChangedCallback(OnSourceFieldChanged);
        leftColumn.Add(sourceField);

        // Audio controls
        audioControlsContainer = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                marginTop = 8,
                marginBottom = 8,
                marginLeft = 8,
                marginRight = 8,
                display = DisplayStyle.None
            }
        };

        playStopButton = new Button(TogglePlayStop)
        {
            text = "Play",
            style = { width = 80, marginRight = 8 }
        };

        swapAudioButton = new Button(SwapAudioChannel)
        {
            text = "Swap to B",
            style = { width = 100, marginRight = 8 }
        };

        currentAudioLabel = new Label("Playing: A")
        {
            style = { unityTextAlign = TextAnchor.MiddleLeft }
        };

        audioControlsContainer.Add(playStopButton);
        audioControlsContainer.Add(swapAudioButton);
        audioControlsContainer.Add(currentAudioLabel);
        leftColumn.Add(audioControlsContainer);

        // Headers
        var headersRow = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                marginTop = 6,
                marginBottom = 4
            }
        };

        var importerAHeader = new VisualElement
        {
            style =
            {
                width = 300,
                marginRight = 8,
                flexDirection = FlexDirection.Column,
                paddingLeft = 6,
                paddingRight = 6,
                paddingTop = 6,
                paddingBottom = 6
            }
        };
        importerAHeader.Add(new Label("Settings A"));
        
        statBoxA = new HelpBox("No Data", HelpBoxMessageType.Info);
        statBoxA.style.display = DisplayStyle.None;
        importerAHeader.Add(statBoxA);

        var importerBHeader = new VisualElement
        {
            style =
            {
                width = 300,
                flexDirection = FlexDirection.Column,
                paddingLeft = 6,
                paddingRight = 6,
                paddingTop = 6,
                paddingBottom = 6
            }
        };
        importerBHeader.Add(new Label("Settings B"));
        
        statBoxB = new HelpBox("No Data", HelpBoxMessageType.Info);
        statBoxB.style.display = DisplayStyle.None;
        importerBHeader.Add(statBoxB);

        headersRow.Add(importerAHeader);
        headersRow.Add(importerBHeader);
        leftColumn.Add(headersRow);

        // Importer Scroll
        var importersScroll = new ScrollView(ScrollViewMode.Vertical)
        {
            style =
            {
                flexGrow = 1,
                marginLeft = 0,
                marginRight = 0,
                marginTop = 2
            }
        };

        var importersRow = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Stretch
            }
        };

        var importerAContainer = new IMGUIContainer(DrawImporterA)
        {
            style =
            {
                width = 300,
                marginRight = 8,
                flexShrink = 0
            }
        };

        var importerBContainer = new IMGUIContainer(DrawImporterB)
        {
            style =
            {
                width = 300,
                flexShrink = 0
            }
        };

        importersRow.Add(importerAContainer);
        importersRow.Add(importerBContainer);

        importersScroll.Add(importersRow);
        leftColumn.Add(importersScroll);

        mainRow.Add(leftColumn);

        // Preview Panel
        var previewPanel = new VisualElement { style = { flexGrow = 1 } };
        previewContainer = new IMGUIContainer(DrawPreviewIMGUI)
        {
            style = { flexGrow = 1 }
        };

        previewPanel.Add(previewContainer);
        mainRow.Add(previewPanel);

        rootVisualElement.Add(mainRow);
    }

    private void DrawImporterA()
    {
        if (importerEditorA == null)
        {
            GUILayout.Label("No Asset A Selected", EditorStyles.boldLabel);
            return;
        }

        EditorGUI.BeginChangeCheck();
        importerEditorA.OnInspectorGUI();
        if (EditorGUI.EndChangeCheck())
        {
            var imp = importerEditorA.target as AssetImporter;
            if (imp != null)
            {
                imp.SaveAndReimport();
                RefreshPreviews();
            }
        }
    }

    private void DrawImporterB()
    {
        if (importerEditorB == null)
        {
            GUILayout.Label("No Asset B Selected", EditorStyles.boldLabel);
            return;
        }

        EditorGUI.BeginChangeCheck();
        importerEditorB.OnInspectorGUI();
        if (EditorGUI.EndChangeCheck())
        {
            var imp = importerEditorB.target as AssetImporter;
            if (imp != null)
            {
                imp.SaveAndReimport();
                RefreshPreviews();
            }
        }
    }

    private void OnSourceFieldChanged(ChangeEvent<UnityEngine.Object> changeEvent)
    {
        SetSourceAsset(changeEvent.newValue);
        GeneratePreview();
    }

    private void SetSourceAsset(UnityEngine.Object asset)
    {
        sourceAsset = asset;

        if (asset == null)
        {
            currentAssetType = AssetType.None;
            audioControlsContainer.style.display = DisplayStyle.None;
            UnregisterTexturePreviewEvents();
            
            statBoxA.style.display = DisplayStyle.None;
            statBoxB.style.display = DisplayStyle.None;
            
            return;
        }

        if (asset is Texture2D)
        {
            currentAssetType = AssetType.Texture;
            sourceTexture = asset as Texture2D;
            sourceAudioClip = null;
            audioControlsContainer.style.display = DisplayStyle.None;
            RegisterTexturePreviewEvents();
            
            statBoxA.style.display = DisplayStyle.Flex;
            statBoxB.style.display = DisplayStyle.Flex;
        }
        else if (asset is AudioClip)
        {
            currentAssetType = AssetType.Audio;
            sourceAudioClip = asset as AudioClip;
            sourceTexture = null;
            audioControlsContainer.style.display = DisplayStyle.Flex;
            StopAudio();
            UnregisterTexturePreviewEvents();
        }
        else
        {
            currentAssetType = AssetType.Unsupported;
            sourceTexture = null;
            sourceAudioClip = null;
            audioControlsContainer.style.display = DisplayStyle.None;
            UnregisterTexturePreviewEvents();
            EditorUtility.DisplayDialog("Unsupported Asset",
                "Only Texture2D and AudioClip assets are supported for comparison.", "OK");
        }

        previewUpdateMode = PreviewUpdateMode.A | PreviewUpdateMode.B;
    }

    private void RegisterTexturePreviewEvents()
    {
        if (previewContainer == null)
        {
            return;
        }

        UnregisterTexturePreviewEvents();
        previewContainer.RegisterCallback<PointerDownEvent>(OnPointerDownPreview);
        previewContainer.RegisterCallback<PointerMoveEvent>(OnPointerMovePreview);
        previewContainer.RegisterCallback<PointerUpEvent>(OnPointerUpPreview);
        previewContainer.RegisterCallback<WheelEvent>(OnPreviewScrollWheel);
    }

    private void UnregisterTexturePreviewEvents()
    {
        if (previewContainer == null)
        {
            return;
        }

        previewContainer.UnregisterCallback<PointerDownEvent>(OnPointerDownPreview);
        previewContainer.UnregisterCallback<PointerMoveEvent>(OnPointerMovePreview);
        previewContainer.UnregisterCallback<PointerUpEvent>(OnPointerUpPreview);
        previewContainer.UnregisterCallback<WheelEvent>(OnPreviewScrollWheel);
    }

    private void GeneratePreview()
    {
        if (previewUpdateMode == PreviewUpdateMode.None)
        {
            return;
        }

        if (sourceAsset == null)
        {
            EditorUtility.DisplayDialog("No source", "Please select a source asset first.", "OK");
            return;
        }

        if (currentAssetType == AssetType.Unsupported)
        {
            return;
        }

        var srcPath = AssetDatabase.GetAssetPath(sourceAsset);
        if (string.IsNullOrEmpty(srcPath))
        {
            EditorUtility.DisplayDialog("Invalid asset", "Selected asset is not valid in the project.", "OK");
            return;
        }

        if (!AssetDatabase.IsValidFolder(assetCompareTempPath))
        {
            AssetDatabase.CreateFolder(assetCompareEditorPath, TempFolderName);
        }

        var ext = Path.GetExtension(srcPath);
        tempAPath = assetCompareTempPath + "/A" + ext;
        tempBPath = assetCompareTempPath + "/B" + ext;

        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(tempAPath) != null)
        {
            AssetDatabase.DeleteAsset(tempAPath);
        }

        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(tempBPath) != null)
        {
            AssetDatabase.DeleteAsset(tempBPath);
        }

        AssetDatabase.CopyAsset(srcPath, tempAPath);
        AssetDatabase.CopyAsset(srcPath, tempBPath);
        AssetDatabase.Refresh();

        if (currentAssetType == AssetType.Texture)
        {
            GenerateTexturePreview();
        }
        else if (currentAssetType == AssetType.Audio)
        {
            GenerateAudioPreview();
        }

        previewUpdateMode = PreviewUpdateMode.None;  
    }

    private void GenerateTexturePreview()
    {
        if (importerEditorA != null)
        {
            DestroyImmediate(importerEditorA);
            importerEditorA = null;
        }
        previewA = AssetDatabase.LoadAssetAtPath<Texture2D>(tempAPath);
        importerEditorA = CreateImporterEditor(previewA);
        
        if (importerEditorB != null)
        {
            DestroyImmediate(importerEditorB);
            importerEditorB = null;
        }
        previewB = AssetDatabase.LoadAssetAtPath<Texture2D>(tempBPath);
        importerEditorB = CreateImporterEditor(previewB);

        previewContainer?.MarkDirtyRepaint();
    }

    private void GenerateAudioPreview()
    {
        if (importerEditorA != null)
        {
            DestroyImmediate(importerEditorA);
            importerEditorA = null;
        }
        previewAudioA = AssetDatabase.LoadAssetAtPath<AudioClip>(tempAPath);
        importerEditorA = CreateImporterEditor(previewAudioA);
        
        if (importerEditorB != null)
        {
            DestroyImmediate(importerEditorB);
            importerEditorB = null;
        }
        previewAudioB = AssetDatabase.LoadAssetAtPath<AudioClip>(tempBPath);
        importerEditorB = CreateImporterEditor(previewAudioB);

        if (previewAudioA != null)
        {
            waveformA = GenerateWaveform(previewAudioA, 512, 128);
        }

        if (previewAudioB != null)
        {
            waveformB = GenerateWaveform(previewAudioB, 512, 128);
        }

        previewContainer?.MarkDirtyRepaint();
    }

    private Editor CreateImporterEditor(UnityEngine.Object selectedAsset)
    {
        if (selectedAsset == null)
        {
            return null;
        }

        string path = AssetDatabase.GetAssetPath(selectedAsset);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var importer = AssetImporter.GetAtPath(path);
        if (importer == null)
        {
            return null;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            delayedImporter = importer;
            delayedPath = path;
            EditorApplication.delayCall += OnDelayedEditorCreation;
            return null;
        }

        return Editor.CreateEditor(importer);
    }

    private AssetImporter delayedImporter;
    private string delayedPath;
    private void OnDelayedEditorCreation()
    {
        if (delayedImporter == null)
        {
            return;
        }

        importerEditorA = Editor.CreateEditor(delayedImporter);
        
        delayedImporter = null;
        delayedPath = null;
    }

    private void StopAudio()
    {
        if (audioSourceA != null)
        {
            audioSourceA.Stop();
        }

        if (audioSourceB != null)
        {
            audioSourceB.Stop();
        }
        
        isPlaying = false;
        playbackPosition = 0f;
        
        UpdatePlayStopButton();
    }

    private void PlayAudio()
    {
        if (previewAudioA == null || previewAudioB == null)
        {
            return;
        }

        audioSourceA.clip = previewAudioA;
        audioSourceB.clip = previewAudioB;

        audioSourceA.volume = isPlayingA ? 1f : 0f;
        audioSourceB.volume = isPlayingA ? 0f : 1f;

        audioSourceA.time = audioSourceB.time = 0f;
        audioSourceA.Play();
        audioSourceB.Play();

        isPlaying = true;
        
        UpdatePlayStopButton();
    }

    private void TogglePlayStop()
    {
        if (currentAssetType != AssetType.Audio)
        {
            return;
        }

        if (!isPlaying)
        {
            PlayAudio();
            return;
        }
        
        StopAudio();
    }

    private void SwapAudioChannel()
    {
        isPlayingA = !isPlayingA;

        if (audioSourceA != null && audioSourceB != null)
        {
            audioSourceA.volume = isPlayingA ? 1f : 0f;
            audioSourceB.volume = isPlayingA ? 0f : 1f;
        }

        UpdateSwapButton();
        previewContainer.MarkDirtyRepaint();
    }

    private void UpdatePlayStopButton()
    {
        if (playStopButton != null)
        {
            playStopButton.text = isPlaying ? "Stop" : "Play";
        }
    }

    private void UpdateSwapButton()
    {
        if (swapAudioButton != null)
        {
            swapAudioButton.text = isPlayingA ? "Swap to B" : "Swap to A";
        }

        if (currentAudioLabel != null)
        {
            currentAudioLabel.text = "Playing: " + (isPlayingA ? "A" : "B");
        }
    }

    private void DrawPreviewIMGUI()
    {
        var rect = GUILayoutUtility.GetRect(previewContainer.contentRect.width, previewContainer.contentRect.height);

        if (currentAssetType == AssetType.None)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, 600, 20), "No asset selected.");
            return;
        }

        if (currentAssetType == AssetType.Unsupported)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, 600, 40),
                "Unsupported asset type.\nOnly Texture2D and AudioClip assets are supported.");
            return;
        }

        if (currentAssetType == AssetType.Texture)
        {
            DrawTexturePreview(rect);
        }
        else if (currentAssetType == AssetType.Audio)
        {
            DrawAudioPreview(rect);
        }
    }

    private void DrawTexturePreview(Rect rect)
    {
        if (previewA == null || previewB == null)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, 600, 20), "No texture preview available.");
            return;
        }

        DrawSplitPreview(rect, previewA, previewB, handlePos);
    }

    private void DrawAudioPreview(Rect rect)
    {
        Texture2D currentWaveform = isPlayingA ? waveformA : waveformB;

        if (currentWaveform == null)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, 600, 20), "No audio preview available.");
            return;
        }

        GUI.DrawTexture(rect, currentWaveform, ScaleMode.StretchToFill);

        if (isPlaying && audioSourceA != null && audioSourceB != null)
        {
            AudioSource activeSource = isPlayingA ? audioSourceA : audioSourceB;
            if (activeSource.clip != null && activeSource.clip.length > 0)
            {
                float posX = rect.x + rect.width * playbackPosition;
                Handles.BeginGUI();
                Handles.color = Color.red;
                Handles.DrawLine(new Vector3(posX, rect.y), new Vector3(posX, rect.y + rect.height));
                Handles.EndGUI();
            }
        }

        GUI.Label(new Rect(rect.x + 8, rect.y + 8, 100, 20),
            isPlayingA ? "A" : "B", EditorStyles.boldLabel);
    }

    private void DrawSplitPreview(Rect rect, Texture2D leftTex, Texture2D rightTex, float handle)
    {
        var handleX = rect.x + rect.width * handle;
        var h = rect.height;
        var w = rect.width;

        Vector2 pivot = rect.center;
        var scaledW = w * zoom;
        var scaledH = h * zoom;
        var offsetX = pivot.x - (scaledW / 2f);
        var offsetY = pivot.y - (scaledH / 2f);
        Rect drawRect = new Rect(offsetX, offsetY, scaledW, scaledH);

        var leftWidth = Mathf.Clamp(handleX - rect.x, 0, w);
        var rightWidth = Mathf.Clamp(rect.x + w - handleX, 0, w);

        if (leftTex != null && leftWidth > 0f)
        {
            GUI.BeginGroup(new Rect(rect.x, rect.y, leftWidth, h));
            var local = new Rect(drawRect.x - rect.x, drawRect.y - rect.y, drawRect.width, drawRect.height);
            GUI.DrawTexture(local, leftTex, ScaleMode.ScaleToFit, true);
            GUI.EndGroup();
        }

        if (rightTex != null && rightWidth > 0f)
        {
            GUI.BeginGroup(new Rect(handleX, rect.y, rightWidth, h));
            var local = new Rect(drawRect.x - handleX, drawRect.y - rect.y, drawRect.width, drawRect.height);
            GUI.DrawTexture(local, rightTex, ScaleMode.ScaleToFit, true);
            GUI.EndGroup();
        }

        Handles.BeginGUI();
        var previousHandleColor = Handles.color;
        Handles.color = Color.white;
        Handles.DrawLine(new Vector3(handleX, rect.y), new Vector3(handleX, rect.y + h));
        Handles.color = previousHandleColor;
        Handles.EndGUI();

        EditorGUIUtility.AddCursorRect(new Rect(handleX - 6, rect.y, 12, h), MouseCursor.ResizeHorizontal);
    }

    private void OnPointerDownPreview(PointerDownEvent evt)
    {
        if (currentAssetType != AssetType.Texture)
        {
            return;
        }

        var rect = previewContainer.contentRect;
        var localX = evt.localPosition.x;
        var w = rect.width;
        var handleX = w * handlePos;
        var handleRect = new Rect(handleX - 6, 0, 12, rect.height);

        if (handleRect.Contains(new Vector2(localX, evt.localPosition.y)))
        {
            dragging = true;
            evt.StopImmediatePropagation();
        }
        else if (rect.Contains(evt.localPosition))
        {
            handlePos = Mathf.Clamp01(localX / w);
            previewContainer.MarkDirtyRepaint();
            evt.StopImmediatePropagation();
        }
    }

    private void OnPointerMovePreview(PointerMoveEvent evt)
    {
        if (currentAssetType != AssetType.Texture || !dragging)
        {
            return;
        }

        var rect = previewContainer.contentRect;
        var localX = evt.localPosition.x;
        var w = rect.width;
        handlePos = Mathf.Clamp01(localX / w);
        previewContainer.MarkDirtyRepaint();
        evt.StopImmediatePropagation();
    }

    private void OnPointerUpPreview(PointerUpEvent evt)
    {
        dragging = false;
    }

    private void OnPreviewScrollWheel(WheelEvent evt)
    {
        if (currentAssetType != AssetType.Texture)
        {
            return;
        }

        var zoomDelta = -evt.delta.y * 0.1f;
        zoom = Mathf.Clamp(zoom + zoomDelta, MinZoom, MaxZoom);
        previewContainer.MarkDirtyRepaint();
        evt.StopImmediatePropagation();
    }

    private Texture2D GenerateWaveform(AudioClip clip, int width, int height)
    {
        if (clip == null)
        {
            return null;
        }

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        var bgColor = new Color(0.1f, 0.1f, 0.1f, 1f);
        var waveColor = new Color(0.3f, 0.8f, 0.3f, 1f);

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                tex.SetPixel(x, y, bgColor);
            }
        }

        int sampleCount = clip.samples * clip.channels;
        float[] samples = new float[sampleCount];
        clip.GetData(samples, 0);

        int samplesPerPixel = sampleCount / width;
        float halfHeight = height / 2f;

        for (int x = 0; x < width; x++)
        {
            float min = 1f;
            float max = -1f;

            for (int i = 0; i < samplesPerPixel; i++)
            {
                int sampleIndex = x * samplesPerPixel + i;
                if (sampleIndex >= sampleCount) break;

                float sample = samples[sampleIndex];
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }

            int minY = Mathf.Clamp((int)(halfHeight + min * halfHeight), 0, height - 1);
            int maxY = Mathf.Clamp((int)(halfHeight + max * halfHeight), 0, height - 1);

            for (int y = minY; y <= maxY; y++)
            {
                tex.SetPixel(x, y, waveColor);
            }
        }

        tex.Apply();
        return tex;
    }

    private void RefreshPreviews()
    {
        if (currentAssetType == AssetType.Audio)
        {
            if (previewAudioA != null)
            {
                waveformA = GenerateWaveform(previewAudioA, 1024, 256);
            }

            if (previewAudioB != null)
            {
                waveformB = GenerateWaveform(previewAudioB, 1024, 256);
            }
        }
        else if (currentAssetType == AssetType.Texture)
        {
            /*var textureUtilType = typeof(Editor).Assembly.GetType("UnityEditor.TextureUtil");

            MethodInfo getStorageMemorySizeLongMethod = textureUtilType?.GetMethod(
                "GetStorageMemorySizeLong",
                BindingFlags.Static | BindingFlags.Public
            );
            var result = (long)getStorageMemorySizeLongMethod.Invoke(null, new object[] { previewA });
            var formattedBytes = EditorUtility.FormatBytes(result);
            
            statBoxA.text = formattedBytes;
            if (IsNPOT(previewA))
            {
                statBoxA.text += "\nNPOT";
            }*/
        }
        previewContainer?.MarkDirtyRepaint();
    }
    
    bool IsNPOT(Texture2D texture)
    {
        if (texture == null)
            return false; // Or throw an error, depending on your needs

        return !IsPowerOfTwo(texture.width) || !IsPowerOfTwo(texture.height);
    }

    bool IsPowerOfTwo(int value)
    {
        // A value is power of two if it's > 0 and has only one bit set in its binary representation
        return value > 0 && (value & (value - 1)) == 0;
    }

    private void CleanupPreviewFiles()
    {
        StopAudio();

        if (!string.IsNullOrEmpty(tempAPath) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(tempAPath) != null)
        {
            AssetDatabase.DeleteAsset(tempAPath);
        }

        if (!string.IsNullOrEmpty(tempBPath) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(tempBPath) != null)
        {
            AssetDatabase.DeleteAsset(tempBPath);
        }

        if (AssetDatabase.IsValidFolder(assetCompareTempPath))
        {
            AssetDatabase.DeleteAsset(assetCompareTempPath);
        }

        tempAPath = null;
        tempBPath = null;
        previewA = null;
        previewB = null;
        previewAudioA = null;
        previewAudioB = null;
        waveformA = null;
        waveformB = null;

        importerEditorA = null;
        importerEditorB = null;

        AssetDatabase.Refresh();
    }

    private VisualElement CreateLabeledRow(string labelText, VisualElement fieldElement, int labelWidth = 130, float spacing = 4)
    {
        var row = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center,
                marginTop = 2,
                marginBottom = 2,
                paddingLeft = 16,
            }
        };

        var label = new Label(labelText)
        {
            style =
            {
                width = labelWidth,
                unityTextAlign = TextAnchor.MiddleLeft,
                marginRight = spacing
            }
        };

        row.Add(label);

        if (fieldElement != null)
        {
            fieldElement.style.flexGrow = 1;
            fieldElement.style.flexShrink = 1;
            fieldElement.style.flexBasis = 0;
            row.Add(fieldElement);
        }

        return row; 
    }   
}
