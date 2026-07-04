#!/usr/bin/env python3
"""
WiFi-Heatmap live receiver + visualiser.

Listens for UDP JSON sample packets streamed from the Quest app
(NetworkStreamer.cs), logs them to CSV, and shows two live views:
  - 2D top-down (Unity X vs Z), coloured by RSSI
  - 3D scatter   (Unity X / Z / height Y), coloured by RSSI
Colour: green = strong, red = weak (matches the in-headset heatmap).

Run on the PC whose IP you configured in NetworkStreamer (default port 9999).

By default every start writes a fresh timestamped log: wifi_log_YYYYMMDD_HHMMSS.csv

Usage:
    python receiver.py                      # listen + live 2D/3D plots, new log file
    python receiver.py --no-plot            # headless: only log to CSV
    python receiver.py --out run42.csv      # fixed log name instead of timestamped
    python receiver.py --out ''             # disable logging
    python receiver.py --load wifi_log_20260627_181500.csv  # view an existing CSV

Live plotting needs matplotlib:
    pip install matplotlib

Each UDP packet is one JSON object:
    {"timestampMs":..., "posX":..., "posY":..., "posZ":...,
     "rssiDbm":..., "linkSpeedMbps":..., "frequencyMhz":...,
     "ssid":"...", "bssid":"..."}
"""
import argparse
import csv
import json
import socket
import sys
import threading
from datetime import datetime

FIELDS = ["timestampMs", "posX", "posY", "posZ", "rssiDbm",
          "linkSpeedMbps", "frequencyMhz", "ssid", "bssid"]


class SampleStore:
    """Thread-safe growing store of received point coordinates + RSSI."""

    def __init__(self):
        self._lock = threading.Lock()
        self.xs, self.ys, self.zs, self.rssi = [], [], [], []
        self.count = 0

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

    def snapshot(self):
        with self._lock:
            return (list(self.xs), list(self.ys),
                    list(self.zs), list(self.rssi), self.count)


