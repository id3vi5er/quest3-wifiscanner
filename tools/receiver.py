#!/usr/bin/env python3
"""
WiFi-Heatmap live receiver + visualiser (Tkinter-GUI).

Listens for UDP JSON sample packets streamed from the Quest app
(NetworkStreamer.cs), logs them to CSV, and shows a full GUI:
  - 2D top-down (Unity X vs Z) with Punkte / Interpoliert / Raster modes
  - 3D scatter (X / Z / height Y)
  - floor-plan overlay: live from the Quest (rooms are streamed while you
    trace them) or from the app's <stem>_rooms.json export
    (toggleable, with wall-length labels, drawn in 2D and 3D)
  - live headset position + view direction (like the in-app minimap)
  - optional background image with cloud transform sliders
Colour: green = strong, red = weak (matches the in-headset heatmap).

Run on the PC whose IP you configured in the Quest app (panel field
"PC-IP für Live-Stream"; default port 9999).

By default every start writes a fresh timestamped log: wifi_log_YYYYMMDD_HHMMSS.csv

Usage:
    python receiver.py                      # listen + GUI, new log file
    python receiver.py --no-plot            # headless: only log to CSV
    python receiver.py --out run42.csv      # fixed log name instead of timestamped
    python receiver.py --out ''             # disable logging
    python receiver.py --load wifi_log_20260627_181500.csv   # view existing CSV
    python receiver.py --rooms wifi_..._rooms.json           # preload floor plan

Needs matplotlib (Tkinter ships with Python on Windows):
    pip install matplotlib

UDP packets are JSON objects. Samples:
    {"timestampMs":..., "posX":..., "posY":..., "posZ":...,
     "rssiDbm":..., "linkSpeedMbps":..., "frequencyMhz":...,
     "ssid":"...", "bssid":"..."}
Live extras (not logged to CSV):
    {"type":"pose", "posX":..., "posY":..., "posZ":..., "yawDeg":...}
    {"type":"rooms", "gridYawDegrees":..., "rooms":[
        {"cornersXZ":[{"x":..,"y":..},...], "floorY":.., "height":..}, ...]}
"""
import argparse
import csv
import json
import math
import socket
import sys
import threading
import time
from datetime import datetime

FIELDS = ["timestampMs", "posX", "posY", "posZ", "rssiDbm",
          "linkSpeedMbps", "frequencyMhz", "ssid", "bssid", "floor"]

FLOORPLAN_COLOR = "#0087cc"  # readable counterpart of the in-app light blue


