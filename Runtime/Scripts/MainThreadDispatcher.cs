using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace LiveLink
{
    /// <summary>
    /// A <see cref="SynchronizationContext"/> that posts continuations back to the Unity
    /// main thread via <see cref="MainThreadDispatcher"/>. Installing this context ensures
    /// that <c>async/await</c> continuations inside methods that start on the main thread
    /// (e.g. those dispatched via <see cref="MainThreadDispatcher.Enqueue"/>) continue
    /// executing on the main thread after each <c>await</c> point.
    /// </summary>
    internal sealed class UnityMainThreadSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object state)
        {
            MainThreadDispatcher.Enqueue(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            if (MainThreadDispatcher.IsMainThread)
            {
                d(state);
            }
            else
            {
                using var done = new ManualResetEventSlim(false);
                MainThreadDispatcher.Enqueue(() => { d(state); done.Set(); });
                done.Wait();
            }
        }
    }

    /// <summary>
    /// Dispatches actions from background threads to the Unity main thread.
    /// Unity API calls must be executed on the main thread to avoid crashes.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly ConcurrentQueue<Action> _executionQueue = new ConcurrentQueue<Action>();
        private static volatile bool _queued = false;
        private static int _mainThreadId;

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
                    _instance = FindExistingDispatcher();
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
            if (_mainThreadId == 0)
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            var _ = Instance;
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        private static MainThreadDispatcher FindExistingDispatcher()
        {
#if UNITY_2022_2_OR_NEWER
            return FindAnyObjectByType<MainThreadDispatcher>();
#else
            return FindObjectOfType<MainThreadDispatcher>();
#endif
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
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            DontDestroyOnLoad(gameObject);

            // Install a SynchronizationContext so that async/await continuations
            // inside methods dispatched to the main thread continue on the main thread.
            SynchronizationContext.SetSynchronizationContext(new UnityMainThreadSynchronizationContext());
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
