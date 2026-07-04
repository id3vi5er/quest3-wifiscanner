import pandas as pd
import matplotlib.pyplot as plt
import numpy as np
from matplotlib import cm, colors
from scipy.interpolate import griddata

df = pd.read_csv("wifi_log_20260627_183121.csv")
xs = df["posX"].values
zs = df["posZ"].values
rssi = df["rssiDbm"].values

fig, (ax1, ax2, ax3) = plt.subplots(1, 3, figsize=(15, 5))

cmap = plt.get_cmap("RdYlGn")
norm = colors.Normalize(vmin=-85, vmax=-40)

# 1. Normal scatter
ax1.scatter(xs, zs, c=rssi, cmap=cmap, norm=norm, s=28)
ax1.set_title("Scatter")

# 2. Hexbin
hb = ax2.hexbin(xs, zs, C=rssi, gridsize=20, cmap=cmap, norm=norm, reduce_C_function=np.mean)
ax2.set_title("Hexbin")

# 3. Griddata + pcolormesh
if len(xs) > 3:
    xi = np.linspace(xs.min(), xs.max(), 100)
    zi = np.linspace(zs.min(), zs.max(), 100)
    xi, zi = np.meshgrid(xi, zi)
    
    # average duplicate coordinates to avoid griddata issues or just let griddata handle it
    # griddata handles it by taking the mean or whatever? Actually griddata might have issues with exact duplicates.
    # Let's just pass it
    ri = griddata((xs, zs), rssi, (xi, zi), method='linear')
    ax3.pcolormesh(xi, zi, ri, cmap=cmap, norm=norm, shading='auto')
    ax3.set_title("Griddata")

plt.savefig("heatmap_test.png")
