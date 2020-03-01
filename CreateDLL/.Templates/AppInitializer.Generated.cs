using System;
using System.Reflection;
using System.Threading;
using JetBrains.Annotations;
using UnityEngine;

namespace Nordeus.Initialization
{
	/// <summary>
	/// After recompiling code Unity only calls methods marked with attribute UnityEditor.InitializeOnLoadMethod
	/// and immediately after that calles Enable on EditorWindows. When you enter play mode it will first call methods
	/// with the same attribute, then it will call Enable on EditorWindows and then it will call methods with attribute
	/// RuntimeInitializeOnLoadMethod. Of course in a build it will only execute RuntimeInitializeOnLoadMethod.
	///
	/// Since we are using a lot of subsystems in Editor method, especially with Flugen designer, we need init to be called
	/// in editor before EditorWindow.OnEnable. That is why it is called from both methods but in play mode we make sure it
	/// is still executed only once.
	///
	/// In case you want to create Unity GameObject on a scene within some initialization you would get this error when
	/// entering play mode if it were to be called from InitializeOnLoadMethod:
	/// 
	/// Some objects were not cleaned up when closing the scene. (Did you spawn new GameObjects from OnDestroy?)
	///
	/// So in case we are entering play mode such objects can only be created later from RuntimeInitializeOnLoadMethod.
	/// That is why we separated the initialization into two methods: InitNonSceneStuff and InitSceneStuff which is
	/// called from InitializeOnLoadMethod if you are in edit mode but if you are entering play mode it is only called
	/// from RuntimeInitializeOnLoadMethod later.
	///
	/// Those objects should usually be created with HideAndDontSave flag so they are not visible in the hierarchy and they
	/// are not saved with scene. The issues you can get if they are saved with the scene is that their MonoBehavior methods
	/// in a build will execute before RuntimeInitializeOnLoadMethod so they might try to access things that are not yet
	/// initialized. In the editor, since initialization is also done in edit mode that means this object will exist on the
	/// scene when you click play but in the editor InitializeOnLoadMethod will execute before methods on that object so
	/// everything will be initialized on time. The real issue is that when you are exiting from the play mode Unity will
	/// keep all static things untuched but will recreate the scene you were on before entering play mode thus destroying
	/// your hidden objects. That is why this class also calls InitSceneStuff when it detects you just exited from play mode
	/// so those objects can be recreated in that case.
	///
	/// In order for these objects to remain alive during Scene transitions also make sure to call Object.DontDestroyOnLoad
	/// on them or place them in a common parent gameobject that is marked with that method.
	/// </summary>
	public static partial class AppInitializer
	{
		public enum State
		{
			NotInitialized,
			Initializing,
			Initialized
		}

		#region Private Fields
		
		private static int? mainThreadId;

		private static State initializeNonSceneStuffStatus;
		private static State initializeSceneStuffStatus;

		#endregion

		#region Public API

		/// <summary>
		/// Use only when Application.isPlaying; do not use in Editor if not in play mode.
		/// </summary>
		public static bool AmIOnMainThread
		{
			get
			{
				if (mainThreadId == null) return false;
				return Thread.CurrentThread.ManagedThreadId == mainThreadId;
			}
		}

		#endregion

		#region Unity Callbacks

#if UNITY_EDITOR
		[UnityEditor.InitializeOnLoadMethod]
		[UsedImplicitly]
		private static void AppInitializerInit()
		{
			UnityEditor.EditorApplication.playModeStateChanged -= OnPlaymodeStateChanged;
			UnityEditor.EditorApplication.playModeStateChanged += OnPlaymodeStateChanged;

			// Call initialization methods here only if we are not entering Play Mode.
			// If we are about to enter Play Mode, they will be called by our UnityRuntimeInitializeOnLoad()
			// method (by RuntimeInitializeOnLoadMethod attribute).
			var enteringPlayMode = !UnityEditor.EditorApplication.isPlaying && UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode;
			if (enteringPlayMode) return; 

			mainThreadId = Thread.CurrentThread.ManagedThreadId;

			InitializeNonSceneStuff();
			InitializeSceneStuff();
		}

		private static void OnPlaymodeStateChanged(UnityEditor.PlayModeStateChange change)
		{
			// Reset main thread id if exiting from play mode in Editor.
			if (!UnityEditor.EditorApplication.isPlaying)
			{
				mainThreadId = null;
			}
		}
#endif

		// RuntimeInitializeOnLoadMethod for some reason doesn't work if it us under any kind of #if or #else
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		[UsedImplicitly]
		private static void UnityRuntimeInitializeOnLoad()
		{
			mainThreadId = Thread.CurrentThread.ManagedThreadId;
			InitializeNonSceneStuff();
			InitializeSceneStuff();
		}
		
		#endregion

		#region InitHelpers

		private static void InitializeNonSceneStuff()
		{
			if (initializeNonSceneStuffStatus != State.NotInitialized) return;
			initializeNonSceneStuffStatus = State.Initializing;
			try
			{
				DoNonSceneInits();
				
#if UNITY_EDITOR
				var editorAssembly = Assembly.Load("Assembly-CSharp-Editor");
				var editorInitializer = editorAssembly.GetType("Initialization.EditorAppInitializer");
				if (editorInitializer != null)
				{
					var initMethod = editorInitializer.GetMethod("InitEditStuff", BindingFlags.Static | BindingFlags.Public);
					initMethod?.Invoke(null, null);
				}
#endif
			}
			catch (Exception e)
			{
				const string exceptionMsg = "Exception in InitializeNonSceneStuff: ";
				Debug.LogError(exceptionMsg + e);
				CustomExceptionLogging(exceptionMsg, e);
			}

			initializeNonSceneStuffStatus = State.Initialized;
		}

		private static void InitializeSceneStuff()
		{
			if (initializeSceneStuffStatus != State.NotInitialized) return;
			initializeSceneStuffStatus = State.Initializing;
			try
			{
				DoSceneInits();
			}
			catch (Exception e)
			{
				const string exceptionMsg = "Exception in InitializeSceneStuff: ";
				Debug.LogError(exceptionMsg + e);
				CustomExceptionLogging(exceptionMsg, e);
			}
			initializeSceneStuffStatus = State.Initialized;
		}

		#endregion

		private static void DoNonSceneInits()
		{
		}

		private static void DoSceneInits()
		{
		}
	}
}
