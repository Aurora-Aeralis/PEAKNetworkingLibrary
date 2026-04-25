using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkingLibrary.Modules
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        static UnityMainThreadDispatcher? instance;
        static readonly object instanceLock = new();
        static readonly Queue<Action> queue = new Queue<Action>();

        public static UnityMainThreadDispatcher Instance()
        {
            if (instance != null) return instance;

            lock (instanceLock)
            {
                if (instance != null) return instance;

                var go = GameObject.Find("UnityMainThreadDispatcher");
                if (go == null)
                {
                    go = new GameObject("UnityMainThreadDispatcher");
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<UnityMainThreadDispatcher>();
                }
                else instance = go.GetComponent<UnityMainThreadDispatcher>() ?? go.AddComponent<UnityMainThreadDispatcher>();

                return instance;
            }
        }


        void Awake()
        {
            lock (instanceLock)
            {
                if (instance == null)
                {
                    instance = this;
                    DontDestroyOnLoad(gameObject);
                    return;
                }

                if (instance != this) Destroy(gameObject);
            }
        }

        public void Enqueue(Action a, float delaySeconds = 0f)
        {
            ArgumentNullException.ThrowIfNull(a);
            if (delaySeconds <= 0f)
            {
                lock (queue) queue.Enqueue(a);
            }
            else StartCoroutine(EnqueueDelayed(a, delaySeconds));
        }

        IEnumerator EnqueueDelayed(Action a, float d)
        {
            ArgumentNullException.ThrowIfNull(a);
            yield return new WaitForSeconds(d);
            lock (queue) queue.Enqueue(a);
        }

        void Update()
        {
            while (true)
            {
                Action a = null!;
                lock (queue)
                {
                    if (queue.Count > 0) a = queue.Dequeue();
                    else break;
                }
                try { a?.Invoke(); } catch (Exception ex) { Debug.LogError($"Dispatcher action error: {ex}"); }
            }
        }
    }
}