class SampleStore:
    """Thread-safe store: sample cloud + live pose + live floor plan."""

    def __init__(self):
        self._lock = threading.Lock()
        self.xs, self.ys, self.zs, self.rssi = [], [], [], []
        self.count = 0
        self._pose = None          # (x, y, z, yawDeg, wall-time)
        self._rooms = []
        self.rooms_version = 0
        self._drills = []
        self.drills_version = 0
        self._apfix = None         # (x, y, z, ssid, rssi)
        self.apfix_version = 0
        self._pings = []           # [(x, y, z, latencyMs)]
        self.pings_version = 0
        self._notes = []           # [(x, y, z, type, text)]
        self.notes_version = 0

    def add(self, s):
        try:
            x = float(s.get("posX", 0.0))
            y = float(s.get("posY", 0.0))
            z = float(s.get("posZ", 0.0))
            r = float(s.get("rssiDbm", -127))
        except (TypeError, ValueError):
            return
        with self._lock:
            self.xs.append(x)
            self.ys.append(y)
            self.zs.append(z)
            self.rssi.append(r)
            self.count += 1

    def clear_samples(self):
        """The app replaced its session (scan load) — drop the old cloud."""
        with self._lock:
            self.xs, self.ys, self.zs, self.rssi = [], [], [], []
            self.count = 0

    def set_pose(self, s):
        try:
            pose = (float(s.get("posX", 0.0)), float(s.get("posY", 0.0)),
                    float(s.get("posZ", 0.0)), float(s.get("yawDeg", 0.0)),
                    time.time())
        except (TypeError, ValueError):
            return
        with self._lock:
            self._pose = pose

    def get_pose(self):
        with self._lock:
            return self._pose

    def set_rooms(self, packet):
        rooms = []
        for r in packet.get("rooms", []):
            pts = [(c.get("x", 0.0), c.get("y", 0.0))
                   for c in r.get("cornersXZ", [])]
            if len(pts) >= 3:
                rooms.append({"pts": pts,
                              "floorY": float(r.get("floorY", 0.0)),
                              "height": float(r.get("height", 0.0))})
        with self._lock:
            if rooms != self._rooms:
                self._rooms = rooms
                self.rooms_version += 1

    def get_rooms(self):
        with self._lock:
            return list(self._rooms), self.rooms_version

    def set_drills(self, packet):
        """Live drill lines: entry point, direction, depth (world coords)."""
        drills = []
        for l in packet.get("lines", []):
            e = l.get("entry", {})
            d = l.get("direction", {})
            drills.append({
                "entry": (float(e.get("x", 0.0)), float(e.get("y", 0.0)),
                          float(e.get("z", 0.0))),
                "dir": (float(d.get("x", 0.0)), float(d.get("y", 0.0)),
                        float(d.get("z", 0.0))),
                "depth": float(l.get("depth", 0.0)),
            })
        with self._lock:
            if drills != self._drills:
                self._drills = drills
                self.drills_version += 1

    def get_drills(self):
        with self._lock:
            return list(self._drills), self.drills_version

    def set_apfix(self, s):
        """Estimated access-point position (weighted centroid)."""
        try:
            fix = (float(s.get("posX", 0.0)), float(s.get("posY", 0.0)),
                   float(s.get("posZ", 0.0)), str(s.get("ssid", "")),
                   int(s.get("rssi", -127)))
        except (TypeError, ValueError):
            return
        with self._lock:
            if fix != self._apfix:
                self._apfix = fix
                self.apfix_version += 1

    def get_apfix(self):
        with self._lock:
            return self._apfix, self.apfix_version

    def set_pings(self, packet):
        """Latency probe nodes: world position + measured round-trip (ms)."""
        pings = []
        for n in packet.get("nodes", []):
            try:
                pings.append((float(n.get("posX", 0.0)), float(n.get("posY", 0.0)),
                              float(n.get("posZ", 0.0)), int(n.get("latencyMs", -2))))
            except (TypeError, ValueError):
                continue
        with self._lock:
            if pings != self._pings:
                self._pings = pings
                self.pings_version += 1

    def get_pings(self):
        with self._lock:
            return list(self._pings), self.pings_version

    def set_notes(self, packet):
        """Spatial notes: world position + type + free text."""
        notes = []
        for n in packet.get("notes", []):
            try:
                notes.append((float(n.get("posX", 0.0)), float(n.get("posY", 0.0)),
                              float(n.get("posZ", 0.0)), str(n.get("type", "")),
                              str(n.get("text", ""))))
            except (TypeError, ValueError):
                continue
        with self._lock:
            if notes != self._notes:
                self._notes = notes
                self.notes_version += 1

    def get_notes(self):
        with self._lock:
            return list(self._notes), self.notes_version

    def snapshot(self):
        with self._lock:
            return (list(self.xs), list(self.ys),
                    list(self.zs), list(self.rssi), self.count)


