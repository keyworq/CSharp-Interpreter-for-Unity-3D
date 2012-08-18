//-----------------------------------------------------------------------
// <summary>
// CSI: A simple C# interpreter
// </summary>
// <copyright file="CSharpInterpreter.cs" company="Tiaan.com">
//   Copyright (c) 2010 Tiaan Geldenhuys
//
//   Permission is hereby granted, free of charge, to any person
//   obtaining a copy of this software and associated documentation
//   files (the "Software"), to deal in the Software without
//   restriction, including without limitation the rights to use,
//   copy, modify, merge, publish, distribute, sublicense, and/or
//   sell copies of the Software, and to permit persons to whom the
//   Software is furnished to do so, subject to the following
//   conditions:
//
//   The above copyright notice and this permission notice shall be
//   included in all copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//   EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
//   OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
//   NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
//   HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
//   WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
//   FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
//   OTHER DEALINGS IN THE SOFTWARE.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

/// <summary>
/// Implements a hosting environment for the C# Interpreter as a Component that can be attached to a GameObject in Unity 3D.
/// </summary>
////[ExecuteInEditMode]
public sealed class CSharpInterpreter : MonoBehaviour, CSI.IConsole
{
    public const string Version = "0.8.24.5";

    private const string PromptStart = ">>>";
    private const string PromptExtra = "...";
    private const string PromptBlank = "----";
    private const string InputTextBoxName = "CsiInputTextBox";
    private const string EmptyCacheSlot = "{7ewyutPloEyVoQ0lPYfsWw}";  // Cannot use null, since Unity resets it to empty during rebuilds

    public string includeFile;
    public UnityEngine.Object includeAsset;
    public UnityEngine.Object queuedAsset;
    public int maxHistorySize;
    public int maxOutputSize;
    public bool showInteractiveGUI;
    public bool showOutputText;
    public bool showOutputAsEditorSelection;
    public bool showTooltipText;
    public float leftMargin;
    public float topMargin;
    public float rightMargin;
    public float bottomMargin;
    public int toolboxWidth;
    public float splitterFraction;
    public int maxOutputLineWidth;
    public int maxOutputLineCount;

    private int currentHistoryIndex;
    private string promptText, inputText, outputText;
    private Vector2 inputScrollPosition, outputScrollPosition;
    private StringBuilder outputStringBuilder;
    private static CSI.Interpreter csharpEngine;
    private CSI.Interpreter.InputHandler inputHandler;
    private Assembly unityEditorAssembly;
    private List<string> inputTextCache;
    private List<string> inputTextHistory;

    /// <summary>
    /// Gets the C# interpreter instance that is currently active.
    /// </summary>
    /// <value>The current CSI instance.</value>
    public static CSharpInterpreter Current
    {
        get
        {
            return (CSI.Interpreter.Console as CSharpInterpreter);
        }
    }

    public bool IsEditorAvailable()
    {
        return (this.unityEditorAssembly != null);
    }

    private static string GetDefaultIncludeFilename()
    {
        string filename;
        try
        {
            filename =
                new System.Diagnostics.StackTrace(true).GetFrame(0).GetFileName();
            filename = Path.Combine(
                Path.GetDirectoryName(filename),
                Path.GetFileNameWithoutExtension(filename) + "_Include.txt");
            if ((!File.Exists(filename)) ||
                string.IsNullOrEmpty(Application.dataPath))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        filename = filename.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string dataPath = Application.dataPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if ((filename.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase)))
        {
            filename = filename.Substring(dataPath.Length).TrimStart(Path.DirectorySeparatorChar);
        }

        return filename;
    }

    //public CSharpInterpreter()
    private void Awake()
    {
        this.Reset();
    }

    /// <summary>
    /// Performs one-time initialization of this instance; called by Unity.
    /// </summary>
    private void Start()
    {
        this.promptText = string.Empty;
        this.inputText = string.Empty;
        this.outputText = string.Empty;
        this.outputScrollPosition = Vector2.zero;
        this.inputScrollPosition = Vector2.zero;

        this.Reinitialize();
    }

    private void Reset()
    {
        this.includeFile = GetDefaultIncludeFilename();
        this.includeAsset = null;
        this.queuedAsset = null;
        this.maxHistorySize = 100;
        this.maxOutputSize = 15250;  // Seems to be okay to avoid errors like these from the Debug inspector: "Optimized GUI Block text buffer too large. Not appending further text."
        this.showInteractiveGUI = true;
        this.showOutputText = true;
        this.showOutputAsEditorSelection = true;
        this.showTooltipText = true;
        this.leftMargin = float.NaN;
        this.topMargin = float.NaN;
        this.rightMargin = float.NaN;
        this.bottomMargin = float.NaN;
        this.toolboxWidth = 45;
        this.splitterFraction = 75f;
        this.maxOutputLineWidth = 32000;
        this.maxOutputLineCount = 80;
        this.currentHistoryIndex = (this.inputTextHistory == null) ? 0 :
            Math.Max(0, this.inputTextHistory.Count - 1);
    }

