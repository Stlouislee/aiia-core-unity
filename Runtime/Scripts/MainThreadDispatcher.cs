using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace LiveLink
{
    /// <summary>
    /// Dispatches actions from background threads to the Unity main thread.
    /// Unity API calls must be executed on the main thread to avoid crashes.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();
        private static volatile bool _queued = false;

        /// <summary>
        /// Gets the singleton instance of the MainThreadDispatcher.
        /// Creates one if it doesn't exist.
        /// </summary>
        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<MainThreadDispatcher>();
                    if (_instance == null)
                    {
                        var go = new GameObject("[LiveLink] MainThreadDispatcher");
                        _instance = go.AddComponent<MainThreadDispatcher>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Ensures the dispatcher exists in the scene.
        /// </summary>
        public static void Initialize()
        {
            var _ = Instance;
        }

        /// <summary>
        /// Enqueues an action to be executed on the main thread.
        /// Thread-safe method that can be called from any thread.
        /// </summary>
        /// <param name="action">The action to execute on the main thread.</param>
        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                Debug.LogWarning("[LiveLink] Attempted to enqueue null action.");
                return;
            }

            _executionQueue.Enqueue(action);
            _queued = true;
        }

        /// <summary>
        /// Enqueues an action with exception handling.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="errorContext">Context string for error logging.</param>
        public static void EnqueueSafe(Action action, string errorContext = "MainThreadDispatcher")
        {
            Enqueue(() =>
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiveLink] Error in {errorContext}: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            // Early exit if nothing is queued
            if (!_queued) return;

            // Process all queued actions
            int processedCount = 0;
            const int maxPerFrame = 100; // Prevent frame stalls

            while (processedCount < maxPerFrame && _executionQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LiveLink] Exception in dispatched action: {ex.Message}\n{ex.StackTrace}");
                }
                processedCount++;
            }

            // Check if queue is now empty
            if (_executionQueue.IsEmpty)
            {
                _queued = false;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Clears all pending actions from the queue.
        /// </summary>
        public static void ClearQueue()
        {
            while (_executionQueue.TryDequeue(out _)) { }
            _queued = false;
        }

        /// <summary>
        /// Gets the number of pending actions in the queue.
        /// </summary>
        public static int PendingCount => _executionQueue.Count;
    }
}
