using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// MonoBehaviour singleton base class. Extend this class to make a singleton component.
/// </summary>
/// <typeparam name="T">The singleton type that inherits from MonoSingleton.</typeparam>
/// <remarks>
/// Example usage:
/// <code>
/// public class Foo : MonoSingleton&lt;Foo&gt;
/// {
///     public override void Init()
///     {
///         // Initialize your singleton here
///     }
/// }
/// </code>
/// To get the instance of Foo class, use <c>Foo.instance</c>.
/// Override the <c>Init()</c> method instead of using <c>Awake()</c>.
/// </remarks>
public abstract class MonoSingleton<T> : MonoBehaviour where T : MonoSingleton<T>
{
    private static T m_Instance = null;

    /// <summary>
    /// Gets the singleton instance. Creates a temporary instance if none exists.
    /// </summary>
    public static T instance
    {
        get
        {
            // Instance required for the first time, we look for it
            if (m_Instance == null)
            {
                m_Instance = GameObject.FindObjectOfType(typeof(T)) as T;

                // Object not found, we create a temporary one
                if (m_Instance == null)
                {
                    Debug.LogWarning("No instance of " + typeof(T).ToString() + ", a temporary one is created.");

                    isTemporaryInstance = true;
                    m_Instance = new GameObject("Temp Instance of " + typeof(T).ToString(), typeof(T)).GetComponent<T>();

                    // Problem during the creation, this should not happen
                    if (m_Instance == null)
                    {
                        Debug.LogError("Problem during the creation of " + typeof(T).ToString());
                    }
                }

                if (!_isInitialized)
                {
                    _isInitialized = true;
                    m_Instance.Init();
                }
            }
            return m_Instance;
        }
    }

    /// <summary>
    /// Gets whether this instance was temporarily created due to no instance existing in the scene.
    /// </summary>
    public static bool isTemporaryInstance { private set; get; }

    private static bool _isInitialized;

    /// <summary>
    /// Called when the component awakens. Handles singleton initialization.
    /// </summary>
    /// <remarks>
    /// If no other MonoBehaviour requests the instance in an Awake function
    /// executing before this one, no need to search for the object.
    /// </remarks>
    private void Awake()
    {
        if (m_Instance == null)
        {
            m_Instance = this as T;
        }
        else if (m_Instance != this)
        {
            Debug.LogError("Another instance of " + GetType() + " already exists! Destroying self...");
            DestroyImmediate(this);
            return;
        }

        if (!_isInitialized)
        {
            DontDestroyOnLoad(gameObject);
            _isInitialized = true;
            m_Instance.Init();
        }
    }

    /// <summary>
    /// Called when the singleton instance is first used.
    /// Put all initializations you need here, as you would do in Awake.
    /// </summary>
    public virtual void Init() { }

    /// <summary>
    /// Ensures the instance isn't referenced anymore when the application quits.
    /// </summary>
    private void OnApplicationQuit()
    {
        m_Instance = null;
    }
}
