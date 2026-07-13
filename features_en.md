# Quest 3 AR Wi-Fi Scanner – Features

Welcome to the feature overview of the **Quest 3 Wi-Fi Scanner**! This app transforms the Meta Quest 3 into a professional, spatial network analysis tool. Instead of staring at flat 2D graphs on a smartphone, you make Wi-Fi signals in your real environment **visible**.

Whether for IT administrators, home network optimizers, or as a fascinating tech demo for mixed reality – this software combines state-of-the-art AR technologies with practical data analysis.

---

## Main Features at a Glance

### Immersive Mixed Reality (Passthrough)
* **Full-Color Passthrough:** You see your real environment razor-sharp and in color, while the digital Wi-Fi data is seamlessly overlaid.
* **Boundaryless Mode:** No annoying walls or Guardian boundaries in the way! You can move freely through the entire house, from room to room, without being restricted by artificial play area boundaries.

### Spatial Signal Visualization
Wi-Fi signal strength (RSSI) is drawn live into the room. You have the choice of how you want to see the data:
* **3D Point Cloud:** Leaves a colored path of measurement points directly in the air, exactly where you walked.
* **Floor Heatmap:** Projects a colored grid directly onto the floor of your room. Cold colors (blue/green) stand for weak reception, warm colors (red/orange) for strong signals.
* **Combined Mode:** Shows point cloud and heatmap simultaneously for maximum overview.

### Dynamic Depth Occlusion
Thanks to the revolutionary Depth API of the Quest 3, the virtual data blends perfectly with reality:
* Virtual measurement points and the heatmap **disappear realistically behind real objects** (furniture, walls, people).
* **No prior room scan is necessary**. The Quest 3's depth sensor calculates occlusion in real-time!
* (Optional) **Ghost Mode:** Let the real walls be displayed virtually, based on the Meta Space Setup.

### Orientation & Navigation
Keep track even in large buildings:
* **Interactive Wrist Minimap:** There is a live map on your right wrist. It shows the completely measured area (floor heatmap), your current position as an arrow, and your viewing direction. The map grows automatically as you explore more area. Right below it: the most important real-time values (network/SSID, RSSI in dBm, measurement points, recording status) at a glance.
* **Tool Palette on the Left Wrist:** A quick glance at your left hand is all it takes — a tappable palette holds every tool (room tracing, doors/windows, drill planning, distance measuring, notes, ping test, delete, scan alignment). The active tool lights up and a short usage hint is shown right on your wrist. Holding the opening, note, or ping button opens its door/window, note-type, or ping-host options directly on the wrist.

### Architecture Tools: Room Tracer
Create scaled floor plans directly in AR:
* **Manual Tracing Mode:** Simply click the corners of your real rooms with the controller. The app connects them into a virtual floor plan.
* **Minimap Overlay:** The drawn walls immediately appear as white guidelines on your wrist minimap, so you can see exactly where dead zones might still be in the room.
* **SVG Floor Plan Export:** All drawn rooms are exported as a combined, **scaled vector graphic (.svg)** (1 Unit = 1mm). The walls are practically labeled in meters!

### Precision Tools & Controls
* **Wall Calibration (Grid Align):** Align the heatmap grid parallel to your real walls with just a few clicks, so that the exported maps are perfectly straight.
* **RSSI Filter:** Only looking for dead zones? Simply hide strong signals using a slider and only show values below e.g. -75 dBm.
* **Floating Control Panel:** A compact virtual tablet floating in front of you — neatly organized into four tabs (Recording / View / Rooms / System). Here you control opacity, view modes, filters, storeys, export names (via system keyboard), and start/stop recording. Point at it and hold the right grip to place it freely; as you walk around it, it stays upright and turns to face you.
* **Clean Session Changes:** “New Scan” first asks whether the current session should be exported. It then clears samples, rooms, storeys, tool data, filters, comparisons, and the rolling autosave; the empty session remains paused until recording is deliberately started. Connected web dashboards switch to the empty scan live.
* **Local Throughput Test without PC Software:** The Quest web dashboard uses the open browser itself as the peer and adaptively measures both directions (Quest → browser and browser → Quest) plus local latency. Start it in the browser or with “Durchsatz messen” on the Quest recording panel. Results are stored as colored nodes at the current Quest position, shown live, and exported to JSON and the HTML report. Quest nodes can be hidden together or removed individually with the delete tool. No `receiver.py` is required.

### Comprehensive Data Export
When the scan is complete, the data doesn't stay in the headset. Save your measurement results locally on the Quest and easily pull them to your computer using a PC script (`pull_scans.ps1`):
* **SVG:** Scaled 2D floor plan of the drawn rooms.
* **PNG:** High-resolution 2D image of the floor heatmap.
* **OBJ:** The complete Wi-Fi point cloud as a 3D model (e.g. for Blender or Unity).
* **CSV:** All raw measurement points (time, X/Y/Z coordinates, RSSI value) for data analysis in Excel, Python, or R.
* **JSON:** Structured raw data (incl. room corner points) for easy machine processing.

---
*Experience Wi-Fi like never before – invisible waves become tangible reality.*
