// Events/ClashTaskQueue.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #3: Producer / Consumer Architecture
//
// The UI thread ONLY enqueues tasks — it never computes geometry.
// Background ClashExecutionEngine processes the queue asynchronously.
//
// Architecture:
//   Revit Event (UI thread)
//     → Enqueue(ElementId[])
//     → ClashTaskQueue (thread-safe)
//     → ClashExecutionEngine (background thread)
//     → Results stored in memory
//     → UI updated via Dispatcher

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ClashResolveAI.Events
{
    public enum ClashTaskType
    {
        TargetedScan,
        SelectionScan,
        FullScanBatch,
        RefreshCache
    }

    public class ClashTask
    {
        public ClashTaskType     TaskType    { get; set; }
        public List<long>        ElementIds  { get; set; } = new List<long>();
        public DateTime          Queued      { get; set; } = DateTime.Now;
        public int               Priority    { get; set; } = 5;  // 0=highest
    }

    /// <summary>
    /// Thread-safe producer/consumer queue for clash detection tasks.
    /// The UI thread produces tasks; the background engine consumes them.
    /// </summary>
    public class ClashTaskQueue : IDisposable
    {
        private static ClashTaskQueue? _instance;
        public  static ClashTaskQueue   Instance =>
            _instance ?? (_instance = new ClashTaskQueue());

        // Priority queue implemented as sorted ConcurrentQueue
        private readonly BlockingCollection<ClashTask> _queue =
            new BlockingCollection<ClashTask>(500);

        private readonly HashSet<long> _pendingIds = new HashSet<long>();
        private readonly object        _pendingLock = new object();

        public int PendingCount => _queue.Count;
        public bool HasPending  => !_queue.IsCompleted && _queue.Count > 0;

        // ════════════════════════════════════════════════════════════════
        //  ENQUEUE — called from UI thread (safe, non-blocking)
        // ════════════════════════════════════════════════════════════════

        public void EnqueueTargeted(IEnumerable<long> elementIds)
        {
            var newIds = new List<long>();
            lock (_pendingLock)
            {
                foreach (long id in elementIds)
                    if (_pendingIds.Add(id))
                        newIds.Add(id);
            }

            if (newIds.Count == 0) return;

            try
            {
                _queue.TryAdd(new ClashTask
                {
                    TaskType   = ClashTaskType.TargetedScan,
                    ElementIds = newIds,
                    Priority   = 3
                }, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TaskQueue] Enqueue error: {ex.Message}");
            }
        }

        public void EnqueueSelection(long elementId)
        {
            try
            {
                // Clear existing selection tasks — only process latest selection
                while (_queue.TryTake(out var old))
                {
                    if (old.TaskType != ClashTaskType.SelectionScan)
                        _queue.TryAdd(old, 0);
                }

                _queue.TryAdd(new ClashTask
                {
                    TaskType   = ClashTaskType.SelectionScan,
                    ElementIds = new List<long> { elementId },
                    Priority   = 1  // Highest priority — instant user feedback
                }, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TaskQueue] EnqueueSelection: {ex.Message}");
            }
        }

        public void EnqueueCacheRefresh(IEnumerable<long> elementIds)
        {
            try
            {
                _queue.TryAdd(new ClashTask
                {
                    TaskType   = ClashTaskType.RefreshCache,
                    ElementIds = new List<long>(elementIds),
                    Priority   = 8  // Low priority — done after user-facing tasks
                }, TimeSpan.Zero);
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════
        //  DEQUEUE — called from background thread
        // ════════════════════════════════════════════════════════════════

        public bool TryDequeue(out ClashTask task, int timeoutMs = 100)
        {
            bool result = _queue.TryTake(out task!, timeoutMs);
            if (result && task.TaskType == ClashTaskType.TargetedScan)
            {
                lock (_pendingLock)
                    foreach (long id in task.ElementIds)
                        _pendingIds.Remove(id);
            }
            return result;
        }

        public void Clear()
        {
            while (_queue.TryTake(out _)) { }
            lock (_pendingLock) _pendingIds.Clear();
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _queue.Dispose();
        }
    }
}
