using UnityEngine;

/// Be aware this will not prevent a non singleton constructor
///   such as `T myT = new T();`
/// To prevent that, add `protected T () {}` to your singleton class.
/// 
/// As a note, this is made as MonoBehaviour because we need Coroutines.
public class Singleton<T> : MonoBehaviour where T : MonoBehaviour {

    private static T _instance;
    private static object _lock = new object();
    private static bool applicationIsQuitting = false;

    public static T Instance {
        get {
            if (applicationIsQuitting) {
                G.Log.Debug("[Singleton] Instance '" + typeof(T) +
                "' already destroyed on application quit." +
                " Won't create again - returning null.");
                return null;
            }

            lock (_lock) {
                if (_instance == null) {
                    _instance = (T)FindObjectOfType(typeof(T));

                    if (FindObjectsOfType(typeof(T)).Length > 1) {
                        G.Log.Error("[Singleton] Something went really wrong " +
                        " - there should never be more than 1 singleton!" +
                        " Reopening the scene might fix it.");
                        return _instance;
                    }

                    if (_instance == null) {
                        GameObject singleton = new GameObject();
                        _instance = singleton.AddComponent<T>();
                        singleton.name = "(singleton) " + typeof(T).ToString();

                        if (Application.isPlaying) {
                            DontDestroyOnLoad(singleton);
                        }
                    }
                }

                return _instance;
            }
        }
    }

    /// When Unity quits, it destroys objects in a random order.
    /// In principle, a Singleton is only destroyed when application quits.
    /// If any script calls Instance after it have been destroyed, 
    ///   it will create a buggy ghost object that will stay on the Editor scene
    ///   even after stopping playing the Application. Really bad!
    /// So, this was made to be sure we're not creating that buggy ghost object.
    public void OnDestroy() {
        applicationIsQuitting = true;
    }

    public void Nop() {}
}