def receiver_loop(sock, store, writer, fileobj, verbose):
    """Runs in a background thread: receive UDP, store, append to CSV."""
    n = 0
    while True:
        try:
            data, addr = sock.recvfrom(8192)
        except OSError:
            break
        try:
            sample = json.loads(data.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            continue

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
    ax.set_box_aspect((1, 1, 1))  # equal visual scale on all three axes


def run_plots(store, args):
    import matplotlib
    import matplotlib.pyplot as plt
    import matplotlib.animation as animation
    from matplotlib import cm, colors
    from matplotlib.widgets import RadioButtons, Slider, Button
    import numpy as np

    cmap = plt.get_cmap("RdYlGn")
    norm = colors.Normalize(vmin=args.rssi_min, vmax=args.rssi_max)

    fig = plt.figure(figsize=(15, 8))
    fig.canvas.manager.set_window_title("Quest WiFi Heatmap")
    
    # Platz für die GUI-Elemente unten schaffen
    fig.subplots_adjust(bottom=0.35)
    
    ax2d = fig.add_subplot(1, 2, 1)
    ax3d = fig.add_subplot(1, 2, 2, projection="3d")

    mappable = cm.ScalarMappable(norm=norm, cmap=cmap)
    fig.colorbar(mappable, ax=[ax2d, ax3d], shrink=0.7,
                 label="RSSI (dBm)   grün = stark · rot = schwach")

    # --- GUI Elemente anlegen ---
    # Spalte 1: Ansicht & Grid
    ax_radio = fig.add_axes([0.05, 0.05, 0.12, 0.20])
    radio = RadioButtons(ax_radio, ('Punkte', 'Interpoliert', 'Raster'), active=1)
    
    ax_slider_grid = fig.add_axes([0.25, 0.25, 0.2, 0.03])
    slider_grid = Slider(ax_slider_grid, 'Raster [m]', 0.1, 2.0, valinit=0.5, valstep=0.1)

    # Spalte 2: Transformation der Punktewolke (um sie ans Bild anzupassen)
    ax_slider_rot = fig.add_axes([0.25, 0.15, 0.2, 0.03])
    slider_rot = Slider(ax_slider_rot, 'Rotation [°]', 0.0, 360.0, valinit=0.0)

    ax_slider_scale = fig.add_axes([0.25, 0.05, 0.2, 0.03])
    slider_scale = Slider(ax_slider_scale, 'Skalierung', 0.1, 5.0, valinit=1.0)

    # Spalte 3: Offsets & Hintergrund
    ax_slider_xoff = fig.add_axes([0.55, 0.25, 0.2, 0.03])
    slider_xoff = Slider(ax_slider_xoff, 'X-Offset [m]', -50.0, 50.0, valinit=0.0)

    ax_slider_zoff = fig.add_axes([0.55, 0.15, 0.2, 0.03])
    slider_zoff = Slider(ax_slider_zoff, 'Z-Offset [m]', -50.0, 50.0, valinit=0.0)

    ax_btn_bg = fig.add_axes([0.55, 0.05, 0.2, 0.05])
    btn_bg = Button(ax_btn_bg, 'Hintergrundbild laden')

    app_state = {'bg_img': None, 'bg_extent': None}

    def load_bg(event):
        try:
            import tkinter as tk
            from tkinter import filedialog
            root = tk.Tk()
            root.withdraw()
            root.attributes('-topmost', True)
            filepath = filedialog.askopenfilename(
                title="Grundriss auswählen",
                filetypes=[("Bilder", "*.png *.jpg *.jpeg *.bmp")]
            )
            if filepath:
                img = plt.imread(filepath)
                app_state['bg_img'] = img
                h, w = img.shape[:2]
                # Bild grob zentrieren, z.B. 20m breit machen als Startwert
                app_state['bg_extent'] = [-10, 10, -10 * (h/w), 10 * (h/w)]
            root.destroy()
        except Exception as e:
            print("Fehler beim Laden des Bildes:", e)

    btn_bg.on_clicked(load_bg)

    def update(_frame):
        xs_raw, ys_raw, zs_raw, rssi, count = store.snapshot()

        ax2d.clear()
        ax3d.clear()

        # Hintergrund zeichnen
        if app_state['bg_img'] is not None:
            # origin='upper' ist wichtig, da Bilder von oben links (0,0) nach unten rechts gezeichnet werden
            ax2d.imshow(app_state['bg_img'], extent=app_state['bg_extent'], 
                        alpha=0.5, zorder=0, origin='upper')

        # --- 2D top-down ---
        ax2d.set_title(f"Top-down (X/Z) — {count} Punkte")
        ax2d.set_xlabel("X [m]")
        ax2d.set_ylabel("Z [m]")
        ax2d.grid(True, alpha=0.3)

        # --- 3D ---
        ax3d.set_title("3D-Wolke")
        ax3d.set_xlabel("X [m]")
        ax3d.set_ylabel("Z [m]")
        ax3d.set_zlabel("Höhe Y [m]")

        if not xs_raw:
            return []

        # Numpy Arrays für Berechnungen nutzen
        xs = np.array(xs_raw)
        ys = np.array(ys_raw)
        zs = np.array(zs_raw)

        # Transformation anwenden
        # 1. Skalieren
        scale = slider_scale.val
        xs = xs * scale
        ys = ys * scale
        zs = zs * scale

        # 2. Rotieren (in der X/Z Ebene)
        angle = np.radians(slider_rot.val)
        c, s = np.cos(angle), np.sin(angle)
        xs_rot = xs * c - zs * s
        zs_rot = xs * s + zs * c

        # 3. Verschieben (Offset)
        xs_final = xs_rot + slider_xoff.val
        zs_final = zs_rot + slider_zoff.val
        ys_final = ys
        
        mode = radio.value_selected
        
        if mode == 'Punkte':
            ax2d.scatter(xs_final, zs_final, c=rssi, cmap=cmap, norm=norm,
                         s=28, edgecolors="none", zorder=2)
                         
        elif mode == 'Interpoliert':
            ax2d.scatter(xs_final, zs_final, c=rssi, cmap=cmap, norm=norm,
                         s=10, edgecolors="none", alpha=0.5, zorder=3)
            if len(xs_final) >= 3:
                try:
                    xs_arr = xs_final + np.random.normal(0, 1e-5, len(xs_final))
                    zs_arr = zs_final + np.random.normal(0, 1e-5, len(zs_final))
                    levels = np.linspace(args.rssi_min, args.rssi_max, 15)
                    ax2d.tricontourf(xs_arr, zs_arr, rssi, levels=levels, 
                                     cmap=cmap, norm=norm, extend='both', alpha=0.6, zorder=2)
                except Exception:
                    pass
                    
        elif mode == 'Raster':
            grid_res = slider_grid.val
            xmin, xmax = min(xs_final), max(xs_final)
            zmin, zmax = min(zs_final), max(zs_final)
            
            xbins = np.arange(xmin - grid_res, xmax + grid_res*2, grid_res)
            zbins = np.arange(zmin - grid_res, zmax + grid_res*2, grid_res)
            
            if len(xbins) > 1 and len(zbins) > 1:
                H_sum, _, _ = np.histogram2d(xs_final, zs_final, bins=(xbins, zbins), weights=rssi)
                H_count, _, _ = np.histogram2d(xs_final, zs_final, bins=(xbins, zbins))
                
                with np.errstate(divide='ignore', invalid='ignore'):
                    H_mean = np.divide(H_sum, H_count)
                    
                H_mean = np.ma.masked_invalid(H_mean)
                
                ax2d.pcolormesh(xbins, zbins, H_mean.T, cmap=cmap, norm=norm, 
                                alpha=0.8, shading='flat', zorder=2)
                                
            ax2d.scatter(xs_final, zs_final, color='black', s=2, alpha=0.3, zorder=3)
                
        ax2d.set_aspect("equal", adjustable="datalim")

        ax3d.scatter(xs_final, zs_final, ys_final, c=rssi, cmap=cmap, norm=norm,
                     s=18, edgecolors="none", depthshade=True)
        _set_axes_equal_3d(ax3d, xs_final, zs_final, ys_final)

        return []

    # Cache disabled so clear()/re-scatter each frame works cleanly.
    _ani = animation.FuncAnimation(
        fig, update, interval=args.refresh_ms, cache_frame_data=False)
    plt.show()


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
    ap.add_argument("--rssi-min", type=float, default=-85.0,
                    help="RSSI mapped to red (weak)")
    ap.add_argument("--rssi-max", type=float, default=-40.0,
                    help="RSSI mapped to green (strong)")
    ap.add_argument("--refresh-ms", type=int, default=500,
                    help="plot refresh interval in ms")
    args = ap.parse_args()

    # Resolve the log file: a fresh timestamped file per start by default,
    # an explicit name if --out was given, or disabled with --out ''.
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
        run_plots(store, args)
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
            run_plots(store, args)  # blocks until window closed
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
