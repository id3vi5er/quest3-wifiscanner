import pandas as pd
import matplotlib.pyplot as plt
import numpy as np
from matplotlib import cm, colors
import matplotlib.tri as mtri

df = pd.read_csv("wifi_log_20260627_183121.csv")
xs = df["posX"].values
zs = df["posZ"].values
rssi = df["rssiDbm"].values

# add some tiny noise to avoid Exact Duplicate points causing Delaunay triangulation failure
xs = xs + np.random.normal(0, 1e-5, len(xs))
zs = zs + np.random.normal(0, 1e-5, len(zs))

fig, ax = plt.subplots(figsize=(6, 6))
cmap = plt.get_cmap("RdYlGn")
norm = colors.Normalize(vmin=-85, vmax=-40)

if len(xs) >= 3:
    # tricontourf
    levels = np.linspace(-85, -40, 15)
    cntr = ax.tricontourf(xs, zs, rssi, levels=levels, cmap=cmap, norm=norm, extend='both')
    ax.plot(xs, zs, 'k.', ms=2, alpha=0.5) # plot data points slightly
    ax.set_title("TriContourF (Interpolated)")

plt.savefig("heatmap_test2.png")