def receiver_loop(sock, store, writer, fileobj, verbose):
    """Runs in a background thread: receive UDP, store, append to CSV."""
    n = 0
    while True:
        try:
            data, addr = sock.recvfrom(65536)
        except OSError:
            break
        try:
            sample = json.loads(data.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            continue

        # Typed live packets (not logged to CSV): headset pose + floor plan.
        ptype = sample.get("type")
        if ptype == "pose":
            store.set_pose(sample)
            continue
        if ptype == "rooms":
            store.set_rooms(sample)
            continue
        if ptype == "drill":
            store.set_drills(sample)
            continue
        if ptype == "apfix":
            store.set_apfix(sample)
            continue
        if ptype == "ping":
            store.set_pings(sample)
            continue
        if ptype == "notes":
            store.set_notes(sample)
            continue
        if ptype == "clear":
            # Sent when the app loads/re-aligns a session; the samples that
            # follow are the fresh state.
            store.clear_samples()
            continue
        if ptype is not None:
            continue  # unknown typed packet — never a sample

        store.add(sample)
        if writer is not None:
            writer.writerow({k: sample.get(k, "") for k in FIELDS})
            fileobj.flush()

        n += 1
        if verbose and n % 25 == 0:
            print(f"#{n:6d}  RSSI {sample.get('rssiDbm','?'):>4} dBm  "
                  f"pos=({sample.get('posX',0):.2f},"
                  f"{sample.get('posY',0):.2f},{sample.get('posZ',0):.2f})  "
                  f"{sample.get('ssid','')}")


def load_csv_into_store(path, store):
    with open(path, newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            store.add(row)
    print(f"Loaded {store.count} samples from {path}")


def load_rooms_json(path):
    """Parses the app's <stem>_rooms.json (world-XZ corners, floorY, height)."""
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    rooms = []
    for r in data.get("rooms", []):
        pts = [(c.get("x", 0.0), c.get("y", 0.0)) for c in r.get("cornersXZ", [])]
        if len(pts) >= 3:
            rooms.append({
                "pts": pts,
                "floorY": float(r.get("floorY", 0.0)),
                "height": float(r.get("height", 0.0)),
            })
    return rooms


def _set_axes_equal_3d(ax, x, y, z):
    """Force a 1:1:1 data scale on a 3D axes (matplotlib distorts by default)."""
    if len(x) == 0:
        return
    rx, ry, rz = max(x) - min(x), max(y) - min(y), max(z) - min(z)
    half = max(rx, ry, rz, 1e-3) / 2.0
    cx, cy, cz = (max(x) + min(x)) / 2, (max(y) + min(y)) / 2, (max(z) + min(z)) / 2
    ax.set_xlim(cx - half, cx + half)
    ax.set_ylim(cy - half, cy + half)
    ax.set_zlim(cz - half, cz + half)
    ax.set_box_aspect((1, 1, 1))


class ViewerApp:
    """Tkinter main window: control panel on the left, plots on the right."""

    def __init__(self, root, store, args, net_label):
        import tkinter as tk
        from tkinter import ttk
        import matplotlib
        matplotlib.use("TkAgg")
        import matplotlib.pyplot as plt
        from matplotlib import cm, colors
        from matplotlib.backends.backend_tkagg import (
            FigureCanvasTkAgg, NavigationToolbar2Tk)

        self.tk = tk
        self.ttk = ttk
        self.plt = plt
        self.root = root
        self.store = store
        self.args = args

        self.cmap = plt.get_cmap("RdYlGn")
        self.norm = colors.Normalize(vmin=args.rssi_min, vmax=args.rssi_max)

        self.rooms = []            # displayed floor plan (file or live)
        self._rooms_version = -1   # last applied live-rooms version
        self.drills = []           # live drill lines from the Quest
        self._drills_version = -1
        self.apfix = None          # (x, y, z, ssid, rssi) live AP estimate
        self._apfix_version = -1
        self.pings = []            # live latency nodes
        self._pings_version = -1
        self.notes = []            # live spatial notes
        self._notes_version = -1
        self.bg_img = None
        self.bg_extent = None
        self._last_drawn = (-1, None)  # (count, settings-hash) redraw dedupe

        root.title("Quest WiFi Heatmap")
        root.geometry("1500x860")

        # ---- Left: control panel ----
        panel = ttk.Frame(root, padding=8)
        panel.pack(side=tk.LEFT, fill=tk.Y)

        def header(text):
            ttk.Label(panel, text=text.upper(), foreground="#666",
                      font=("Segoe UI", 8, "bold")).pack(
                anchor="w", pady=(10, 2))

        self.status_var = tk.StringVar(value=net_label)
        ttk.Label(panel, textvariable=self.status_var, wraplength=230,
                  font=("Segoe UI", 9)).pack(anchor="w")

        # Ansicht
        header("Ansicht")
        self.mode_var = tk.StringVar(value="Interpoliert")
        for mode in ("Punkte", "Interpoliert", "Raster"):
            ttk.Radiobutton(panel, text=mode, value=mode,
                            variable=self.mode_var,
                            command=self.request_redraw).pack(anchor="w")
        self.grid_var = tk.DoubleVar(value=0.5)
        self._scale(panel, "Rastergröße [m]", self.grid_var, 0.1, 2.0)
        self.show3d_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="3D-Ansicht", variable=self.show3d_var,
                        command=self.request_redraw).pack(anchor="w")

        # Grundriss
        header("Grundriss")
        ttk.Button(panel, text="Grundriss laden (…_rooms.json)",
                   command=self.on_load_rooms).pack(fill=tk.X)
        self.rooms_file_var = tk.StringVar(value="(kein Grundriss geladen)")
        ttk.Label(panel, textvariable=self.rooms_file_var, wraplength=230,
                  foreground="#666").pack(anchor="w")
        self.show_rooms_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Grundriss anzeigen",
                        variable=self.show_rooms_var,
                        command=self.request_redraw).pack(anchor="w")
        self.show_dims_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Wandmaße anzeigen",
                        variable=self.show_dims_var,
                        command=self.request_redraw).pack(anchor="w")
        self.live_rooms_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Live-Grundriss von Quest",
                        variable=self.live_rooms_var,
                        command=self.request_redraw).pack(anchor="w")
        self.show_pose_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Headset-Position anzeigen",
                        variable=self.show_pose_var,
                        command=self.request_redraw).pack(anchor="w")

        # Analyse (Live) — AP-Standort, Ping-Knoten, Notizen
        header("Analyse (Live)")
        self.show_ap_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="AP-Standort", variable=self.show_ap_var,
                        command=self.request_redraw).pack(anchor="w")
        self.show_pings_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Ping-Knoten (Latenz)",
                        variable=self.show_pings_var,
                        command=self.request_redraw).pack(anchor="w")
        self.show_notes_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Notizen", variable=self.show_notes_var,
                        command=self.request_redraw).pack(anchor="w")

        # Hintergrundbild
        header("Hintergrundbild")
        ttk.Button(panel, text="Bild laden…",
                   command=self.on_load_bg).pack(fill=tk.X)
        self.show_bg_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(panel, text="Bild anzeigen", variable=self.show_bg_var,
                        command=self.request_redraw).pack(anchor="w")

        # Ausrichtung (cloud + floor plan together; both share world coords)
        header("Ausrichtung (Wolke + Grundriss)")
        self.rot_var = tk.DoubleVar(value=0.0)
        self.scale_var = tk.DoubleVar(value=1.0)
        self.xoff_var = tk.DoubleVar(value=0.0)
        self.zoff_var = tk.DoubleVar(value=0.0)
        self._scale(panel, "Rotation [°]", self.rot_var, 0.0, 360.0)
        self._scale(panel, "Skalierung", self.scale_var, 0.1, 5.0)
        self._scale(panel, "X-Offset [m]", self.xoff_var, -50.0, 50.0)
        self._scale(panel, "Z-Offset [m]", self.zoff_var, -50.0, 50.0)
        ttk.Button(panel, text="Ausrichtung zurücksetzen",
                   command=self.on_reset_transform).pack(fill=tk.X, pady=(4, 0))

        # Daten
        header("Daten")
        ttk.Button(panel, text="CSV laden…",
                   command=self.on_load_csv).pack(fill=tk.X)

        # ---- Right: matplotlib figure + toolbar ----
        fig = plt.Figure(figsize=(11, 8))
        self.fig = fig
        self.ax2d = fig.add_subplot(1, 2, 1)
        self.ax3d = fig.add_subplot(1, 2, 2, projection="3d")
        mappable = cm.ScalarMappable(norm=self.norm, cmap=self.cmap)
        fig.colorbar(mappable, ax=[self.ax2d, self.ax3d], shrink=0.7,
                     label="RSSI (dBm)   grün = stark · rot = schwach")

        plot_frame = ttk.Frame(root)
        plot_frame.pack(side=tk.RIGHT, fill=tk.BOTH, expand=True)
        self.canvas = FigureCanvasTkAgg(fig, master=plot_frame)
        NavigationToolbar2Tk(self.canvas, plot_frame)
        self.canvas.get_tk_widget().pack(fill=tk.BOTH, expand=True)

        if args.rooms:
            self._load_rooms_file(args.rooms)

        self._tick()

    # ---- UI helpers / callbacks ----

    def _scale(self, parent, label, var, lo, hi):
        tk, ttk = self.tk, self.ttk
        row = ttk.Frame(parent)
        row.pack(fill=tk.X)
        ttk.Label(row, text=label, width=14).pack(side=tk.LEFT)
        s = ttk.Scale(row, from_=lo, to=hi, variable=var,
                      command=lambda _v: self.request_redraw())
        s.pack(side=tk.LEFT, fill=tk.X, expand=True)
        val = ttk.Label(row, width=6)
        val.pack(side=tk.LEFT)

        def sync(*_):
            val.config(text=f"{var.get():.2f}")
        var.trace_add("write", sync)
        sync()

    def request_redraw(self):
        self._last_drawn = (-1, None)

    def on_reset_transform(self):
        self.rot_var.set(0.0)
        self.scale_var.set(1.0)
        self.xoff_var.set(0.0)
        self.zoff_var.set(0.0)
        self.request_redraw()

    def on_load_rooms(self):
        from tkinter import filedialog
        path = filedialog.askopenfilename(
            title="Grundriss-Export auswählen",
            filetypes=[("Raum-Export", "*_rooms.json"), ("JSON", "*.json")])
        if path:
            self._load_rooms_file(path)

    def _load_rooms_file(self, path):
        try:
            self.rooms = load_rooms_json(path)
            import os
            self.rooms_file_var.set(
                f"{os.path.basename(path)} — {len(self.rooms)} Raum/Räume")
            # A manually loaded file wins over the live stream.
            self.live_rooms_var.set(False)
            self.request_redraw()
        except Exception as e:
            self.rooms_file_var.set(f"Fehler: {e}")

    def on_load_bg(self):
        from tkinter import filedialog
        path = filedialog.askopenfilename(
            title="Grundriss-/Hintergrundbild auswählen",
            filetypes=[("Bilder", "*.png *.jpg *.jpeg *.bmp")])
        if not path:
            return
        img = self.plt.imread(path)
        self.bg_img = img
        h, w = img.shape[:2]
        self.bg_extent = [-10, 10, -10 * (h / w), 10 * (h / w)]
        self.request_redraw()

    def on_load_csv(self):
        from tkinter import filedialog
        path = filedialog.askopenfilename(
            title="CSV-Log auswählen", filetypes=[("CSV", "*.csv")])
        if path:
            load_csv_into_store(path, self.store)
            self.request_redraw()

    # ---- Transform shared by cloud and floor plan ----

    def _transform_xz(self, xs, zs):
        import numpy as np
        s = self.scale_var.get()
        a = math.radians(self.rot_var.get())
        c, sn = math.cos(a), math.sin(a)
        xs = np.asarray(xs, dtype=float) * s
        zs = np.asarray(zs, dtype=float) * s
        xr = xs * c - zs * sn
        zr = xs * sn + zs * c
        return xr + self.xoff_var.get(), zr + self.zoff_var.get()

    # ---- Redraw loop ----

    def _settings_hash(self):
        pose = self.store.get_pose()
        pose_key = None
        if pose is not None and self.show_pose_var.get():
            pose_key = (round(pose[0], 2), round(pose[2], 2),
                        round(pose[3], 0), time.time() - pose[4] > 5.0)
        return (self.mode_var.get(), round(self.grid_var.get(), 2),
                self.show3d_var.get(), self.show_rooms_var.get(),
                self.show_dims_var.get(), self.show_bg_var.get(),
                round(self.rot_var.get(), 1), round(self.scale_var.get(), 2),
                round(self.xoff_var.get(), 1), round(self.zoff_var.get(), 1),
                len(self.rooms), self._rooms_version,
                len(self.drills), self._drills_version,
                self._apfix_version, self.show_ap_var.get(),
                self._pings_version, self.show_pings_var.get(),
                self._notes_version, self.show_notes_var.get(),
                self.bg_img is not None, pose_key)

    def _tick(self):
        try:
            # Adopt live floor-plan updates streamed from the Quest.
            if self.live_rooms_var.get():
                rooms, version = self.store.get_rooms()
                if version != self._rooms_version and rooms:
                    self._rooms_version = version
                    self.rooms = rooms
                    self.rooms_file_var.set(
                        f"Live von Quest — {len(rooms)} Raum/Räume")

            drills, dversion = self.store.get_drills()
            if dversion != self._drills_version:
                self._drills_version = dversion
                self.drills = drills

            fix, aversion = self.store.get_apfix()
            if aversion != self._apfix_version:
                self._apfix_version = aversion
                self.apfix = fix
            pings, pversion = self.store.get_pings()
            if pversion != self._pings_version:
                self._pings_version = pversion
                self.pings = pings
            notes, nversion = self.store.get_notes()
            if nversion != self._notes_version:
                self._notes_version = nversion
                self.notes = notes

            xs, ys, zs, rssi, count = self.store.snapshot()
            self.status_var.set(
                self.status_var.get().split("\n")[0] + f"\n{count} Punkte")
            key = (count, self._settings_hash())
            if key != self._last_drawn:
                self._last_drawn = key
                self._redraw(xs, ys, zs, rssi, count)
        finally:
            self.root.after(self.args.refresh_ms, self._tick)

    def _draw_rooms_2d(self, ax):
        for room in self.rooms:
            pts = room["pts"]
            rx, rz = self._transform_xz([p[0] for p in pts],
                                        [p[1] for p in pts])
            xs = list(rx) + [rx[0]]
            zs = list(rz) + [rz[0]]
            ax.plot(xs, zs, color=FLOORPLAN_COLOR, linewidth=2.0, zorder=5)
            if self.show_dims_var.get():
                n = len(pts)
                for i in range(n):
                    j = (i + 1) % n
                    mx, mz = (rx[i] + rx[j]) / 2, (rz[i] + rz[j]) / 2
                    metres = math.hypot(rx[j] - rx[i], rz[j] - rz[i])
                    ax.annotate(f"{metres:.2f} m", (mx, mz), fontsize=7,
                                color="#004466", ha="center", zorder=6,
                                bbox=dict(boxstyle="round,pad=0.1",
                                          fc="white", ec="none", alpha=0.7))

    def _draw_rooms_3d(self, ax):
        for room in self.rooms:
            pts = room["pts"]
            rx, rz = self._transform_xz([p[0] for p in pts],
                                        [p[1] for p in pts])
            y0 = room["floorY"]
            xs = list(rx) + [rx[0]]
            zs = list(rz) + [rz[0]]
            ax.plot(xs, zs, [y0] * len(xs), color=FLOORPLAN_COLOR,
                    linewidth=1.5)
            h = room["height"]
            if h > 0.02:
                y1 = y0 + h
                ax.plot(xs, zs, [y1] * len(xs), color=FLOORPLAN_COLOR,
                        linewidth=1.0, alpha=0.7)
                for i in range(len(pts)):
                    ax.plot([rx[i], rx[i]], [rz[i], rz[i]], [y0, y1],
                            color=FLOORPLAN_COLOR, linewidth=0.8, alpha=0.7)

    def _draw_drills_2d(self, ax):
        """Drill lines from the Quest: red X at the entry point, thin line
        to the projected exit point."""
        for d in self.drills:
            ex, ez = self._transform_xz([d["entry"][0]], [d["entry"][2]])
            out = (d["entry"][0] + d["dir"][0] * d["depth"],
                   d["entry"][2] + d["dir"][2] * d["depth"])
            ox, oz = self._transform_xz([out[0]], [out[1]])
            ax.plot([ex[0], ox[0]], [ez[0], oz[0]], color="#cc2222",
                    linewidth=1.2, zorder=7)
            ax.scatter([ex[0]], [ez[0]], marker="x", color="#cc2222",
                       s=60, linewidths=2.0, zorder=8)

    def _draw_drills_3d(self, ax):
        for d in self.drills:
            e, v, depth = d["entry"], d["dir"], d["depth"]
            start = (e[0] - v[0] * 0.1, e[1] - v[1] * 0.1, e[2] - v[2] * 0.1)
            end = (e[0] + v[0] * depth, e[1] + v[1] * depth, e[2] + v[2] * depth)
            sx, sz = self._transform_xz([start[0], end[0]], [start[2], end[2]])
            ax.plot(list(sx), list(sz), [start[1], end[1]],
                    color="#cc2222", linewidth=1.5)
            ex, ez = self._transform_xz([e[0]], [e[2]])
            ax.scatter([ex[0]], [ez[0]], [e[1]], marker="x", color="#cc2222")

    @staticmethod
    def _ping_color(ms):
        if ms < 0:
            return "#cc2222"      # failed
        if ms < 50:
            return "#22bb44"      # good
        if ms < 120:
            return "#e6b800"      # ok
        return "#cc5522"          # slow

    @staticmethod
    def _note_color(ntype):
        return {"VirtualAp": "#00b0dd", "Interference": "#dd4433",
                "Text": "#cccccc"}.get(ntype, "#cccccc")

    def _draw_apfix_2d(self, ax):
        fx, _fy, fz, ssid, rssi = self.apfix
        (tx,), (tz,) = self._transform_xz([fx], [fz])
        ax.scatter([tx], [tz], marker="*", color="#00d2ff", s=280,
                   edgecolors="#003a50", linewidths=1.5, zorder=9)
        label = f"AP {ssid} ({rssi} dBm)" if ssid else f"AP ({rssi} dBm)"
        ax.annotate(label, (tx, tz), fontsize=8, color="#004466",
                    ha="center", va="bottom", xytext=(0, 10),
                    textcoords="offset points", zorder=9,
                    bbox=dict(boxstyle="round,pad=0.15", fc="white",
                              ec="none", alpha=0.75))

    def _draw_pings_2d(self, ax):
        for px, _py, pz, ms in self.pings:
            (tx,), (tz,) = self._transform_xz([px], [pz])
            ax.scatter([tx], [tz], marker="o", color=self._ping_color(ms),
                       s=70, edgecolors="black", linewidths=0.6, zorder=8)
            ax.annotate("Fehler" if ms < 0 else f"{ms} ms", (tx, tz),
                        fontsize=7, color="#222", ha="center", va="bottom",
                        xytext=(0, 8), textcoords="offset points", zorder=8,
                        bbox=dict(boxstyle="round,pad=0.1", fc="white",
                                  ec="none", alpha=0.7))

    def _draw_notes_2d(self, ax):
        for nx, _ny, nz, ntype, text in self.notes:
            (tx,), (tz,) = self._transform_xz([nx], [nz])
            ax.scatter([tx], [tz], marker="s", color=self._note_color(ntype),
                       s=60, edgecolors="black", linewidths=0.6, zorder=8)
            if text:
                ax.annotate(text, (tx, tz), fontsize=7, color="#222",
                            ha="center", va="bottom", xytext=(0, 8),
                            textcoords="offset points", zorder=8,
                            bbox=dict(boxstyle="round,pad=0.1", fc="white",
                                      ec="none", alpha=0.7))

    def _draw_analysis_3d(self, ax):
        s = self.scale_var.get()
        if self.apfix and self.show_ap_var.get():
            fx, fy, fz, _ssid, _rssi = self.apfix
            (tx,), (tz,) = self._transform_xz([fx], [fz])
            ax.scatter([tx], [tz], [fy * s], marker="*", color="#00d2ff",
                       s=160, edgecolors="#003a50", depthshade=False)
        if self.pings and self.show_pings_var.get():
            for px, py, pz, ms in self.pings:
                (tx,), (tz,) = self._transform_xz([px], [pz])
                ax.scatter([tx], [tz], [py * s], marker="o",
                           color=self._ping_color(ms), s=30, depthshade=False)
        if self.notes and self.show_notes_var.get():
            for nx, ny, nz, ntype, _text in self.notes:
                (tx,), (tz,) = self._transform_xz([nx], [nz])
                ax.scatter([tx], [tz], [ny * s], marker="s",
                           color=self._note_color(ntype), s=28,
                           depthshade=False)

    def _redraw(self, xs_raw, ys_raw, zs_raw, rssi, count):
        import numpy as np
        ax2d, ax3d = self.ax2d, self.ax3d
        cmap, norm, args = self.cmap, self.norm, self.args

        ax2d.clear()
        ax3d.clear()
        ax3d.set_visible(self.show3d_var.get())

        if self.bg_img is not None and self.show_bg_var.get():
            ax2d.imshow(self.bg_img, extent=self.bg_extent,
                        alpha=0.5, zorder=0, origin="upper")

        ax2d.set_title(f"Top-down (X/Z) — {count} Punkte")
        ax2d.set_xlabel("X [m]")
        ax2d.set_ylabel("Z [m]")
        ax2d.grid(True, alpha=0.3)
        ax3d.set_title("3D-Wolke")
        ax3d.set_xlabel("X [m]")
        ax3d.set_ylabel("Z [m]")
        ax3d.set_zlabel("Höhe Y [m]")

        have_cloud = bool(xs_raw)
        if have_cloud:
            xs, zs = self._transform_xz(xs_raw, zs_raw)
            ys = np.asarray(ys_raw, dtype=float) * self.scale_var.get()
            mode = self.mode_var.get()

            if mode == "Punkte":
                ax2d.scatter(xs, zs, c=rssi, cmap=cmap, norm=norm,
                             s=28, edgecolors="none", zorder=2)
            elif mode == "Interpoliert":
                ax2d.scatter(xs, zs, c=rssi, cmap=cmap, norm=norm,
                             s=10, edgecolors="none", alpha=0.5, zorder=3)
                if len(xs) >= 3:
                    try:
                        jx = xs + np.random.normal(0, 1e-5, len(xs))
                        jz = zs + np.random.normal(0, 1e-5, len(zs))
                        levels = np.linspace(args.rssi_min, args.rssi_max, 15)
                        ax2d.tricontourf(jx, jz, rssi, levels=levels,
                                         cmap=cmap, norm=norm, extend="both",
                                         alpha=0.6, zorder=2)
                    except Exception:
                        pass
            elif mode == "Raster":
                res = self.grid_var.get()
                xbins = np.arange(min(xs) - res, max(xs) + res * 2, res)
                zbins = np.arange(min(zs) - res, max(zs) + res * 2, res)
                if len(xbins) > 1 and len(zbins) > 1:
                    h_sum, _, _ = np.histogram2d(xs, zs, bins=(xbins, zbins),
                                                 weights=rssi)
                    h_cnt, _, _ = np.histogram2d(xs, zs, bins=(xbins, zbins))
                    with np.errstate(divide="ignore", invalid="ignore"):
                        h_mean = np.ma.masked_invalid(np.divide(h_sum, h_cnt))
                    ax2d.pcolormesh(xbins, zbins, h_mean.T, cmap=cmap,
                                    norm=norm, alpha=0.8, shading="flat",
                                    zorder=2)
                ax2d.scatter(xs, zs, color="black", s=2, alpha=0.3, zorder=3)

        if self.rooms and self.show_rooms_var.get():
            self._draw_rooms_2d(ax2d)
        if self.drills:
            self._draw_drills_2d(ax2d)
        if self.apfix and self.show_ap_var.get():
            self._draw_apfix_2d(ax2d)
        if self.pings and self.show_pings_var.get():
            self._draw_pings_2d(ax2d)
        if self.notes and self.show_notes_var.get():
            self._draw_notes_2d(ax2d)

        self._draw_pose(ax2d, ax3d)

        ax2d.set_aspect("equal", adjustable="datalim")

        if self.show3d_var.get():
            if have_cloud:
                ax3d.scatter(xs, zs, ys, c=rssi, cmap=cmap, norm=norm,
                             s=18, edgecolors="none", depthshade=True)
            if self.rooms and self.show_rooms_var.get():
                self._draw_rooms_3d(ax3d)
            if self.drills:
                self._draw_drills_3d(ax3d)
            self._draw_analysis_3d(ax3d)
            if have_cloud:
                _set_axes_equal_3d(ax3d, list(xs), list(zs), list(ys))

        self.canvas.draw_idle()

    def _draw_pose(self, ax2d, ax3d):
        """Live headset position + view direction, like the in-app minimap."""
        if not self.show_pose_var.get():
            return
        pose = self.store.get_pose()
        if pose is None or time.time() - pose[4] > 5.0:  # stale = Quest gone
            return
        px, py, pz, yaw_deg, _t = pose
        (tx,), (tz,) = self._transform_xz([px], [pz])
        # View direction: world yaw plus the view-rotation slider.
        a = math.radians(yaw_deg + self.rot_var.get())
        dx, dz = math.sin(a), math.cos(a)
        ax2d.plot([tx], [tz], marker="o", color="#00b0dd", markersize=9,
                  markeredgecolor="#003a50", zorder=7)
        ax2d.annotate("", xy=(tx + dx * 0.6, tz + dz * 0.6), xytext=(tx, tz),
                      zorder=7, arrowprops=dict(arrowstyle="-|>",
                                                color="#00b0dd", lw=2))
        if self.show3d_var.get():
            ax3d.scatter([tx], [tz], [py * self.scale_var.get()],
                         color="#00b0dd", s=45, edgecolors="#003a50",
                         depthshade=False)


