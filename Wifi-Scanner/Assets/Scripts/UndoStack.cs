using System;
using System.Collections.Generic;
using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// Global undo for destructive actions (element deleted, list cleared).
    /// Before the action, the call site pushes a label plus a restore closure
    /// that captures a deep snapshot of the affected data; undoing pops the
    /// most recent entry and replays the snapshot through the tools' Load APIs
    /// (LoadRooms/LoadLines/LoadNodes/LoadNotes/LoadMeasurements/LoadSamples).
    ///
    /// Push at USER call sites only — never inside the Load/Clear paths that a
    /// restore itself runs through, or undoing would push bogus entries.
    /// The "↺" button on the ToolPalette pops the stack;
    /// <see cref="SessionLoader.LoadCurrent"/> clears it (entries from before
    /// a session swap would restore foreign data).
    /// </summary>
    public static class UndoStack
    {
        private class Entry
        {
            public string label;
            public Action restore;
        }

        private const int MaxEntries = 10;
        private static readonly List<Entry> _entries = new List<Entry>();

        public static bool CanUndo => _entries.Count > 0;

        /// <summary>Label of the entry the next undo would restore ("" if none).</summary>
        public static string TopLabel =>
            _entries.Count > 0 ? _entries[_entries.Count - 1].label : "";

        public static void Push(string label, Action restore)
        {
            _entries.Add(new Entry { label = label, restore = restore });
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        public static bool UndoLast()
        {
            if (_entries.Count == 0) return false;
            var entry = _entries[_entries.Count - 1];
            _entries.RemoveAt(_entries.Count - 1);
            entry.restore();
            Debug.Log($"[UndoStack] Rückgängig: {entry.label} " +
                      $"({_entries.Count} weitere).");
            return true;
        }

        /// <summary>Drops all entries (called on session load).</summary>
        public static void Clear() => _entries.Clear();

        // ---- Snapshot helpers ----

        /// <summary>Deep copy of the traced rooms (corners + openings), safe to
        /// replay through RoomTracer.LoadRooms after the originals mutated.</summary>
        public static List<RoomOutline> CloneRooms(IReadOnlyList<RoomOutline> rooms)
        {
            var copy = new List<RoomOutline>(rooms.Count);
            foreach (var room in rooms)
            {
                var c = new RoomOutline
                {
                    floorY = room.floorY,
                    height = room.height,
                    cornersXZ = new List<Vector2>(room.cornersXZ),
                };
                if (room.openings != null)
                    foreach (var op in room.openings)
                        c.openings.Add(new WallOpening
                        {
                            edgeIndex = op.edgeIndex,
                            startAlong = op.startAlong,
                            endAlong = op.endAlong,
                            bottomY = op.bottomY,
                            topY = op.topY,
                            isDoor = op.isDoor,
                            hingeAlong = op.hingeAlong,
                        });
                copy.Add(c);
            }
            return copy;
        }

        /// <summary>Deep copy of the drill lines.</summary>
        public static List<DrillLine> CloneLines(IReadOnlyList<DrillLine> lines)
        {
            var copy = new List<DrillLine>(lines.Count);
            foreach (var l in lines)
                copy.Add(new DrillLine
                {
                    entry = l.entry,
                    direction = l.direction,
                    depth = l.depth,
                });
            return copy;
        }

        /// <summary>Deep copy of the spatial notes.</summary>
        public static List<SpatialNote> CloneNotes(IReadOnlyList<SpatialNote> notes)
        {
            var copy = new List<SpatialNote>(notes.Count);
            foreach (var n in notes)
                copy.Add(new SpatialNote
                {
                    posX = n.posX, posY = n.posY, posZ = n.posZ,
                    type = n.type, text = n.text,
                });
            return copy;
        }

        /// <summary>Deep copy of the measurements.</summary>
        public static List<Measurement> CloneMeasurements(IReadOnlyList<Measurement> ms)
        {
            var copy = new List<Measurement>(ms.Count);
            foreach (var m in ms)
                copy.Add(new Measurement
                {
                    ax = m.ax, ay = m.ay, az = m.az,
                    bx = m.bx, by = m.by, bz = m.bz,
                    meters = m.meters,
                });
            return copy;
        }
    }
}
