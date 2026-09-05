using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.DocumentParts;
using ABSwitchBack.Core;

using NavisApp = Autodesk.Navisworks.Api.Application;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// Turns Navisworks selection changes into "the user just triggered on this element".
    ///
    /// There is no mouse hook: a global one made the whole application sluggish, because
    /// Windows routed every mouse event in the system through the Navisworks UI thread
    /// before the input could proceed. Ctrl+click always toggles the clicked item in or
    /// out of the selection, so the change events are enough - and the snapshot is taken
    /// only while the trigger modifiers are actually held, so ordinary picking and
    /// navigation cost nothing at all.
    /// </summary>
    internal sealed class SelectionWatcher : IDisposable
    {
        /// <summary>
        /// Above this many selected items the before/after snapshot is skipped. Hashing
        /// ModelItems crosses into native code, so an unbounded snapshot on a Select All
        /// would be felt by the user.
        /// </summary>
        public const int MaxTrackedSelection = 10000;

        private readonly Action<ModelItem> _onTriggered;
        private readonly Action<int> _onSelectionTooLarge;

        private DocumentCurrentSelection _watched;
        private HashSet<ModelItem> _snapshot;
        private bool _snapshotSkipped;
        private bool _disposed;

        /// <param name="onTriggered">The element the user triggered on. Called on the UI thread.</param>
        /// <param name="onSelectionTooLarge">
        /// The gesture was used while too many items were selected to diff. Receives the cap,
        /// so the caller can explain rather than silently do nothing.
        /// </param>
        public SelectionWatcher(Action<ModelItem> onTriggered, Action<int> onSelectionTooLarge)
        {
            _onTriggered = onTriggered;
            _onSelectionTooLarge = onSelectionTooLarge;
        }

        /// <summary>Whether the gesture is armed at all.</summary>
        public bool Enabled { get; set; }

        /// <summary>Modifiers that must be held for a selection change to count.</summary>
        public TriggerModifiers Trigger { get; set; }

        public bool IsAttached { get { return _watched != null; } }

        /// <summary>
        /// Subscribes to the active document's selection. Safe to call repeatedly; the
        /// document object changes when a different model is opened.
        /// </summary>
        public void AttachToActiveDocument()
        {
            if (_disposed) return;

            try
            {
                Document doc = NavisApp.ActiveDocument;
                if (doc == null) return;

                DocumentCurrentSelection selection = doc.CurrentSelection;
                if (ReferenceEquals(selection, _watched)) return;

                Detach();

                selection.Changing += OnSelectionChanging;
                selection.Changed += OnSelectionChanged;
                _watched = selection;
                ClearSnapshot();

                Log.Info("Selection tracking active.");
            }
            catch (Exception ex)
            {
                Log.Warn("Could not subscribe to selection changes: " + ex.Message);
            }
        }

        public void Detach()
        {
            try
            {
                if (_watched != null)
                {
                    _watched.Changing -= OnSelectionChanging;
                    _watched.Changed -= OnSelectionChanged;
                }
            }
            catch { }

            _watched = null;
            ClearSnapshot();
        }

        private void ClearSnapshot()
        {
            _snapshot = null;
            _snapshotSkipped = false;
        }

        /// <summary>
        /// Fires before the selection changes. The snapshot is the only way to know what
        /// the selection looked like beforehand, and it is taken only when the gesture is
        /// actually in progress.
        /// </summary>
        private void OnSelectionChanging(object sender, EventArgs e)
        {
            ClearSnapshot();
            if (!Enabled) return;

            try
            {
                if (!TriggerGesture.AreHeld(Trigger)) return;

                Document doc = NavisApp.ActiveDocument;
                if (doc == null || doc.IsClear) return;

                ModelItemCollection current = doc.CurrentSelection.SelectedItems;
                if (current == null) return;

                if (current.Count > MaxTrackedSelection)
                {
                    _snapshotSkipped = true;
                    return;
                }

                _snapshot = new HashSet<ModelItem>(current);
            }
            catch (Exception ex)
            {
                Log.Warn("Selection snapshot failed: " + ex.Message);
                _snapshot = null;
            }
        }

        /// <summary>Fires after the selection changed. Diffs against the snapshot.</summary>
        private void OnSelectionChanged(object sender, EventArgs e)
        {
            HashSet<ModelItem> before = _snapshot;
            bool skipped = _snapshotSkipped;
            ClearSnapshot();

            if (!Enabled) return;

            try
            {
                // The modifiers must still be down: this rules out a change that merely
                // happened to follow a snapshot.
                if (!TriggerGesture.AreHeld(Trigger)) return;

                if (skipped)
                {
                    if (_onSelectionTooLarge != null) _onSelectionTooLarge(MaxTrackedSelection);
                    return;
                }

                if (before == null) return;

                Document doc = NavisApp.ActiveDocument;
                if (doc == null || doc.IsClear) return;

                ModelItemCollection current = doc.CurrentSelection.SelectedItems;
                if (current == null || current.Count > MaxTrackedSelection) return;

                ModelItem clicked = SelectionDiff.FindSingleChange(before, new HashSet<ModelItem>(current));
                if (clicked == null) return;

                if (_onTriggered != null) _onTriggered(clicked);
            }
            catch (Exception ex)
            {
                Log.Warn("Selection tracking failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
        }
    }
}