    /// <summary>
    /// Performs initialization of this instance, which can be called at startup or in play mode when the Unity Editor rebuilds scripts.
    /// </summary>
    private bool Reinitialize()
    {
        if ((csharpEngine != null /* Has an interpreter */) &&
            (CSI.Interpreter.Console != null /* Has a console */) &&
            (!object.ReferenceEquals(CSI.Interpreter.Console, this) /* Console is another object */))
        {
            UnityEngine.Object otherUnityObject = null;
            if ((!(CSI.Interpreter.Console is UnityEngine.Object) /* Not a Unity object */) ||
                ((bool)(otherUnityObject = CSI.Interpreter.Console as UnityEngine.Object) /* Another live Unity object */))
            {
                this.enabled = false;
                if (otherUnityObject)
                {
                    Debug.LogWarning(
                        "Only one C# Interpreter may be created per scene; " +
                        "use the one on the object named: " + otherUnityObject.name, this);
                    if (otherUnityObject is Behaviour)
                    {
                        ((Behaviour)otherUnityObject).enabled = true;
                    }
                }
                else
                {
                    Debug.LogWarning(
                        "Only one C# Interpreter console may be created!", this);
                }

                return false;  // Not initialized
            }
        }

        this.EnforceParameterLimits();
        if (string.IsNullOrEmpty(this.outputText))
        {
            this.outputText = "CSI Simple C# Interpreter v." + Version + " from Tiaan.com in CLR v." + Environment.Version.ToString() + " on Unity v." + Application.unityVersion;
        }
        else
        {
            this.outputText += string.Format("{0}[CSI reloaded and reset some data @ {1}]", Environment.NewLine, DateTime.Now.ToLongTimeString());
        }

        this.outputStringBuilder = new StringBuilder(this.outputText + Environment.NewLine);
        this.outputScrollPosition.y = Mathf.Infinity;

        if (this.inputTextCache == null)
        {
            this.inputTextCache = new List<string>(this.maxHistorySize + 1);
        }

        if (this.inputTextHistory == null)
        {
            this.inputTextHistory = new List<string>(this.maxHistorySize + 1);
            this.inputTextCache.Clear();
        }

        this.currentHistoryIndex = Math.Max(0, this.inputTextHistory.Count - 1);

        InitializeCompilerForUnity3D.RunOnce();
        csharpEngine = new CSI.Interpreter();
        csharpEngine.OnGetUnknownItem += this.OnGetUnknownItem;

        CSI.Interpreter.Console = this;
        string libraryPath = null;
        if (Application.isEditor)
        {
            // For Editor: Seach project's "Library\ScriptAssemblies" directory
            libraryPath = Path.GetDirectoryName(
                csharpEngine.FullExecutablePath());
        }
        else
        {
#if UNITY_2_6
            // Unity 2.6.1 Player: Seach "<ApplicationName>_Data"
            libraryPath = Application.dataPath;
#else  // i.e., 3.0 and greater
            // Players of Unity 3.0.0 and 3.1.0: Seach "<ApplicationName>_Data\Managed"
            try
            {
                libraryPath = Path.GetDirectoryName(GetFullPathOfAssembly(typeof(int).Assembly));
            }
            catch
            {
                libraryPath = Path.Combine(Application.dataPath ?? string.Empty, "Managed");
            }
#endif
        }

        try
        {
            // Add DLLs from the project's "Library\ScriptAssemblies" directory
            if (!string.IsNullOrEmpty(libraryPath))
            {
                foreach (string reference in
#if UNITY_2_6
                    Directory.GetFiles(libraryPath, "Assembly - *.dll"))
#else  // i.e., 3.0 and greater
                    Directory.GetFiles(libraryPath, "Assembly-*.dll"))
#endif
                {
                    // When using Unity 2.6.1 convention:
                    //  * "Assembly - CSharp.dll"
                    //  * "Assembly - CSharp - Editor.dll"
                    //  * "Assembly - CSharp - first pass.dll"
                    //  * "Assembly - UnityScript - first pass.dll"
                    // When using Unity 3.0.0 and 3.1.0 convention:
                    //  * "Assembly-CSharp.dll"
                    //  * "Assembly-CSharp-firstpass.dll"
                    //  * "Assembly-UnityScript-firstpass.dll"
                    csharpEngine.AddReference(reference);
                }
            }

            string includeFile = this.includeFile;
            if (!string.IsNullOrEmpty(includeFile))
            {
                string cachedFilename = includeFile;
                includeFile = ResolveFilename(includeFile);
                if (!csharpEngine.ReadIncludeFile(includeFile))
                {
                    ForceWarning(
                        "CSI include-file not loaded (" + cachedFilename + ")", this);
                }
            }

            string includeAssetName;
            string includeCode = GetAssetText(this.includeAsset, out includeAssetName);
            if ((!string.IsNullOrEmpty(includeCode)) &&
                (!csharpEngine.ReadIncludeCode(includeCode)))
            {
                ForceWarning(
                    "CSI include-asset not loaded: " + (includeAssetName ?? string.Empty), this);
            }

            Assembly unityEngineAssembly = null;
            string fullAssemblyPath = GetFullPathOfAssembly(
                typeof(UnityEngine.GameObject).Assembly);
            if (File.Exists(fullAssemblyPath))
            {
                // Adds "UnityEngine.dll", or rather "UnityEngine-Debug.dll", for the 
                // Editor, which is located in the "...\Unity\Editor\Data\lib" directory.
                // However, this does not work for the Standalone Windows Player, which 
                // uses the same mechanism for UnityEngine as for UnityEditor (below).
                unityEngineAssembly = typeof(UnityEngine.GameObject).Assembly;
            }

            // Add the Unity Editor's assembly only when available
            this.unityEditorAssembly = null;
            foreach (Assembly assembly in
                AppDomain.CurrentDomain.GetAssemblies())
            {
                if (this.unityEditorAssembly == null)
                {
                    try
                    {
                        if ((assembly.FullName.StartsWith("UnityEditor,", StringComparison.OrdinalIgnoreCase)) &&
                            (assembly.GetType("UnityEditor.EditorApplication") != null))
                        {
                            this.unityEditorAssembly = assembly;
                        }
                    }
                    catch
                    {
                        // Skip problematic assemblies
                    }
                }

                if (unityEngineAssembly == null)
                {
                    try
                    {
                        if (((assembly.FullName.StartsWith("UnityEngine,", StringComparison.OrdinalIgnoreCase)) ||
                            (assembly.FullName.StartsWith("UnityEngine-Debug,", StringComparison.OrdinalIgnoreCase))) &&
                            (assembly.GetType("UnityEngine.GameObject") != null))
                        {
                            unityEngineAssembly = assembly;
                        }
                    }
                    catch
                    {
                        // Skip problematic assemblies
                    }
                }

                if ((this.unityEditorAssembly != null) &&
                    (unityEngineAssembly != null))
                {
                    break;
                }
            }

            if (unityEngineAssembly != null)
            {
                // Include "UnityEngine.dll" or "UnityEngine-Debug.dll"
                string filename = GetFullPathOfAssembly(unityEngineAssembly);
#if !UNITY_2_6  // i.e., 3.0 and greater
                if (!File.Exists(filename))
                {
                    try
                    {
                        filename = GetFullPathOfAssembly(typeof(int).Assembly);
                        filename = Path.Combine(
                            Path.GetDirectoryName(filename) ?? string.Empty,
                            "UnityEngine.dll");
                    }
                    catch
                    {
                        filename = null;
                    }
                }
#endif

                if (File.Exists(filename))
                {
                    csharpEngine.AddReference(filename);
                    csharpEngine.AddNamespace("UnityEngine");
                }
                else
                {
                    unityEngineAssembly = null;
                }
            }

            if (unityEngineAssembly == null)
            {
                ForceWarning("UnityEngine is not referenced!");
            }

            if (this.unityEditorAssembly != null)
            {
                // Include "UnityEditor.dll"
                string filename =
                    GetFullPathOfAssembly(this.unityEditorAssembly);
                if (File.Exists(filename))
                {
                    csharpEngine.AddReference(filename);
                    csharpEngine.AddNamespace("UnityEditor");
                }
                else
                {
                    this.unityEditorAssembly = null;
                }
            }

            if ((this.unityEditorAssembly == null) 
                && Application.isEditor)
            {
                Debug.LogWarning("UnityEditor is not referenced!");
            }

            this.PromptForInput(PromptStart);
            this.AddGlobal("csi", this);
            return true;  // Initialized successfully
        }
        catch (IOException exception)
        {
            // Probably running in the web player without required rights
            Debug.LogError(
                "CSI failed to initialize (web player not supported): " + exception.Message, this);
            return false;
        }
    }

    private static void ForceWarning(string message)
    {
        ForceWarning(message, null /* context */);
    }

    private static void ForceWarning(string message, UnityEngine.Object context)
    {
        if (Application.isEditor)
        {
            if (context == null)
            {
                Debug.LogWarning(message);
            }
            else
            {
                Debug.LogWarning(message, context);
            }
        }
        else
        {
            CSI.Utils.Print("Warning: " + message);
        }
    }

    private static string GetFullPathOfAssembly(Assembly assembly)
    {
        if (assembly == null)
        {
            return null;
        }

        string codeBase = assembly.CodeBase;
        if (string.IsNullOrEmpty(codeBase))
        {
            return null;
        }

        string filename = new Uri(codeBase).LocalPath;
        if (!File.Exists(filename))
        {
            string tempName = assembly.FullName ?? string.Empty;
            int index = tempName.IndexOf(',');
            if (index > 0)
            {
                tempName = Path.Combine(
                    Path.GetDirectoryName(filename) ?? string.Empty,
                    Path.ChangeExtension(tempName.Substring(0, index), ".dll"));
                if (File.Exists(tempName))
                {
                    filename = tempName;
                }
            }
        }

        return filename;
    }

    private static string GetAssetText(object asset, out string assetName)
    {
        assetName = null;
        string includeCode = null;
        if (asset != null)
        {
            // Handle any Unity object (dead or alive)
            if (asset is UnityEngine.Object)
            {
                UnityEngine.Object unityObject;
                if ((bool)(unityObject = asset as UnityEngine.Object))
                {
                    // Handle live Unity objects
                    assetName = unityObject.name;
                    if (unityObject is TextAsset)
                    {
                        includeCode = ((TextAsset)unityObject).text;
                    }
                }
            }
            else if (asset is string)
            {
                includeCode = (string)asset;
            }
        }

        return includeCode;
    }

    private void EnforceParameterLimits()
    {
        // Auto-size the window layout area, if needed...
        if (float.IsNaN(this.leftMargin))
        {
            this.leftMargin = 800f / Screen.width;
        }

        if (float.IsNaN(this.topMargin))
        {
            this.topMargin = 800f / Screen.height;
        }

        if (float.IsNaN(this.rightMargin))
        {
            this.rightMargin = 6500f / Screen.width;
        }

        if (float.IsNaN(this.bottomMargin))
        {
            this.bottomMargin = 800f / Screen.height;
        }

        // Clip parameters within bounds...
        if (this.maxHistorySize < 1)
        {
            this.maxHistorySize = 1;
        }
        else if (this.maxHistorySize > 9999)
        {
            this.maxHistorySize = 9999;
        }

        if (this.maxOutputSize < 2048)
        {
            this.maxOutputSize = 2048;
        }
        else if (this.maxOutputSize > 16380)
        {
            // Almost 16KB seems to be the upper limit of a Unity TextArea
            this.maxOutputSize = 16380;
        }

        if (this.splitterFraction < 15f)
        {
            this.splitterFraction = 15f;
        }
        else if (this.splitterFraction > 77.5f)
        {
            this.splitterFraction = 77.5f;
        }

        if (this.toolboxWidth < 35)
        {
            this.toolboxWidth = 35;
        }

        if (this.maxOutputLineWidth < 20)
        {
            this.maxOutputLineWidth = 20;
        }

        if (this.maxOutputLineCount < 3)
        {
            this.maxOutputLineCount = 3;
        }
    }

    private object OnGetUnknownItem(object key)
    {
        if (key is string)
        {
            string stringKey = (string)key;
            try
            {
                GameObject gameObject = GameObject.Find(stringKey);
                if (gameObject)
                {
                    return gameObject;
                }
            }
            catch
            {
                // Interpret any error as not finding the item
            }

            try
            {
                GameObject[] gameObjects =
                    GameObject.FindGameObjectsWithTag(stringKey);
                if ((gameObjects != null) &&
                    (gameObjects.Length > 0))
                {
                    return gameObjects;
                }
            }
            catch
            {
                // Interpret any error as not finding the items
            }

            try
            {
                Type type = CSI.Utils.GetType(stringKey);
                if (type != null)
                {
                    key = type;
                }
            }
            catch
            {
                // Ignore
            }
        }

        if (key is Type)
        {
            Type typeKey = (Type)key;
            try
            {
                UnityEngine.Object[] objects =
                    UnityEngine.Object.FindObjectsOfType(typeKey);
                if ((objects != null) &&
                    (objects.Length > 0))
                {
                    return objects;
                }
            }
            catch
            {
                // Interpret any error as not finding the item
            }
        }

        return null;
    }

    private static string ResolveFilename(string filename)
    {
        try
        {
            if (string.IsNullOrEmpty(filename))
            {
                return filename;
            }

            if (File.Exists(filename))
            {
                return Path.GetFullPath(filename);
            }

            filename = Path.Combine(Application.dataPath ?? string.Empty, filename);
            if (File.Exists(filename))
            {
                return filename;
            }
        }
        catch (IOException)
        {
            // Probably running in the web player without required rights
        }

        return null;
    }

    /// <summary>
    /// Adds the specified global variable to the interpreter environment.
    /// </summary>
    public object AddGlobal(string name, object value)
    {
        csharpEngine.SetValue(name, value);
        return value;
    }

    public bool HasGlobal(string name)
    {
        return csharpEngine.VarTable.ContainsKey(name);
    }

    public bool RemoveGlobal(string name)
    {
        if (this.HasGlobal(name))
        {
            csharpEngine.VarTable.Remove(name);
            return true;
        }

        return false;
    }

    public void ClearOutput()
    {
        string outputText;
        this.ClearOutput(out outputText);
    }

    public void ClearOutput(out string outputText)
    {
        outputText = this.outputText;
        this.outputText = string.Empty;
        this.outputStringBuilder.Length = 0;
    }

    public string GetOutput()
    {
        return this.outputText;
    }

    public string[] GetHistory()
    {
        return this.inputTextHistory.ToArray();
    }

    public void ClearHistory()
    {
        string[] history;
        this.ClearHistory(out history);
    }

    public void ClearHistory(out string[] history)
    {
        history = this.GetHistory();
        this.inputTextHistory.Clear();
        this.inputTextCache.Clear();
        this.currentHistoryIndex = 0;
    }

    public object GetLastExecuteResult()
    {
        if ((csharpEngine != null) &&
            (csharpEngine.returnsValue))
        {
            return csharpEngine.VarTable["_"];
        }

        return null;
    }

    /// <summary>
    /// Execute the specified code in the interpreter environment.
    /// </summary>
    public bool ExecuteCode(string inputText)
    {
        try
        {
            if (csharpEngine.ProcessLine(inputText))
            {
                this.PromptForInput((csharpEngine.BlockLevel > 0) ? PromptExtra : PromptStart);

                if (this.showOutputAsEditorSelection &&
                    this.IsEditorAvailable())
                {
                    try
                    {
                        object result = this.GetLastExecuteResult();
                        if (result != null)
                        {
                            this.Select(result);
                        }
                    }
                    catch
                    {
                        // Ignore error during result selection
                    }
                }

                return true;
            }
        }
        catch (Exception exception)
        {
            CSI.Interpreter.Console.Write("ERROR: " + exception.Message);
            ////CSI.Interpreter.Console.Write("ERROR: " + exception.ToString());
            CSI.Interpreter.Console.Write(Environment.NewLine);
            this.PromptForInput(PromptStart);
        }

        return false;
    }

    /// <summary>
    /// Execute the specified file in the interpreter environment.
    /// </summary>
    public bool ExecuteFile(string inputFilename)
    {
        inputFilename = ResolveFilename(inputFilename);
        if (File.Exists(inputFilename))
        {
            string inputText = File.ReadAllText(inputFilename);
            return this.ExecuteCode(inputText);
        }

        return false;
    }

    public bool Select(object obj)
    {
        if ((obj == null) ||
            (!this.IsEditorAvailable()))
        {
            return false;
        }

        if (obj is GameObject)
        {
            return this.Select((GameObject)obj);
        }

        if (obj is Transform)
        {
            return this.Select((Transform)obj);
        }

        if (obj is IEnumerable<UnityEngine.Object>)
        {
            return this.Select((IEnumerable<UnityEngine.Object>)obj);
        }

        if (obj is UnityEngine.Object)
        {
            return this.Select((UnityEngine.Object)obj);
        }

        return false;
    }

    public bool Select(GameObject gameObject)
    {
        return this.Select("activeGameObject", gameObject);
    }

    public bool Select(Transform transform)
    {
        return this.Select("activeTransform", transform);
    }

    public delegate void Action();

    /// <summary>
    /// Implements a helper method that can be used to execute a statement and hide the result from the interpreter output windows.
    /// </summary>
    /// <param name="action">The action delegate to be executed.</param>
    public void Invoke(Action action)
    {
        if (action != null)
        {
            action();
        }
    }

    public bool Select(UnityEngine.Object unityObject)
    {
        return this.Select("activeObject", unityObject);
    }

    public bool Select(IEnumerable<UnityEngine.Object> unityObjects)
    {
        if ((unityObjects == null) ||
            (!this.IsEditorAvailable()))
        {
            return false;
        }

        List<UnityEngine.Object> objects =
            new List<UnityEngine.Object>(unityObjects);
        if (objects.Count <= 0)
        {
            return false;
        }

        if ((objects.Count == 1) &&
            this.Select((object)objects[0]))
        {
            return true;
        }

        // For Component objects, select the containing game-object 
        // instead, so that the items would be highlighted in the editor
        Component component;
        GameObject gameObject;
        for (int index = objects.Count - 1; index >= 0; index--)
        {
            component = objects[index] as Component;
            if (!component)
            {
                continue;
            }

            gameObject = component.gameObject;
            if (!gameObject)
            {
                continue;
            }

            objects[index] = gameObject;
        }

        return this.Select("objects", objects.ToArray());
    }

    private bool Select(
        string selectionPropertyName, object selectionPropertyValue)
    {
        if ((selectionPropertyValue == null) ||
            (!this.IsEditorAvailable()))
        {
            return false;
        }

        return this.SetUnityEditorProperty(
            "UnityEditor.Selection",
            selectionPropertyName,
            selectionPropertyValue);
    }

    private bool SetUnityEditorProperty(
        string objectTypeName, string propertyName, object propertyValue)
    {
        const object ObjectInstance = null;  // Static property
        if (this.unityEditorAssembly != null)
        {
            Type type = this.unityEditorAssembly.GetType(objectTypeName);
            if (type != null)
            {
                PropertyInfo property = type.GetProperty(propertyName);
                if (property != null)
                {
                    property.SetValue(ObjectInstance, propertyValue, null);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Perform the update logic; called by Unity on every frame.
    /// </summary>
    private void Update()
    {
        // If any code is queued for execution, run it
        UnityEngine.Object queuedAsset = this.queuedAsset;
        if (queuedAsset != null)
        {
            this.queuedAsset = null;
            string queuedAssetName;
            string queuedCode = GetAssetText(queuedAsset, out queuedAssetName);
            if ((!string.IsNullOrEmpty(queuedCode)) ||
                (!string.IsNullOrEmpty(queuedAssetName)))
            {
                if (!string.IsNullOrEmpty(queuedCode))
                {
                    this.ExecuteCode(queuedCode);
                    this.outputScrollPosition.y = Mathf.Infinity;
                }
                else if (!string.IsNullOrEmpty(queuedAssetName))
                {
                    Debug.LogWarning(
                        "CSI queued-asset not executed: " + queuedAssetName, this);
                }
            }
        }
    }

    /// <summary>
    /// Draws the GUI and execute its interaction logic; called by Unity on a frequent basis.
    /// </summary>
    private void OnGUI()
    {
        if ((csharpEngine == null) ||
            (!object.ReferenceEquals(CSI.Interpreter.Console, this)))
        {
            // Detect and re-initialize after a rebuild 
            // or when the component has been re-enabled
            if (!this.Reinitialize())
            {
                return;  // Cannot have multiple active consoles
            }
        }

        // Process keyboard input
        this.EnforceParameterLimits();
        Event currentEvent = Event.current;
        if ((currentEvent.isKey) &&
            (!currentEvent.control) &&
            (!currentEvent.shift))
        {
            bool isKeyDown = (currentEvent.type == EventType.KeyDown);
            if (currentEvent.alt)
            {
                if (currentEvent.keyCode == KeyCode.F2)
                {
                    if (isKeyDown)
                    {
                        // For Alt+F2, toggle whether the GUI gets displayed
                        this.showInteractiveGUI = !this.showInteractiveGUI;
                    }

                    currentEvent.Use();
                }
            }

            if (GUI.GetNameOfFocusedControl() == InputTextBoxName)
            {
                if (currentEvent.alt)
                {
                    if (currentEvent.keyCode == KeyCode.F1)
                    {
                        if (isKeyDown)
                        {
                            // For Alt+F1, display metadata of the last result
                            this.OnMetaRequest();
                        }

                        currentEvent.Use();
                    }
                }
                else
                {
                    while (this.inputTextHistory.Count <= this.currentHistoryIndex)
                    {
                        this.inputTextHistory.Add(string.Empty);
                    }

                    while (this.inputTextCache.Count <= this.currentHistoryIndex)
                    {
                        this.inputTextCache.Add(EmptyCacheSlot);
                    }

                    if ((currentEvent.keyCode == KeyCode.UpArrow) ||
                        (currentEvent.keyCode == KeyCode.DownArrow) ||
                        (currentEvent.keyCode == KeyCode.Escape))
                    {
                        // Navigate the input history
                        // NOTE: Holding down Caps Lock would bypass navigation
                        if (!currentEvent.capsLock)
                        {
                            if (isKeyDown)
                            {
                                KeyCode keyCode = currentEvent.keyCode;
                                bool? useTrueForOlderOrFalseForNewerOrNullForUndo =
                                    (keyCode == KeyCode.UpArrow) ? true :
                                    (keyCode == KeyCode.DownArrow) ? (bool?)false :
                                    null;
                                this.OnNavigateHistory(
                                    useTrueForOlderOrFalseForNewerOrNullForUndo);
                            }

                            currentEvent.Use();
                        }
                    }
                    else if ((this.inputHandler != null) &&
                       (currentEvent.keyCode == KeyCode.Return))
                    {
                        // Handle the enter key; process the input text
                        currentEvent.Use();
                        if (isKeyDown)
                        {
                            this.OnExecuteInput();
                        }
                    }
                }
            }
        }

        // Draw the GUI
        try
        {
            this.OnDrawGUI();
        }
        catch (ArgumentException exception)
        {
            // Ignore known exceptions that can happen during shutdown
            if (!exception.Message.Contains("repaint"))
            {
                throw;  // Rethrow unknow exceptions
            }
        }

        // Prevent extra whitespace at the start of the edit box
        if (GUI.changed)
        {
            this.inputText = this.inputText.TrimStart();
        }
    }

    /// <summary>
    /// Called when the history needs ot be navigated (e.g., when the up-arrow, down-arrow or escape key is pressed).
    /// </summary>
    /// <param name="useTrueForOlderOrFalseForNewerOrNullForUndo">Specify how to navigate the history: <c>true</c> for older history, <c>false</c> for newer history, or <c>null</c> for to undo some history editing or navigation.</param>
    private void OnNavigateHistory(
        bool? useTrueForOlderOrFalseForNewerOrNullForUndo)
    {
        // Save the current input text into its slot in the cache
        this.inputTextCache[this.currentHistoryIndex] = this.inputText;
        if (useTrueForOlderOrFalseForNewerOrNullForUndo.HasValue)
        {
            if (useTrueForOlderOrFalseForNewerOrNullForUndo.Value)
            {
                if (--this.currentHistoryIndex < 0)
                {
                    this.currentHistoryIndex += this.inputTextHistory.Count;
                }
            }
            else
            {
                if (++this.currentHistoryIndex >= this.inputTextHistory.Count)
                {
                    this.currentHistoryIndex -= this.inputTextHistory.Count;
                }
            }
        }
        else
        {
            // For the escape, "undo" in steps depending on the current state
            if (!string.Equals(
                    this.inputText,
                    this.inputTextHistory[this.currentHistoryIndex],
                    StringComparison.Ordinal))
            {
                // Revert the text to the unmodified version
                this.inputTextCache[this.currentHistoryIndex] = EmptyCacheSlot;
            }
            else
            {
                // Go to the latest history item
                this.currentHistoryIndex = this.inputTextHistory.Count - 1;
            }
        }

        // Load the current input text from the historic slot
        this.inputText = this.inputTextCache[this.currentHistoryIndex];
        if (this.inputText == EmptyCacheSlot)
        {
            this.inputText = this.inputTextHistory[this.currentHistoryIndex];
        }
    }

    /// <summary>
    /// Called when the GUI needs to be drawn.
    /// </summary>
    private void OnDrawGUI()
    {
        // Draw the GUI
        const int SpacePixelCount = 4;
        bool showAutoSelectToggle = this.IsEditorAvailable();
        bool showBlankPrompt = string.IsNullOrEmpty(this.promptText) || (this.inputHandler == null);
        float splitHeight = (float)Screen.height * (1f - ((Mathf.Min(0f, this.topMargin) + Mathf.Min(0f, this.bottomMargin)) / 100f));
        Rect areaRect = new Rect(
            Screen.width * (this.leftMargin / 100f),
            Screen.height * (this.topMargin / 100f),
            (float)Screen.width * (1f - ((this.leftMargin + this.rightMargin) / 100f)),
            (float)Screen.height * (1f - ((this.topMargin + this.bottomMargin) / 100f)));
        GUILayout.BeginArea(areaRect);
        GUILayout.BeginVertical(
            GUILayout.MinHeight(60f),
            GUILayout.MaxHeight((splitHeight * (this.splitterFraction / 100f)) - (SpacePixelCount >> 1)),
            GUILayout.Width(areaRect.width));
        GUILayout.FlexibleSpace();
        this.outputScrollPosition =
            GUILayout.BeginScrollView(this.outputScrollPosition);
        if (this.showInteractiveGUI && this.showOutputText)
        {
            // Ignore changes to text to make it read-only
            GUILayout.TextArea(this.outputText, this.outputText.Length);
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUILayout.Space(SpacePixelCount);
        GUILayout.BeginHorizontal(
            GUILayout.MinHeight(Mathf.Max(90f, ((splitHeight * ((100f - this.splitterFraction) / 100f)) - (SpacePixelCount >> 1)))),
            GUILayout.Width(areaRect.width));
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        this.showInteractiveGUI = GUILayout.Toggle(this.showInteractiveGUI, new GUIContent(showBlankPrompt ? PromptBlank : this.promptText, (this.showInteractiveGUI ? "Click to hide the C# interpreter GUI" : "Click to show the C# interpreter GUI")), "Button");
        GUILayout.EndHorizontal();
        if (this.showInteractiveGUI)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(string.Empty, "Click to clear the output text window"), "Toggle"))
            {
                this.ClearOutput();
            }

            this.showOutputText = GUILayout.Toggle(this.showOutputText, new GUIContent(string.Empty, (this.showOutputText ? "Click to hide the output text window" : "Click to show the output text window")));
            GUILayout.EndHorizontal();
            if (showAutoSelectToggle)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                this.showOutputAsEditorSelection = GUILayout.Toggle(this.showOutputAsEditorSelection, new GUIContent(string.Empty, (this.showOutputAsEditorSelection ? "Click to disable automatic selection of results in the editor" : "Click to enable automatic selection of results in the editor")));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            this.showTooltipText = GUILayout.Toggle(this.showTooltipText, new GUIContent(string.Empty, (this.showTooltipText ? "Click to hide the tooltip text bar" : "Click to display the tooltip text bar")));
            GUILayout.EndHorizontal();
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
        GUILayout.BeginVertical();
        this.inputScrollPosition = GUILayout.BeginScrollView(
            this.inputScrollPosition,
            GUILayout.MaxWidth(areaRect.width - this.toolboxWidth));
        if (this.showInteractiveGUI)
        {
            GUI.SetNextControlName(InputTextBoxName);
            this.inputText = GUILayout.TextArea(this.inputText);
        }

        GUILayout.EndScrollView();
        if (this.showInteractiveGUI && this.showTooltipText)
        {
            GUILayout.Label(GUI.tooltip, "TextField");
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    /// <summary>
    /// Called when the input text need to be executed (e.g., when Enter is pressed).
    /// </summary>
    private void OnExecuteInput()
    {
        string inputText = this.inputText.Trim();
        this.inputText = string.Empty;
        if (inputText == string.Empty)
        {
            // Repeat the previous command, if any
            for (int index = this.inputTextHistory.Count - 1; index >= 0; index--)
            {
                if (!string.IsNullOrEmpty(this.inputTextHistory[index]))
                {
                    this.ExecuteCode(this.inputTextHistory[index]);
                    this.outputScrollPosition.y = Mathf.Infinity;
                    break;
                }
            }

            return;
        }

        // Move the text from the input to output console
        CSI.Interpreter.Console.Write(
            this.promptText + "  " + inputText + Environment.NewLine);
        this.inputScrollPosition = Vector2.zero;
        this.outputScrollPosition.y = Mathf.Infinity;

        // Update the input history
        this.inputTextCache.Clear();
        if ((this.inputTextHistory.Count > 0) ||
            (this.inputTextHistory[this.inputTextHistory.Count - 1] == string.Empty))
        {
            this.inputTextHistory[this.inputTextHistory.Count - 1] = inputText;
        }
        else
        {
            this.inputTextHistory.Add(inputText);
        }

        if (this.inputTextHistory.Count > this.maxHistorySize)
        {
            this.inputTextHistory.RemoveRange(
                0, (this.inputTextHistory.Count - this.maxHistorySize));
        }
        else if ((this.inputTextHistory.Count > 0) &&
            (this.inputTextHistory[0] == string.Empty))
        {
            this.inputTextHistory.RemoveAt(0);
        }

        this.currentHistoryIndex = this.inputTextHistory.Count;

        // Notify the async-handler of the keyboard input
        CSI.Interpreter.InputHandler inputHandler = this.inputHandler;
        this.inputHandler = null;
        inputHandler(inputText);
    }

    /// <summary>
    /// Called when metadata should be displayed (e.g., when Alt+F1 is pressed).
    /// </summary>
    private bool OnMetaRequest()
    {
        object lastResult = this.GetLastExecuteResult();
        if (lastResult == null)
        {
            return false;
        }

        string inputText = this.inputText.Trim();
        string memberNameFilter;
        int seachTextStartIndex;
        if (string.IsNullOrEmpty(inputText))
        {
            memberNameFilter = null;
            seachTextStartIndex = -1;
        }
        else
        {
            for (seachTextStartIndex = inputText.Length - 1;
                seachTextStartIndex >= 0;
                seachTextStartIndex--)
            {
                if ((!char.IsLetterOrDigit(inputText[seachTextStartIndex])) &&
                    (inputText[seachTextStartIndex] != '_'))
                {
                    break;
                }
            }

            seachTextStartIndex = seachTextStartIndex + 1;
            memberNameFilter = inputText.Substring(seachTextStartIndex);
        }

        string[] memberNames = CSI.Utils.GetMeta(lastResult, memberNameFilter);
        if ((memberNames == null) ||
            (memberNames.Length <= 0))
        {
            return false;
        }

        CSI.IConsole console = CSI.Interpreter.Console;
        string separationLine = new string('-', Math.Min(35, (1 + (console.GetLineWidth() >> 1)))) + Environment.NewLine;
        string memberNameForDetail = null;
        if (memberNames.Length == 1)
        {
            // Incorporate the single found entry's name into the 
            // input text, which provides some kind of auto-completion
            memberNameForDetail = memberNames[0];
            if (seachTextStartIndex < 0)
            {
                inputText += memberNameForDetail;
            }
            else
            {
                inputText = inputText.Substring(0, seachTextStartIndex) +
                    memberNameForDetail;
            }

            this.inputText = inputText;
        }
        else
        {
            // Look for the longest substring shared among results
            int matchTextStopIndex = 0;
            while (true)
            {
                if (matchTextStopIndex >= memberNames[0].Length)
                {
                    break;
                }

                char matchChar = memberNames[0][matchTextStopIndex];
                bool isDone = false;
                for (int memberIndex = memberNames.Length - 1; memberIndex > 0; memberIndex--)
                {
                    if ((matchTextStopIndex >= memberNames[memberIndex].Length) ||
                        (matchChar != memberNames[memberIndex][matchTextStopIndex]))
                    {
                        if (matchTextStopIndex == memberNames[memberIndex].Length)
                        {
                            memberNameForDetail = memberNames[memberIndex];
                        }

                        isDone = true;
                        break;
                    }
                }

                if (isDone)
                {
                    break;
                }

                matchTextStopIndex++;
            }

            matchTextStopIndex--;
            if ((matchTextStopIndex >= 0) &&
                ((seachTextStartIndex < 0) ||
                    (matchTextStopIndex >= (inputText.Length - seachTextStartIndex))))
            {
                string commonText = memberNames[0].Substring(0, matchTextStopIndex + 1);
                if (seachTextStartIndex < 0)
                {
                    inputText += commonText;
                }
                else
                {
                    inputText =
                        inputText.Substring(0, seachTextStartIndex) + commonText;
                }

                if (!string.Equals(
                        commonText,
                        memberNameForDetail,
                        StringComparison.Ordinal))
                {
                    memberNameForDetail = null;
                }

                this.inputText = inputText;
            }
            else
            {
                memberNameForDetail = null;
            }

            console.Write(separationLine);
            foreach (string memberName in memberNames)
            {
                console.Write(memberName);
                console.Write(" ");
            }

            console.Write(Environment.NewLine);
        }

        if (!string.IsNullOrEmpty(memberNameForDetail))
        {
            console.Write(separationLine);
            try
            {
                CSI.Utils.MInfo(lastResult, memberNameForDetail);
            }
            catch
            {
                // Ignore exceptions while trying to auto-complete
            }
        }

        this.outputScrollPosition.y = Mathf.Infinity;
        return true;
    }

    private void PromptForInput(string prompt)
    {
        this.promptText = prompt;
        CSI.Interpreter.Console.ReadLineAsync(this.ExecuteCode);
    }

    #region IConsole Members

    void CSI.IConsole.ReadLineAsync(CSI.Interpreter.InputHandler callback)
    {
        CSI.Interpreter.InputHandler inputHandler = this.inputHandler;
        this.inputHandler = callback;  // Register new callback
        if (inputHandler != null)
        {
            inputHandler(null);  // Cancel previous handler
        }
    }

    string CSI.IConsole.Write(string s)
    {
        this.outputStringBuilder.Append(s);
        this.EnforceParameterLimits();
        if (this.outputStringBuilder.Length > this.maxOutputSize)
        {
            this.outputText = this.outputStringBuilder.ToString();
            this.outputStringBuilder.Remove(0, (this.outputStringBuilder.Length - (this.maxOutputSize >> 1)));
        }

        this.outputText = this.outputStringBuilder.ToString().TrimEnd();
        return s;
    }

    int CSI.IConsole.GetLineWidth()
    {
        return this.maxOutputLineWidth;
    }

    int CSI.IConsole.GetMaxLines()
    {
        return this.maxOutputLineCount;
    }

    #endregion

    /// <summary>
    /// Performs initialization of the C# compiler.
    /// </summary>
    /// <remarks>
    /// Most of this method's code is a hack as a workaround for the wrong GAC path and compiler search logic that is baked into Unity.
    /// </remarks>
    private static class InitializeCompilerForUnity3D
    {
        private static bool didRunOnce;

        public static void RunOnce()
        {
            if (didRunOnce)
            {
                return;
            }

            try
            {
                didRunOnce = true;
                Type monoCompilerType = null;
                foreach (Type type in
                    typeof(Microsoft.CSharp.CSharpCodeProvider).Assembly.GetTypes())
                {
                    if (type.FullName == "Mono.CSharp.CSharpCodeCompiler")
                    {
                        monoCompilerType = type;
                        break;
                    }
                }

                if (monoCompilerType == null)
                {
                    Debug.LogWarning(
                        "The C# compiler may not yet work on this version of " +
                        "Unity!  Please provide feedback about test results.");
                    return;
                }

                if (Path.DirectorySeparatorChar != '\\')
                {
                    Debug.LogWarning(
                        "The C# compiler may not yet work on this operating " +
                        "system!  Please provide feedback about test results.");
                    return;
                }

                // This begins a hack to bypass the static constructor of 
                // Mono.CSharp.CSharpCodeCompiler and initialize that data 
                // type in an alternative way for Unity 3D (v.2.6.1); it 
                // attempts to locate the Mono and MCS executables correctly
                const BindingFlags StaticNonPublicBindingFlags =
                        BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags StaticPublicBindingFlags =
                        BindingFlags.Public | BindingFlags.Static;
                const string EnvVarMonoPath = "MONO_PATH";
                const string EnvVarCsiCompPath = "CSI_COMPILER_PATH";
#if UNITY_2_6
                const string CompilerDirectoryName = "MonoCompiler.framework";
                const string RuntimeDirectoryName = CompilerDirectoryName;
#else  // i.e., 3.0 and greater
                const string CompilerDirectoryName = "Mono/lib/mono/2.0";
                const string RuntimeDirectoryName = "Mono/bin";
#endif
                const string DataRootProgramGuessPath = "Unity/Editor/Data/";
                const string CompilerProgramGuessPath = DataRootProgramGuessPath + CompilerDirectoryName;
                const string RuntimeProgramGuessPath = DataRootProgramGuessPath + RuntimeDirectoryName;
                string mcsPath, monoPath;
                string envValMonoPath =
                    Environment.GetEnvironmentVariable(EnvVarMonoPath);
                string[] envSplitMonoPath =
                    string.IsNullOrEmpty(envValMonoPath) ? new string[0] :
                        envValMonoPath.Split(Path.PathSeparator);
                FieldInfo mcsPathField = monoCompilerType.GetField(
                    "windowsMcsPath", StaticNonPublicBindingFlags);
                FieldInfo monoPathField = monoCompilerType.GetField(
                    "windowsMonoPath", StaticNonPublicBindingFlags);
                FieldInfo directorySeparatorCharField = typeof(Path).
                    GetField("DirectorySeparatorChar", StaticPublicBindingFlags);
                if ((mcsPathField == null) ||
                    (monoPathField == null) ||
                    (directorySeparatorCharField == null))
                {
                    Debug.LogWarning(
                        "The C# compiler may not yet work on this version " +
                        "of Mono, since some of the expected fields were not " +
                        "found!  Please provide feedback about test results.");
                    return;
                }

                // To bypass the problematic initialization 
                // code, pretend we're running of Unix
                directorySeparatorCharField.SetValue(null, '/');
                try
                {
                    // Now access a static member to ensure that 
                    // the static constructor has been executed
                    mcsPath = (string)mcsPathField.GetValue(null);
                    monoPath = (string)monoPathField.GetValue(null);
                }
                finally
                {
                    // Restore the bypass mechanism made earlier
                    directorySeparatorCharField.SetValue(null, '\\');
                }

                // Attempt to locate the MCS and Mono executables for Unity
                if ((!File.Exists(mcsPath)) ||
                    (!File.Exists(monoPath)))
                {
                    string compilerPath =
                        GetFullPathOfAssembly(Assembly.GetEntryAssembly()) ?? // Unity 2.6.1
                        GetFullPathOfAssembly(typeof(System.Uri).Assembly);  // Unity 3.x
                    string runtimePath = compilerPath;
                    if (!string.IsNullOrEmpty(compilerPath))
                    {
                        compilerPath = Path.GetFullPath(compilerPath);
                        do
                        {
                            compilerPath = Path.GetDirectoryName(compilerPath);
                            if (compilerPath == null)
                            {
                                break;
                            }

                            if (Directory.Exists(Path.Combine(
                                    compilerPath, CompilerDirectoryName)))
                            {
                                compilerPath = Path.Combine(
                                    compilerPath, CompilerDirectoryName);
                                break;
                            }
                        }
                        while (Directory.Exists(compilerPath));
                    }

                    if (string.Equals(
                            CompilerDirectoryName, 
                            RuntimeDirectoryName, 
                            StringComparison.Ordinal))
                    {
                        runtimePath = null;
                    }
                    else if (!string.IsNullOrEmpty(runtimePath))
                    {
                        runtimePath = Path.GetFullPath(runtimePath);
                        do
                        {
                            runtimePath = Path.GetDirectoryName(runtimePath);
                            if (runtimePath == null)
                            {
                                break;
                            }

                            if (Directory.Exists(Path.Combine(
                                    runtimePath, RuntimeDirectoryName)))
                            {
                                runtimePath = Path.Combine(
                                    runtimePath, RuntimeDirectoryName);
                                break;
                            }
                        }
                        while (Directory.Exists(runtimePath));
                    }

                    string envValCsiCompPath =
                        Environment.GetEnvironmentVariable(EnvVarCsiCompPath);
                    string[] envSplitCsiCompPath =
                        string.IsNullOrEmpty(envValCsiCompPath) ? new string[0] :
                            envValCsiCompPath.Split(Path.PathSeparator);
                    List<string> searchPaths = new List<string>();
                    searchPaths.Add(Directory.GetCurrentDirectory());
                    searchPaths.AddRange(envSplitCsiCompPath);
                    searchPaths.Add(compilerPath);
                    searchPaths.Add(runtimePath);
                    searchPaths.AddRange(envSplitMonoPath);
                    foreach (string programFilesRoot in new string[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                        Environment.GetEnvironmentVariable("ProgramFiles"),
                        Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                    })
                    {
                        if (!string.IsNullOrEmpty(programFilesRoot))
                        {
                            searchPaths.Add(Path.Combine(
                                programFilesRoot, CompilerProgramGuessPath));
                            searchPaths.Add(Path.Combine(
                                programFilesRoot, RuntimeProgramGuessPath));
                        }
                    }

                    if (!File.Exists(mcsPath))
                    {
                        mcsPath = SearchForFullPath("gmcs", searchPaths, ".exe", ".bat");  // Must be EXE for Unity 3.x
                    }

                    if (!File.Exists(monoPath))
                    {
                        monoPath = SearchForFullPath("mono", searchPaths, ".bat", ".exe");
                    }

                    if ((!File.Exists(mcsPath)) ||
                        (!File.Exists(monoPath)))
                    {
                        // Attempt to revert to calling the bypassed static constructor
                        ConstructorInfo staticConstructor =
                            monoCompilerType.TypeInitializer;
                        if (staticConstructor == null)
                        {
                            Debug.LogWarning(
                                "The C# compiler may not yet work on " +
                                "this version of Mono, since some of " +
                                "the paths are still missing!  Please " +
                                "provide feedback about test results.");
                        }
                        else
                        {
                            staticConstructor.Invoke(null, null);
                            string alternativePath =
                                (string)mcsPathField.GetValue(null);
                            if (File.Exists(alternativePath))
                            {
                                mcsPath = alternativePath;
                            }

                            alternativePath =
                                (string)monoPathField.GetValue(null);
                            if (File.Exists(monoPath))
                            {
                                monoPath = alternativePath;
                            }
                        }
                    }

                    // Keep any valid paths that were located
                    if (File.Exists(mcsPath))
                    {
                        mcsPathField.SetValue(null, mcsPath);
                    }

                    if (File.Exists(monoPath))
                    {
                        monoPathField.SetValue(null, monoPath);
                    }
                }

#if UNITY_2_6
                // Ensure that the Mono-path environment-variable exists, 
                // since the C# compiler needs this to find mscorlib.dll.
                // NOTE: Since Unity 3.0.0, this is apparently no longer required.
                if ((envSplitMonoPath.Length <= 0) ||
                    (/* single path that doesn't exist */ (envSplitMonoPath.Length == 1) &&
                        (!Directory.Exists(envSplitMonoPath[0]))))
                {
                    if (File.Exists(mcsPath))
                    {
                        envValMonoPath = Path.GetDirectoryName(mcsPath);
                    }

                    if ((!Directory.Exists(envValMonoPath)) &&
                        File.Exists(monoPath))
                    {
                        envValMonoPath = Path.GetDirectoryName(monoPath);
                    }

                    if (!Directory.Exists(envValMonoPath))
                    {
                        envValMonoPath = Path.GetDirectoryName(
                            GetFullPathOfAssembly(typeof(int).Assembly));
                    }

                    if (Directory.Exists(envValMonoPath))
                    {
                        Environment.SetEnvironmentVariable(
                            EnvVarMonoPath, envValMonoPath);
                    }
                }
#endif
            }
            catch (IOException)
            {
                // Probably running in the web player without required rights
            }
        }

        private static string SearchForFullPath(
            string fileNameWithoutExtension,
            IEnumerable<string> searchPaths,
            params string[] fileExtensions)
        {
            if (searchPaths == null)
            {
                return null;
            }

            string applicationPath;
            foreach (string searchPath in searchPaths)
            {
                if (string.IsNullOrEmpty(searchPath))
                {
                    continue;
                }

                applicationPath = Path.Combine(
                    searchPath, fileNameWithoutExtension);
                if ((fileExtensions == null) ||
                    (fileExtensions.Length <= 0))
                {
                    if (File.Exists(applicationPath))
                    {
                        return applicationPath;
                    }
                }
                else
                {
                    foreach (string fileExtension in fileExtensions)
                    {
                        applicationPath = Path.ChangeExtension(
                            applicationPath, fileExtension);
                        if (File.Exists(applicationPath))
                        {
                            return applicationPath;
                        }
                    }
                }
            }

            return null;
        }
    }
}
