using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Object = UnityEngine.Object;

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
    
    private readonly Color AudioPreviewBGColor = new Color(0.192f, 0.192f, 0.192f, 1f);
    private readonly Color WaveColor = new Color(1f, 0.549f, 0f, 1f);
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
    private AssetImporter delayedImporter;

    private const string AssetCompareTitle = "AssetCompare";
    private const string TempFolderName = "Temp";
    private string assetCompareEditorPath;
    private string assetCompareTempPath;
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

    private Label sourceStatBox;
    private Label statBoxA;
    private Label statBoxB;
    
    private const int WaveformWidth = 512;
    private const int WaveformHeight = 256;
    private const int PreviewErrorLabelWidth = 600;

    private enum AssetType
    {
        None,
        Texture,
        Audio,
        Unsupported
    }

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
        
        assetCompareEditorPath = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/");

        if (string.IsNullOrEmpty(assetCompareEditorPath))
        {
            Debug.LogError("assetCompareEditorPath not found.");
            return;
        }

        assetCompareTempPath = $"{assetCompareEditorPath}/{TempFolderName}";

        CleanupPreviewFiles();

        if (!AssetDatabase.IsValidFolder(assetCompareTempPath))
        {
            AssetDatabase.CreateFolder(assetCompareEditorPath, TempFolderName);
            // AssetDatabase.Refresh();
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
        var leftPanel = new VisualElement
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
        leftPanel.Add(sourceField);
        
        sourceStatBox = new Label("No Data")
        {
            style =
            {
                width = 130,
                unityTextAlign = TextAnchor.MiddleLeft,
                marginRight = 4,
                marginLeft = 4,
                display = DisplayStyle.None,
            }
        };
        leftPanel.Add(sourceStatBox);
        
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
            style =
            {
                width = 80, 
                marginRight = 8
            }
        };

        swapAudioButton = new Button(SwapAudioChannel)
        {
            text = "Swap to B",
            style = { 
                width = 100, 
                marginRight = 8 
            }
        };

        currentAudioLabel = new Label("Selected: A")
        {
            style = { unityTextAlign = TextAnchor.MiddleLeft }
        };

        audioControlsContainer.Add(playStopButton);
        audioControlsContainer.Add(swapAudioButton);
        audioControlsContainer.Add(currentAudioLabel);
        leftPanel.Add(audioControlsContainer);

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

        StyleLength importerContainerWidth = 300;
        StyleLength importerContainerPadding = 6;
        
        StyleLength importerOverviewMargin = 4;
        StyleLength importerOverviewWidth = 130;

        var importerAOverview = new VisualElement
        {
            style =
            {
                width = importerContainerWidth,
                flexDirection = FlexDirection.Column,
                paddingRight = importerContainerPadding,
                paddingBottom = importerContainerPadding
            }
        };
        
        importerAOverview.Add(new Label("Asset A")
        {
            style =
            {
                width = importerOverviewWidth,
                unityTextAlign = TextAnchor.MiddleLeft,
                marginRight = importerOverviewMargin,
                marginLeft = importerOverviewMargin,
                unityFontStyleAndWeight = FontStyle.Bold,
            }
        });
        
        statBoxA = new Label("No Data")
        {
            style =
            {
                width = importerOverviewWidth,
                unityTextAlign = TextAnchor.MiddleLeft,
                marginRight = importerOverviewMargin,
                marginLeft = importerOverviewMargin,
                display = DisplayStyle.None,
            }
        };
        importerAOverview.Add(statBoxA);

        var importerBOverview = new VisualElement
        {
            style =
            {
                width = importerContainerWidth,
                flexDirection = FlexDirection.Column,
                paddingLeft = importerContainerPadding,
                paddingRight = importerContainerPadding,
                paddingBottom = importerContainerPadding
            }
        };
        
        importerBOverview.Add(new Label("Asset B")
        {
            style =
            {
                width = importerOverviewWidth,
                unityTextAlign = TextAnchor.MiddleLeft,
                marginRight = importerOverviewMargin,
                marginLeft = importerOverviewMargin,
                unityFontStyleAndWeight = FontStyle.Bold,
            }
        });
        
        statBoxB = new Label("No Data")
        {
            style =
            {
                width = importerOverviewWidth,
                unityTextAlign = TextAnchor.MiddleLeft,
                marginRight = importerOverviewMargin,
                marginLeft = importerOverviewMargin,
                display = DisplayStyle.None,
            }
        };
        importerBOverview.Add(statBoxB);

        headersRow.Add(importerAOverview);
        headersRow.Add(importerBOverview);
        leftPanel.Add(headersRow);

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
                width = importerContainerWidth,
                marginRight = 8,
                flexShrink = 0
            }
        };

        var importerBContainer = new IMGUIContainer(DrawImporterB)
        {
            style =
            {
                width = importerContainerWidth,
                flexShrink = 0
            }
        };

        importersRow.Add(importerAContainer);
        importersRow.Add(importerBContainer);

        importersScroll.Add(importersRow);
        leftPanel.Add(importersScroll);

        mainRow.Add(leftPanel);

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
        EditorGUI.BeginChangeCheck();
        
        if (importerEditorA != null)
        {
            importerEditorA.OnInspectorGUI();
        }
        
        if (EditorGUI.EndChangeCheck())
        {
            var imp = importerEditorA.target as AssetImporter;
            if (imp != null)
            {
                imp.SaveAndReimport();
                RefreshPreviews();
                
                if (sourceTexture != null)
                {
                    UpdateTextureStats(previewA, statBoxA);
                }
                else
                {
                    UpdateAudioStats(importerEditorA, statBoxA);
                }
            }
        }
    }

    private void DrawImporterB()
    {
        EditorGUI.BeginChangeCheck();
        
        if (importerEditorB != null)
        {
            importerEditorB.OnInspectorGUI();
        }
        
        if (EditorGUI.EndChangeCheck())
        {
            var imp = importerEditorB.target as AssetImporter;
            if (imp != null)
            {
                imp.SaveAndReimport();
                RefreshPreviews();
                if (sourceTexture != null)
                {
                    UpdateTextureStats(previewB, statBoxB);
                }
                else
                {
                    UpdateAudioStats(importerEditorB, statBoxB);
                }
            }
        }
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

    private void OnSourceFieldChanged(ChangeEvent<UnityEngine.Object> changeEvent)
    {
        SetSourceAsset(changeEvent.newValue);
    }

    private void SetSourceAsset(UnityEngine.Object asset)
    {
        sourceAsset = asset;

        if (asset == null)
        {
            currentAssetType = AssetType.None;
            audioControlsContainer.style.display = DisplayStyle.None;
            UnregisterTexturePreviewEvents();
            
            sourceStatBox.style.display = DisplayStyle.None;
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
        }
        else if (asset is AudioClip)
        {
            currentAssetType = AssetType.Audio;
            sourceAudioClip = asset as AudioClip;
            sourceTexture = null;
            audioControlsContainer.style.display = DisplayStyle.Flex;
            StopAudio();
            UnregisterTexturePreviewEvents();
            
            statBoxA.style.display = DisplayStyle.None;
            statBoxB.style.display = DisplayStyle.None;
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
            
            statBoxA.style.display = DisplayStyle.None;
            statBoxB.style.display = DisplayStyle.None;
        }
        
        UpdateSourceStats();
        GeneratePreview();
    }

    private void GeneratePreview()
    {
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
        UpdateTextureStats(previewA, statBoxA);
        
        if (importerEditorB != null)
        {
            DestroyImmediate(importerEditorB);
            importerEditorB = null;
        }
        previewB = AssetDatabase.LoadAssetAtPath<Texture2D>(tempBPath);
        importerEditorB = CreateImporterEditor(previewB);
        UpdateTextureStats(previewB, statBoxB);

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
        UpdateAudioStats(importerEditorA, statBoxA);
        
        if (importerEditorB != null)
        {
            DestroyImmediate(importerEditorB);
            importerEditorB = null;
        }
        previewAudioB = AssetDatabase.LoadAssetAtPath<AudioClip>(tempBPath);
        importerEditorB = CreateImporterEditor(previewAudioB);
        UpdateAudioStats(importerEditorB, statBoxB);

        if (previewAudioA != null)
        {
            waveformA = GenerateWaveform(previewAudioA, WaveformWidth, WaveformHeight);
        }

        if (previewAudioB != null)
        {
            waveformB = GenerateWaveform(previewAudioB, WaveformWidth, WaveformHeight);
        }

        previewContainer?.MarkDirtyRepaint();
    }

    private Editor CreateImporterEditor(UnityEngine.Object selectedAsset)
    {
        if (selectedAsset == null)
        {
            return null;
        }

        var path = AssetDatabase.GetAssetPath(selectedAsset);
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
            EditorApplication.delayCall += OnDelayedEditorCreation;
            return null;
        }

        return Editor.CreateEditor(importer);
    }

    private void OnDelayedEditorCreation()
    {
        if (delayedImporter == null)
        {
            return;
        }

        importerEditorA = Editor.CreateEditor(delayedImporter);
        
        delayedImporter = null;
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

    private void PlayAudio()
    {
        if (previewAudioA == null || previewAudioB == null)
        {
            return;
        }

        audioSourceA.clip = previewAudioA;
        audioSourceB.clip = previewAudioB;

        SetActiveChannel(isPlayingA);

        audioSourceA.time = audioSourceB.time = 0f;
        audioSourceA.Play();
        audioSourceB.Play();

        isPlaying = true;
        
        UpdatePlayStopButton();
    }
    
    private void SetActiveChannel(bool a)
    {
        isPlayingA = a;
        if (audioSourceA != null && audioSourceB != null)
        {
            audioSourceA.volume = a ? 1f : 0f;
            audioSourceB.volume = a ? 0f : 1f;
        }
        UpdateSwapButton();
        
        previewContainer.MarkDirtyRepaint();
    }

    private void SwapAudioChannel()
    {
        SetActiveChannel(!isPlayingA);
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
            currentAudioLabel.text = "Selected: " + (isPlayingA ? "A" : "B");
        }
    }

    private void DrawPreviewIMGUI()
    {
        var rect = GUILayoutUtility.GetRect(previewContainer.contentRect.width, previewContainer.contentRect.height);

        if (currentAssetType == AssetType.None)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, PreviewErrorLabelWidth, 20), "No asset selected.");
            return;
        }

        if (currentAssetType == AssetType.Unsupported)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, PreviewErrorLabelWidth, 40),
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
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, PreviewErrorLabelWidth, 20), "No texture preview available.");
            return;
        }

        DrawSplitPreview(rect, previewA, previewB, handlePos);
    }

    private void DrawAudioPreview(Rect rect)
    {
        Texture2D currentWaveform = isPlayingA ? waveformA : waveformB;

        if (currentWaveform == null)
        {
            GUI.Label(new Rect(rect.x + 8, rect.y + 8, PreviewErrorLabelWidth, 20), "No audio preview available.");
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

        var sampleLetter = isPlayingA ? "A" : "B";
        GUI.Label(new Rect(rect.x + 8, rect.y + 8, 100, 20), sampleLetter, EditorStyles.boldLabel);
    }

    private void DrawSplitPreview(Rect rect, Texture2D leftTex, Texture2D rightTex, float handle)
    {
        var handleX = rect.x + rect.width * handle;
        var height = rect.height;
        var width = rect.width;

        Vector2 pivot = rect.center;
        var scaledW = width * zoom;
        var scaledH = height * zoom;
        var offsetX = pivot.x - (scaledW / 2f);
        var offsetY = pivot.y - (scaledH / 2f);
        Rect drawRect = new Rect(offsetX, offsetY, scaledW, scaledH);

        var leftWidth = Mathf.Clamp(handleX - rect.x, 0, width);
        var rightWidth = Mathf.Clamp(rect.x + width - handleX, 0, width);

        if (leftTex != null && leftWidth > 0f)
        {
            GUI.BeginGroup(new Rect(rect.x, rect.y, leftWidth, height));
            var local = new Rect(drawRect.x - rect.x, drawRect.y - rect.y, drawRect.width, drawRect.height);
            GUI.DrawTexture(local, leftTex, ScaleMode.ScaleToFit, true);
            GUI.EndGroup();
        }

        if (rightTex != null && rightWidth > 0f)
        {
            GUI.BeginGroup(new Rect(handleX, rect.y, rightWidth, height));
            var local = new Rect(drawRect.x - handleX, drawRect.y - rect.y, drawRect.width, drawRect.height);
            GUI.DrawTexture(local, rightTex, ScaleMode.ScaleToFit, true);
            GUI.EndGroup();
        }

        Handles.BeginGUI();
        var previousHandleColor = Handles.color;
        Handles.color = Color.white;
        Handles.DrawLine(new Vector3(handleX, rect.y), new Vector3(handleX, rect.y + height));
        Handles.color = previousHandleColor;
        Handles.EndGUI();

        EditorGUIUtility.AddCursorRect(new Rect(handleX - 6, rect.y, 12, height), MouseCursor.ResizeHorizontal);
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

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                tex.SetPixel(x, y, AudioPreviewBGColor);
            }
        }

        var sampleCount = clip.samples * clip.channels;
        var samples = new float[sampleCount];
        clip.GetData(samples, 0);

        var samplesPerPixel = sampleCount / width;
        var halfHeight = height / 2f;

        for (int x = 0; x < width; x++)
        {
            var min = 1f;
            var max = -1f;

            for (int i = 0; i < samplesPerPixel; i++)
            {
                var sampleIndex = x * samplesPerPixel + i;
                if (sampleIndex >= sampleCount)
                {
                    break;
                }

                var sample = samples[sampleIndex];
                if (sample < min)
                {
                    min = sample;
                }

                if (sample > max)
                {
                    max = sample;
                }
            }

            var minY = Mathf.Clamp((int)(halfHeight + min * halfHeight), 0, height - 1);
            var maxY = Mathf.Clamp((int)(halfHeight + max * halfHeight), 0, height - 1);

            for (int y = minY; y <= maxY; y++)
            {
                tex.SetPixel(x, y, WaveColor);
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
                waveformA = GenerateWaveform(previewAudioA, WaveformWidth, WaveformHeight);
            }

            if (previewAudioB != null)
            {
                waveformB = GenerateWaveform(previewAudioB, WaveformWidth, WaveformHeight);
            }
        }
        previewContainer?.MarkDirtyRepaint();
    }
    
    private static long GetStorageMemorySize(Texture2D texture) {
        var unityEditorAssembly = typeof(Editor).Assembly;
        var textureUtilClass = unityEditorAssembly.GetType("UnityEditor.TextureUtil");
        var method = textureUtilClass.GetMethod(
            "GetStorageMemorySizeLong",
            BindingFlags.Static | BindingFlags.Public
        );
			
        var size = (long)method.Invoke(
            null,
            new object[] {
                texture
            }
        );
			
        return size;
    }

    void UpdateTextureStats(Texture2D targetTexture, Label targetLabel)
    {
        var sb = new StringBuilder();
        var result = GetStorageMemorySize(targetTexture);
        var formattedBytes = EditorUtility.FormatBytes(result);
        
        sb.AppendLine($"Imported Size: {formattedBytes}");

        targetLabel.text = sb.ToString();
        targetLabel.style.display = DisplayStyle.Flex;
    }
    
    private static int GetSoundSize(Editor editor) {
        var importer = editor.target as AudioImporter;
        var so = new SerializedObject(importer);
        var prop = so.FindProperty("m_PreviewData.m_CompSize");
        return prop.intValue;
    }

    void UpdateAudioStats(Editor importerEditor, Label targetLabel)
    {
        var sb = new StringBuilder();
        var result = GetSoundSize(importerEditor);
        var formattedBytes = EditorUtility.FormatBytes(result);
        
        sb.AppendLine($"Imported Size: {formattedBytes}");

        targetLabel.text = sb.ToString();
        targetLabel.style.display = DisplayStyle.Flex;
    }
    
    void UpdateSourceStats()
    {
        var sb = new StringBuilder();
        
        var assetPath = AssetDatabase.GetAssetPath(sourceAsset);
        var absolutePath = Path.Combine(Application.dataPath.Replace("Assets", ""), assetPath);
        var originalFileInfo = new FileInfo(absolutePath);
        var originalFileSizeBytes = originalFileInfo.Length;
        
        sb.AppendLine($"Source Type: {originalFileInfo.Extension.Substring(1).ToUpper()}");
        sb.AppendLine($"Source Size: {EditorUtility.FormatBytes(originalFileSizeBytes)}");
        
        if (IsNPOT(sourceTexture))
        {
            sb.AppendLine("\nNon-power-of-two (NPOT) textures are incompatible with some compression formats.");
        }

        sourceStatBox.text = sb.ToString();
        sourceStatBox.style.display = DisplayStyle.Flex;
    }
    
    bool IsNPOT(Texture2D texture)
    {
        if (texture == null)
        {
            return false;
        }

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

        if (waveformA != null)
        {
            Object.DestroyImmediate(waveformA); 
            waveformA = null;
        }

        if (waveformB != null)
        {
            Object.DestroyImmediate(waveformB); 
            waveformB = null;
        }

        if (importerEditorA != null)
        {
            Object.DestroyImmediate(importerEditorA); 
            importerEditorA = null;
        }

        if (importerEditorB != null)
        {
            Object.DestroyImmediate(importerEditorB); 
            importerEditorB = null;
        }
        
        AssetDatabase.Refresh();
    }
}