def run_gui(store, args, net_label):
    import tkinter as tk
    root = tk.Tk()
    ViewerApp(root, store, args, net_label)
    root.mainloop()


def main():
    ap = argparse.ArgumentParser(description="Quest WiFi-heatmap UDP receiver + viewer")
    ap.add_argument("--host", default="0.0.0.0", help="bind address")
    ap.add_argument("--port", type=int, default=9999, help="UDP port")
    ap.add_argument("--out", default=None,
                    help="CSV log file. Default: a new timestamped file per start "
                         "(wifi_log_YYYYMMDD_HHMMSS.csv). Use '' to disable logging.")
    ap.add_argument("--no-plot", action="store_true", help="headless: log only")
    ap.add_argument("--load", default=None,
                    help="view an existing CSV instead of listening on UDP")
    ap.add_argument("--rooms", default=None,
                    help="preload a <stem>_rooms.json floor-plan export")
    ap.add_argument("--rssi-min", type=float, default=-85.0,
                    help="RSSI mapped to red (weak)")
    ap.add_argument("--rssi-max", type=float, default=-40.0,
                    help="RSSI mapped to green (strong)")
    ap.add_argument("--refresh-ms", type=int, default=500,
                    help="plot refresh interval in ms")
    args = ap.parse_args()

    if args.out is None:
        out_path = f"wifi_log_{datetime.now():%Y%m%d_%H%M%S}.csv"
    elif args.out == "":
        out_path = None
    else:
        out_path = args.out

    store = SampleStore()

    # Offline mode: just view a CSV.
    if args.load:
        load_csv_into_store(args.load, store)
        if args.no_plot:
            return 0
        run_gui(store, args, f"Offline: {args.load}")
        return 0

    # Live mode: bind UDP and receive in a background thread.
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((args.host, args.port))
    print(f"Listening for samples on {args.host}:{args.port} … (Ctrl+C to stop)")

    fileobj = None
    writer = None
    if out_path:
        fileobj = open(out_path, "w", newline="", encoding="utf-8")
        writer = csv.DictWriter(fileobj, fieldnames=FIELDS)
        writer.writeheader()
        print(f"Logging to {out_path}")

    thread = threading.Thread(
        target=receiver_loop,
        args=(sock, store, writer, fileobj, True),
        daemon=True)
    thread.start()

    try:
        if args.no_plot:
            thread.join()
        else:
            run_gui(store, args, f"UDP {args.host}:{args.port}")
    except KeyboardInterrupt:
        pass
    finally:
        sock.close()
        if fileobj:
            fileobj.close()
        print(f"\nStopped. {store.count} samples received"
              + (f", logged to {out_path}" if out_path else "") + ".")
    return 0


if __name__ == "__main__":
    sys.exit(main())
